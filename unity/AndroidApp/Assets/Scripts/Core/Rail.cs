using System;

namespace TravelCore
{
    /// <summary>
    /// 高铁/动车班次推算（与 Python agent/rail.py 逐位一致）。
    /// 沙箱拿不到 12306 实时时刻，这里按平均车速与发车密度用距离推出一张确定性班次表。
    /// </summary>
    public static class Rail
    {
        public const int FirstDep = 6 * 60 + 40;
        public const int LastDep = 21 * 60 + 30;
        public const int HubMinutes = 45;
        const int CheckIn = 25;
        static readonly string[] Suffix = { "", "东", "西", "南", "北" };

        /// <summary>与 Python _seed 一致的 31 进制散列。</summary>
        public static int Seed(string text)
        {
            int s = 0;
            foreach (char ch in text) s = (s * 31 + ch) & 0x7FFFFFFF;
            return s;
        }

        static int Step(double km)
        {
            if (km < 180) return 30;
            if (km < 500) return 45;
            if (km < 1000) return 60;
            return 75;
        }

        static string Station(string key, string prefix)
        {
            string name = Data.CityName(key);
            string suf = Suffix[Seed(prefix + key) % Suffix.Length];
            return name + suf + "站";
        }

        /// <summary>返回 notBefore 之后可乘的第一班车；当天赶不上则记为次日首班。</summary>
        public static Leg Trip(string aKey, string bKey, int notBefore)
        {
            if (string.IsNullOrEmpty(aKey) || string.IsNullOrEmpty(bKey) || aKey == bKey) return null;
            var ic = Data.Intercity(aKey, bKey);
            int minutes = ic.Item1, fare = ic.Item2;
            double km = ic.Item3;
            int run = Math.Max(35, minutes - HubMinutes);
            int step = Step(km);
            int earliest = notBefore + CheckIn;
            int first = FirstDep + (Seed(aKey + ">" + bKey) % step);
            int dep;
            if (earliest <= first) dep = first;
            else
            {
                int k = (earliest - first + step - 1) / step;
                dep = first + k * step;
            }
            bool nextDay = false;
            if (dep > LastDep) { dep = first; nextDay = true; }
            var leg = new Leg
            {
                From = aKey, FromName = Data.CityName(aKey),
                To = bKey, ToName = Data.CityName(bKey),
                Code = "G" + (100 + (Seed(bKey + "<" + aKey) % 8900)),
                DepStation = Station(aKey, "dep"),
                ArrStation = Station(bKey, "arr"),
                Dep = dep, Arr = dep + run, Run = run,
                Minutes = run, Km = Math.Round(km, 1), Fare = fare, NextDay = nextDay,
            };
            return leg;
        }

        public static Leg FirstDeparture(string aKey, string bKey) => Trip(aKey, bKey, FirstDep - CheckIn);

        public static string Fmt(int minutes)
        {
            int m = minutes % 1440;
            return (m / 60).ToString("00") + ":" + (m % 60).ToString("00");
        }

        public static string Label(Leg leg)
        {
            if (leg == null || leg.IsEmpty && string.IsNullOrEmpty(leg.Code)) return "";
            return leg.Code + " " + leg.DepStation + " " + Fmt(leg.Dep) + " → " + leg.ArrStation + " " + Fmt(leg.Arr);
        }
    }
}