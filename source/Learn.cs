// Learning (kept on this PC only, in %APPDATA%\PowerWatch\learn.json): what each app costs you,
// and the most accurate way to estimate battery time for you, found by testing on your own history.

using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PowerWatch
{
    [DataContract] class Sample
    {
        [DataMember] public DateTime T;
        [DataMember] public double W;
        [DataMember] public double MWh = -1;     // battery gauge's energy counter, -1 when not on battery
    }

    [DataContract] class Prediction
    {
        [DataMember] public DateTime T;
        [DataMember] public DateTime EmptyAt;
        [DataMember] public int Percent;
    }

    [DataContract] class History
    {
        [DataMember] public List<Sample> Samples = new List<Sample>();
        [DataMember] public List<Prediction> Predictions = new List<Prediction>();
    }

    /// One minute of use: average watts, the app in front, on battery or not.
    [DataContract] class Minute
    {
        [DataMember] public DateTime T;
        [DataMember] public double W;
        [DataMember] public string App = "";
        [DataMember] public bool Bat;
    }

    /// What an app costs you: watt-hours used while it was in front, over how many hours.
    [DataContract] class Prof
    {
        [DataMember] public double Wh;
        [DataMember] public double Hours;
        public double Avg { get { return Hours > 0 ? Wh / Hours : 0; } }
    }

    /// The best way to predict for you, found by testing on your own history.
    [DataContract] class Tuned
    {
        [DataMember] public double Ws;      // weight of the last 10 min (the rest: the last hour)
        [DataMember] public double A;       // weight of what the app in front usually uses
        [DataMember] public double Err;     // 2 times out of 3 the estimate was within this fraction
        [DataMember] public int N;          // how many checks it was tested on
        [DataMember] public double Hours;   // hours of history tested
    }

    [DataContract] class LearnStore
    {
        [DataMember] public List<Minute> Minutes = new List<Minute>();          // last 7 days
        [DataMember] public Dictionary<string, Prof> Profiles = new Dictionary<string, Prof>();
        [DataMember] public double K = 1.0;                                     // gauge ÷ live readings
        [DataMember] public Tuned Tuned;

        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            if (Minutes == null) Minutes = new List<Minute>();
            if (Profiles == null) Profiles = new Dictionary<string, Prof>();
            if (K <= 0) K = 1;
        }
    }

    static class Learn
    {
        public static LearnStore Load() { return Json.Load<LearnStore>("learn.json") ?? new LearnStore(); }
        public static void Save(LearnStore s) { Json.Save("learn.json", s); }

        static readonly double[] WeightsToTry = { 0, 0.2, 0.4, 0.55, 0.7, 0.85, 1 };
        static readonly double[] AppWeightsToTry = { 0, 0.2, 0.35, 0.5, 0.7, 0.85 };

        /// Tests every way of mixing (last 10 min, last hour, the app in front) on your own history:
        /// every 5 minutes, what would each mix have predicted for the next 30 minutes, and how far off was it?
        public static Tuned Backtest(List<Minute> source)
        {
            if (source.Count < 120) return null;
            List<Minute> m = new List<Minute>(source);
            m.Sort(delegate(Minute a, Minute b) { return a.T.CompareTo(b.T); });
            Dictionary<string, Prof> running = new Dictionary<string, Prof>();
            List<double>[,] errs = new List<double>[WeightsToTry.Length, AppWeightsToTry.Length];
            for (int a = 0; a < WeightsToTry.Length; a++)
                for (int b = 0; b < AppWeightsToTry.Length; b++) errs[a, b] = new List<double>();

            for (int i = 0; i < m.Count; i++)
            {
                if (i % 5 == 0)
                {
                    DateTime t = m[i].T;
                    int ns, nl, na;
                    double shortW = Avg(m, t.AddSeconds(-600), t, i, true, out ns);
                    double longW = Avg(m, t.AddSeconds(-3600), t, i, true, out nl);
                    double actual = Avg(m, t, t.AddSeconds(1800), i, false, out na);
                    if (ns >= 5 && nl >= 20 && na >= 20 && actual > 0.5)
                    {
                        Prof pr;
                        double? appW = running.TryGetValue(m[i].App, out pr) && pr.Hours >= 1.0 / 3 ? pr.Avg : (double?)null;
                        for (int wi = 0; wi < WeightsToTry.Length; wi++)
                        {
                            double baseW = WeightsToTry[wi] * shortW + (1 - WeightsToTry[wi]) * longW;
                            for (int ai = 0; ai < AppWeightsToTry.Length; ai++)
                            {
                                double pred = appW.HasValue ? (1 - AppWeightsToTry[ai]) * baseW + AppWeightsToTry[ai] * appW.Value : baseW;
                                errs[wi, ai].Add(Math.Abs(pred - actual) / actual);
                            }
                        }
                    }
                }
                Prof r;
                if (!running.TryGetValue(m[i].App, out r)) running[m[i].App] = r = new Prof();
                r.Wh += m[i].W / 60;
                r.Hours += 1.0 / 60;
            }

            double bestMean = double.MaxValue;
            int bw = -1, ba = -1;
            for (int wi = 0; wi < WeightsToTry.Length; wi++)
                for (int ai = 0; ai < AppWeightsToTry.Length; ai++)
                {
                    List<double> e = errs[wi, ai];
                    if (e.Count == 0) continue;
                    double sum = 0;
                    foreach (double x in e) sum += x;
                    if (sum / e.Count < bestMean) { bestMean = sum / e.Count; bw = wi; ba = ai; }
                }
            if (bw < 0) return null;
            List<double> best = new List<double>(errs[bw, ba]);
            best.Sort();
            if (best.Count < 72) return null;                    // at least 6 hours of checks
            double p68 = best[Math.Min(best.Count - 1, (int)(best.Count * 0.68))];
            return new Tuned { Ws = WeightsToTry[bw], A = AppWeightsToTry[ba], Err = p68, N = best.Count, Hours = m.Count / 60.0 };
        }

        static double Avg(List<Minute> m, DateTime from, DateTime to, int i, bool back, out int n)
        {
            double sum = 0;
            n = 0;
            int j = back ? i : i + 1;
            while (j >= 0 && j < m.Count)
            {
                DateTime t = m[j].T;
                if (back) { if (t <= from) break; }
                else { if (t > to) break; }
                if (t > from && t <= to) { sum += m[j].W; n++; }
                j += back ? -1 : 1;
            }
            return n > 0 ? sum / n : 0;
        }
    }
}
