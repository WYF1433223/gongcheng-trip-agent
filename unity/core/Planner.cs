// 一次性生成（baseline，对应 Python 的 agent/planner.py）。
using System;
using System.Collections.Generic;
using System.Linq;

namespace TravelCore
{
    public static class Planner
    {
        private static double Score(Poi poi, Profile profile)
        {
            double s = poi.Rating * 2;
            var prefs = new HashSet<string>(profile.Prefs);
            if (poi.Tags.Any(t => prefs.Contains(t))) s += 6;
            if (profile.MustVisit.Contains(poi.Id)) s += 20;
            if (profile.Party == "家庭" && poi.Tags.Contains("亲子")) s += 4;
            if (profile.Party == "朋友" && poi.Tags.Contains("夜景")) s += 2;
            if (profile.Budget < 1500 && poi.Price > 0) s -= poi.Price / 20.0;
            return s;
        }

        public static List<string> RankPois(Profile profile)
        {
            return Data.Pois
                .OrderByDescending(p => Score(p, profile))
                .ThenBy(p => p.Name, StringComparer.Ordinal)
                .Select(p => p.Id)
                .ToList();
        }

        public static Itinerary PlanOnce(Profile profile)
        {
            var ranked = RankPois(profile);
            int n = profile.StopsPerDay();
            var used = new HashSet<string>();
            var days = new List<Day>();
            var must = profile.MustVisit.Where(m => Data.PoiById.ContainsKey(m)).ToList();
            var byCity = new Dictionary<string, List<string>>();
            foreach (var pid in ranked)
            {
                string c = Data.CityOfPoi(pid);
                if (!byCity.ContainsKey(c)) byCity[c] = new List<string>();
                byCity[c].Add(pid);
            }

            foreach (var kv in profile.CityPlan())
            {
                string city = kv.Key;
                int nd = kv.Value;
                List<string> pool;
                if (!byCity.TryGetValue(city, out pool)) pool = new List<string>();
                var mustHere = must.Where(m => Data.CityOfPoi(m) == city).ToList();
                for (int d = 0; d < nd; d++)
                {
                    var picks = new List<string>();
                    foreach (var mid in mustHere)
                    {
                        if (!used.Contains(mid) && picks.Count < n) { picks.Add(mid); used.Add(mid); }
                    }
                    foreach (var pid in pool)
                    {
                        if (picks.Count >= n) break;
                        if (!used.Contains(pid)) { picks.Add(pid); used.Add(pid); }
                    }
                    var day = new Day { City = city };
                    foreach (var pid in picks) day.Stops.Add(new Stop(pid));
                    days.Add(day);
                }
            }
            return new Itinerary { Days = days, Hotel = profile.Hotel, Transport = profile.Transport,
                Rests = (profile.Rests ?? Data.DefaultRests()).Select(r => r.Clone()).ToList() };
        }
    }
}