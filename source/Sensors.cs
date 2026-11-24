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

        static List<ManagementBaseObject> Query(string scope, string wql)
        {
            List<ManagementBaseObject> list = new List<ManagementBaseObject>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, wql))
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

        static bool Flag(ManagementBaseObject o, string prop)
        {
            try { object v = o[prop]; return v is bool && (bool)v; }
            catch { return false; }
        }

        public static PowerInfo Power()
        {
            PowerInfo p = new PowerInfo();
            Native.SYSTEM_POWER_STATUS st;
            if (Native.GetSystemPowerStatus(out st))
            {
                p.OnBattery = st.ACLineStatus == 0;
                p.HasBattery = (st.BatteryFlag & 128) == 0 && st.BatteryFlag != 255;
                p.Percent = st.BatteryLifePercent <= 100 ? st.BatteryLifePercent : 0;
                p.Charging = (st.BatteryFlag & 8) != 0;
                p.BatterySaver = (st.SystemStatusFlag & 1) != 0;
                if (st.BatteryLifeTime > 0) p.WindowsMinutesLeft = st.BatteryLifeTime / 60;
            }
            if (!p.HasBattery) { p.OnBattery = false; return p; }

            // Capacity, wear and cycles change slowly: read every 5 minutes.
            if ((DateTime.UtcNow - staticRead).TotalMinutes >= 5)
            {
                staticRead = DateTime.UtcNow;
                double f = 0, d = 0; int c = 0;
                foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")) f += Num(o, "FullChargedCapacity");
                foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT DesignedCapacity FROM BatteryStaticData")) d += Num(o, "DesignedCapacity");
                foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT CycleCount FROM BatteryCycleCount")) c = Math.Max(c, (int)Num(o, "CycleCount"));
                if (f > 0) fullWh = f / 1000;
                if (d > 0) designWh = d / 1000;
                cycles = c;
                critical = ReadCriticalLevel();
            }
            p.FullWh = fullWh;
            p.DesignWh = designWh;
            p.Cycles = cycles;
            p.CriticalPercent = critical;
            if (designWh > 0 && fullWh > 0) p.Health = (int)Math.Round(Math.Min(100, fullWh / designWh * 100));

            // Live readings from the battery: energy left and how fast it's going in or out.
            double remaining = 0, discharge = 0, charge = 0;
            bool any = false, online = false, charging = false;
            foreach (ManagementBaseObject o in Query(@"root\wmi", "SELECT PowerOnline, Charging, DischargeRate, ChargeRate, RemainingCapacity FROM BatteryStatus"))
            {
                any = true;
                remaining += Num(o, "RemainingCapacity");
                double dr = Num(o, "DischargeRate"), cr = Num(o, "ChargeRate");
                if (dr > 0 && dr < 500000) discharge += dr;
                if (cr > 0 && cr < 500000) charge += cr;
                online |= Flag(o, "PowerOnline");
                charging |= Flag(o, "Charging");
            }
            if (any)
            {
                p.GaugeMWh = remaining;
                p.RemainingWh = remaining / 1000;
                if (online) p.OnBattery = false;
                p.Charging |= charging;
                if (p.OnBattery && discharge > 0) { p.Watts = discharge / 1000; p.WattsKnown = true; }
                if (!p.OnBattery && charge > 0) p.ChargeWatts = charge / 1000;
            }
            else if (fullWh > 0) p.RemainingWh = fullWh * p.Percent / 100.0;

            if (p.FullWh <= 0 && p.Percent > 0 && p.RemainingWh > 0) p.FullWh = p.RemainingWh * 100 / p.Percent;
            // Windows turns the laptop off (or hibernates) at the critical level, so that charge isn't time left.
            p.UsableWh = Math.Max(0, p.RemainingWh - p.FullWh * p.CriticalPercent / 100.0);
            return p;
        }

        static readonly Regex Hex = new Regex(@":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline);

        // The battery level where Windows takes its "critical battery" action (shut down / hibernate).
        static int ReadCriticalLevel()
        {
            string o = Shell.Run(Paths.System32("powercfg.exe"), "/qh SCHEME_CURRENT SUB_BATTERY BATLEVELCRIT");
            MatchCollection m = Hex.Matches(o);
            if (m.Count >= 2)
            {
                int dc = int.Parse(m[m.Count - 1].Groups[1].Value, NumberStyles.HexNumber);
                if (dc >= 0 && dc <= 50) return dc;
            }
            return 5;
        }

        // Graphics chip use, per program and overall (Windows' GPU counters, like Task Manager).
        public static GpuReading Gpu()
        {
            GpuReading g = new GpuReading();
            Dictionary<string, double> perEngine = new Dictionary<string, double>();
            foreach (ManagementBaseObject o in Query(@"root\cimv2",
                         "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                string name = Convert.ToString(o["Name"]) ?? "";
                double u = Num(o, "UtilizationPercentage");
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
                perEngine.TryGetValue(type, out cur);
                perEngine[type] = cur + u;
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
