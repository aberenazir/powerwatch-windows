// The model: readings every 3 seconds, the battery estimate, advice, power modes, checkup and Focus.
// A port of the Mac Power Watch logic, with Windows' own readings and settings.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PowerWatch
{
    class Estimate
    {
        public double Watts, Minutes, Low, High;
        public string Basis = "";
    }

    class Verdict
    {
        public string Glyph;
        public Color Color;
        public string Title;
        public List<string> Lines = new List<string>();
        public bool EfficiencyTip;
    }

    class ModeEstimate
    {
        public int Mode;
        public double Watts, Minutes;
        public bool IsCurrent;
        public string Note;
    }

    enum Activity { Light, Video, Heavy }

    class BestWay
    {
        public string Activity = "";
        public int Mode;
        public string Reason = "";
        public List<string> Tips = new List<string>();
    }

    class Checkup
    {
        public double UptimeDays;
        public int FreeMemPct = 100;
        public double PagedGB;
        public List<string> StartupVendors = new List<string>();
        public int StartupCount;
        public int SleepMinutesOnBattery = -1;     // 0 = never
        public int ScreenMinutesOnBattery = -1;
    }

    class CheckItem
    {
        public string Glyph;
        public Color Color;
        public string Title, Text;
        public string ButtonLabel, ButtonUrl;
    }

    static class Palette
    {
        public static readonly Color Green = Color.FromArgb(110, 214, 140);
        public static readonly Color Orange = Color.FromArgb(245, 166, 70);
        public static readonly Color Red = Color.FromArgb(240, 95, 95);
        public static readonly Color Blue = Color.FromArgb(96, 165, 250);
        public static readonly Color Yellow = Color.FromArgb(245, 205, 80);
        public static readonly Color Dim = Color.FromArgb(165, 165, 165);

        public static Color Level(int l) { return l == 2 ? Red : (l == 1 ? Orange : Green); }
    }

    class Model
    {
        public PowerInfo Power = new PowerInfo();
        public List<AppGroup> Apps = new List<AppGroup>();
        public List<AppGroup> SystemGroups = new List<AppGroup>();
        public DateTime? Updated;
        public AppGroup HiddenHog;
        public List<Sample> Samples = new List<Sample>();
        public DateTime? UnplugTime;
        public int UnplugPercent;
        public int FrontPid;
        public string FrontName = "";
        public int PowerMode, BatteryMode, ChargerMode;
        public Checkup Checkup = new Checkup();
        public string FocusMessage;
        public List<string> LastQuit = new List<string>();
        public List<Prediction> Predictions = new List<Prediction>();
        public LearnStore LearnData = new LearnStore();
        public double VideoDecode;

        public event Action Changed;

        readonly Scanner scanner = new Scanner();
        readonly Timer timer = new Timer();
        double minuteSum;
        int minuteN;
        DateTime minuteStart = DateTime.UtcNow;
        readonly Dictionary<string, int> minuteApps = new Dictionary<string, int>();
        bool? wasOnBattery;
        Dictionary<string, DateTime> heavySince = new Dictionary<string, DateTime>();
        bool fast, busy, reading;
        int ticks;
        DateTime checkupRead = DateTime.MinValue;

        public Scanner Scanner { get { return scanner; } }

        public Model()
        {
            History h = Json.Load<History>("history.json");
            DateTime now = DateTime.UtcNow;
            if (h != null)
            {
                if (h.Samples != null) Samples = h.Samples.FindAll(delegate(Sample s) { return (now - s.T).TotalSeconds <= 3600; });
                if (h.Predictions != null) Predictions = h.Predictions.FindAll(delegate(Prediction p) { return (now - p.T).TotalSeconds <= 7200; });
            }
            LearnData = Learn.Load();
            RunBacktest();
            timer.Interval = 3000;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Tick();
            Refresh();
        }

        public void Stop()
        {
            timer.Stop();
            SaveHistory();
            Learn.Save(LearnData);
        }

        void SaveHistory()
        {
            Json.Save("history.json", new History { Samples = Samples, Predictions = Predictions });
        }

        void Notify() { if (Changed != null) Changed(); }

        async void Tick()
        {
            if (reading) return;
            reading = true;
            PowerInfo p;
            try { p = await Task.Run(delegate { return Sensors.Power(); }); }
            catch (Exception ex) { Log.Write("power: " + ex.Message); reading = false; return; }
            reading = false;
            ticks++;
            Power = p;
            DateTime now = DateTime.UtcNow;
            if (wasOnBattery == false && p.OnBattery) { UnplugTime = now; UnplugPercent = p.Percent; }
            if (!p.OnBattery) UnplugTime = null;
            if (wasOnBattery.HasValue && wasOnBattery.Value != p.OnBattery) Predictions.Clear();
            wasOnBattery = p.OnBattery;

            if (p.OnBattery && p.WattsKnown)
                Samples.Add(new Sample { T = now, W = p.Watts, MWh = p.GaugeMWh > 0 ? p.GaugeMWh : -1 });
            Samples.RemoveAll(delegate(Sample s) { return (now - s.T).TotalSeconds > 3600; });

            if (ticks % 20 == 0)
            {
                if (p.OnBattery && (Predictions.Count == 0 || (now - Predictions[Predictions.Count - 1].T).TotalSeconds >= 295))
                {
                    Estimate e = GetEstimate();
                    if (e.Minutes > 0) Predictions.Add(new Prediction { T = now, EmptyAt = now.AddMinutes(e.Minutes), Percent = p.Percent });
                    Predictions.RemoveAll(delegate(Prediction x) { return (now - x.T).TotalSeconds > 7200; });
                }
                SaveHistory();
            }

            int fp = Scanner.ForegroundPid();
            if (fp != 0 && fp != scanner.MyPid)
            {
                FrontPid = fp;
                FrontName = NameOfPid(fp);
            }
            LearnTick(p, now);
            if (fast || ticks % 10 == 0) Refresh();
            else Notify();
        }

        readonly Dictionary<int, string> frontNames = new Dictionary<int, string>();

        string NameOfPid(int pid)
        {
            string n;
            if (frontNames.TryGetValue(pid, out n)) return n;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            string path = "";
            if (h != IntPtr.Zero) { path = Native.ExePath(h); Native.CloseHandle(h); }
            n = scanner.FriendlyName(path, Path.GetFileNameWithoutExtension(path));
            if (frontNames.Count > 200) frontNames.Clear();
            frontNames[pid] = n;
            return n;
        }

        // ---------- learning

        void LearnTick(PowerInfo p, DateTime now)
        {
            // Match the live readings to the battery gauge (the readings can miss short bursts).
            if (ticks % 20 == 0)
            {
                double? g = GaugeWatts(600);
                if (g.HasValue)
                {
                    double r = g.Value / Math.Max(SampleWatts(600), 0.1);
                    if (r > 0.7 && r < 1.4) LearnData.K = 0.9 * LearnData.K + 0.1 * r;
                }
            }
            if ((now - minuteStart).TotalSeconds > 90) { minuteSum = 0; minuteN = 0; minuteApps.Clear(); minuteStart = now; }   // slept
            if (p.OnBattery && p.WattsKnown)
            {
                minuteSum += p.Watts; minuteN++;
                if (FrontName.Length > 0) { int c; minuteApps.TryGetValue(FrontName, out c); minuteApps[FrontName] = c + 1; }
            }
            if ((now - minuteStart).TotalSeconds >= 60)
            {
                if (minuteN >= 15 && minuteApps.Count > 0)
                {
                    string front = minuteApps.OrderByDescending(kv => kv.Value).First().Key;
                    string app = front + "|" + PowerMode;                    // apps use different power in each mode
                    double w = minuteSum / minuteN * LearnData.K;
                    LearnData.Minutes.Add(new Minute { T = now, W = w, App = app, Bat = true });
                    Prof pr;
                    if (!LearnData.Profiles.TryGetValue(app, out pr)) LearnData.Profiles[app] = pr = new Prof();
                    pr.Wh += w / 60; pr.Hours += 1.0 / 60;
                    if (pr.Hours > 20) { pr.Wh *= 20 / pr.Hours; pr.Hours = 20; }
                    LearnData.Minutes.RemoveAll(delegate(Minute x) { return (now - x.T).TotalDays > 7; });
                }
                minuteSum = 0; minuteN = 0; minuteApps.Clear(); minuteStart = now;
            }
            if (ticks % 100 == 0) Learn.Save(LearnData);
            if (ticks % 1200 == 0) RunBacktest();
        }

        void RunBacktest()
        {
            List<Minute> ms = new List<Minute>(LearnData.Minutes);
            Task.Run(delegate { return Learn.Backtest(ms); }).ContinueWith(delegate(Task<Tuned> t)
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null) LearnData.Tuned = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void SetOpen(bool open)
        {
            fast = open;
            if (open) Refresh();
        }

        // ---------- scanning programs (every 3 s while open, every 30 s in the background)

        public async void Refresh()
        {
            if (busy) return;
            busy = true;
            bool onBattery = Power.OnBattery;
            bool readCheck = (DateTime.UtcNow - checkupRead).TotalSeconds >= 30;
            List<AppGroup> apps = null, sys = null;
            int now = 0, bat = 0, chg = 0;
            Checkup ck = null;
            GpuReading gpu = null;
            try
            {
                await Task.Run(delegate
                {
                    gpu = Sensors.Gpu();
                    scanner.Build(gpu.PerPid, out apps, out sys);
                    Sensors.PowerModes(onBattery, out now, out bat, out chg);
                    if (readCheck) ck = ReadCheckup();
                });
            }
            catch (Exception ex) { Log.Write("refresh: " + ex); busy = false; return; }
            PowerMode = now; BatteryMode = bat; ChargerMode = chg;
            if (ck != null) { Checkup = ck; checkupRead = DateTime.UtcNow; }
            Power.GpuBusy = gpu.Total;
            VideoDecode = gpu.VideoDecode;

            // Only warn about a hidden program once it has stayed heavy for 20 seconds (not a quick blip).
            Dictionary<string, DateTime> still = new Dictionary<string, DateTime>();
            DateTime t = DateTime.UtcNow;
            foreach (AppGroup g in apps)
            {
                if (!g.Warn) continue;
                DateTime since;
                if (!heavySince.TryGetValue(g.Id, out since)) since = t;
                still[g.Id] = since;
                if ((t - since).TotalSeconds < 20) g.Warn = false;
            }
            heavySince = still;
            Apps = apps;
            SystemGroups = sys;
            HiddenHog = apps.Find(delegate(AppGroup g) { return g.Warn; });
            Updated = DateTime.Now;
            busy = false;
            Notify();
        }

        // ---------- quitting

        public bool IsProtected(AppGroup g)
        {
            return g.IsSelf || g.Kind == Kind.System || Path.GetFileName(g.ExePath).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
        }

        // Normal quit: asks each window to close, so apps with unsaved work ask you first.
        public void CloseWindows(AppGroup g)
        {
            foreach (int pid in g.Pids)
            {
                List<IntPtr> wins;
                if (scanner.LastWindows.TryGetValue(pid, out wins))
                    foreach (IntPtr h in wins) Native.PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero);   // WM_CLOSE
            }
        }

        public int Kill(AppGroup g)
        {
            int failed = 0;
            foreach (int pid in g.Pids)
            {
                try { using (Process p = Process.GetProcessById(pid)) p.Kill(); }
                catch { failed++; }
            }
            return failed;
        }

        public void FocusQuit(List<AppGroup> gs)
        {
            List<string> reopen = new List<string>();
            foreach (AppGroup g in gs)
            {
                if (g.HasWindow && !string.IsNullOrEmpty(g.ExePath) && g.ExePath.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) < 0)
                    reopen.Add(g.ExePath);
                if (g.HasWindow) CloseWindows(g); else Kill(g);
            }
            LastQuit = reopen;
            FocusMessage = "Quitting " + gs.Count + (gs.Count == 1 ? " app..." : " apps...");
            Notify();
            Timer check = new Timer();
            check.Interval = 5000;
            check.Tick += delegate
            {
                check.Stop();
                check.Dispose();
                List<string> stillOpen = new List<string>();
                foreach (AppGroup g in gs)
                {
                    bool alive = false;
                    foreach (int pid in g.Pids)
                    {
                        try { using (Process p = Process.GetProcessById(pid)) if (!p.HasExited) alive = true; } catch { }
                    }
                    if (alive) stillOpen.Add(g.Name);
                }
                int done = gs.Count - stillOpen.Count;
                string s = "Quit " + done + (done == 1 ? " app." : " apps.");
                if (stillOpen.Count > 0)
                    s += " Still open: " + string.Join(", ", stillOpen.ToArray()) + ". It's probably asking you to save, so answer it in that app.";
                FocusMessage = s;
                Refresh();
            };
            check.Start();
        }

        public void ReopenLast()
        {
            foreach (string path in LastQuit)
            {
                try { Process.Start(path); } catch { }
            }
            LastQuit = new List<string>();
            FocusMessage = null;
            Notify();
        }

        // ---------- estimates

        public double AvgMinutes
        {
            get { return Samples.Count == 0 ? 0 : Math.Min(10, (DateTime.UtcNow - Samples[0].T).TotalMinutes); }
        }

        public double AvgWatts(double secs = 600)
        {
            double? g = GaugeWatts(secs);
            return g.HasValue ? g.Value : SampleWatts(secs);
        }

        public double SampleWatts(double secs)
        {
            DateTime cut = DateTime.UtcNow.AddSeconds(-secs);
            double sum = 0; int n = 0;
            foreach (Sample s in Samples) if (s.T >= cut) { sum += s.W; n++; }
            if (n > 0) return sum / n;
            return Power.WattsKnown ? Power.Watts : PluggedEstimate();
        }

        /// Average watts from the drop in the battery's own energy counter (needs 5 minutes and 0.1 Wh).
        public double? GaugeWatts(double secs)
        {
            DateTime cut = DateTime.UtcNow.AddSeconds(-secs);
            double time = 0, drop = 0;
            Sample prev = null;
            foreach (Sample s in Samples)
            {
                if (s.T < cut) continue;
                if (prev != null && prev.MWh > 0 && s.MWh > 0)
                {
                    double dt = (s.T - prev.T).TotalSeconds;
                    if (dt > 0 && dt <= 30) { time += dt; drop += Math.Max(0, prev.MWh - s.MWh); }
                }
                prev = s;
            }
            if (time < Math.Min(secs, 300) * 0.8 || drop < 100) return null;
            return drop / 1000 / (time / 3600);
        }

        /// Windows only measures the laptop's use on battery. Plugged in, use what it learned on battery.
        public double PluggedEstimate()
        {
            if (LearnData.Minutes.Count < 30) return 0;
            double sum = 0;
            foreach (Minute m in LearnData.Minutes) sum += m.W;
            return sum / LearnData.Minutes.Count;
        }

        public double PredictWatts()
        {
            double shortW = AvgWatts(600);
            double shortMins = AvgMinutes;
            Tuned tuned = LearnData.Tuned;
            double baseW = shortW;
            double? longW = LongWatts;
            if (longW.HasValue)
            {
                double ws = (tuned != null ? tuned.Ws : 0.55) * Math.Min(1, shortMins / 5);
                baseW = ws * shortW + (1 - ws) * longW.Value;
            }
            double? appW = FrontAppWatts;
            if (!appW.HasValue) return baseW;
            double a = tuned != null ? tuned.A : 0.3;
            return (1 - a) * baseW + a * appW.Value;
        }

        public double? FrontAppWatts
        {
            get
            {
                Prof pr;
                if (LearnData.Profiles.TryGetValue(FrontName + "|" + PowerMode, out pr) && pr.Hours >= 1.0 / 3) return pr.Avg;
                return null;
            }
        }

        public double? LongWatts
        {
            get
            {
                double hourMins = Samples.Count == 0 ? 0 : (DateTime.UtcNow - Samples[0].T).TotalMinutes;
                if (hourMins >= 20) return AvgWatts(3600);
                return UnplugWatts;
            }
        }

        public double? UnplugWatts
        {
            get
            {
                if (!Power.OnBattery || !UnplugTime.HasValue || Power.FullWh <= 0) return null;
                double hours = (DateTime.UtcNow - UnplugTime.Value).TotalHours, used = UnplugPercent - Power.Percent;
                if (hours < 1.0 / 3 || used < 2) return null;
                return used / 100 * Power.FullWh / hours;
            }
        }

        public double MinutesLeft(double watts) { return watts > 0.3 ? Power.UsableWh / watts * 60 : 0; }

        public Estimate GetEstimate()
        {
            double w = PredictWatts();
            Estimate e = new Estimate { Watts = w, Minutes = MinutesLeft(w) };
            DateTime now = DateTime.UtcNow;
            List<double> blocks = new List<double>();
            for (int k = 0; k < 12; k++)
            {
                DateTime a = now.AddSeconds(-(k + 1) * 300), b = now.AddSeconds(-k * 300);
                double sum = 0; int n = 0;
                foreach (Sample s in Samples) if (s.T >= a && s.T < b) { sum += s.W; n++; }
                if (n >= 20) blocks.Add(sum / n);
            }
            double? g = GaugeWatts(3600);
            if (g.HasValue)
            {
                double kk = Math.Min(1.25, Math.Max(0.8, g.Value / Math.Max(SampleWatts(3600), 0.1)));
                for (int i = 0; i < blocks.Count; i++) blocks[i] *= kk;
            }
            blocks.Sort();
            if (LearnData.Tuned != null)
            {
                double err = Math.Min(0.6, LearnData.Tuned.Err);
                e.Low = MinutesLeft(w * (1 + err));
                e.High = MinutesLeft(w * (1 - err));
            }
            else if (blocks.Count >= 4)
            {
                double lo = blocks[blocks.Count / 4], hi = blocks[(blocks.Count * 3) / 4];
                e.High = Math.Max(MinutesLeft(Math.Max(lo, 0.5)), e.Minutes);
                e.Low = MinutesLeft(Math.Max(hi, w));
            }
            int hourMins = Samples.Count == 0 ? 0 : (int)Math.Min(60, (now - Samples[0].T).TotalMinutes);
            if (hourMins >= 20) e.Basis = "your last " + hourMins + " min of use, weighted toward the last 10 min";
            else if (UnplugWatts.HasValue) e.Basis = "your use since you unplugged, mixed with the last " + Math.Max(1, hourMins) + " min";
            else e.Basis = hourMins >= 1 ? "the last " + hourMins + " min" : "right now";
            double? aw = FrontAppWatts;
            if (aw.HasValue) e.Basis += ", and what " + FrontName + " usually uses for you (" + aw.Value.ToString("0.0") + " W)";
            return e;
        }

        bool AccuracyCheck(out DateTime said, out DateTime nowSays, out double minutesOff)
        {
            said = nowSays = DateTime.MinValue;
            minutesOff = 0;
            if (!Power.OnBattery) return false;
            Estimate e = GetEstimate();
            if (e.Minutes <= 0) return false;
            DateTime target = DateTime.UtcNow.AddSeconds(-1800);
            Prediction best = null;
            foreach (Prediction p in Predictions)
                if (Math.Abs((p.T - target).TotalSeconds) < 360 && p.Percent >= Power.Percent &&
                    (best == null || Math.Abs((p.T - target).TotalSeconds) < Math.Abs((best.T - target).TotalSeconds))) best = p;
            if (best == null) return false;
            said = best.EmptyAt.ToLocalTime();
            nowSays = DateTime.Now.AddMinutes(e.Minutes);
            minutesOff = (nowSays - said).TotalMinutes;
            return true;
        }

        /// Rough watts one app is responsible for: its share of everything above the screen + Windows' base load (~5 W).
        public double AppWatts(AppGroup g) { return g.Share * Math.Max(0, PredictWatts() - 5); }

        public double MinutesGained(IEnumerable<AppGroup> gs)
        {
            double w = PredictWatts(), a = 0;
            foreach (AppGroup g in gs) a += AppWatts(g);
            if (!Power.OnBattery || w - a <= 0.5) return 0;
            return MinutesLeft(w - a) - MinutesLeft(w);
        }

        public double MinutesGained(AppGroup g) { return MinutesGained(new[] { g }); }

        public string AverageWindowText
        {
            get
            {
                double m = AvgMinutes;
                return m >= 9.5 ? "the last 10 min" : (m < 1 ? "right now" : "the last " + (int)Math.Round(m) + " min");
            }
        }

        static readonly HashSet<string> BrowserNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Google Chrome", "Brave Browser", "Microsoft Edge", "Firefox", "Opera", "Vivaldi", "Arc" };

        public static readonly HashSet<string> Editors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ThumbStudio 3D", "ThumbStudio Video", "ThumbStudio 2D", "ThumbStudio Music", "ThumbStudio Pro", "Blender", "DaVinci Resolve",
            "Adobe Premiere Pro", "CapCut", "Adobe After Effects", "Adobe Photoshop", "OBS Studio", "Visual Studio", "Clipchamp", "Unity", "Unreal Editor"
        };

        public bool IsBrowser(AppGroup g) { return BrowserNames.Contains(g.Name) || g.Pages > 0; }

        public Verdict GetVerdict()
        {
            PowerInfo p = Power;
            Estimate est = GetEstimate();
            double w = est.Watts;
            AppGroup top = Apps.Find(delegate(AppGroup g) { return !g.IsSelf; });
            Verdict v;
            if (!p.HasBattery)
            {
                v = new Verdict { Glyph = "", Color = Palette.Green, Title = "No battery: this PC runs on mains power" };
                v.Lines.Add("Battery estimates don't apply. The list below still shows which apps work the hardest.");
            }
            else if (p.OnBattery)
            {
                double left = est.Minutes;
                int lvl = w >= 20 ? 2 : (w >= 8 ? 1 : 0);
                v = new Verdict
                {
                    Glyph = lvl == 0 ? "" : (lvl == 1 ? "" : ""),
                    Color = Palette.Level(lvl),
                    Title = new[] { "All good: light use", "Okay: medium use", "Heavy use: the battery is draining fast" }[lvl]
                };
                if (left > 0)
                {
                    string s = "Battery left: about " + Fmt.Duration(left) + ", so it would turn off around " + Fmt.Clock(DateTime.Now.AddMinutes(left)) + ".";
                    if (est.High - est.Low >= 10)
                        s += " Likely between " + Fmt.Duration(est.Low) + " (busier) and " + Fmt.Duration(est.High) + " (lighter), depending on what you do.";
                    s += " Based on " + est.Basis + " (" + w.ToString("0.0") + " W).";
                    v.Lines.Add(s);
                    if (LearnData.Tuned != null)
                    {
                        double err = Math.Min(0.6, LearnData.Tuned.Err);
                        double plusMin = (MinutesLeft(w * (1 - err)) - MinutesLeft(w * (1 + err))) / 2;
                        v.Lines.Add("Accuracy: tested on your last " + Fmt.Duration(LearnData.Tuned.Hours * 60) + " of use, this estimate was within ±" +
                                    (int)Math.Round(err * 100) + "% two times out of three. Right now that's about ±" + Fmt.Duration(plusMin) + ".");
                    }
                    DateTime said, nowSays;
                    double off;
                    if (AccuracyCheck(out said, out nowSays, out off))
                    {
                        v.Lines.Add(Math.Abs(off) < 5
                            ? "Accuracy: 30 min ago this said it would turn off around " + Fmt.Clock(said) + ". It still says " + Fmt.Clock(nowSays) + ", so it's on track."
                            : "Accuracy: 30 min ago this said " + Fmt.Clock(said) + ", now " + Fmt.Clock(nowSays) + " (" + Fmt.Duration(Math.Abs(off)) + " " +
                              (off > 0 ? "later, you've been lighter" : "earlier, you've been busier") + ").");
                    }
                }
                else if (!p.WattsKnown) v.Lines.Add("Measuring... (the battery reports its use every few seconds).");
                if (top != null && lvl >= 1 && AppWatts(top) >= 2)
                {
                    string s = "Biggest user: " + top.Name + " (" + (int)Math.Round(top.Share * 100) + "% of power).";
                    double gain = MinutesGained(top);
                    if (top.Pids.Contains(FrontPid)) s += " You're using it now, which is fine if you need it.";
                    else if (gain >= 5) s += " Quitting it could add about " + Fmt.Duration(gain) + ".";
                    v.Lines.Add(s);
                }
                if (lvl == 0 && left > 0) v.Lines.Add("Nothing to do. This is how a healthy laptop should look.");
                if (p.Percent <= 20 || (left > 0 && left < 40)) { v.Lines.Add("Plug in soon."); v.Color = Palette.Red; }
                if (!p.BatterySaver && PowerMode != 1 && w >= 12) v.EfficiencyTip = true;
            }
            else
            {
                if (p.Charging)
                    v = new Verdict { Glyph = "", Color = Palette.Green, Title = "Plugged in: charging" + (p.ChargeWatts > 0 ? " at " + p.ChargeWatts.ToString("0") + " W" : "") };
                else
                    v = new Verdict { Glyph = "", Color = Palette.Green, Title = "Plugged in: the battery isn't being used" };
                double pw = PluggedEstimate();
                if (pw > 0.3 && p.FullWh > 0)
                    v.Lines.Add("On battery your laptop usually uses about " + pw.ToString("0.0") + " W, so a full battery lasts about " + Fmt.Duration(p.FullWh / pw * 60) + " for you.");
                else
                    v.Lines.Add("Windows only measures how much the laptop uses while it's on battery. Unplug for a few minutes and Power Watch starts learning.");
                if (!p.Charging && p.Percent < 95 && p.Percent > 0)
                    v.Lines.Add("Not charging at " + p.Percent + "%. Many laptops stop around 80% on purpose to protect the battery (a \"battery care\" setting in your laptop maker's app).");
            }
            if (HiddenHog != null)
            {
                v.Lines.Add(HiddenHog.Name + " is running with no window and using a lot of power. If you didn't start a job, stop it.");
                if (v.Color == Palette.Green) v.Color = Palette.Orange;
            }
            return v;
        }

        public string SinceUnplugged
        {
            get
            {
                if (!Power.OnBattery || !UnplugTime.HasValue) return null;
                double mins = (DateTime.UtcNow - UnplugTime.Value).TotalMinutes;
                int used = UnplugPercent - Power.Percent;
                if (mins < 1 || used < 0) return null;
                string s = "Unplugged at " + Fmt.Clock(UnplugTime.Value.ToLocalTime()) + " at " + UnplugPercent + "% · used " + used + "% in " + Fmt.Duration(mins);
                if (mins >= 5 && used > 0 && Power.FullWh > 0) s += " · average " + (used / 100.0 * Power.FullWh / (mins / 60)).ToString("0") + " W";
                return s;
            }
        }

        public void Advice(AppGroup g, out string text, out Color color)
        {
            if (g.IsSelf) { text = "This app. It uses almost no power."; color = Palette.Dim; return; }
            bool front = g.Pids.Contains(FrontPid);
            int lvl = g.Warn ? 2 : g.Level;
            if (g.Kind == Kind.Cli)
            {
                if (g.Tags.Exists(delegate(string t) { return t.StartsWith("runs a hidden"); }))
                    text = "A script is driving a hidden browser (for example recording a course or testing). Fine while that job runs; otherwise stop it.";
                else if (lvl >= 1) text = "Running hidden and using power. If you didn't start this, or its job is finished, stop it.";
                else text = "A small background helper for an app or tool. Uses almost nothing, so it's fine to leave.";
            }
            else if (!g.HasWindow)
                text = lvl >= 1 ? "Running in the background with no window. Quit it if you're not using it."
                                : "Runs in the background with no window (often near the clock). Uses very little.";
            else if (IsBrowser(g))
                text = new[] { "Fine.", "Normal for a browser with a few tabs open.",
                               "Heavy for a browser. Probably a tab with video, a game or a heavy site; close the tabs you don't need." }[lvl];
            else if (Editors.Contains(g.Name))
                text = lvl == 0 ? "Fine right now."
                     : front ? "Working hard because you're using it. Normal for editing; plug in for long renders."
                     : "Editing apps keep using the graphics chip even in the background. Quit it when you're done editing.";
            else if (lvl == 2)
                text = front ? "Working hard because you're using it. Fine if you need it; plug in for long jobs."
                             : "Working hard in the background. If you're not using it, quit it.";
            else if (lvl == 1)
                text = front ? "Moderate use while you're using it. Fine." : "Using some power in the background. Quit it if you're done with it.";
            else text = "Fine. Uses very little power.";
            double gain = MinutesGained(g);
            if (lvl >= 1 && gain >= 5) text += " Quitting it: about +" + Fmt.Duration(gain) + " of battery.";
            color = lvl == 2 ? Palette.Orange : (lvl == 1 ? Palette.Dim : Palette.Green);
        }

        // ---------- power modes and "best way"

        public static readonly string[] ModeNames = { "Balanced", "Best power efficiency", "Best performance" };

        /// Battery time in each power mode for what you're doing now. The mode you're in is measured; the
        /// others are estimated from it using how laptops usually behave in each mode.
        public List<ModeEstimate> ModeEstimates()
        {
            double w = PredictWatts();
            double baseW = Math.Min(w, 5);
            double active = Math.Max(0, w - baseW);
            Func<double, double> perfExtra = delegate(double a) { return 1 + 0.25 * Math.Min(1, a / 25); };
            double activeBalanced = PowerMode == 1 ? active / 0.7 : (PowerMode == 2 ? active / perfExtra(active) : active);
            double[] watts = { baseW + activeBalanced, baseW * 0.92 + activeBalanced * 0.7, baseW + activeBalanced * perfExtra(activeBalanced) };
            watts[PowerMode] = w;
            string[] notes =
            {
                "Normal speed. A good balance.",
                "Slower for heavy work (renders take longer). You won't notice it for browsing, writing or video.",
                "Fastest for long renders, exports and games. Fans get louder. Only helps with heavy work."
            };
            List<ModeEstimate> list = new List<ModeEstimate>();
            foreach (int m in new[] { 1, 0, 2 })
                list.Add(new ModeEstimate { Mode = m, Watts = watts[m], Minutes = MinutesLeft(watts[m]), IsCurrent = m == PowerMode, Note = notes[m] });
            return list;
        }

        public Activity CurrentActivity
        {
            get
            {
                double w = AvgWatts();
                AppGroup front = Apps.Find(delegate(AppGroup g) { return g.Pids.Contains(FrontPid); });
                if (w >= 20 || (front != null && Editors.Contains(front.Name) && front.Level >= 1)) return Activity.Heavy;
                if (VideoDecode >= 1) return Activity.Video;
                return Activity.Light;
            }
        }

        /// What to do to get the most out of the laptop right now (priority: 0 battery, 1 balance, 2 speed).
        public BestWay GetBestWay(int priority)
        {
            Activity act = CurrentActivity;
            bool onBattery = Power.OnBattery;
            BestWay b = new BestWay();
            b.Activity = act == Activity.Light ? "You're doing light work (browsing, writing, chatting)."
                       : act == Activity.Video ? "You're watching or playing video."
                       : "You're doing heavy work (editing, rendering, recording or other big jobs).";
            Dictionary<int, ModeEstimate> est = ModeEstimates().ToDictionary(e => e.Mode);
            Func<int, int, string> gain = delegate(int from, int to)
            {
                double d = est[to].Minutes - est[from].Minutes;
                return d >= 5 ? " About +" + Fmt.Duration(d) + " of battery compared with " + ModeNames[from] + "." : "";
            };
            if (onBattery)
            {
                if (priority == 0)
                {
                    b.Mode = 1;
                    b.Reason = act == Activity.Heavy
                        ? "Heavy work will be slower, but the battery lasts longer." + gain(0, 1) + " Even better: plug in for heavy work."
                        : "For this you won't feel the difference, and the battery lasts longer." + gain(0, 1);
                }
                else if (priority == 1 && act == Activity.Heavy)
                {
                    b.Mode = 0;
                    b.Reason = "Keeps heavy work at normal speed. Best power efficiency would stretch the battery but make it noticeably slower.";
                }
                else if (priority == 1)
                {
                    b.Mode = 1;
                    b.Reason = "Light work doesn't need full speed, so Best power efficiency costs you nothing noticeable." + gain(0, 1);
                }
                else
                {
                    b.Mode = 0;
                    b.Reason = act == Activity.Heavy
                        ? "Full speed on battery. Best performance on battery drains it fastest. For long renders, plug in and use Best performance."
                        : "Full speed for everything. Best performance won't make light work any faster, it would only drain the battery.";
                }
            }
            else if (act == Activity.Heavy || priority == 2)
            {
                b.Mode = act == Activity.Heavy ? 2 : 0;
                b.Reason = act == Activity.Heavy
                    ? "You're plugged in, so the battery doesn't matter. Best performance keeps long heavy jobs at top speed (fans get louder)."
                    : "Plugged in. Balanced is already full speed for light work; switch to Best performance only for long renders or games.";
            }
            else
            {
                b.Mode = 0;
                b.Reason = "Plugged in. Balanced is the right choice; the battery isn't being used.";
            }

            if (HiddenHog != null) b.Tips.Add("Stop \"" + HiddenHog.Name + "\": it's running hidden and using a lot of power.");
            List<AppGroup> others = Apps.FindAll(delegate(AppGroup g) { return g.HasWindow && !g.Pids.Contains(FrontPid) && !IsProtected(g); });
            if (others.Count >= 4) b.Tips.Add("You have " + others.Count + " other apps open. Use Focus on the Now tab to quit them all at once and keep only the one you're using.");
            AppGroup idleEditor = Apps.Find(delegate(AppGroup g) { return Editors.Contains(g.Name) && !g.Pids.Contains(FrontPid) && g.Power >= 2; });
            if (idleEditor != null && onBattery) b.Tips.Add("Quit " + idleEditor.Name + " when you're not editing. Editing apps keep using the graphics chip in the background.");
            List<AppGroup> openBrowsers = Apps.FindAll(delegate(AppGroup g) { return IsBrowser(g) && g.HasWindow; });
            if (openBrowsers.Count >= 2)
                b.Tips.Add("You have " + openBrowsers.Count + " browsers open (" + string.Join(", ", openBrowsers.Select(x => x.Name).ToArray()) + "). Using one saves power and memory.");
            if (onBattery && priority == 0 && openBrowsers.Count > 0)
                b.Tips.Add("In Edge or Chrome, turn on Efficiency mode / Energy Saver and sleeping tabs (Settings › System and performance). It helps a lot on battery.");
            if (onBattery && (priority == 0 || act == Activity.Video))
                b.Tips.Add("Lower the screen brightness a few steps. The screen is one of the biggest drains during light use.");
            if (onBattery && !Power.BatterySaver && Power.Percent <= 30) b.Tips.Add("Turn on Battery saver (in the Wi-Fi/sound panel by the clock). It cuts background activity.");
            if (onBattery && act == Activity.Heavy) b.Tips.Add("Renders, exports and recordings drain the battery fast. Plug in for long jobs.");
            if (onBattery && Power.Percent <= 20) b.Tips.Add("Charge soon. Keeping the battery between about 20% and 80% is gentlest on it.");
            if (!onBattery && act == Activity.Light && priority == 2) b.Tips.Add("Nothing to change. Your laptop is already at full speed.");
            if (b.Tips.Count == 0) b.Tips.Add("Nothing to fix right now. Your laptop is running well.");
            return b;
        }

        // ---------- checkup

        static readonly Regex Hex = new Regex(@":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline);

        static int PowerSettingDcSeconds(string sub, string setting)
        {
            string o = Shell.Run(Paths.System32("powercfg.exe"), "/qh SCHEME_CURRENT " + sub + " " + setting);
            MatchCollection m = Hex.Matches(o);
            if (m.Count < 2) return -1;
            return int.Parse(m[m.Count - 1].Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
        }

        static Checkup ReadCheckup()
        {
            Checkup c = new Checkup();
            c.UptimeDays = Native.GetTickCount64() / 86400000.0;
            Native.MEMORYSTATUSEX ms = new Native.MEMORYSTATUSEX();
            ms.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref ms))
            {
                c.FreeMemPct = 100 - (int)ms.dwMemoryLoad;
                double commitUsed = ms.ullTotalPageFile - ms.ullAvailPageFile, physUsed = ms.ullTotalPhys - ms.ullAvailPhys;
                c.PagedGB = Math.Max(0, (commitUsed - physUsed) / 1073741824.0);
            }
            int sleep = PowerSettingDcSeconds("SUB_SLEEP", "STANDBYIDLE"), screen = PowerSettingDcSeconds("SUB_VIDEO", "VIDEOIDLE");
            c.SleepMinutesOnBattery = sleep < 0 ? -1 : sleep / 60;
            c.ScreenMinutesOnBattery = screen < 0 ? -1 : screen / 60;

            List<string> vendors = new List<string>();
            Action<RegistryKey, string, RegistryKey, string> scan = delegate(RegistryKey runRoot, string runPath, RegistryKey apprRoot, string apprPath)
            {
                try
                {
                    using (RegistryKey run = runRoot.OpenSubKey(runPath, false))
                    using (RegistryKey appr = apprRoot.OpenSubKey(apprPath, false))
                    {
                        if (run == null) return;
                        foreach (string name in run.GetValueNames())
                        {
                            byte[] b = appr == null ? null : appr.GetValue(name) as byte[];
                            if (b != null && b.Length > 0 && (b[0] & 1) == 1) continue;      // switched off
                            c.StartupCount++;
                            string cmd = Environment.ExpandEnvironmentVariables(Convert.ToString(run.GetValue(name, "")));
                            string exe = cmd.StartsWith("\"") ? cmd.Substring(1, Math.Max(0, cmd.IndexOf('"', 1) - 1)) : cmd.Split(' ')[0];
                            string vendor = name;
                            try { if (File.Exists(exe)) { string co = FileVersionInfo.GetVersionInfo(exe).CompanyName; if (!string.IsNullOrWhiteSpace(co)) vendor = co.Trim(); } } catch { }
                            vendor = vendor.Replace(" Corporation", "").Replace(" Inc.", "").Replace(", Inc.", "").Replace(" LLC", "").Replace(" Ltd.", "").Trim();
                            if (!vendors.Contains(vendor)) vendors.Add(vendor);
                        }
                    }
                }
                catch { }
            };
            const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
            using (RegistryKey hkcu = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default))
            using (RegistryKey hklm64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (RegistryKey hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            {
                scan(hkcu, runKey, hkcu, approved + "Run");
                scan(hklm64, runKey, hklm64, approved + "Run");
                scan(hklm32, runKey, hklm64, approved + "Run32");
            }
            foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            {
                try
                {
                    if (!Directory.Exists(folder)) continue;
                    foreach (string f in Directory.GetFiles(folder))
                    {
                        if (Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                        c.StartupCount++;
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (!vendors.Contains(n)) vendors.Add(n);
                    }
                }
                catch { }
            }
            c.StartupVendors = vendors;
            return c;
        }

        public List<CheckItem> CheckupItems()
        {
            Checkup c = Checkup;
            PowerInfo p = Power;
            List<CheckItem> items = new List<CheckItem>();

            int days = (int)c.UptimeDays;
            if (c.UptimeDays >= 7)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Orange, Title = "Restart your laptop",
                    Text = "It has been running for " + days + " days. Use Restart, not Shut down: with Windows' Fast Startup, shutting down doesn't clear things out. A restart clears programs stuck in the background and built-up memory, which often brings the power use down." });
            else
                items.Add(new CheckItem { Glyph = "", Color = Palette.Green, Title = "Restarted recently",
                    Text = days == 0 ? "Your laptop was restarted today. Nothing to do." : "Restarted " + days + (days == 1 ? " day" : " days") + " ago. Restarting about once a week is plenty (Restart, not Shut down)." });

            if (c.FreeMemPct < 20 || c.PagedGB >= 4)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Orange, Title = "Memory is getting full",
                    Text = "Only " + c.FreeMemPct + "% of memory is free" + (c.PagedGB >= 1 ? " and Windows has moved about " + c.PagedGB.ToString("0.0") + " GB to the disk" : "") + ". That costs speed and power. Use Focus on the Now tab to quit the apps you're not using." });
            else
                items.Add(new CheckItem { Glyph = "", Color = Palette.Green, Title = "Memory: " + c.FreeMemPct + "% free", Text = "Plenty of room. Nothing to do." });

            if (p.HasBattery && c.SleepMinutesOnBattery == 0)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Orange, Title = "Your laptop never sleeps on battery",
                    Text = "If you walk away with the lid open, it stays awake and drains the battery. Set \"On battery, put my device to sleep after\" to 10-15 minutes.",
                    ButtonLabel = "Power & battery settings", ButtonUrl = "ms-settings:powersleep" });
            else if (p.HasBattery && c.SleepMinutesOnBattery > 0)
                items.Add(new CheckItem { Glyph = "", Color = c.SleepMinutesOnBattery > 30 ? Palette.Blue : Palette.Green,
                    Title = "Sleeps after " + c.SleepMinutesOnBattery + " min on battery",
                    Text = (c.ScreenMinutesOnBattery > 0 ? "The screen turns off after " + c.ScreenMinutesOnBattery + " min. " : "") +
                           (c.SleepMinutesOnBattery > 30 ? "A shorter time (10-15 min) saves battery when you walk away." : "Good. Closing the lid also puts it to sleep."),
                    ButtonLabel = c.SleepMinutesOnBattery > 30 ? "Power & battery settings" : null, ButtonUrl = "ms-settings:powersleep" });

            foreach (AppGroup b in Apps)
                if (b.Pages >= 12)
                    items.Add(new CheckItem { Glyph = "", Color = Palette.Orange, Title = b.Name + " has about " + b.Pages + " pages open",
                        Text = "Every open tab (and some extensions) keeps running. Close the ones you don't need, and turn on Efficiency mode / Energy Saver and sleeping tabs in " + b.Name + "'s settings (System and performance)." });

            Func<string[], double> sysPower = delegate(string[] names)
            {
                double sum = 0;
                foreach (AppGroup g in SystemGroups)
                    foreach (string n in names)
                        if (g.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase)) sum += g.Power;
                return sum;
            };
            if (sysPower(new[] { "SearchIndexer", "SearchProtocolHost", "SearchFilterHost" }) >= 5)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Blue, Title = "Windows Search is indexing your files",
                    Text = "Normal after copying lots of files or on a new laptop. It uses extra power for a while, then stops by itself." });
            if (sysPower(new[] { "TiWorker", "TrustedInstaller", "MoUsoCoreWorker" }) >= 5)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Blue, Title = "Windows Update is working",
                    Text = "Downloading or installing updates uses a lot of power for a while. Plug in if you can. (QuietPC can make updates wait until you say so.)" });
            if (sysPower(new[] { "MsMpEng" }) >= 5)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Blue, Title = "Windows Security is scanning",
                    Text = "A virus scan is running. It finishes on its own. (QuietPC can make it scan only when you ask.)" });

            if (p.HasBattery)
            {
                if (BatteryMode == 1)
                    items.Add(new CheckItem { Glyph = "", Color = Palette.Green, Title = "Best power efficiency on battery: on",
                        Text = "Good for battery life. For a heavy job on battery, switch to Balanced for that job, or plug in." });
                else
                    items.Add(new CheckItem { Glyph = "", Color = Palette.Blue, Title = "Power mode on battery: " + ModeNames[BatteryMode],
                        Text = "Setting the power mode on battery to \"Best power efficiency\" makes the battery last much longer for light work.",
                        ButtonLabel = "Power & battery settings", ButtonUrl = "ms-settings:powersleep" });
            }

            if (c.StartupCount > 0)
                items.Add(new CheckItem { Glyph = "", Color = Palette.Blue, Title = c.StartupCount + " apps start with Windows",
                    Text = "From " + string.Join(", ", c.StartupVendors.Take(10).ToArray()) + (c.StartupVendors.Count > 10 ? " and more" : "") +
                           ". Most use very little, but each one starts every time. Turn off the ones you don't need straight away.",
                    ButtonLabel = "Startup apps settings", ButtonUrl = "ms-settings:startupapps" });

            if (p.Health > 0)
                items.Add(new CheckItem { Glyph = "", Color = p.Health >= 80 ? Palette.Green : Palette.Orange,
                    Title = "Battery health " + p.Health + "%" + (p.Cycles > 0 ? " · " + p.Cycles + " cycles" : ""),
                    Text = p.Health >= 80
                        ? "Healthy. To keep it that way: avoid heat (don't charge on a pillow or in the sun), and if your laptop maker's app has a battery care / 80% limit, it's worth turning on when you're mostly plugged in."
                        : "The battery has worn down. Your laptop's maker can test or replace it." });
            return items;
        }

        // ---------- Focus

        public List<AppGroup> FocusCandidates(AppGroup keep)
        {
            return Apps.FindAll(delegate(AppGroup g)
            {
                return !IsProtected(g) && (keep == null || g.Id != keep.Id) && g.Name != AppInfo.Name
                       && (g.HasWindow || g.Power >= 1 || g.Cpu >= 5);
            });
        }

        static readonly HashSet<string> KeepOpenByDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Claude", "Windows Terminal", "Command Prompt", "Windows PowerShell", "PowerShell", "KeyPatch", "QuietPC" };

        public bool FocusDefault(AppGroup g) { return g.HasWindow && g.Kind == Kind.App && !KeepOpenByDefault.Contains(g.Name); }

        public string FocusNote(AppGroup g)
        {
            if (g.Name.Equals("Claude", StringComparison.OrdinalIgnoreCase)) return "Left on so your Claude chats keep running.";
            if (g.Name.IndexOf("Terminal", StringComparison.OrdinalIgnoreCase) >= 0 || g.Name.IndexOf("PowerShell", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.Name.Equals("Command Prompt", StringComparison.OrdinalIgnoreCase)) return "Ends anything running in it.";
            if (g.Kind == Kind.Cli) return "Hidden job. Stopping it ends what it's doing.";
            if (!g.HasWindow) return "Hidden helper with no window.";
            return null;
        }
    }
}
