// The panel that opens from the icon by the clock: Now, Modes & tips, Checkup.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace PowerWatch
{
    class PanelForm : Form
    {
        readonly Model m;
        readonly DocView head = new DocView();
        readonly Panel scroll = new Panel();
        readonly DocView body = new DocView();
        readonly CheckBox startup = new CheckBox();
        readonly Dictionary<string, Image> icons = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        int tab;
        readonly HashSet<string> expanded = new HashSet<string>();
        bool showAll, showSystem, focusing, focusPicked;
        string focusKeepId;
        HashSet<string> focusPick = new HashSet<string>();
        public bool DialogOpen;                      // a dialog from the panel is open: don't hide
        public DateTime HiddenAt = DateTime.MinValue;
        public Action QuitApp;
        public bool AllowClose;                 // only when Power Watch quits

        int Priority
        {
            get { return Store.GetInt("priority", 1); }
            set { Store.Set("priority", value.ToString()); }
        }

        public PanelForm(Model model, Icon icon)
        {
            m = model;
            Text = "Power Watch";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;
            KeyPreview = true;

            head.Dock = DockStyle.Top;
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.BackColor = Theme.Back;
            body.Location = new Point(0, 0);
            scroll.Controls.Add(body);
            scroll.Resize += delegate { FitBody(); };

            Panel foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = Theme.S(44);
            foot.BackColor = Color.FromArgb(27, 27, 27);
            startup.Text = "Open when Windows starts";
            startup.ForeColor = Theme.Dim;
            startup.Font = Theme.Small;
            startup.AutoSize = true;
            startup.Location = new Point(Theme.S(14), Theme.S(13));
            startup.Checked = Installer.StartsWithWindows();
            startup.CheckedChanged += delegate { Installer.SetStartWithWindows(startup.Checked); };
            foot.Controls.Add(startup);
            Button quit = FootButton("Quit Power Watch");
            quit.Click += delegate { if (QuitApp != null) QuitApp(); };
            Button tm = FootButton("Task Manager");
            tm.Click += delegate { try { Process.Start("taskmgr.exe"); } catch { } };
            foot.Controls.Add(quit);
            foot.Controls.Add(tm);
            foot.Resize += delegate
            {
                quit.Location = new Point(foot.Width - quit.Width - Theme.S(12), Theme.S(9));
                tm.Location = new Point(quit.Left - tm.Width - Theme.S(6), Theme.S(9));
            };

            Controls.Add(scroll);
            Controls.Add(foot);
            Controls.Add(head);

            m.Changed += delegate { if (Visible) Rebuild(); };
        }

        Button FootButton(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = Theme.Line;
            b.BackColor = Color.FromArgb(44, 44, 44);
            b.ForeColor = Theme.Text;
            b.Font = Theme.Small;
            b.AutoSize = true;
            b.Height = Theme.S(26);
            b.Cursor = Cursors.Hand;
            return b;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;            // no taskbar button
                cp.ClassStyle |= 0x20000;      // drop shadow
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        }

        // Alt+F4 just hides the panel (it's needed again next time).
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (Visible && !DialogOpen) { HiddenAt = DateTime.UtcNow; Hide(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            m.WantCheckup = Visible && tab == 2;
            m.SetOpen(Visible);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Hide();
            base.OnKeyDown(e);
        }

        public void ShowNearTray()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            Rectangle wa = screen.WorkingArea, b = screen.Bounds;
            int margin = Theme.S(12);
            int w = Theme.S(480), h = Math.Min(wa.Height - margin * 2, Theme.S(880));
            Size = new Size(w, h);
            int x = wa.Right - w - margin, y = wa.Bottom - h - margin;
            if (wa.Top > b.Top) y = wa.Top + margin;
            if (wa.Left > b.Left) x = wa.Left + margin;
            Location = new Point(x, y);
            startup.Checked = Installer.StartsWithWindows();
            Rebuild();
            Show();
            Activate();
            Native.SetForegroundWindow(Handle);
        }

        void FitBody()
        {
            int w = scroll.ClientSize.Width;
            if (body.Width != w) body.Width = w;
        }

        Image IconFor(AppGroup g)
        {
            if (string.IsNullOrEmpty(g.IconPath)) return null;
            Image img;
            if (icons.TryGetValue(g.IconPath, out img)) return img;
            try
            {
                using (Icon ic = Icon.ExtractAssociatedIcon(g.IconPath)) img = ic.ToBitmap();
            }
            catch { img = null; }
            icons[g.IconPath] = img;
            return img;
        }

        // ------------------------------------------------------------------ building the view

        public void Rebuild()
        {
            try
            {
                head.Width = ClientSize.Width;
                head.SetBlocks(HeaderBlocks());
                int scrollY = -scroll.AutoScrollPosition.Y;
                FitBody();
                List<Block> list = tab == 0 ? NowTab() : (tab == 1 ? ModesTab() : CheckupTab());
                body.SetBlocks(list);
                FitBody();
                scroll.AutoScrollPosition = new Point(0, scrollY);
            }
            catch (Exception ex) { Log.Write("panel: " + ex); }
        }

        List<Block> HeaderBlocks()
        {
            PowerInfo p = m.Power;
            List<Block> list = new List<Block>();
            list.Add(new Gap(6));
            list.Add(new HeaderBlock(m));
            list.Add(new Segmented(new[] { "Now", "Modes & tips", "Checkup" }, tab, delegate(int i)
            {
                tab = i;
                m.WantCheckup = i == 2;
                if (i == 2) m.Refresh();
                scroll.AutoScrollPosition = Point.Empty;
                Rebuild();
            }));
            return list;
        }

        // ---------- Now

        List<Block> NowTab()
        {
            List<Block> list = new List<Block>();
            Verdict v = m.GetVerdict();
            List<Block> card = new List<Block>();
            card.Add(new TextBlock(v.Title, Theme.Bold, v.Color, v.Glyph, v.Color, 4));
            foreach (string l in v.Lines) card.Add(new TextBlock(l, Theme.Body, Theme.Text, null, null, 4));
            string since = m.SinceUnplugged;
            if (since != null) card.Add(new TextBlock(since, Theme.Small, Theme.Dim, null, null, 2));
            if (v.EfficiencyTip)
            {
                card.Add(new TextBlock("Tip: \"Best power efficiency\" mode makes the battery last longer.", Theme.Small, Theme.Text, null, null, 4));
                card.Add(new Buttons().Add("Open power settings", delegate { Open("ms-settings:powersleep"); }));
            }
            list.Add(new Card(Theme.Tint(v.Color, 28), card));

            // power over the last 30 minutes
            DateTime cut = DateTime.UtcNow.AddMinutes(-30);
            List<Sample> s = m.Samples.FindAll(delegate(Sample x) { return x.T >= cut && x.W >= 0; });
            if (s.Count >= 2 && (s[s.Count - 1].T - s[0].T).TotalSeconds >= 60)
            {
                int step = Math.Max(1, s.Count / 120);
                List<double> values = new List<double>();
                for (int i = 0; i < s.Count; i += step) values.Add(s[i].W);
                double avg = s.Average(x => x.W), peak = s.Max(x => x.W);
                list.Add(new Sparkline(values, "Power over the last " + Fmt.Duration((s[s.Count - 1].T - s[0].T).TotalMinutes) +
                                               " · average " + avg.ToString("0.0") + " W · peak " + peak.ToString("0") + " W"));
            }
            list.Add(new Divider());

            if (m.FocusMessage != null)
            {
                List<Block> fm = new List<Block>();
                fm.Add(new TextBlock(m.FocusMessage, Theme.Body, Theme.Text, "", Theme.Accent, 4));
                Buttons bb = new Buttons();
                if (m.LastQuit.Count > 0) bb.Add("Reopen them", delegate { m.ReopenLast(); });
                bb.Add("Dismiss", delegate { m.FocusMessage = null; m.LastQuit = new List<string>(); Rebuild(); });
                fm.Add(bb);
                list.Add(new Card(Theme.Tint(Theme.Accent, 22), fm));
            }

            if (focusing) list.AddRange(FocusBlocks());
            else list.AddRange(AppListBlocks());
            return list;
        }

        List<Block> AppListBlocks()
        {
            List<Block> list = new List<Block>();
            list.Add(new TextBlock("What's using power", Theme.Headline, Theme.Text, null, null, 2));
            list.Add(new TextBlock("Each app's share is worked out from its processor and graphics-chip use (Windows doesn't measure power per app).", Theme.Tiny, Theme.Dim, null, null, 4));
            list.Add(new Buttons().Add("Focus: quit the others...", delegate { StartFocus(null); }));
            if (m.Updated == null) list.Add(new TextBlock("Looking...", Theme.Body, Theme.Dim));

            IEnumerable<AppGroup> shown = showAll ? m.Apps : m.Apps.Where(g => g.Power >= 0.5 || g.Cpu >= 1 || g.Warn).Take(15);
            foreach (AppGroup g in shown) list.Add(Row(g));
            TextBlock more = new TextBlock(showAll ? "Show fewer" : "Show all " + m.Apps.Count + " apps and programs", Theme.Small, Theme.Accent, null, null, 8);
            more.OnClick = delegate { showAll = !showAll; Rebuild(); };
            list.Add(more);
            list.Add(new Divider());
            TextBlock sys = new TextBlock((showSystem ? "▾ " : "▸ ") + "Windows itself (can't be quit)", Theme.Bold, Theme.Text, null, null, 6);
            sys.OnClick = delegate { showSystem = !showSystem; Rebuild(); };
            list.Add(sys);
            if (showSystem) foreach (AppGroup g in m.SystemGroups.Take(12)) list.Add(new SystemRow(g));
            return list;
        }

        Block Row(AppGroup g)
        {
            string advice;
            Color color;
            m.Advice(g, out advice, out color);
            bool open = expanded.Contains(g.Id);
            return new AppRow(g, IconFor(g), advice, color, open, !m.IsProtected(g),
                delegate { if (!expanded.Remove(g.Id)) expanded.Add(g.Id); Rebuild(); },
                delegate { Quit(g, false); },
                delegate(Point at) { RowMenu(g, at); });
        }

        void RowMenu(AppGroup g, Point at)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            bool open = expanded.Contains(g.Id);
            menu.Items.Add(open ? "Hide details" : "Show details", null, delegate { if (!expanded.Remove(g.Id)) expanded.Add(g.Id); Rebuild(); });
            if (g.HasWindow && !m.IsProtected(g)) menu.Items.Add("Focus: keep only this, quit the others...", null, delegate { StartFocus(g.Id); });
            if (!m.IsProtected(g))
            {
                menu.Items.Add(g.Kind == Kind.Cli ? "Stop" : "Quit", null, delegate { Quit(g, false); });
                menu.Items.Add("Force quit", null, delegate { Quit(g, true); });
            }
            if (!string.IsNullOrEmpty(g.ExePath))
                menu.Items.Add("Open file location", null, delegate { try { Process.Start("explorer.exe", "/select,\"" + g.ExePath + "\""); } catch { } });
            menu.Closed += delegate { BeginInvoke(new Action(menu.Dispose)); };
            menu.Show(at);
        }

        DialogResult Ask(string text, string title, MessageBoxIcon icon = MessageBoxIcon.Question, MessageBoxButtons buttons = MessageBoxButtons.YesNo)
        {
            DialogOpen = true;
            try { return MessageBox.Show(this, text, title, buttons, icon); }
            finally { DialogOpen = false; Activate(); }
        }

        void Quit(AppGroup g, bool force)
        {
            if (m.IsProtected(g)) return;
            if (!force && g.HasWindow)
            {
                m.CloseWindows(g);           // normal quit: the app asks to save if needed
                RefreshSoon();
                return;
            }
            string title, text;
            if (g.Kind == Kind.Cli)
            {
                title = "Stop \"" + g.Name + "\"?";
                string by = g.Tags.FirstOrDefault(t => t.StartsWith("Started by"));
                text = "This is a program running in the background with no window" + (by != null ? " (" + by.ToLowerInvariant() + ")" : "") + ". Stopping it ends whatever job it was doing.";
            }
            else if (force)
            {
                title = "Force quit " + g.Name + "?";
                text = "Unsaved work in " + g.Name + " will be lost.";
            }
            else
            {
                title = "Quit " + g.Name + "?";
                text = "It has no window, so it is quit by stopping its processes.";
            }
            if (Ask(text, title, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            int failed = m.Kill(g);
            if (failed > 0 && failed == g.Pids.Count)
                Ask("Windows didn't allow stopping it (it may be running as administrator).", "Power Watch", MessageBoxIcon.Information, MessageBoxButtons.OK);
            RefreshSoon();
        }

        void RefreshSoon()
        {
            Timer t = new Timer();
            t.Interval = 2000;
            t.Tick += delegate { t.Stop(); t.Dispose(); m.Refresh(); };
            t.Start();
        }

        // ---------- Focus

        AppGroup FocusKeep()
        {
            if (focusKeepId != null) return m.Apps.Find(delegate(AppGroup g) { return g.Id == focusKeepId; });
            return m.Apps.Find(delegate(AppGroup g) { return g.Pids.Contains(m.FrontPid) && !g.IsSelf; });
        }

        void StartFocus(string keep)
        {
            focusKeepId = keep;
            focusPicked = false;
            focusing = true;
            Rebuild();
        }

        List<Block> FocusBlocks()
        {
            AppGroup keep = FocusKeep();
            List<AppGroup> cands = m.FocusCandidates(keep);
            HashSet<string> pick = focusPicked ? focusPick : new HashSet<string>(cands.Where(g => m.FocusDefault(g)).Select(g => g.Id));
            List<AppGroup> chosen = cands.FindAll(delegate(AppGroup g) { return pick.Contains(g.Id); });
            double gain = m.MinutesGained(chosen);

            List<Block> c = new List<Block>();
            c.Add(new TextBlock("Focus: quit everything else", Theme.Headline, Theme.Text, "", Theme.Accent, 4));
            c.Add(new TextBlock(keep != null ? "Keeping " + keep.Name + (focusKeepId == null ? ", the app you're using" : "")
                                             : "Click the app you want to keep first, then open Focus again.", Theme.Bold, Theme.Text, null, null, 4));
            c.Add(new TextBlock("Ticked apps quit the normal way, so any app with unsaved work asks you to save first. Right-click any app in the list to keep that one instead.",
                Theme.Small, Theme.Dim, null, null, 4));
            c.Add(new Buttons()
                .Add("Tick all", delegate { focusPick = new HashSet<string>(cands.Select(g => g.Id)); focusPicked = true; Rebuild(); })
                .Add("Untick all", delegate { focusPick = new HashSet<string>(); focusPicked = true; Rebuild(); }));

            List<AppGroup> appsPart = cands.Where(g => g.HasWindow).OrderBy(g => m.FocusDefault(g) ? 0 : 1).ThenByDescending(g => g.Power).ToList();
            List<AppGroup> hiddenPart = cands.Where(g => !g.HasWindow).ToList();
            if (appsPart.Count > 0) c.Add(new TextBlock("Apps", Theme.SmallBold, Theme.Dim, null, null, 2));
            foreach (AppGroup g in appsPart) c.Add(FocusLine(g, pick));
            if (hiddenPart.Count > 0) c.Add(new TextBlock("Hidden jobs and helpers (left unticked: only tick ones you know you don't need)", Theme.SmallBold, Theme.Dim, null, null, 2));
            foreach (AppGroup g in hiddenPart) c.Add(FocusLine(g, pick));
            if (cands.Count == 0) c.Add(new TextBlock("Nothing else is open. You're already focused.", Theme.Body, Theme.Text));
            if (gain >= 5) c.Add(new TextBlock("Quitting these: about +" + Fmt.Duration(gain) + " of battery.", Theme.Body, Palette.Green, null, null, 4));
            Buttons act = new Buttons();
            if (chosen.Count > 0 && keep != null)
                act.Add("Quit " + chosen.Count + (chosen.Count == 1 ? " app" : " apps"), delegate { m.FocusQuit(chosen); focusing = false; Rebuild(); }, true);
            act.Add("Cancel", delegate { focusing = false; Rebuild(); });
            c.Add(act);
            return new List<Block> { new Card(Theme.Tint(Theme.Accent, 18), c) };
        }

        Block FocusLine(AppGroup g, HashSet<string> pick)
        {
            bool on = pick.Contains(g.Id);
            return new FocusRow(g, IconFor(g), on, m.FocusNote(g), (int)Math.Round(g.Share * 100) + "% of power", delegate
            {
                HashSet<string> p = new HashSet<string>(pick);
                if (!p.Remove(g.Id)) p.Add(g.Id);
                focusPick = p;
                focusPicked = true;
                Rebuild();
            });
        }

        // ---------- Modes & tips

        List<Block> ModesTab()
        {
            List<Block> list = new List<Block>();
            PowerInfo p = m.Power;
            BestWay best = m.GetBestWay(Priority);
            list.Add(new TextBlock(!p.HasBattery ? "Power modes" : p.OnBattery ? "Battery left in each power mode (" + p.Percent + "% left)" : "If you unplugged now (" + p.Percent + "% left)", Theme.Headline, Theme.Text, null, null, 8));
            foreach (ModeEstimate e in m.ModeEstimates()) list.Add(new ModeRow(e, e.Mode == best.Mode));
            list.Add(new TextBlock("The mode in use is measured from your laptop" + (p.OnBattery ? "" : " (from what it uses on battery)") +
                                   ". The others are estimates for the same work, based on how laptops usually behave in each mode. Your settings: on battery " +
                                   Model.ModeNames[m.BatteryMode] + ", plugged in " + Model.ModeNames[m.ChargerMode] + ".", Theme.Tiny, Theme.Dim, null, null, 6));
            list.Add(new Buttons().Add("Change power mode (Power & battery settings)", delegate { Open("ms-settings:powersleep"); }));
            list.Add(new Divider());
            list.Add(new TextBlock("Get the most out of it", Theme.Headline, Theme.Text, null, null, 6));
            list.Add(new TextBlock("What matters most to you?", Theme.Small, Theme.Dim, null, null, 4));
            list.Add(new Segmented(new[] { "Battery life", "Balanced", "Speed" }, Priority, delegate(int i) { Priority = i; Rebuild(); }));
            list.Add(new TextBlock(best.Activity, Theme.Body, Theme.Dim, null, null, 6));
            List<Block> box = new List<Block>();
            box.Add(new TextBlock("Best: " + Model.ModeNames[best.Mode] + (best.Mode == m.PowerMode ? " (you're already on it ✓)" : ""), Theme.Bold, Theme.Text, "", Palette.Yellow, 3));
            box.Add(new TextBlock(best.Reason, Theme.Body, Theme.Text, null, null, 2, 20));
            list.Add(new Card(Theme.Tint(Palette.Yellow, 24), box));
            foreach (string t in best.Tips) list.Add(new TextBlock(t, Theme.Body, Theme.Text, "", Palette.Yellow, 6));
            string since = m.SinceUnplugged;
            if (since != null) { list.Add(new Divider()); list.Add(new TextBlock(since, Theme.Small, Theme.Dim)); }
            return list;
        }

        // ---------- Checkup

        List<Block> CheckupTab()
        {
            List<Block> list = new List<Block>();
            list.Add(new TextBlock("Other things that affect your battery", Theme.Headline, Theme.Text, null, null, 10));
            foreach (CheckItem it in m.CheckupItems()) list.Add(new CheckRow(it, Open));
            list.Add(new Divider());
            list.AddRange(LearnedBlocks());
            list.Add(new Divider());
            list.Add(new TextBlock("Good habits", Theme.Headline, Theme.Text, null, null, 8));
            string[] habits =
            {
                "Use Focus before a long job on battery, so only that app is running.",
                "Close the lid when you step away. Sleep uses very little battery.",
                "Quit editing apps (ThumbStudio, Blender, CapCut...) when you're done. Closing the window often isn't enough.",
                "Do renders, exports and recordings plugged in, with Best performance if it's a long one.",
                "On battery, use Best power efficiency for browsing, writing and video. You won't notice the difference.",
                "Restart (not Shut down) about once a week. With Fast Startup, Shut down doesn't clear things out.",
                "Keep the screen a few steps below full brightness. It's one of the biggest drains during light use.",
            };
            foreach (string h in habits) list.Add(new TextBlock(h, Theme.Body, Theme.Text, "", Palette.Green, 6));
            return list;
        }

        List<Block> LearnedBlocks()
        {
            List<Block> list = new List<Block>();
            LearnStore L = m.LearnData;
            double hours = L.Minutes.Count / 60.0;
            list.Add(new TextBlock("What it has learned about you", Theme.Headline, Theme.Text, null, null, 8));
            if (L.Tuned != null)
            {
                Tuned t = L.Tuned;
                list.Add(new CheckRow(new CheckItem
                {
                    Glyph = "", Color = Palette.Green,
                    Title = "Estimate tuned to you: within ±" + (int)Math.Round(Math.Min(0.6, t.Err) * 100) + "%",
                    Text = "Tested " + t.N + " times on your last " + Fmt.Duration(t.Hours * 60) + " of use: 2 times out of 3 the estimate was this close. It mixes " +
                           (int)Math.Round(t.Ws * 100) + "% last 10 min, " + (int)Math.Round((1 - t.Ws) * 100) + "% last hour" +
                           (t.A > 0 ? ", then " + (int)Math.Round(t.A * 100) + "% what the app in front usually uses." : ".") + " Re-tested every hour."
                }, Open));
            }
            else
            {
                list.Add(new CheckRow(new CheckItem
                {
                    Glyph = "", Color = Palette.Blue,
                    Title = "Learning your use: " + Fmt.Duration(hours * 60) + " on battery so far",
                    Text = "After about 6 hours of use on battery (2-3 normal days) it tests itself on your history, picks the most accurate way to estimate for you, and shows how accurate it is."
                }, Open));
            }
            list.Add(new CheckRow(new CheckItem
            {
                Glyph = "", Color = Palette.Green,
                Title = "Shutdown point: " + m.Power.CriticalPercent + "%",
                Text = "Windows turns the laptop off (or hibernates it) at " + m.Power.CriticalPercent + "% battery (your \"critical battery level\"), so that last bit isn't counted as time left."
            }, Open));

            string mode = m.PowerMode.ToString();
            var profs = L.Profiles.Where(kv => kv.Key.EndsWith("|" + mode) && kv.Value.Hours >= 0.25)
                                  .Select(kv => new { Name = kv.Key.Substring(0, kv.Key.LastIndexOf('|')), P = kv.Value })
                                  .OrderByDescending(x => x.P.Avg).Take(8).ToList();
            if (profs.Count > 0)
            {
                list.Add(new TextBlock("What each app costs you (" + Model.ModeNames[m.PowerMode] + ", while it's the app in front)", Theme.SmallBold, Theme.Text, null, null, 6));
                foreach (var x in profs)
                    list.Add(new ValueRow(x.Name, x.P.Avg.ToString("0.0") + " W",
                        m.Power.FullWh > 0 && x.P.Avg > 0.5 ? "full battery ≈ " + Fmt.Duration(m.Power.FullWh / x.P.Avg * 60) : ""));
                list.Add(new TextBlock("Learned from " + Fmt.Duration(hours * 60) + " of use. Includes everything else that was running at the time.", Theme.Tiny, Theme.Dim));
            }
            return list;
        }

        void Open(string url)
        {
            try { Process.Start(url); } catch (Exception ex) { Log.Write("open " + url + ": " + ex.Message); }
        }
    }

    // The top of the panel: big watts number, level, battery percent and details.
    class HeaderBlock : Block
    {
        readonly Model m;
        public HeaderBlock(Model m) { this.m = m; }

        string Sub
        {
            get
            {
                PowerInfo p = m.Power;
                if (!p.HasBattery) return "No battery found";
                if (p.OnBattery) return "On battery · what the laptop is using right now";
                return p.Charging ? "Plugged in · charging" + (p.ChargeWatts > 0 ? " at " + p.ChargeWatts.ToString("0") + " W" : "") +
                                    " · Windows only measures use on battery"
                                  : "Plugged in · Windows only measures use on battery";
            }
        }

        string Details
        {
            get
            {
                PowerInfo p = m.Power;
                List<string> parts = new List<string>();
                if (m.GpuBusy.HasValue) parts.Add("Graphics chip " + m.GpuBusy.Value + "%");
                if (p.Health > 0) parts.Add("Battery health " + p.Health + "% (" + (p.Health >= 80 ? "normal" : "worn") + ")" + (p.Cycles > 0 ? " · " + p.Cycles + " cycles" : ""));
                parts.Add(Model.ModeNames[m.PowerMode]);
                if (p.BatterySaver) parts.Add("Battery saver on");
                return string.Join(" · ", parts.ToArray());
            }
        }

        public override int Layout(int width)
        {
            return Theme.S(42) + Theme.Measure(Sub, Theme.Small, width) + Theme.S(2) + Theme.Measure(Details, Theme.Small, width) + Theme.S(10);
        }

        public override void Paint(Graphics g, int width)
        {
            PowerInfo p = m.Power;
            double? gauge = p.OnBattery && !p.WattsKnown ? m.GaugeWatts(600) : null;
            bool known = p.OnBattery && (p.WattsKnown || gauge.HasValue);
            double watts = p.WattsKnown ? p.Watts : (gauge ?? 0);
            string big = known ? (p.WattsKnown ? "" : "\u2248 ") + watts.ToString("0.0") + " W" : (p.OnBattery ? "... W" : (p.HasBattery ? "Plugged in" : "Mains power"));
            int lvl = watts >= 25 ? 2 : (watts >= 10 ? 1 : 0);
            Theme.Draw(g, big, Theme.Huge, new Rectangle(0, 0, width, Theme.S(40)), Theme.Text, Theme.Line1);
            int bw = Theme.WidthOf(big, Theme.Huge);
            if (known)
                Theme.Draw(g, new[] { "Light use", "Medium use", "Heavy use" }[lvl], Theme.Bold, new Rectangle(bw + Theme.S(10), Theme.S(15), Theme.S(120), Theme.S(20)), Palette.Level(lvl), Theme.Line1);
            if (p.HasBattery)
            {
                string pct = p.Percent + "%";
                Theme.Draw(g, pct, Theme.Big, new Rectangle(width - Theme.S(110), Theme.S(8), Theme.S(84), Theme.S(28)), Theme.Text, TextFormatFlags.Right | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                Theme.Draw(g, p.OnBattery ? "" : "", Theme.IconsBig, new Rectangle(width - Theme.S(22), Theme.S(10), Theme.S(24), Theme.S(24)), Theme.Text, TextFormatFlags.NoPadding);
            }
            int y = Theme.S(42);
            int h = Theme.Measure(Sub, Theme.Small, width);
            Theme.Draw(g, Sub, Theme.Small, new Rectangle(0, y, width, h), Theme.Dim);
            y += h + Theme.S(2);
            Theme.Draw(g, Details, Theme.Small, new Rectangle(0, y, width, Height), Theme.Dim);
        }
    }
}
