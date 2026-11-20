// 领域模型：用户需求、行程、冲突、运行结果（对应 Python 的 agent/model.py）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TravelCore
{
    public sealed class Weather
    {
        public string Day = "";
        public string Text = "晴";
        public int Low;
        public int High;
        public int Rain;

        public Weather Clone()
        {
            return new Weather { Day = Day, Text = Text, Low = Low, High = High, Rain = Rain };
        }
    }

    /// <summary>休息时段：在 [Start, End] 窗口内至少休息 Minutes 分钟。</summary>
    public sealed class RestSpec
    {
        public string Label = "休息";
        public int Start = 690;    // 11:30
        public int End = 840;      // 14:00
        public int Minutes = 90;

        public RestSpec Clone() { return new RestSpec { Label = Label, Start = Start, End = End, Minutes = Minutes }; }
    }

    public sealed class Profile
    {
        public int Days = 3;
        public int Budget = 3000;
        public string Party = "情侣";
        public string Pace = "normal";
        public string Transport = "transit";   // walk / transit / taxi
        public List<string> Prefs = new List<string>();
        public List<string> MustVisit = new List<string>();
        public string Hotel = Data.DefaultHotel;
        public string StartDate = "";
        public List<string> Cities = new List<string>();
        public List<int> CityDays = new List<int>();
        public List<RestSpec> Rests = Data.DefaultRests();
        public string Origin = "";        // 出发地（城市 key）
        public bool ReturnHome = true;    // 最后一天是否回到出发地

        public int StopsPerDay()
        {
            if (Pace == "relaxed") return 3;
            if (Pace == "intense") return 5;
            return 4;
        }

        public List<KeyValuePair<string, int>> CityPlan()
        {
            var outp = new List<KeyValuePair<string, int>>();
            if (Cities.Count == 0)
            {
                string key = Data.CityKeys.Count > 0 ? Data.CityKeys[0] : Data.City;
                outp.Add(new KeyValuePair<string, int>(key, Days));
                return outp;
            }
            var keys = new List<string>(Cities);
            var days = new List<int>(CityDays);
            int sum = days.Sum();
            if (days.Count != keys.Count || sum != Days)
            {
                days = Distribute(Days, keys.Count);
            }
            for (int i = 0; i < keys.Count && i < days.Count; i++)
            {
                if (days[i] > 0) outp.Add(new KeyValuePair<string, int>(keys[i], days[i]));
            }
            return outp;
        }

        public static List<int> Distribute(int total, int n)
        {
            var res = new List<int>();
            if (n <= 0) return res;
            int b = total / n, rem = total % n;
            for (int i = 0; i < n; i++) res.Add(b + (i < rem ? 1 : 0));
            if (b == 0)
            {
                res = new List<int>();
                for (int i = 0; i < n; i++) res.Add(i < total ? 1 : 0);
            }
            return res;
        }

        public DateTime StartDt()
        {
            if (!string.IsNullOrEmpty(StartDate))
            {
                string s = StartDate.Length > 10 ? StartDate.Substring(0, 10) : StartDate;
                DateTime d;
                if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out d)) return d;
            }
            return DateTime.Now.Date;
        }

        /// <summary>周一=0 … 周日=6，与 Python date.weekday() 一致。</summary>
        public int Weekday() { return ((int)StartDt().DayOfWeek + 6) % 7; }

        public int WeekdayOf(int i) { return Weekday() + i; }

        public string DateOf(int i) { return StartDt().AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
    }

    public sealed class Stop
    {
        public string PoiId;
        public int Arrive;
        public int Depart;
        public int FromPrev;
        public int Wait;
        public ViaPlan Via;          // 本段推荐出行方案

        public Stop() { }
        public Stop(string poiId) { PoiId = poiId; }
        public Stop(string poiId, int arrive, int depart, int fromPrev, int wait)
        {
            PoiId = poiId; Arrive = arrive; Depart = depart; FromPrev = fromPrev; Wait = wait;
        }

        public Stop Clone() { return new Stop(PoiId, Arrive, Depart, FromPrev, Wait); }
    }

    public sealed class Leg
    {
        public string From = "";
        public string FromName = "";
        public string To = "";
        public string ToName = "";
        public int Minutes;          // 车程（不含候车接驳）
        public int Fare;
        public double Km;
        public string Code = "";     // 车次号，如 G1234
        public string DepStation = "";
        public string ArrStation = "";
        public int Dep;              // 发车时刻（分钟）
        public int Arr;
        public int Run;
        public bool NextDay;

        public bool IsEmpty { get { return string.IsNullOrEmpty(To); } }

        public Leg Clone()
        {
            return new Leg { From = From, FromName = FromName, To = To, ToName = ToName, Minutes = Minutes,
                             Fare = Fare, Km = Km, Code = Code, DepStation = DepStation,
                             ArrStation = ArrStation, Dep = Dep, Arr = Arr, Run = Run, NextDay = NextDay };
        }
    }

    public sealed class Day
    {
        public List<Stop> Stops = new List<Stop>();
        public Weather Weather = new Weather();
        public string City = "";
        public Leg Leg = new Leg();
        public List<bool> RestDone = new List<bool>();   // 每个休息时段是否已安排
        public int LastLeave;                            // 当天最后一站离开时刻

        public Day() { }
        public Day(List<Stop> stops, Weather weather, string city, Leg leg)
        {
            Stops = stops; Weather = weather ?? new Weather(); City = city ?? ""; Leg = leg ?? new Leg();
            RestDone = new List<bool>();
        }

        public Day Clone()
        {
            var d = new Day();
            d.Stops = Stops.Select(s => s.Clone()).ToList();
            d.Weather = Weather == null ? new Weather() : Weather.Clone();
            d.City = City;
            d.Leg = Leg == null ? new Leg() : Leg.Clone();
            d.RestDone = new List<bool>(RestDone);
            return d;
        }
    }

    public sealed class Itinerary
    {
        public List<Day> Days = new List<Day>();
        public string Hotel = Data.DefaultHotel;
        public string Transport = "transit";   // walk / transit / taxi
        public List<RestSpec> Rests = Data.DefaultRests();
        public Leg OriginLeg = new Leg();   // 出发地 → 首城
        public Leg ReturnLeg = new Leg();   // 末城 → 出发地

        public Itinerary Clone()
        {
            return new Itinerary
            {
                Days = Days.Select(d => d.Clone()).ToList(), Hotel = Hotel, Transport = Transport,
                Rests = Rests.Select(r => r.Clone()).ToList(),
                OriginLeg = OriginLeg.Clone(), ReturnLeg = ReturnLeg.Clone(),
            };
        }

        public List<string> Ids()
        {
            var res = new List<string>();
            foreach (var d in Days) foreach (var s in d.Stops) res.Add(s.PoiId);
            return res;
        }
    }

    public sealed class Violation
    {
        public string Code;
        public string Severity;
        public int? Day;
        public string PoiId;
        public string Message;
        public string Fix = "";

        public Violation(string code, string severity, int? day, string poiId, string message, string fix = "")
        {
            Code = code; Severity = severity; Day = day; PoiId = poiId; Message = message; Fix = fix;
        }
    }

    public sealed class Report
    {
        public List<Violation> Violations = new List<Violation>();
        public int HardTotal;
        public int HardFailed;
        public int SoftTotal;
        public int SoftFailed;

        public List<Violation> Hard { get { return Violations.Where(v => v.Severity == "hard").ToList(); } }
        public List<Violation> Soft { get { return Violations.Where(v => v.Severity == "soft").ToList(); } }

        public double Csr
        {
            get
            {
                if (HardTotal == 0) return 1.0;
                double v = 1.0 - (double)HardFailed / HardTotal;
                return v < 0 ? 0.0 : v;
            }
        }

        public bool Feasible { get { return HardFailed == 0; } }

        public int[] Score() { return new[] { HardFailed, SoftFailed, Violations.Count }; }

        public static int CompareScore(int[] a, int[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            }
            return 0;
        }
    }

    public sealed class Metrics
    {
        public int Cost;
        public double PrefScore;
        public int ToolCalls;
        public int RepairRounds;
        public double LatencyMs;
        public int Stops;
    }

    public sealed class RunResult
    {
        public string Mode;
        public Profile Profile;
        public Itinerary Itinerary;
        public Report Report;
        public Metrics Metrics;
        public List<Dictionary<string, object>> Trace = new List<Dictionary<string, object>>();
    }

    public static class Num
    {
        /// <summary>Python round() 的银行家舍入。</summary>
        public static int RoundHalfEven(double v) { return (int)Math.Round(v, MidpointRounding.ToEven); }
        public static double Round4(double v) { return Math.Round(v, 4, MidpointRounding.ToEven); }
    }
}