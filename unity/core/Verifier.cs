// 排程与约束校验（对应 Python 的 agent/verifier.py）。
using System;
using System.Collections.Generic;
using System.Linq;

namespace TravelCore
{
    public static class Verifier
    {
        public const int DayStart = 8 * 60 + 30;
        public const int DayEnd = 22 * 60;
        public const int MaxWait = 90;

        public static Tuple<int, int> Window(Poi p)
        {
            int o = p.OpenMin, c = p.CloseMin;
            if (c <= o) c += 1440;
            return Tuple.Create(o, c);
        }

        public const int RestEarly = 30;   // 允许在休息窗口开始前 30 分钟就开休

        /// <summary>若 now 落在某个尚未安排的休息窗口内，标记完成并返回要插入的分钟数。</summary>
        static int TakeRest(Day day, List<RestSpec> rests, int now)
        {
            for (int i = 0; i < rests.Count; i++)
            {
                if (i < day.RestDone.Count && day.RestDone[i]) continue;
                var r = rests[i];
                if (r.Start - RestEarly <= now && now <= r.End)
                {
                    if (i < day.RestDone.Count) day.RestDone[i] = true;
                    return r.Minutes;
                }
            }
            return 0;
        }

        public static void ScheduleDay(Day day, string hotel, int dayIndex, int start = DayStart,
            string mode = "transit", List<RestSpec> rests = null)
        {
            if (rests == null || rests.Count == 0) rests = Data.DefaultRests();
            int t = start;
            day.RestDone = new List<bool>();      // 每次排程都重新计算休息时段
            for (int i = 0; i < rests.Count; i++) day.RestDone.Add(false);
            string prev = null;
            foreach (var stop in day.Stops)
            {
                var poi = Data.PoiById[stop.PoiId];
                string startId = prev == null ? "hotel:" + hotel : prev;
                var plan = Multimodal.Plan(startId, poi.Id, mode);
                stop.Via = plan;
                int travel = plan.Minutes;
                int arrive = t + travel;
                var win = Window(poi);
                int open = win.Item1;
                int wait = 0;
                if (open > DayStart && arrive < open && open - arrive <= MaxWait)
                {
                    wait = open - arrive;
                    arrive = open;
                }
                arrive += TakeRest(day, rests, arrive);   // 到站正好落在休息窗口：先休息
                stop.FromPrev = travel;
                stop.Wait = wait;
                stop.Arrive = arrive;
                stop.Depart = arrive + poi.Duration;
                t = stop.Depart;
                prev = poi.Id;
            }
            // 收尾：落在窗口内就补休息；当天根本没走到该时段则视为不需要
            for (int ri = 0; ri < rests.Count; ri++)
            {
                if (ri < day.RestDone.Count && day.RestDone[ri]) continue;
                var rr = rests[ri];
                if (t < rr.Start - RestEarly)
                {
                    if (ri < day.RestDone.Count) day.RestDone[ri] = true;
                }
                else if (rr.Start - RestEarly <= t && t <= rr.End)
                {
                    t += rr.Minutes;
                    if (ri < day.RestDone.Count) day.RestDone[ri] = true;
                }
            }
            var ws = Data.WeatherList();
            if (ws.Count > 0)
            {
                int idx = Math.Min(dayIndex, ws.Count - 1);
                day.Weather = ws[idx].Clone();
            }
            day.LastLeave = t;
        }

        public static Itinerary Schedule(Itinerary itinerary, string hotel = null,
            string origin = "", bool returnHome = true)
        {
            string prevCity = null;
            string firstCity = null;
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < itinerary.Days.Count; i++)
            {
                var day = itinerary.Days[i];
                string city = day.City;
                if (string.IsNullOrEmpty(city))
                {
                    if (i == 0) city = itinerary.Days.Count > 0 && !string.IsNullOrEmpty(itinerary.Days[0].City)
                        ? itinerary.Days[0].City : Data.City;
                    else city = prevCity;
                }
                if (string.IsNullOrEmpty(city)) city = Data.City;
                day.City = city;
                int start;
                if (prevCity != null && city != prevCity)
                {
                    var leg = Rail.Trip(prevCity, city, DayStart);
                    day.Leg = leg;
                    start = leg.Arr + 30;
                }
                else
                {
                    day.Leg = new Leg();
                    start = DayStart;
                }
                int k;
                seen.TryGetValue(city, out k);
                k = seen.ContainsKey(city) ? seen[city] : 0;
                seen[city] = k + 1;
                day.Weather = Data.WeatherAt(city, k);
                ScheduleDay(day, string.IsNullOrEmpty(hotel) ? Data.HotelOf(city) : hotel, i, start, itinerary.Transport, itinerary.Rests);
                prevCity = city;
                if (firstCity == null) firstCity = city;
            }
            // 出发地 → 首城（去程）
            itinerary.OriginLeg = new Leg();
            if (!string.IsNullOrEmpty(origin) && !string.IsNullOrEmpty(firstCity) && origin != firstCity)
            {
                var go = Rail.Trip(origin, firstCity, 6 * 60 + 30);
                itinerary.OriginLeg = go;
                if (itinerary.Days.Count > 0)
                {
                    var d0 = itinerary.Days[0];
                    d0.City = firstCity;
                    d0.Weather = Data.WeatherAt(firstCity, 0);
                    ScheduleDay(d0, string.IsNullOrEmpty(hotel) ? Data.HotelOf(firstCity) : hotel, 0,
                        Math.Max(DayStart, go.Arr + 30), itinerary.Transport, itinerary.Rests);
                }
            }
            // 末城 → 出发地（返程）
            itinerary.ReturnLeg = new Leg();
            if (!string.IsNullOrEmpty(origin) && !string.IsNullOrEmpty(firstCity) && origin != firstCity
                && returnHome && itinerary.Days.Count > 0)
            {
                var last = itinerary.Days[itinerary.Days.Count - 1];
                int lastLeave = last.Stops.Count > 0 ? last.LastLeave : DayStart;
                itinerary.ReturnLeg = Rail.Trip(last.City, origin, lastLeave + 45);
            }
            if (!string.IsNullOrEmpty(hotel)) itinerary.Hotel = hotel;
            return itinerary;
        }

        public static int CostOf(Itinerary itinerary)
        {
            int days = itinerary.Days.Count;
            int tickets = 0;
            foreach (var d in itinerary.Days) foreach (var s in d.Stops) tickets += Data.PoiById[s.PoiId].Price;
            int legs = itinerary.Days.Sum(d => d.Leg == null ? 0 : d.Leg.Fare);
            if (itinerary.OriginLeg != null) legs += itinerary.OriginLeg.Fare;
            if (itinerary.ReturnLeg != null) legs += itinerary.ReturnLeg.Fare;
            int ride = itinerary.Days.Sum(d => d.Stops.Sum(x => x.Via == null ? 0 : x.Via.Cost));
            return tickets + Data.FoodPerDay * days + legs + ride;
        }

        public static double IntercityKm(Itinerary itinerary)
        {
            return Math.Round(itinerary.Days.Sum(d => d.Leg == null ? 0 : d.Leg.Km), 1, MidpointRounding.ToEven);
        }

        public static double PrefScore(Itinerary itinerary, Profile profile)
        {
            var ids = itinerary.Ids();
            if (ids.Count == 0) return 0.0;
            var prefs = new HashSet<string>(profile.Prefs);
            if (prefs.Count == 0) return 0.75;
            int hits = 0;
            foreach (var pid in ids)
            {
                if (Data.PoiById[pid].Tags.Any(t => prefs.Contains(t))) hits++;
            }
            return (double)hits / ids.Count;
        }

        public static Report Verify(Itinerary itinerary, Profile profile, int? startWeekday = null)
        {
            var vs = new List<Violation>();
            int hardTotal = 2 + 2 * itinerary.Days.Count;
            var seen = new Dictionary<string, int>();
            int baseWd = startWeekday.HasValue ? startWeekday.Value : profile.Weekday();
            string[] weekdayName = { "一", "二", "三", "四", "五", "六", "日" };

            for (int di = 0; di < itinerary.Days.Count; di++)
            {
                var day = itinerary.Days[di];
                int wd = (baseWd + di) % 7;
                if (day.Stops.Count == 0)
                {
                    vs.Add(new Violation("DAY_EMPTY", "hard", di, null, "第" + (di + 1) + "天没有任何行程"));
                }
                foreach (var stop in day.Stops)
                {
                    var poi = Data.PoiById[stop.PoiId];
                    hardTotal += 3;
                    int seenDay;
                    if (seen.TryGetValue(poi.Id, out seenDay))
                    {
                        vs.Add(new Violation("DUPLICATE", "hard", di, poi.Id,
                            poi.Name + " 在第" + (seenDay + 1) + "天已安排，第" + (di + 1) + "天重复", "删除重复项"));
                    }
                    else
                    {
                        seen[poi.Id] = di;
                    }
                    if (poi.ClosedWeekday.HasValue && poi.ClosedWeekday.Value == wd)
                    {
                        vs.Add(new Violation("CLOSED_WEEKDAY", "hard", di, poi.Id,
                            poi.Name + " 周" + weekdayName[wd] + "闭馆", "换日或替换同类景点"));
                    }
                    var win = Window(poi);
                    if (stop.Arrive < win.Item1 || stop.Depart > win.Item2)
                    {
                        vs.Add(new Violation("OUT_OF_WINDOW", "hard", di, poi.Id,
                            poi.Name + " 安排 " + Fmt(stop.Arrive) + "–" + Fmt(stop.Depart) +
                            "，开放 " + Fmt(win.Item1) + "–" + Fmt(win.Item2),
                            "平移时段或换到适合的日期"));
                    }
                    if (stop.Wait > 60)
                    {
                        vs.Add(new Violation("LONG_WAIT", "soft", di, poi.Id,
                            poi.Name + " 早到需等待 " + stop.Wait + " 分钟", "调整游览顺序"));
                    }
                    if (day.Weather != null && day.Weather.Rain >= 60 && poi.Outdoor)
                    {
                        vs.Add(new Violation("RAIN_OUTDOOR", "soft", di, poi.Id,
                            poi.Name + " 为户外景点，当天降雨概率 " + day.Weather.Rain + "%", "替换为室内景点或改到晴天"));
                    }
                }
                if (day.Stops.Count > profile.StopsPerDay())
                {
                    vs.Add(new Violation("PACE_EXCESS", "hard", di, null,
                        "第" + (di + 1) + "天安排 " + day.Stops.Count + " 站，超过「" + profile.Pace + "」节奏上限 "
                        + profile.StopsPerDay() + " 站", "移出或删除部分站点"));
                }
                if (day.Stops.Count > 0 && day.Stops[day.Stops.Count - 1].Depart > DayEnd)
                {
                    var last = day.Stops[day.Stops.Count - 1];
                    vs.Add(new Violation("DAY_OVERRUN", "hard", di, last.PoiId,
                        "第" + (di + 1) + "天 " + Fmt(last.Depart) + " 才结束，超过 22:00", "减少站点或前移"));
                }
                if (day.Stops.Count > 0)
                {
                    for (int ri = 0; ri < itinerary.Rests.Count; ri++)
                    {
                        bool done = ri < day.RestDone.Count && day.RestDone[ri];
                        if (done) continue;
                        var r = itinerary.Rests[ri];
                        vs.Add(new Violation("REST_MISSING", "soft", di, null,
                            "第" + (di + 1) + "天未安排" + r.Label + "（" + Fmt(r.Start) + "–" + Fmt(r.End)
                            + " 至少 " + r.Minutes + " 分钟）", "在休息窗口内留出空档，或减少当天景点"));
                    }
                }
            }

            // 6b) 返程
            var back = itinerary.ReturnLeg;
            var lastDay = itinerary.Days.Count > 0 ? itinerary.Days[itinerary.Days.Count - 1] : null;
            if (back != null && !string.IsNullOrEmpty(back.Code))
            {
                int leave = (lastDay != null && lastDay.Stops.Count > 0) ? lastDay.LastLeave : DayStart;
                int di = itinerary.Days.Count - 1;
                if (back.NextDay || back.Dep <= leave)
                {
                    vs.Add(new Violation("RETURN_LATE", "soft", di, null,
                        "第" + (di + 1) + "天 " + Fmt(leave) + " 才结束，赶不上当天返程，改乘次日 "
                        + Rail.Fmt(back.Dep) + " 的车", "提前收尾或减少末日的景点"));
                }
                else if (back.Arr > 23 * 60)
                {
                    vs.Add(new Violation("RETURN_LATE", "soft", di, null,
                        "返程 " + Fmt(back.Arr) + " 才到" + back.ToName + "，已过 23:00", "选更早的返程车次"));
                }
            }

            int cost = CostOf(itinerary);
            if (cost > profile.Budget)
            {
                vs.Add(new Violation("BUDGET_EXCEEDED", "hard", null, null,
                    "预计花费 " + cost + " 元，超出预算 " + profile.Budget + " 元", "替换高价门票或减少付费景点"));
            }

            var ids = new HashSet<string>(itinerary.Ids());
            foreach (var mid in profile.MustVisit)
            {
                if (!ids.Contains(mid))
                {
                    vs.Add(new Violation("MISSING_MUST_VISIT", "hard", null, mid,
                        "必去点 " + Data.PoiById[mid].Name + " 未安排", "插入行程"));
                }
            }

            if (profile.Prefs.Count > 0 && PrefScore(itinerary, profile) < 0.4)
            {
                double s = PrefScore(itinerary, profile);
                vs.Add(new Violation("PREF_LOW", "soft", null, null,
                    "偏好匹配率仅 " + ((int)Math.Round(s * 100)) + "%", "增加偏好标签匹配的景点"));
            }

            var rep = new Report
            {
                Violations = vs,
                HardTotal = hardTotal,
                HardFailed = vs.Count(v => v.Severity == "hard"),
                SoftTotal = vs.Count(v => v.Severity == "soft"),
                SoftFailed = vs.Count(v => v.Severity == "soft"),
            };
            return rep;
        }

        public static string Fmt(int minutes)
        {
            minutes %= 1440;
            if (minutes < 0) minutes += 1440;
            return (minutes / 60).ToString("00") + ":" + (minutes % 60).ToString("00");
        }
    }
}