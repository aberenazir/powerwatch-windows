// Whole-laptop readings: the battery's own gauge (through Windows), the graphics chip, power modes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PowerWatch
{
    class PowerInfo
    {
        public bool HasBattery = true;
        public double Watts;               // what the laptop uses now (measured on battery)
        public bool WattsKnown;            // Windows only reports it on battery
        public bool OnBattery = true;
        public bool Charging;
        public double ChargeWatts;         // how fast it's charging
        public int Percent;
        public double RemainingWh;         // energy left in the battery
        public double UsableWh;            // energy left before Windows turns the laptop off (critical level)
        public double FullWh;              // a full charge now
        public double DesignWh;            // a full charge when new
        public int Health;                 // % of original capacity
        public int Cycles;
        public double GaugeMWh;            // battery's own energy counter, for averaging
        public int? GpuBusy;
        public bool BatterySaver;
        public int CriticalPercent = 5;
        public int? WindowsMinutesLeft;
    }

    class GpuReading
    {
        public int Total;
        public Dictionary<int, double> PerPid = new Dictionary<int, double>();
        public double VideoDecode;         // > 0 while video is playing
    }

    static class Sensors
    {
        static DateTime staticRead = DateTime.MinValue;
        static double fullWh, designWh;
        static int cycles, critical = 5;

        static readonly Guid SubBattery = new Guid("e73a048d-bf27-4f12-9731-8b2076e8891f");
        static readonly Guid BatLevelCrit = new Guid("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469");
        public static readonly Guid SubSleep = new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20");
        public static readonly Guid StandbyIdle = new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
        public static readonly Guid SubVideo = new Guid("7516b95f-f776-4464-8c53-06167f40cc99");
        public static readonly Guid VideoIdle = new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

        // WMI with timeouts, so a stuck Windows service can never freeze the app.
        static List<ManagementBaseObject> Query(string scope, string wql)
        {
            List<ManagementBaseObject> list = new List<ManagementBaseObject>();
            try
            {
                ManagementScope sc = new ManagementScope(scope, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(5) });
                EnumerationOptions opts = new EnumerationOptions { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true, Rewindable = false };
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(sc, new ObjectQuery(wql), opts))
                using (ManagementObjectCollection c = s.Get())
                    foreach (ManagementBaseObject o in c) list.Add(o);
            }
            catch { }
            return list;
        }

        static double Num(ManagementBaseObject o, string prop)
        {
            try { object v = o[prop]; return v == null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        public static PowerInfo Power()
        {
            PowerInfo p = new PowerInfo();
            Native.SYSTEM_POWER_STATUS st;
            bool haveStatus = Native.GetSystemPowerStatus(out st);
            if (haveStatus)
            {
                p.OnBattery = st.ACLineStatus == 0;
                p.HasBattery = (st.BatteryFlag & 128) == 0 && st.BatteryFlag != 255;
                p.Percent = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : -1;
                p.Charging = st.BatteryFlag != 255 && (st.BatteryFlag & 8) != 0;
                p.BatterySaver = (st.SystemStatusFlag & 1) != 0;
                if (st.BatteryLifeTime > 0) p.WindowsMinutesLeft = st.BatteryLifeTime / 60;
            }

            // Live battery readings straight from Windows' power manager (cheap: no WMI).
            Native.SYSTEM_BATTERY_STATE bs;
            double remaining = -1, maxCap = 0;
            if (Native.CallNtPowerInformation(5, IntPtr.Zero, 0, out bs, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.SYSTEM_BATTERY_STATE))) == 0)
            {
                if (bs.BatteryPresent != 0) p.HasBattery = true;
                p.OnBattery = bs.AcOnLine == 0 && bs.BatteryPresent != 0;
                if (bs.Charging != 0) p.Charging = true;
                if (bs.RemainingCapacity > 0 && bs.RemainingCapacity < 10000000) remaining = bs.RemainingCapacity;
                if (bs.MaxCapacity > 0 && bs.MaxCapacity < 10000000) maxCap = bs.MaxCapacity;
                if ((uint)bs.Rate != 0x80000000u)
                {
                    if (bs.Rate < 0 && -bs.Rate < 500000) { p.Watts = -bs.Rate / 1000.0; p.WattsKnown = p.OnBattery; }
                    else if (bs.Rate > 0 && bs.Rate < 500000) p.ChargeWatts = bs.Rate / 1000.0;
                }
            }
            if (!p.HasBattery) { p.OnBattery = false; p.WattsKnown = false; p.Percent = 0; return p; }

            // Capacity, wear, cycles and the critical level change slowly: every 10 minutes.
            if ((DateTime.UtcNow - staticRead).TotalMinutes >= 10)
            {
                staticRead = DateTime.UtcNow;
                double f = 0, d = 0; int c = 0;
                foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")) f += Num(o, "FullChargedCapacity");
                foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData")) d += Num(o, "DesignedCapacity");
                foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount")) c = Math.Max(c, (int)Num(o, "CycleCount"));
                if (f > 0 && f < 10000000) fullWh = f / 1000;
                if (d > 0 && d < 10000000) designWh = d / 1000;
                cycles = c;
                int? crit = Native.ReadDcSetting(SubBattery, BatLevelCrit);
                critical = crit.HasValue && crit.Value >= 0 && crit.Value <= 50 ? crit.Value : 5;
            }
            p.FullWh = fullWh > 0 ? fullWh : maxCap / 1000;
            p.DesignWh = designWh;
            p.Cycles = cycles;
            p.CriticalPercent = critical;
            if (designWh > 0 && p.FullWh > 0) p.Health = (int)Math.Round(Math.Min(100, p.FullWh / designWh * 100));

            if (remaining > 0)
            {
                p.GaugeMWh = remaining;
                p.RemainingWh = remaining / 1000;
            }
            else if (p.FullWh > 0 && p.Percent >= 0) p.RemainingWh = p.FullWh * p.Percent / 100.0;
            if (p.Percent < 0) p.Percent = p.FullWh > 0 ? (int)Math.Round(Math.Min(100, p.RemainingWh / p.FullWh * 100)) : 0;   // Windows said "unknown"
            if (p.FullWh <= 0 && p.Percent > 0 && p.RemainingWh > 0) p.FullWh = p.RemainingWh * 100 / p.Percent;
            // Windows turns the laptop off (or hibernates) at the critical level, so that charge isn't time left.
            p.UsableWh = Math.Max(0, p.RemainingWh - p.FullWh * p.CriticalPercent / 100.0);
            return p;
        }

        // Graphics chip use, per program and overall (Windows' GPU counters, like Task Manager).
        public static GpuReading Gpu()
        {
            GpuReading g = new GpuReading();
            Dictionary<string, double> perEngine = new Dictionary<string, double>();   // per physical engine
            foreach (ManagementBaseObject o in Query(@"root\cimv2",
                         "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                string name = Convert.ToString(o["Name"]) ?? "";
                double u = Math.Min(100, Num(o, "UtilizationPercentage"));
                if (u <= 0) continue;
                // pid_1234_luid_0x..._phys_0_eng_3_engtype_3D
                int pid = 0;
                int a = name.IndexOf("pid_", StringComparison.Ordinal);
                if (a >= 0)
                {
                    int b = name.IndexOf('_', a + 4);
                    if (b > a) int.TryParse(name.Substring(a + 4, b - a - 4), out pid);
                }
                int e = name.IndexOf("engtype_", StringComparison.Ordinal);
                string type = e >= 0 ? name.Substring(e + 8) : "";
                double cur;
                g.PerPid.TryGetValue(pid, out cur);
                g.PerPid[pid] = Math.Max(cur, u);
                int l = name.IndexOf("luid_", StringComparison.Ordinal);
                string engine = l >= 0 ? name.Substring(l) : name;          // luid_..._phys_0_eng_3_engtype_3D
                perEngine.TryGetValue(engine, out cur);
                perEngine[engine] = Math.Min(100, cur + u);
                if (type.IndexOf("VideoDecode", StringComparison.OrdinalIgnoreCase) >= 0) g.VideoDecode += u;
            }
            double best = 0;
            foreach (double v in perEngine.Values) best = Math.Max(best, v);
            g.Total = (int)Math.Round(Math.Min(100, best));
            return g;
        }

        // Windows power modes: 0 Balanced, 1 Best power efficiency, 2 Best performance.
        static int ModeOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return 0;
            guid = guid.Trim('{', '}').ToLowerInvariant();
            if (guid.StartsWith("961cc777")) return 1;
            if (guid.StartsWith("ded574b5")) return 2;
            return 0;
        }

        public static void PowerModes(bool onBattery, out int now, out int battery, out int charger)
        {
            battery = charger = 0;
            const string key = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(key, false))
                {
                    if (k != null)
                    {
                        charger = ModeOf(Convert.ToString(k.GetValue("ActiveOverlayAcPowerScheme", "")));
                        battery = ModeOf(Convert.ToString(k.GetValue("ActiveOverlayDcPowerScheme", "")));
                    }
                }
            }
            catch { }
            now = onBattery ? battery : charger;
            try
            {
                Guid g;
                if (Native.PowerGetEffectiveOverlayScheme(out g) == 0) now = ModeOf(g.ToString());
            }
            catch { }
        }
    }
}
