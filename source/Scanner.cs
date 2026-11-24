// Processes: which apps (including hidden background programs) are running and what each one costs.
// Windows doesn't measure power per program, so each program's share is worked out from its processor
// and graphics-chip use (the same things Windows uses for its own "power usage" column).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PowerWatch
{
    enum Kind { App, Bundle, Cli, System }

    class Proc
    {
        public int Pid, Ppid, Session;
        public long Create;            // creation time: tells a process apart from a later one with the same number
        public string Exe = "";        // file name, e.g. chrome.exe
        public string Path = "";
        public double Cpu, Gpu, Power;
        public long Mem;
        public string Args = "";
        public string Name { get { return Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Exe.Substring(0, Exe.Length - 4) : Exe; } }
    }

    class ProcLine
    {
        public int Pid;
        public string Name, Command;
        public double Cpu;
    }

    class AppGroup
    {
        public string Id = "", Name = "";
        public Kind Kind;
        public string IconPath;
        public List<string> Tags = new List<string>();
        public double Cpu, Power, Share;
        public long Mem;
        public List<int> Pids = new List<int>();
        public Dictionary<int, long> Created = new Dictionary<int, long>();
        public List<ProcLine> Procs = new List<ProcLine>();
        public bool HasWindow;
        public string Explain;
        public bool IsSelf, Warn;
        public int Pages;
        public string ExePath = "";
        public int Level
        {
            get
            {
                if (Power >= 40 || Cpu >= 60) return 2;
                if (Power >= 8 || Cpu >= 15) return 1;
                return 0;
            }
        }
    }

    static class Known
    {
        public static readonly Dictionary<string, string> SystemExplanations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "System", "The core of Windows and its drivers. High use can mean a driver problem." },
            { "dwm", "Draws everything on your screen. Goes up with video, animations and many open windows." },
            { "explorer", "File Explorer, the taskbar and Start." },
            { "MsMpEng", "Windows Security scanning files for viruses. Normal; it finishes on its own." },
            { "MpDefenderCoreService", "Windows Security." },
            { "SearchIndexer", "Windows Search indexing your files. Normal for a while after copying lots of files." },
            { "SearchProtocolHost", "Windows Search reading files to index them." },
            { "SearchFilterHost", "Windows Search reading files to index them." },
            { "SearchHost", "The search box in Start." },
            { "TiWorker", "Windows Update installing updates. Heavy, but it finishes." },
            { "TrustedInstaller", "Installing Windows updates or features." },
            { "MoUsoCoreWorker", "Windows Update checking for or downloading updates." },
            { "CompatTelRunner", "Microsoft's compatibility check (telemetry). Scans your apps now and then." },
            { "svchost", "Many small Windows services grouped together." },
            { "audiodg", "Sound. Goes up while audio is playing." },
            { "Memory Compression", "Windows squeezing memory to fit more. Normal when memory is full." },
            { "csrss", "Windows core." }, { "lsass", "Sign-in and security." }, { "services", "Starts and manages Windows services." },
            { "wininit", "Windows start-up." }, { "winlogon", "Your sign-in session." }, { "smss", "Windows session manager." },
            { "spoolsv", "Printing." }, { "WmiPrvSE", "System information. Often busy because a laptop maker's tool keeps asking for it." },
            { "RuntimeBroker", "Checks what Store apps are allowed to do." }, { "sihost", "Windows shell." },
            { "ctfmon", "Keyboard and typing." }, { "fontdrvhost", "Fonts." }, { "taskhostw", "Runs scheduled background jobs." },
            { "StartMenuExperienceHost", "The Start menu." }, { "ShellExperienceHost", "Taskbar panels and notifications." },
            { "TextInputHost", "Touch keyboard and emoji panel." }, { "SecurityHealthService", "Windows Security." },
            { "smartscreen", "Checks downloads for danger." }, { "Widgets", "News and weather Widgets panel." },
            { "WidgetService", "News and weather Widgets panel." }, { "PhoneExperienceHost", "Phone Link." },
            { "OneDrive", "OneDrive syncing your files." }, { "Registry", "The Windows registry." },
            { "dllhost", "Helper for Windows features (thumbnails, previews)." }, { "conhost", "Window for a command-line program." },
            { "WUDFHost", "Drivers for devices like fingerprint readers and sensors." },
            { "LockApp", "The lock screen." }, { "ApplicationFrameHost", "Windows for Store apps." },
        };

        public static readonly HashSet<string> CliNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "powershell", "pwsh", "python", "pythonw", "py", "node", "java", "javaw", "ruby", "perl", "php",
            "bash", "sh", "wsl", "git", "deno", "bun", "go", "cargo", "ffmpeg", "ffprobe", "curl", "wget", "uv", "npm", "npx", "claude"
        };

        public static readonly HashSet<string> Shells = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "cmd", "powershell", "pwsh", "bash", "sh", "wsl" };

        // Never treated as "the app that started something" (they start everything).
        public static readonly HashSet<string> Launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "explorer", "svchost", "sihost", "RuntimeBroker", "ShellExperienceHost", "StartMenuExperienceHost", "services", "userinit", "winlogon" };

        public static readonly HashSet<string> Browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "brave", "firefox", "opera", "vivaldi", "arc" };
    }

    class Scanner
    {
        readonly Dictionary<long, long> lastTimes = new Dictionary<long, long>();   // key: pid ^ creation time
        readonly Dictionary<int, string> argsCache = new Dictionary<int, string>();
        readonly Dictionary<string, string> nameCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Stopwatch clock = Stopwatch.StartNew();
        long lastWall = -1;
        readonly int myPid = Process.GetCurrentProcess().Id;
        readonly int mySession = Process.GetCurrentProcess().SessionId;
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";

        public int MyPid { get { return myPid; } }

        // Program name as people know it (from the program's own description), e.g. "Google Chrome".
        public string FriendlyName(string path, string fallback)
        {
            if (string.IsNullOrEmpty(path)) return fallback;
            string n;
            lock (nameCache) if (nameCache.TryGetValue(path, out n)) return n;
            n = fallback;
            try
            {
                FileVersionInfo fv = FileVersionInfo.GetVersionInfo(path);
                string d = (fv.FileDescription ?? "").Trim();
                if (d.Length < 2 || d.Length > 60) d = (fv.ProductName ?? "").Trim();
                if (d.Length >= 2 && d.Length <= 60) n = d;
            }
            catch { }
            if (fallback.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) n = "Web parts inside apps (WebView2)";
            lock (nameCache) nameCache[path] = n;
            return n;
        }

        // Which processes have a window you can see (Store apps: the real app inside the frame window).
        public static HashSet<int> WindowPids(out Dictionary<int, List<IntPtr>> windows)
        {
            HashSet<int> pids = new HashSet<int>();
            Dictionary<int, List<IntPtr>> wins = new Dictionary<int, List<IntPtr>>();
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr l)
            {
                try
                {
                    if (!Native.IsWindowVisible(hwnd) || Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true;    // GW_OWNER
                    int ex = Native.GetWindowLong(hwnd, -20);
                    if ((ex & 0x80) != 0 || (ex & 0x08000000) != 0) return true;                                     // tool / no-activate window
                    if (Native.GetWindowTextLength(hwnd) == 0) return true;
                    Native.RECT rc;
                    if (!Native.GetWindowRect(hwnd, out rc) || rc.Right <= rc.Left || rc.Bottom <= rc.Top) return true;
                    int cloaked;
                    // cloaked = hidden by Windows; 2 (shell) = on another virtual desktop, still a real window
                    if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0 && !(cloaked == 2 && OnOtherDesktop(hwnd))) return true;
                    int pid;
                    Native.GetWindowThreadProcessId(hwnd, out pid);
                    StringBuilder cls = new StringBuilder(64);
                    Native.GetClassName(hwnd, cls, 64);
                    if (cls.ToString() == "ApplicationFrameWindow")
                    {
                        int inner = pid;
                        Native.EnumChildWindows(hwnd, delegate(IntPtr c, IntPtr l2)
                        {
                            int cp;
                            Native.GetWindowThreadProcessId(c, out cp);
                            if (cp != pid) { inner = cp; return false; }
                            return true;
                        }, IntPtr.Zero);
                        if (inner == pid) return true;               // empty frame (suspended Store app): skip
                        pid = inner;
                    }
                    pids.Add(pid);
                    List<IntPtr> list;
                    if (!wins.TryGetValue(pid, out list)) wins[pid] = list = new List<IntPtr>();
                    list.Add(hwnd);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            windows = wins;
            return pids;
        }

        static readonly HashSet<string> ShellClasses = new HashSet<string>
        { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow" };

        public static int ForegroundPid()
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return 0;
            int pid;
            Native.GetWindowThreadProcessId(h, out pid);
            StringBuilder cls = new StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            if (ShellClasses.Contains(cls.ToString())) return 0;   // taskbar, desktop, Start: not an app
            if (cls.ToString() == "ApplicationFrameWindow")
            {
                int frame = pid, inner = pid;
                Native.EnumChildWindows(h, delegate(IntPtr c, IntPtr l)
                {
                    int cp;
                    Native.GetWindowThreadProcessId(c, out cp);
                    if (cp != frame) { inner = cp; return false; }
                    return true;
                }, IntPtr.Zero);
                pid = inner;
            }
            return pid;
        }

        readonly Dictionary<long, string> pathCache = new Dictionary<long, string>();     // key: pid ^ creation time

        // Every process in one cheap call: number, parent, session, name, processor time, memory, creation time.
        // Works for Windows' own processes too (no permission needed).
        Dictionary<int, Proc> Snapshot(double wallSeconds)
        {
            Dictionary<int, Proc> procs = new Dictionary<int, Proc>();
            int size = 1 << 20;
            IntPtr buf = IntPtr.Zero;
            try
            {
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    buf = Marshal.AllocHGlobal(size);
                    int needed;
                    int status = Native.NtQuerySystemInformation(5, buf, size, out needed);
                    if (status == 0) break;
                    Marshal.FreeHGlobal(buf);
                    buf = IntPtr.Zero;
                    if (status != unchecked((int)0xC0000004)) return null;    // not "buffer too small"
                    size = Math.Max(size * 2, needed + (1 << 16));
                }
                if (buf == IntPtr.Zero) return null;
                long offset = 0;
                HashSet<int> seen = new HashSet<int>();
                while (true)
                {
                    IntPtr e = new IntPtr(buf.ToInt64() + offset);
                    int next = Marshal.ReadInt32(e, 0x00);
                    if (next < 0 || offset + next >= size) next = 0;            // never read past the buffer
                    long create = Marshal.ReadInt64(e, 0x20);
                    long user = Marshal.ReadInt64(e, 0x28), kernel = Marshal.ReadInt64(e, 0x30);
                    int nameLen = (ushort)Marshal.ReadInt16(e, 0x38);
                    IntPtr namePtr = Marshal.ReadIntPtr(e, 0x40);
                    int pid = (int)Marshal.ReadIntPtr(e, 0x50).ToInt64();
                    int ppid = (int)Marshal.ReadIntPtr(e, 0x58).ToInt64();
                    int session = Marshal.ReadInt32(e, 0x64);
                    long ws = Marshal.ReadIntPtr(e, 0x90).ToInt64();
                    if (pid != 0)
                    {
                        Proc p = new Proc { Pid = pid, Ppid = ppid, Session = session, Create = create, Mem = ws };
                        p.Exe = nameLen > 0 && namePtr != IntPtr.Zero ? Marshal.PtrToStringUni(namePtr, nameLen / 2) : (pid == 4 ? "System" : "");
                        long t = user + kernel, before;
                        long key = pid ^ create;
                        if (wallSeconds > 0 && lastTimes.TryGetValue(key, out before) && t >= before)
                            p.Cpu = 100.0 * ((t - before) / 1e7) / wallSeconds;
                        lastTimes[key] = t;
                        seenKeys.Add(key);
                        procs[pid] = p;
                    }
                    if (next == 0) break;
                    offset += next;
                }
                return procs;
            }
            catch (Exception ex) { Log.Write("snapshot: " + ex.Message); return null; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        readonly HashSet<long> seenKeys = new HashSet<long>();

        string PathOf(Proc p)
        {
            long key = p.Pid ^ p.Create;
            string path;
            if (pathCache.TryGetValue(key, out path)) return path;
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            path = "";
            if (h != IntPtr.Zero) { path = Native.ExePath(h); Native.CloseHandle(h); }
            pathCache[key] = path;
            return path;
        }

        // Stops a process only if it's still the one we saw. 1 = stopped, 0 = already gone / different program, -1 = refused.
        public static int TerminateIfSame(int pid, long create)
        {
            IntPtr h = Native.OpenProcess(0x0001 | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);   // PROCESS_TERMINATE
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                return err == 5 ? -1 : 0;                                   // 5 = access denied
            }
            try
            {
                Native.FILETIME c, x, k, u;
                if (!Native.GetProcessTimes(h, out c, out x, out k, out u) || (create != 0 && c.Value != create)) return 0;
                return Native.TerminateProcess(h, 1) ? 1 : -1;
            }
            finally { Native.CloseHandle(h); }
        }

        [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopManager
        {
            [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
            [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktop);
        }

        [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
        class VirtualDesktopManager { }

        // A window hidden only because it's on another virtual desktop is still a real window.
        static bool OnOtherDesktop(IntPtr hwnd)
        {
            try
            {
                IVirtualDesktopManager vdm = (IVirtualDesktopManager)new VirtualDesktopManager();
                Guid id;
                int onCurrent;
                bool other = vdm.GetWindowDesktopId(hwnd, out id) == 0 && id != Guid.Empty &&
                             vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out onCurrent) == 0 && onCurrent == 0;
                Marshal.ReleaseComObject(vdm);
                return other;
            }
            catch { return false; }
        }

        // Is this still the same process we saw (not a new one that got the same number)?
        public static bool StillSame(int pid, long create)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return false;
            try
            {
                Native.FILETIME c, x, k, u;
                return Native.GetProcessTimes(h, out c, out x, out k, out u) && (create == 0 || c.Value == create);
            }
            finally { Native.CloseHandle(h); }
        }

        // detail = false (panel closed): processor use only, no command lines, cheaper.
        public void Build(Dictionary<int, double> gpuByPid, bool detail, out List<AppGroup> apps, out List<AppGroup> system)
        {
            long wall = clock.ElapsedTicks;
            double wallSeconds = lastWall < 0 ? 0 : (wall - lastWall) / (double)Stopwatch.Frequency;
            lastWall = wall;

            seenKeys.Clear();
            Dictionary<int, Proc> procs = Environment.Is64BitProcess ? Snapshot(wallSeconds) : null;
            if (procs == null) procs = LegacySnapshot(wallSeconds);
            foreach (Proc p in procs.Values) p.Path = PathOf(p);
            List<long> goneKeys = new List<long>();
            foreach (long k in lastTimes.Keys) if (!seenKeys.Contains(k)) goneKeys.Add(k);
            foreach (long k in goneKeys) { lastTimes.Remove(k); pathCache.Remove(k); }
            List<int> goneArgs = new List<int>();
            foreach (int pid in argsCache.Keys) if (!procs.ContainsKey(pid)) goneArgs.Add(pid);
            foreach (int pid in goneArgs) argsCache.Remove(pid);

            foreach (Proc p in procs.Values)
            {
                double g;
                if (gpuByPid != null && gpuByPid.TryGetValue(p.Pid, out g)) p.Gpu = g;
                p.Power = p.Cpu + p.Gpu;
            }

            // 2. What kind of program each one is.
            Dictionary<int, List<IntPtr>> windows;
            HashSet<int> withWindow = WindowPids(out windows);
            Dictionary<int, Kind> kinds = new Dictionary<int, Kind>();
            foreach (Proc p in procs.Values)
            {
                Kind k;
                if (p.Pid == 4 || p.Path.Length == 0 && p.Session == 0) k = Kind.System;
                else if (withWindow.Contains(p.Pid) && p.Session == mySession) k = Kind.App;
                else if (p.Session == 0 || p.Session != mySession || p.Path.Length == 0 ||
                         p.Path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) k = Kind.System;
                else if (Known.CliNames.Contains(p.Name)) k = Kind.Cli;
                else k = Kind.Bundle;
                kinds[p.Pid] = k;
            }
            // Shells started from a window (cmd, PowerShell) count as scripts, not as system programs.
            foreach (Proc p in procs.Values)
                if (kinds[p.Pid] == Kind.System && p.Session == mySession && Known.Shells.Contains(p.Name)) kinds[p.Pid] = Kind.Cli;
            foreach (Proc p in procs.Values)
            {
                if (!p.Name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) continue;
                Proc a = p;
                for (int n = 0; n < 20 && a != null && a.Name.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase); n++)
                {
                    Proc par;
                    a = procs.TryGetValue(a.Ppid, out par) && (par.Create == 0 || a.Create == 0 || par.Create <= a.Create) ? par : null;
                }
                if (a != null && kinds.ContainsKey(a.Pid) && kinds[a.Pid] == Kind.System) kinds[p.Pid] = Kind.System;
            }

            Func<int, Proc> parentOf = delegate(int pid)
            {
                Proc me, par;
                if (!procs.TryGetValue(pid, out me) || me.Ppid == pid) return null;
                if (!procs.TryGetValue(me.Ppid, out par)) return null;
                if (par.Create > 0 && me.Create > 0 && par.Create > me.Create) return null;   // the real parent has exited
                return par;
            };

            Func<int, int> cliRoot = delegate(int pid)
            {
                int r = pid, n = 0;
                Proc par;
                while ((par = parentOf(r)) != null && kinds[par.Pid] == Kind.Cli && n < 50) { r = par.Pid; n++; }
                return r;
            };

            // Helpers without a window belong to the app that started them (Chrome's tabs belong to Chrome).
            Func<int, int> appRoot = delegate(int pid)
            {
                int r = pid, n = 0;
                Proc par;
                while ((par = parentOf(r)) != null && n < 50)
                {
                    Kind pk = kinds[par.Pid];
                    if (pk != Kind.App && pk != Kind.Bundle) break;
                    if (Known.Launchers.Contains(par.Name)) break;
                    bool samePath = string.Equals(par.Path, procs[r].Path, StringComparison.OrdinalIgnoreCase);
                    if (!samePath && withWindow.Contains(r)) break;      // a separate app with its own window
                    r = par.Pid; n++;
                }
                return r;
            };

            Func<int, string> groupId = delegate(int pid)
            {
                Kind k = kinds[pid];
                if (k == Kind.System) return "S:" + procs[pid].Name.ToLowerInvariant();
                if (k == Kind.Cli) return "C:" + cliRoot(pid);
                int root = appRoot(pid);
                Proc par = parentOf(root);
                if (par != null && kinds[par.Pid] == Kind.Cli) return "C:" + cliRoot(par.Pid);   // an app started by a script
                Proc rp = procs[root];
                return "B:" + (rp.Path.Length > 0 ? rp.Path.ToLowerInvariant() : "?" + rp.Exe.ToLowerInvariant());
            };

            Func<int, string> startedBy = delegate(int pid)
            {
                Proc par = parentOf(pid);
                int n = 0;
                while (par != null && n < 20)
                {
                    Kind k = kinds[par.Pid];
                    if ((k == Kind.App || k == Kind.Bundle) && !Known.Launchers.Contains(par.Name)) return FriendlyName(par.Path, par.Name);
                    par = parentOf(par.Pid);
                    n++;
                }
                return null;
            };

            Dictionary<string, List<Proc>> groups = new Dictionary<string, List<Proc>>();
            foreach (Proc p in procs.Values)
            {
                string id = groupId(p.Pid);
                List<Proc> list;
                if (!groups.TryGetValue(id, out list)) groups[id] = list = new List<Proc>();
                list.Add(p);
            }
            double totalPower = 0;
            foreach (Proc p in procs.Values) totalPower += p.Power;
            totalPower = Math.Max(totalPower, 0.001);

            apps = new List<AppGroup>();
            system = new List<AppGroup>();
            foreach (KeyValuePair<string, List<Proc>> kv in groups)
            {
                List<Proc> members = kv.Value;
                AppGroup g = new AppGroup();
                g.Id = kv.Key;
                foreach (Proc p in members)
                {
                    g.Pids.Add(p.Pid);
                    g.Created[p.Pid] = p.Create;
                    g.Cpu += p.Cpu;
                    g.Power += p.Power;
                    g.Mem += p.Mem;
                    if (withWindow.Contains(p.Pid)) g.HasWindow = true;
                }
                g.Share = g.Power / totalPower;
                members.Sort(delegate(Proc a, Proc b) { return b.Power.CompareTo(a.Power); });

                if (kv.Key.StartsWith("S:"))
                {
                    Proc p = members[0];
                    g.Kind = Kind.System;
                    g.Name = p.Name;
                    string ex;
                    g.Explain = Known.SystemExplanations.TryGetValue(p.Name, out ex) ? ex : null;
                    if (members.Count > 1) g.Name += " (" + members.Count + ")";
                    system.Add(g);
                    continue;
                }

                if (kv.Key.StartsWith("B:"))
                {
                    string keyPath = kv.Key.Substring(2);
                    Proc main = members.Find(delegate(Proc p) { return string.Equals(p.Path, keyPath, StringComparison.OrdinalIgnoreCase); }) ?? members[0];
                    g.ExePath = main.Path;
                    g.Kind = g.HasWindow ? Kind.App : Kind.Bundle;
                    g.Name = FriendlyName(g.ExePath, g.ExePath.Length > 0 ? Path.GetFileNameWithoutExtension(g.ExePath) : main.Name);
                    g.IconPath = g.ExePath;
                    g.IsSelf = members.Exists(delegate(Proc p) { return p.Pid == myPid; });
                    if (!g.HasWindow) g.Tags.Add("Hidden: no window");
                    if (detail && Known.Browsers.Contains(main.Name))
                    {
                        foreach (Proc p in members)
                        {
                            string a = ArgsOf(p.Pid);
                            if (a.Contains("--type=renderer") && !a.Contains("--extension-process")) g.Pages++;
                        }
                    }
                    if (members.Count > 1) g.Tags.Add(members.Count + " processes");
                    if (g.IsSelf) g.Tags = new List<string> { "This app" };
                    if (!g.HasWindow && !g.IsSelf && (g.Power >= 20 || g.Cpu >= 80)) g.Warn = true;
                }
                else
                {
                    int root = int.Parse(kv.Key.Substring(2));
                    g.Kind = Kind.Cli;
                    List<Proc> scripts = members.FindAll(delegate(Proc p) { return kinds[p.Pid] == Kind.Cli && !Known.Shells.Contains(p.Name); });
                    Proc main = scripts.Count > 0 ? scripts[0] : procs[root];
                    g.Name = Known.Shells.Contains(main.Name) ? "Shell (" + main.Name + ")" : (detail ? Pretty(main) : main.Name);
                    g.Tags.Add("Hidden: no window");
                    string by = startedBy(root);
                    if (by != null) g.Tags.Add("Started by " + by);
                    Proc inside = members.Find(delegate(Proc p) { return kinds[p.Pid] == Kind.App || kinds[p.Pid] == Kind.Bundle; });
                    if (inside != null)
                    {
                        int count = members.FindAll(delegate(Proc p) { return kinds[p.Pid] == Kind.App || kinds[p.Pid] == Kind.Bundle; }).Count;
                        g.Tags.Add("runs a hidden " + FriendlyName(inside.Path, inside.Name) + " (" + count + " processes)");
                    }
                    if (g.Power >= 20 || g.Cpu >= 80) g.Warn = true;
                }

                int lines = 0;
                foreach (Proc p in members)
                {
                    if (lines++ >= 30) break;
                    g.Procs.Add(new ProcLine { Pid = p.Pid, Name = p.Name, Cpu = p.Cpu, Command = p.Path });
                }
                apps.Add(g);
            }
            apps.Sort(delegate(AppGroup a, AppGroup b) { return a.Power != b.Power ? b.Power.CompareTo(a.Power) : b.Cpu.CompareTo(a.Cpu); });
            system.Sort(delegate(AppGroup a, AppGroup b) { return b.Power.CompareTo(a.Power); });
            LastWindows = windows;
        }

        // Fallback for 32-bit Windows: Toolhelp + opening each process.
        Dictionary<int, Proc> LegacySnapshot(double wallSeconds)
        {
            Dictionary<int, Proc> procs = new Dictionary<int, Proc>();
            IntPtr snap = Native.CreateToolhelp32Snapshot(Native.TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return procs;
            try
            {
                Native.PROCESSENTRY32W e = new Native.PROCESSENTRY32W();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.PROCESSENTRY32W));
                if (Native.Process32FirstW(snap, ref e))
                {
                    do
                    {
                        int pid = (int)e.th32ProcessID;
                        if (pid == 0) continue;
                        procs[pid] = new Proc { Pid = pid, Ppid = (int)e.th32ParentProcessID, Exe = e.szExeFile ?? "" };
                    } while (Native.Process32NextW(snap, ref e));
                }
            }
            finally { Native.CloseHandle(snap); }
            foreach (Proc p in procs.Values)
            {
                int s;
                if (Native.ProcessIdToSessionId(p.Pid, out s)) p.Session = s;
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    Native.FILETIME c, x, k, u;
                    if (Native.GetProcessTimes(h, out c, out x, out k, out u))
                    {
                        p.Create = c.Value;
                        long t = k.Value + u.Value, before, key = p.Pid ^ p.Create;
                        if (wallSeconds > 0 && lastTimes.TryGetValue(key, out before) && t >= before)
                            p.Cpu = 100.0 * ((t - before) / 1e7) / wallSeconds;
                        lastTimes[key] = t;
                        seenKeys.Add(key);
                    }
                    Native.PROCESS_MEMORY_COUNTERS mc;
                    if (Native.GetProcessMemoryInfo(h, out mc, (uint)Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS))))
                        p.Mem = (long)mc.WorkingSetSize.ToUInt64();
                }
                finally { Native.CloseHandle(h); }
            }
            return procs;
        }

        // Top-level windows per process from the last scan (used to close apps the normal way).
        public Dictionary<int, List<IntPtr>> LastWindows = new Dictionary<int, List<IntPtr>>();

        string ArgsOf(int pid)
        {
            string a;
            if (argsCache.TryGetValue(pid, out a)) return a;
            a = Native.CommandLine(pid);
            argsCache[pid] = a;
            return a;
        }

        // "python record_course.py" instead of a long command line.
        string Pretty(Proc p)
        {
            string args = ArgsOf(p.Pid);
            if (args.IndexOf("playwright", StringComparison.OrdinalIgnoreCase) >= 0) return "Playwright browser robot";
            List<string> parts = new List<string> { p.Name };
            List<string> toks = SplitArgs(args);
            for (int i = 1; i < toks.Count; i++)
            {
                string t = toks[i];
                if (t.StartsWith("-")) continue;
                string last = t.TrimEnd('\\', '/');
                int slash = Math.Max(last.LastIndexOf('\\'), last.LastIndexOf('/'));
                parts.Add(slash >= 0 ? last.Substring(slash + 1) : last);
                if (parts.Count == 4) break;
            }
            return string.Join(" ", parts.ToArray());
        }

        static List<string> SplitArgs(string s)
        {
            List<string> list = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool quote = false;
            foreach (char c in s ?? "")
            {
                if (c == '"') { quote = !quote; continue; }
                if (c == ' ' && !quote) { if (cur.Length > 0) { list.Add(cur.ToString()); cur.Length = 0; } continue; }
                cur.Append(c);
            }
            if (cur.Length > 0) list.Add(cur.ToString());
            return list;
        }
    }
}
