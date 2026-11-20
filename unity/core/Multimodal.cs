using System;
using System.Collections.Generic;
using System.Linq;

namespace TravelCore
{
    public sealed class ViaSeg
    {
        public string Mode = "";    // walk / transit / metro / taxi
        public string Text = "";
        public int Minutes;
        public int Cost;
        public ViaSeg Clone() { return new ViaSeg { Mode = Mode, Text = Text, Minutes = Minutes, Cost = Cost }; }
    }

    public sealed class ViaPlan
    {
        public List<ViaSeg> Segments = new List<ViaSeg>();
        public int Minutes;
        public int Cost;
        public string Headline = "";
        public string Why = "";

        public ViaPlan Clone()
        {
            return new ViaPlan
            {
                Segments = Segments.Select(s => s.Clone()).ToList(),
                Minutes = Minutes, Cost = Cost, Headline = Headline, Why = Why,
            };
        }

        public bool IsEmpty { get { return Segments.Count == 0; } }
    }

    /// <summary>
    /// 单段出行的「最佳方案」推荐（与 Python agent/multimodal.py 逐位一致）。
    /// 评分 = 分钟数 + 花费 × TimeCost（默认 1.82 分钟/元，即 33 元/小时的时间价值）。
    /// </summary>
    public static class Multimodal
    {
        public const double TimeCost = 1.82;
        public const double ComboMinKm = 3.5;
        public const int FeederMinutes = 7;
        public const int FeederCost = 12;
        public const int LastWalk = 4;

        static ViaSeg Seg(string mode, string text, int minutes, int cost)
        {
            return new ViaSeg { Mode = mode, Text = text, Minutes = minutes, Cost = cost };
        }

        static ViaPlan Wrap(List<ViaSeg> segs, string headline, string why)
        {
            int mins = 0, cost = 0;
            foreach (var s in segs) { mins += s.Minutes; cost += s.Cost; }
            return new ViaPlan { Segments = segs, Minutes = mins, Cost = cost, Headline = headline, Why = why };
        }

        static ViaPlan Single(string mode, double km, int minutes)
        {
            if (mode == "walk")
                return Wrap(new List<ViaSeg> { Seg("walk", "步行 " + minutes + " 分钟", minutes, 0) },
                            "步行 · 免费", "步行距离内，不用等车");
            if (mode == "taxi")
            {
                int fare = Data.TaxiFare(km);
                return Wrap(new List<ViaSeg> { Seg("taxi", "打车 " + minutes + " 分钟", minutes, fare) },
                            "打车 " + minutes + " 分钟 · ¥" + fare, "最快，费用较高");
            }
            int mf = Data.MetroFare(km);
            return Wrap(new List<ViaSeg> { Seg("transit", "公交/地铁 " + minutes + " 分钟", minutes, mf) },
                        "公交/地铁 " + minutes + " 分钟 · ¥" + mf, "不用打车，时间较稳");
        }

        static ViaPlan Combo(double km, int transitMin)
        {
            int ride = Math.Max(6, Num.RoundHalfEven((transitMin - 10) * 0.78));
            int fare = Data.MetroFare(km);
            var segs = new List<ViaSeg>
            {
                Seg("taxi", "打车到地铁站 " + FeederMinutes + " 分钟", FeederMinutes, FeederCost),
                Seg("metro", "地铁/轨道 " + ride + " 分钟", ride, fare),
                Seg("walk", "出站步行 " + LastWalk + " 分钟", LastWalk, 0),
            };
            return Wrap(segs,
                "打车 " + FeederMinutes + " 分 + 轨道 " + ride + " 分 + 步行 " + LastWalk + " 分 · ¥" + (FeederCost + fare),
                "打车接驳省掉等车，轨道避开路面拥堵");
        }

        static double Score(ViaPlan p) { return p.Minutes + p.Cost * TimeCost; }

        /// <summary>返回该段的最佳出行方案；mode 固定为 walk/transit/taxi 时不比较。</summary>
        public static ViaPlan Plan(string aId, string bId, string mode = "auto")
        {
            if (aId == bId) return Wrap(new List<ViaSeg>(), "原地", "");
            double km = Data.IdKm(aId, bId);
            if (mode == "walk" || mode == "transit" || mode == "taxi")
                return Single(mode, km, Data.IdMinutes(aId, bId, mode));

            int walkMin = Data.IdMinutes(aId, bId, "walk");
            int busMin = Data.IdMinutes(aId, bId, "transit");
            int taxiMin = Data.IdMinutes(aId, bId, "taxi");
            var options = new List<ViaPlan>();
            if (km <= 3.0 || walkMin <= 35) options.Add(Single("walk", km, walkMin));
            options.Add(Single("transit", km, busMin));
            options.Add(Single("taxi", km, taxiMin));
            if (km >= ComboMinKm && busMin > 20) options.Add(Combo(km, busMin));

            var best = options[0];
            for (int i = 1; i < options.Count; i++)
                if (Score(options[i]) < Score(best) - 1e-9) best = options[i];

            var rest = options.Where(o => !ReferenceEquals(o, best)).ToList();
            if (rest.Count > 0)
            {
                var alt = rest[0];
                foreach (var o in rest) if (Score(o) < Score(alt) - 1e-9) alt = o;
                int dm = best.Minutes - alt.Minutes;
                int dc = best.Cost - alt.Cost;
                var bits = new List<string>();
                if (dm < 0) bits.Add("比「" + alt.Headline + "」快 " + (-dm) + " 分钟");
                else if (dm > 0) bits.Add("比「" + alt.Headline + "」慢 " + dm + " 分钟");
                if (dc < 0) bits.Add("省 ¥" + (-dc));
                else if (dc > 0) bits.Add("多花 ¥" + dc);
                if (bits.Count > 0)
                {
                    var copy = best.Clone();
                    copy.Why = (string.IsNullOrEmpty(copy.Why) ? "" : copy.Why + "；") + string.Join("、", bits);
                    best = copy;
                }
            }
            return best;
        }
    }
}