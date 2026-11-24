// Power Watch for Windows: shows how much power the laptop is using, which apps (including hidden
// background ones) are using it, how long the battery will last, and lets you quit them.
// A Windows version of the Power Watch menu bar app for the Mac.
//
// Core pieces: app info, Windows functions, log, settings, JSON files.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PowerWatch
{
    static class AppInfo
    {
        public const string Name = "Power Watch";
        public const string Version = "1.0.0";
        public const string Repo = "aberenazir/powerwatch-windows";
        public const string VersionUrl = "https://github.com/" + Repo + "/releases/latest/download/version.txt";

        public static string SetupUrl(Version v)
        {
            return "https://github.com/" + Repo + "/releases/download/v" + v + "/PowerWatch-Setup.exe";
        }

        public static Version Current { get { return new Version(Version); } }
    }

    static class Paths
    {
        public static string DataDir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PowerWatch");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string InstallDir
        {
            get { return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "PowerWatch"); }
        }

        public static string InstalledExe { get { return Path.Combine(InstallDir, "PowerWatch.exe"); } }

        public static string System32(string exe)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);
        }
    }

    static class Native
    {
        // ---- processes
        public const uint TH32CS_SNAPPROCESS = 0x00000002;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FILETIME
        {
            public uint Low, High;
            public long Value { get { return ((long)High << 32) | Low; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                           QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll")]
        public static extern bool GetProcessTimes(IntPtr h, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll")]
        public static extern bool ProcessIdToSessionId(int pid, out int session);

        [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo")]
        public static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS counters, uint size);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryInformationProcess(IntPtr h, int infoClass, IntPtr buffer, int length, out int returnLength);

        // ---- windows
        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc fn, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc fn, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hwnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        // ---- power & memory
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        [DllImport("powrprof.dll")]
        public static extern uint PowerGetEffectiveOverlayScheme(out Guid scheme);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterApplicationRestart(string commandLine, int flags);

        public static void RoundCorners(IntPtr hwnd)
        {
            try { int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); } catch { }
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            try { int on = 1; if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); } catch { }
        }

        public static string ExePath(IntPtr h)
        {
            StringBuilder sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : "";
        }

        // The command line of a process (Windows 8.1+), readable with the "limited" access level.
        public static string CommandLine(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            IntPtr buf = IntPtr.Zero;
            try
            {
                int len;
                NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out len);
                if (len <= 0 || len > 1 << 20) return "";
                buf = Marshal.AllocHGlobal(len);
                if (NtQueryInformationProcess(h, 60, buf, len, out len) != 0) return "";
                int strLen = Marshal.ReadInt16(buf);                       // UNICODE_STRING.Length (bytes)
                IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size);       // UNICODE_STRING.Buffer
                return strLen > 0 && text != IntPtr.Zero ? Marshal.PtrToStringUni(text, strLen / 2) : "";
            }
            catch { return ""; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                CloseHandle(h);
            }
        }
    }

    static class Log
    {
        static readonly object gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (gate)
                {
                    string path = Path.Combine(Paths.DataDir, "log.txt");
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 300000) fi.Delete();
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  v" + AppInfo.Version + "  " + message + "\r\n");
                }
            }
            catch { }
        }
    }

    // Small key=value settings file.
    static class Store
    {
        static readonly object gate = new object();
        static Dictionary<string, string> values;

        static string FilePath { get { return Path.Combine(Paths.DataDir, "settings.txt"); } }

        static void Load()
        {
            if (values != null) return;
            values = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) values[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (Exception ex) { Log.Write("settings load: " + ex.Message); }
        }

        public static string Get(string key, string fallback = "")
        {
            lock (gate)
            {
                Load();
                string v;
                return values.TryGetValue(key, out v) ? v : fallback;
            }
        }

        public static int GetInt(string key, int fallback)
        {
            int v;
            return int.TryParse(Get(key), out v) ? v : fallback;
        }

        public static void Set(string key, string value)
        {
            lock (gate)
            {
                Load();
                if (value == null) values.Remove(key); else values[key] = value;
                try
                {
                    List<string> lines = new List<string>();
                    foreach (KeyValuePair<string, string> kv in values) lines.Add(kv.Key + "=" + kv.Value);
                    File.WriteAllLines(FilePath, lines.ToArray());
                }
                catch (Exception ex) { Log.Write("settings save: " + ex.Message); }
            }
        }
    }

    // JSON files kept on this PC only (history and what it has learned).
    static class Json
    {
        public static T Load<T>(string file) where T : class
        {
            try
            {
                string path = Path.Combine(Paths.DataDir, file);
                if (!File.Exists(path)) return null;
                using (FileStream fs = File.OpenRead(path))
                    return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(fs);
            }
            catch (Exception ex) { Log.Write("load " + file + ": " + ex.Message); return null; }
        }

        public static void Save<T>(string file, T value)
        {
            try
            {
                string path = Path.Combine(Paths.DataDir, file);
                string temp = path + ".tmp";
                using (FileStream fs = new FileStream(temp, FileMode.Create, FileAccess.Write))
                    new DataContractJsonSerializer(typeof(T)).WriteObject(fs, value);
                if (File.Exists(path)) File.Replace(temp, path, null, true);
                else File.Move(temp, path);
            }
            catch (Exception ex) { Log.Write("save " + file + ": " + ex.Message); }
        }
    }

    static class Shell
    {
        public static string Run(string exe, string args, int timeoutMs = 15000)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    StringBuilder sb = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return ""; }
                    p.WaitForExit();
                    lock (sb) return p.ExitCode == 0 ? sb.ToString() : "";
                }
            }
            catch { return ""; }
        }
    }

    static class Fmt
    {
        public static string Duration(double mins)
        {
            int m = (int)Math.Round(mins);
            if (m < 60) return m + " min";
            return m % 60 == 0 ? (m / 60) + " h" : (m / 60) + " h " + (m % 60) + " min";
        }

        public static string Clock(DateTime d) { return d.ToString("t"); }

        public static string Mem(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            return Math.Max(0, bytes / (1L << 20)) + " MB";
        }

        public static string W(double w) { return w.ToString("0.0") + " W"; }
    }
}
