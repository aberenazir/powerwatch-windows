// The icon by the clock (shows the watts live), self-updates, the installer and the start-up code.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PowerWatch
{
    class UpdateInfo
    {
        public Version Version;
        public string Sha256 = "";
        public List<string> Notes = new List<string>();
    }

    static class Updater
    {
        static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "PowerWatch/" + AppInfo.Version;
            wc.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            wc.Encoding = Encoding.UTF8;
            return wc;
        }

        public static UpdateInfo Fetch()
        {
            string text;
            using (WebClient wc = NewClient()) text = wc.DownloadString(AppInfo.VersionUrl);
            UpdateInfo info = new UpdateInfo();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                if (k == "version") info.Version = new Version(v);
                else if (k == "sha256") info.Sha256 = v.ToLowerInvariant();
                else if (k == "notes") foreach (string n in v.Split('|')) if (n.Trim().Length > 0) info.Notes.Add(n.Trim());
            }
            if (info.Version == null) throw new Exception("The update information was empty.");
            return info;
        }

        public static string Download(UpdateInfo info)
        {
            string dir = Path.Combine(Paths.DataDir, "update");
            Directory.CreateDirectory(dir);
            foreach (string old in Directory.GetFiles(dir, "*.exe")) { try { File.Delete(old); } catch { } }
            string path = Path.Combine(dir, "PowerWatch-Setup-" + info.Version + ".exe");
            using (WebClient wc = NewClient()) wc.DownloadFile(AppInfo.SetupUrl(info.Version), path);
            using (FileStream fs = File.OpenRead(path))
            {
                byte[] head = new byte[2];
                if (fs.Length < 10000 || fs.Read(head, 0, 2) != 2 || head[0] != 'M' || head[1] != 'Z')
                    throw new Exception("The downloaded file was not a Windows program.");
            }
            if (info.Sha256.Length > 0)
            {
                string hash;
                using (SHA256 sha = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                    hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
                if (hash != info.Sha256)
                {
                    try { File.Delete(path); } catch { }
                    throw new Exception("The download was damaged (it didn't match). Please try again.");
                }
            }
            return path;
        }
    }

    class PowerWatchApp : ApplicationContext
    {
        readonly Model model;
        readonly PanelForm panel;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Icon appIcon;
        readonly ToolStripMenuItem installUpdateItem;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer();
        readonly RegisteredWaitHandle showWait;
        IntPtr trayHandle = IntPtr.Zero;
        string trayKey = "";
        bool quitting, checking, userWaiting, installing;
        UpdateInfo available;
        Version notified;

        public PowerWatchApp(bool justUpdated, EventWaitHandle showSignal)
        {
            appIcon = MakeAppIcon(Theme.S(32));
            model = new Model();
            panel = new PanelForm(model, appIcon);
            panel.QuitApp = Quit;
            IntPtr unused = panel.Handle;

            ContextMenuStrip menu = new ContextMenuStrip();
            installUpdateItem = new ToolStripMenuItem("Install update", null, delegate { if (available != null) AskInstall(available); });
            installUpdateItem.Font = new Font(installUpdateItem.Font, FontStyle.Bold);
            installUpdateItem.Visible = false;
            menu.Items.Add(installUpdateItem);
            menu.Items.Add("Open Power Watch", null, delegate { panel.ShowNearTray(); });
            menu.Items.Add("Check for updates", null, delegate { CheckForUpdates(true); });
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem about = new ToolStripMenuItem("Power Watch " + AppInfo.Version);
            about.Enabled = false;
            menu.Items.Add(about);
            menu.Items.Add("Quit Power Watch", null, delegate { Quit(); });

            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (panel.Visible) { panel.Hide(); return; }
                if ((DateTime.UtcNow - panel.HiddenAt).TotalMilliseconds < 350) return;   // the click that closed it
                panel.ShowNearTray();
            };
            tray.BalloonTipClicked += delegate { if (available != null) AskInstall(available); };
            UpdateTray();
            tray.Visible = true;
            model.Changed += UpdateTray;

            updateTimer.Interval = 45000;
            updateTimer.Tick += delegate { updateTimer.Interval = 6 * 60 * 60 * 1000; CheckForUpdates(false); };
            updateTimer.Start();

            showWait = ThreadPool.RegisterWaitForSingleObject(showSignal, delegate { RunOnUi(delegate { panel.ShowNearTray(); }); }, null, -1, false);
            try { Native.RegisterApplicationRestart("/restarted", 0); } catch { }
            if (justUpdated)
            {
                tray.BalloonTipTitle = "Power Watch updated";
                tray.BalloonTipText = "You now have version " + AppInfo.Version + ".";
                tray.ShowBalloonTip(6000);
            }
        }

        void RunOnUi(Action a)
        {
            try { if (!quitting && panel.IsHandleCreated) panel.BeginInvoke(a); } catch { }
        }

        // ---------- the icon by the clock: the watts number, coloured by how heavy the use is

        void UpdateTray()
        {
            if (quitting) return;
            PowerInfo p = model.Power;
            string text;
            Color back;
            if (p.OnBattery && p.WattsKnown)
            {
                text = p.Watts >= 99.5 ? "99" : Math.Round(p.Watts).ToString("0");
                int lvl = p.Watts >= 25 ? 2 : (p.Watts >= 10 ? 1 : 0);
                back = new[] { Color.FromArgb(40, 140, 80), Color.FromArgb(200, 120, 20), Color.FromArgb(200, 50, 50) }[lvl];
            }
            else if (p.HasBattery)
            {
                text = p.Percent >= 100 ? "⚡" : p.Percent.ToString();
                back = Color.FromArgb(55, 95, 160);
            }
            else { text = "⚡"; back = Color.FromArgb(55, 95, 160); }
            if (model.HiddenHog != null) back = Color.FromArgb(220, 120, 0);

            string tip = "Power Watch";
            if (p.OnBattery && p.WattsKnown)
            {
                tip += " · " + p.Watts.ToString("0") + " W";
                int left = (int)model.GetEstimate().Minutes;
                if (left > 0) tip += " · " + (left / 60) + "h " + (left % 60).ToString("00") + "m left";
            }
            else if (p.HasBattery) tip += " · plugged in";
            if (p.HasBattery) tip += " · " + p.Percent + "%";
            if (model.HiddenHog != null) tip += " · hidden app busy";
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            tray.Text = tip;

            string key = text + "|" + back.ToArgb();
            if (key == trayKey) return;
            trayKey = key;
            int size = SystemInformation.SmallIconSize.Width;
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath path = Theme.Rounded(new RectangleF(0, 0, size - 1, size - 1), size * 0.22f))
                    using (SolidBrush b = new SolidBrush(back))
                        g.FillPath(b, path);
                    float px = text.Length >= 3 ? size * 0.50f : (text.Length == 2 ? size * 0.66f : size * 0.78f);
                    using (Font f = new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (StringFormat sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(text, f, Brushes.White, new RectangleF(-1, 0, size + 2, size + 1), sf);
                    }
                }
                IntPtr h = bmp.GetHicon();
                Icon old = tray.Icon;
                tray.Icon = Icon.FromHandle(h);
                if (trayHandle != IntPtr.Zero) Native.DestroyIcon(trayHandle);
                trayHandle = h;
                if (old != null && old != appIcon) old.Dispose();
            }
        }

        public static Icon MakeAppIcon(int size)
        {
            using (Bitmap bmp = new Bitmap(size, size))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath path = Theme.Rounded(new RectangleF(0, 0, size - 1, size - 1), size * 0.22f))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 140, 80)))
                        g.FillPath(b, path);
                    PointF[] bolt =
                    {
                        new PointF(size * 0.58f, size * 0.12f), new PointF(size * 0.26f, size * 0.56f), new PointF(size * 0.48f, size * 0.56f),
                        new PointF(size * 0.40f, size * 0.88f), new PointF(size * 0.74f, size * 0.42f), new PointF(size * 0.52f, size * 0.42f)
                    };
                    g.FillPolygon(Brushes.White, bolt);
                }
                IntPtr h = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        // ---------- updates

        public void CheckForUpdates(bool userAsked)
        {
            if (checking) { if (userAsked) userWaiting = true; return; }
            checking = true;
            userWaiting = userAsked;
            ThreadPool.QueueUserWorkItem(delegate
            {
                UpdateInfo info = null;
                Exception error = null;
                try { info = Updater.Fetch(); } catch (Exception ex) { error = ex; }
                RunOnUi(delegate
                {
                    checking = false;
                    bool asked = userWaiting;
                    userWaiting = false;
                    if (error != null)
                    {
                        Log.Write("update check: " + error.Message);
                        if (asked) Message("Couldn't check for updates right now. Make sure you're connected to the internet, then try again.", MessageBoxIcon.Warning);
                        return;
                    }
                    if (info.Version > AppInfo.Current)
                    {
                        available = info;
                        installUpdateItem.Text = "Install update (version " + info.Version + ")";
                        installUpdateItem.Visible = true;
                        if (asked) AskInstall(info);
                        else if (notified == null || notified != info.Version)
                        {
                            notified = info.Version;
                            tray.BalloonTipTitle = "Power Watch update ready";
                            tray.BalloonTipText = "Version " + info.Version + " is ready. Click here to install it.";
                            tray.ShowBalloonTip(10000);
                        }
                    }
                    else if (asked) Message("You have the newest version (" + AppInfo.Version + ").", MessageBoxIcon.Information);
                });
            });
        }

        DialogResult Message(string text, MessageBoxIcon kind, MessageBoxButtons buttons = MessageBoxButtons.OK)
        {
            return MessageBox.Show(text, "Power Watch", buttons, kind, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
        }

        void AskInstall(UpdateInfo info)
        {
            string text = "Power Watch " + info.Version + " is ready (you have " + AppInfo.Version + ").";
            if (info.Notes.Count > 0)
            {
                text += "\n\nWhat's new:";
                foreach (string n in info.Notes) text += "\n  • " + n;
            }
            text += "\n\nInstall it now? It takes a few seconds and keeps what it has learned.";
            if (Message(text, MessageBoxIcon.Question, MessageBoxButtons.YesNo) != DialogResult.Yes || installing) return;
            installing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null;
                Exception error = null;
                try { path = Updater.Download(info); } catch (Exception ex) { error = ex; }
                RunOnUi(delegate
                {
                    if (error != null)
                    {
                        installing = false;
                        Message("The update couldn't be downloaded.\n\n" + error.Message, MessageBoxIcon.Warning);
                        return;
                    }
                    try { Process.Start(path); Quit(); }
                    catch (Exception ex) { installing = false; Message("The update couldn't start.\n\n" + ex.Message, MessageBoxIcon.Warning); }
                });
            });
        }

        public void Quit()
        {
            if (quitting) return;
            quitting = true;
            try
            {
                if (showWait != null) showWait.Unregister(null);
                updateTimer.Stop();
                model.Stop();
                tray.Visible = false;
                tray.Dispose();
                if (trayHandle != IntPtr.Zero) Native.DestroyIcon(trayHandle);
                panel.Close();
            }
            catch { }
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ install / uninstall

    static class Installer
    {
        public const string MutexName = "PowerWatch_SingleInstance_1";
        public const string ShowEventName = "PowerWatch_Show_1";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "PowerWatch";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PowerWatch";

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Power Watch.lnk"); }
        }

        public static bool RunningInstalledCopy()
        {
            try { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Paths.InstalledExe), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        public static bool StartsWithWindows()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false)) return k != null && k.GetValue(RunName) != null; }
            catch { return false; }
        }

        public static void SetStartWithWindows(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Paths.InstalledExe + "\" /startup");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
                Store.Set("startup", on ? "1" : "0");
            }
            catch (Exception ex) { Log.Write("startup: " + ex.Message); }
        }

        static Version InstalledVersion()
        {
            try
            {
                if (!File.Exists(Paths.InstalledExe)) return null;
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(Paths.InstalledExe);
                return new Version(fv.FileMajorPart, fv.FileMinorPart, fv.FileBuildPart);
            }
            catch { return null; }
        }

        static bool IsRunning()
        {
            bool created;
            using (Mutex probe = new Mutex(false, MutexName, out created)) return !created;
        }

        static void SignalShow()
        {
            try { EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting(ShowEventName, out ev)) using (ev) ev.Set(); } catch { }
        }

        static void StartInstalled(string args)
        {
            try { if (File.Exists(Paths.InstalledExe)) Process.Start(Paths.InstalledExe, args); } catch { }
        }

        static void CloseRunningCopies()
        {
            int me = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == me || !p.ProcessName.StartsWith("PowerWatch", StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // The file you double-click (PowerWatch-Setup.exe) installs or updates Power Watch.
        public static void RunSetup()
        {
            Version installed = InstalledVersion();
            if (installed != null && installed >= AppInfo.Current)
            {
                if (IsRunning()) SignalShow(); else StartInstalled("");
                return;
            }
            Mutex m = null;
            for (int i = 0; i < 40 && m == null; i++)
            {
                bool created;
                Mutex attempt = new Mutex(true, MutexName, out created);
                if (created) { m = attempt; break; }
                attempt.Dispose();
                if (i == 8) CloseRunningCopies();
                Thread.Sleep(250);
            }
            if (m == null)
            {
                MessageBox.Show("Please quit Power Watch first (right-click its icon by the clock > Quit), then run this again.", "Power Watch");
                return;
            }
            CloseRunningCopies();
            bool ok = Install(installed == null);
            m.ReleaseMutex();
            m.Dispose();
            StartInstalled(ok && installed != null ? "/updated" : "");
        }

        static bool Install(bool fresh)
        {
            Exception last = null;
            string temp = Paths.InstalledExe + ".new";
            try
            {
                Directory.CreateDirectory(Paths.InstallDir);
                File.Copy(Application.ExecutablePath, temp, true);
            }
            catch (Exception ex) { last = ex; }
            bool swapped = false;
            for (int i = 0; i < 20 && !swapped && last == null; i++)
            {
                try
                {
                    if (File.Exists(Paths.InstalledExe)) File.Replace(temp, Paths.InstalledExe, null, true);
                    else File.Move(temp, Paths.InstalledExe);
                    swapped = true;
                }
                catch (IOException ex) { Thread.Sleep(300); if (i == 19) last = ex; }
                catch (UnauthorizedAccessException ex) { Thread.Sleep(300); if (i == 19) last = ex; }
                catch (Exception ex) { last = ex; }
            }
            if (!swapped)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                MessageBox.Show("Power Watch could not be installed.\n\n" + (last != null ? last.Message : ""), "Power Watch");
                return false;
            }
            MakeShortcut(ShortcutPath, Paths.InstalledExe);
            if (fresh || Store.Get("startup", "1") == "1") SetStartWithWindows(true);
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", "Power Watch");
                    k.SetValue("DisplayIcon", Paths.InstalledExe);
                    k.SetValue("DisplayVersion", AppInfo.Version);
                    k.SetValue("Publisher", "Power Watch");
                    k.SetValue("InstallLocation", Paths.InstallDir);
                    k.SetValue("UninstallString", "\"" + Paths.InstalledExe + "\" /uninstall");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    k.SetValue("EstimatedSize", 400, RegistryValueKind.DWord);
                }
            }
            catch { }
            Log.Write("installed " + AppInfo.Version);
            return true;
        }

        static void MakeShortcut(string lnk, string target)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Battery and power use" });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
                Marshal.FinalReleaseComObject(shell);
            }
            catch { }
        }

        public static void Uninstall()
        {
            bool first;
            using (Mutex only = new Mutex(true, "PowerWatch_Uninstall_1", out first))
            {
                if (!first) return;
                if (MessageBox.Show("Remove Power Watch from this computer?\n\n(What it has learned about your battery use is deleted too.)", "Power Watch",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                CloseRunningCopies();
                SetStartWithWindows(false);
                try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
                try { string d = Paths.DataDir; if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
                MessageBox.Show("Power Watch has been removed.", "Power Watch");
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"" + Paths.InstallDir + "\"");
                    psi.CreateNoWindow = true;
                    psi.UseShellExecute = false;
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    Process.Start(psi);
                }
                catch { }
            }
        }
    }

    static class Program
    {
        static bool HasArg(string[] args, string name)
        {
            foreach (string a in args) if (a.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Write("error: " + e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Write("fatal: " + e.ExceptionObject); };

            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = Math.Max(1f, g.DpiX / 96f);

            if (HasArg(args, "/uninstall")) { Installer.Uninstall(); return; }
            if (!Installer.RunningInstalledCopy()) { Installer.RunSetup(); return; }

            bool created;
            Mutex m = new Mutex(true, Installer.MutexName, out created);
            if (!created)
            {
                m.Dispose();
                if (!HasArg(args, "/startup") && !HasArg(args, "/restarted"))
                {
                    try { EventWaitHandle ev; if (EventWaitHandle.TryOpenExisting(Installer.ShowEventName, out ev)) using (ev) ev.Set(); } catch { }
                }
                return;
            }
            using (EventWaitHandle show = new EventWaitHandle(false, EventResetMode.AutoReset, Installer.ShowEventName))
            {
                PowerWatchApp app = new PowerWatchApp(HasArg(args, "/updated"), show);
                // Opened by hand (Start menu, or right after installing): show the panel straight away.
                if (!HasArg(args, "/startup") && !HasArg(args, "/restarted"))
                {
                    System.Windows.Forms.Timer first = new System.Windows.Forms.Timer();
                    first.Interval = 800;
                    first.Tick += delegate { first.Stop(); first.Dispose(); show.Set(); };
                    first.Start();
                }
                Application.Run(app);
            }
            GC.KeepAlive(m);
            try { m.ReleaseMutex(); } catch { }
            m.Dispose();
        }
    }
}
