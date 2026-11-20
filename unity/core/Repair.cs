// 校验驱动的定向修复（对应 Python 的 agent/repair.py）。
using System;
using System.Collections.Generic;
using System.Linq;

namespace TravelCore
{
    public static class Repair
    {
        public const int MaxEvalsPerRound = 40;

        public static List<string> MealIds()
        {
            return Data.Pois
                .Where(p => p.Tags.Contains("美食") || p.Tags.Contains("夜市"))
                .Select(p => p.Id).ToList();
        }

        // ---------- 行程构造工具 ----------
        public static List<List<string>> ToIds(Itinerary it)
        {
            return it.Days.Select(d => d.Stops.Select(s => s.PoiId).ToList()).ToList();
        }

        public static List<List<string>> Copy(List<List<string>> ids)
        {
            return ids.Select(d => new List<string>(d)).ToList();
        }

        public static bool EqIds(List<List<string>> a, List<List<string>> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Count != b[i].Count) return false;
                for (int j = 0; j < a[i].Count; j++) if (a[i][j] != b[i][j]) return false;
            }
            return true;
        }

        public static Itinerary Build(List<List<string>> ids, string hotel = null, List<string> cities = null,
            string transport = "transit", List<RestSpec> rests = null)
        {
            cities = cities ?? new List<string>();
            var it = new Itinerary { Hotel = hotel ?? "", Transport = transport,
                Rests = Data.NormalizeRests(rests).Select(r => r.Clone()).ToList() };
            for (int i = 0; i < ids.Count; i++)
            {
                var day = new Day { City = i < cities.Count ? cities[i] : "" };
                foreach (var pid in ids[i]) day.Stops.Add(new Stop(pid));
                it.Days.Add(day);
            }
            return it;
        }

        /// <summary>候选解必须保持“某天的景点都属于该天所在城市”。</summary>
        public static bool CityOk(List<List<string>> ids, List<string> cities)
        {
            if (cities == null || cities.Count == 0) return true;
            for (int i = 0; i < ids.Count && i < cities.Count; i++)
            {
                foreach (var pid in ids[i])
                {
                    if (Data.CityOfPoi(pid) != cities[i]) return false;
                }
            }
            return true;
        }

        private static void InsertSorted(List<string> day, string pid)
        {
            int pos = day.Count;
            int open = Data.PoiById[pid].OpenMin;
            for (int i = 0; i < day.Count; i++)
            {
                if (Data.PoiById[day[i]].OpenMin > open) { pos = i; break; }
            }
            day.Insert(pos, pid);
        }

        private static List<string> SortByOpen(List<string> day)
        {
            return day.OrderBy(p => Data.PoiById[p].OpenMin)
                      .ThenBy(p => Data.PoiById[p].Name, StringComparer.Ordinal)
                      .ToList();
        }

        public static List<List<string>> OpReorder(List<List<string>> ids, int di)
        {
            var n = Copy(ids);
            n[di] = SortByOpen(n[di]);
            return n;
        }

        public static List<List<string>> OpMove(List<List<string>> ids, int di, int si, int ti)
        {
            var n = Copy(ids);
            string pid = n[di][si];
            n[di].RemoveAt(si);
            InsertSorted(n[ti], pid);
            return n;
        }

        public static List<List<string>> OpRemove(List<List<string>> ids, int di, int si)
        {
            var n = Copy(ids);
            n[di].RemoveAt(si);
            return n;
        }

        public static List<List<string>> OpReplace(List<List<string>> ids, int di, int si, string pid)
        {
            var n = Copy(ids);
            n[di][si] = pid;
            return n;
        }

        public static List<List<string>> OpInsert(List<List<string>> ids, int di, string pid)
        {
            var n = Copy(ids);
            InsertSorted(n[di], pid);
            return n;
        }

        public static List<List<string>> OpSwap(List<List<string>> ids, int d1, int i1, int d2, int i2)
        {
            var n = Copy(ids);
            string tmp = n[d1][i1];
            n[d1][i1] = n[d2][i2];
            n[d2][i2] = tmp;
            n[d1] = SortByOpen(n[d1]);
            n[d2] = SortByOpen(n[d2]);
            return n;
        }

        // ---------- 候选替代点 ----------
        public static List<string> Alts(string pid, HashSet<string> used, Profile profile = null,
            bool indoorOnly = false, int limit = 3, string city = null)
        {
            var baseP = Data.PoiById[pid];
            var baseTags = new HashSet<string>(baseP.Tags);
            var prefs = profile == null ? new HashSet<string>() : new HashSet<string>(profile.Prefs);
            var scored = new List<Tuple<int, int, int, double, string>>();
            foreach (var p in Data.Pois)
            {
                if (used.Contains(p.Id) || p.Id == pid) continue;
                if (city != null && Data.CityOfPoi(p.Id) != city) continue;
                int overlap = p.Tags.Count(t => baseTags.Contains(t));
                if (overlap == 0) continue;
                if (indoorOnly && p.Outdoor) continue;
                if (p.Price > baseP.Price + 60) continue;
                int prefHit = p.Tags.Any(t => prefs.Contains(t)) ? 1 : 0;
                scored.Add(Tuple.Create(prefHit, overlap, -p.Price, p.Rating, p.Id));
            }
            scored.Sort((x, y) =>
            {
                int c = y.Item1.CompareTo(x.Item1); if (c != 0) return c;
                c = y.Item2.CompareTo(x.Item2); if (c != 0) return c;
                c = y.Item3.CompareTo(x.Item3); if (c != 0) return c;
                c = y.Item4.CompareTo(x.Item4); if (c != 0) return c;
                return string.CompareOrdinal(y.Item5, x.Item5);
            });
            var outl = new List<string>();
            foreach (var t in scored)
            {
                outl.Add(t.Item5);
                if (outl.Count >= limit) break;
            }
            return outl;
        }

        private static bool HasSpace(List<List<string>> ids, int ti, int limit)
        {
            return ids[ti].Count < limit;
        }

        // ---------- 候选生成 ----------
        public static List<KeyValuePair<List<List<string>>, string>> Candidates(Itinerary it, Report rep, Profile profile)
        {
            var ids = ToIds(it);
            var used = new HashSet<string>(ids.SelectMany(d => d));
            int cap = profile.StopsPerDay();
            var outp = new List<KeyValuePair<List<List<string>>, string>>();
            int daysN = ids.Count;

            Action<List<List<string>>, string> add = (newIds, action) =>
            {
                if (newIds == null) return;
                if (EqIds(newIds, ids)) return;
                outp.Add(new KeyValuePair<List<List<string>>, string>(newIds, action));
            };

            Func<string, int?> dayOf = pid =>
            {
                for (int di = 0; di < ids.Count; di++) if (ids[di].Contains(pid)) return di;
                return null;
            };

            Func<int, List<int>> otherDayWithSpace = di =>
            {
                var res = new List<int>();
                for (int ti = 0; ti < daysN; ti++) if (ti != di && HasSpace(ids, ti, cap)) res.Add(ti);
                return res;
            };

            var all = rep.Hard.Concat(rep.Soft).ToList();
            foreach (var v in all)
            {
                if ((v.Code == "OUT_OF_WINDOW" || v.Code == "CLOSED_WEEKDAY" || v.Code == "LONG_WAIT" || v.Code == "DUPLICATE")
                    && v.PoiId != null && v.Day.HasValue)
                {
                    int di = v.Day.Value;
                    string pid = v.PoiId;
                    if (!ids[di].Contains(pid))
                    {
                        var found = dayOf(pid);
                        if (!found.HasValue) continue;
                        di = found.Value;
                    }
                    int si = ids[di].IndexOf(pid);
                    string name = Data.PoiById[pid].Name;
                    if (profile.MustVisit.Contains(pid))
                    {
                        var qs = new List<string>(ids[di]);
                        for (int qi = 0; qi < qs.Count; qi++)
                        {
                            string q = qs[qi];
                            if (q == pid) continue;
                            foreach (int ti in otherDayWithSpace(di))
                            {
                                add(OpMove(ids, di, qi, ti),
                                    "为必去点 " + name + " 让位：" + Data.PoiById[q].Name + " 第" + (di + 1) + "天 → 第" + (ti + 1) + "天");
                            }
                        }
                        int swaps = 0;
                        for (int ti = 0; ti < daysN; ti++)
                        {
                            if (ti == di) continue;
                            for (int si2 = 0; si2 < ids[ti].Count; si2++)
                            {
                                if (swaps >= 4) break;
                                swaps++;
                                string q = ids[ti][si2];
                                add(OpSwap(ids, di, si, ti, si2),
                                    "互换腾挪：" + name + " 与第" + (ti + 1) + "天 " + Data.PoiById[q].Name + " 对调");
                            }
                        }
                    }
                    if (profile.MustVisit.Contains(pid))
                    {
                        for (int qi = 0; qi < ids[di].Count; qi++)
                        {
                            var q = ids[di][qi];
                            if (q == pid || profile.MustVisit.Contains(q)) continue;
                            add(OpRemove(ids, di, qi),
                                "为必去点 " + name + " 腾出第" + (di + 1) + "天：删除 " + Data.PoiById[q].Name);
                        }
                    }
                    add(OpReorder(ids, di), "重排第" + (di + 1) + "天游览顺序（" + name + "）");
                    foreach (int ti in otherDayWithSpace(di))
                    {
                        add(OpMove(ids, di, si, ti), name + " 第" + (di + 1) + "天 → 第" + (ti + 1) + "天");
                    }
                    foreach (var alt in Alts(pid, used, profile))
                    {
                        add(OpReplace(ids, di, si, alt), name + " 替换为 " + Data.PoiById[alt].Name);
                    }
                    if (!profile.MustVisit.Contains(pid))
                    {
                        add(OpRemove(ids, di, si), "删除 " + name);
                    }
                }
                else if ((v.Code == "PACE_EXCESS" || v.Code == "DAY_OVERRUN") && v.Day.HasValue)
                {
                    int di = v.Day.Value;
                    if (ids[di].Count == 0) continue;
                    int si = ids[di].Count - 1;
                    string pid = ids[di][si];
                    foreach (int ti in otherDayWithSpace(di))
                    {
                        add(OpMove(ids, di, si, ti), Data.PoiById[pid].Name + " 第" + (di + 1) + "天 → 第" + (ti + 1) + "天");
                    }
                    if (!profile.MustVisit.Contains(pid))
                    {
                        add(OpRemove(ids, di, si), "删除 " + Data.PoiById[pid].Name + "，压缩第" + (di + 1) + "天行程");
                    }
                }
                else if (v.Code == "BUDGET_EXCEEDED")
                {
                    var paid = ids.SelectMany(d => d)
                        .Where(p => Data.PoiById[p].Price > 0)
                        .Distinct()
                        .OrderByDescending(p => Data.PoiById[p].Price)
                        .ThenBy(p => p, StringComparer.Ordinal)
                        .ToList();
                    foreach (var pid in paid.Take(2))
                    {
                        var found = dayOf(pid);
                        if (!found.HasValue) continue;
                        int di = found.Value;
                        int si = ids[di].IndexOf(pid);
                        foreach (var alt in Alts(pid, used, profile))
                        {
                            if (Data.PoiById[alt].Price < Data.PoiById[pid].Price)
                            {
                                add(OpReplace(ids, di, si, alt),
                                    Data.PoiById[pid].Name + " 替换为更便宜的 " + Data.PoiById[alt].Name);
                            }
                        }
                        if (!profile.MustVisit.Contains(pid))
                        {
                            add(OpRemove(ids, di, si), "删除付费点 " + Data.PoiById[pid].Name + " 以控制预算");
                        }
                    }
                }
                else if (v.Code == "MISSING_MUST_VISIT" && v.PoiId != null)
                {
                    string pid = v.PoiId;
                    for (int ti = 0; ti < daysN; ti++)
                    {
                        if (HasSpace(ids, ti, cap))
                        {
                            add(OpInsert(ids, ti, pid), "插入必去点 " + Data.PoiById[pid].Name + " 到第" + (ti + 1) + "天");
                        }
                    }
                }
                else if (v.Code == "RAIN_OUTDOOR" && v.PoiId != null && v.Day.HasValue)
                {
                    int di = v.Day.Value;
                    string pid = v.PoiId;
                    if (!ids[di].Contains(pid)) continue;
                    int si = ids[di].IndexOf(pid);
                    foreach (var alt in Alts(pid, used, profile, true))
                    {
                        add(OpReplace(ids, di, si, alt),
                            "雨天将 " + Data.PoiById[pid].Name + " 替换为室内 " + Data.PoiById[alt].Name);
                    }
                    for (int ti = 0; ti < daysN; ti++)
                    {
                        if (ti != di && it.Days[ti].Weather.Rain < 60 && HasSpace(ids, ti, cap))
                        {
                            add(OpMove(ids, di, si, ti), Data.PoiById[pid].Name + " 移到第" + (ti + 1) + "天（无雨）");
                        }
                    }
                    if (!profile.MustVisit.Contains(pid))
                    {
                        add(OpRemove(ids, di, si), "删除雨天户外点 " + Data.PoiById[pid].Name);
                    }
                }
                else if (v.Code == "REST_MISSING" && v.Day.HasValue)
                {
                    int di = v.Day.Value;
                    foreach (var mid in MealIds())
                    {
                        if (used.Contains(mid)) continue;
                        if (HasSpace(ids, di, cap))
                        {
                            add(OpInsert(ids, di, mid), "第" + (di + 1) + "天插入餐饮节点 " + Data.PoiById[mid].Name);
                        }
                        for (int si = 0; si < ids[di].Count; si++)
                        {
                            string pid = ids[di][si];
                            if (!profile.MustVisit.Contains(pid))
                            {
                                add(OpReplace(ids, di, si, mid),
                                    "第" + (di + 1) + "天以 " + Data.PoiById[mid].Name + " 替换 " + Data.PoiById[pid].Name);
                            }
                        }
                    }
                }
            }

            var seenHardX = new HashSet<string>();
            int sw5b = 0;
            foreach (var v in rep.Hard)
            {
                if (sw5b >= 8) break;
                string hpid = v.PoiId;
                if (string.IsNullOrEmpty(hpid) || v.Day == null) continue;
                int di;
                if (ids[v.Day.Value].Contains(hpid)) di = v.Day.Value;
                else { var d0 = dayOf(hpid); if (d0 == null) continue; di = d0.Value; }
                int hsi = ids[di].IndexOf(hpid);
                if (hsi < 0) continue;
                if (!seenHardX.Add(di + ":" + hsi)) continue;
                for (int ti = 0; ti < daysN; ti++)
                {
                    if (ti == di) continue;
                    for (int si2 = 0; si2 < ids[ti].Count; si2++)
                    {
                        if (sw5b >= 8) break;
                        sw5b++;
                        add(OpSwap(ids, di, hsi, ti, si2),
                            "跨天对调：" + Data.PoiById[hpid].Name + " 与第" + (ti + 1) + "天 " + Data.PoiById[ids[ti][si2]].Name + " 互换");
                    }
                }
            }

            if (rep.Soft.Any(v => v.Code == "PREF_LOW"))
            {
                var prefs = new HashSet<string>(profile.Prefs);
                var pool = Data.Pois.Where(p => !used.Contains(p.Id) && p.Tags.Any(t => prefs.Contains(t)))
                    .OrderByDescending(p => p.Rating)
                    .ThenBy(p => p.Id, StringComparer.Ordinal)
                    .Select(p => p.Id).ToList();
                for (int di = 0; di < ids.Count; di++)
                {
                    for (int si = 0; si < ids[di].Count; si++)
                    {
                        string pid = ids[di][si];
                        if (profile.MustVisit.Contains(pid)) continue;
                        if (Data.PoiById[pid].Tags.Any(t => prefs.Contains(t))) continue;
                        foreach (var alt in pool.Take(2))
                        {
                            add(OpReplace(ids, di, si, alt),
                                "偏好补强：" + Data.PoiById[pid].Name + " → " + Data.PoiById[alt].Name);
                        }
                    }
                }
            }
            return outp;
        }

        public static string Sig(List<List<string>> ids)
        {
            return string.Join("|", ids.Select(d => string.Join(",", d)));
        }

        public static Tuple<Itinerary, Report, int, List<Dictionary<string, object>>> Run(
            Itinerary it, Profile profile, Report rep = null, int maxRounds = 15,
            bool dedup = true, string strategy = "first")
        {
            rep = rep ?? Verifier.Verify(it, profile);
            var cities = it.Days.Select(d => d.City).ToList();
            var trace = new List<Dictionary<string, object>>();
            var visited = new HashSet<string> { Sig(ToIds(it)) };
            int rounds = 0;

            while (rounds < maxRounds && !(rep.Feasible && rep.SoftFailed == 0))
            {
                Itinerary bestIt = null;
                Report bestRep = null;
                string bestSig = null;
                string bestAction = null;
                int evals = 0;
                foreach (var kv in Candidates(it, rep, profile))
                {
                    evals++;
                    if (evals > MaxEvalsPerRound) break;
                    var ids = kv.Key;
                    if (!CityOk(ids, cities)) continue;
                    string sig = Sig(ids);
                    if (dedup && visited.Contains(sig)) continue;
                    var cand = Build(ids, null, cities, it.Transport, it.Rests);
                    Verifier.Schedule(cand, origin: profile.Origin, returnHome: profile.ReturnHome);
                    var r = Verifier.Verify(cand, profile);
                    if (Report.CompareScore(r.Score(), rep.Score()) < 0)
                    {
                        if (strategy == "first")
                        {
                            bestIt = cand; bestRep = r; bestSig = sig; bestAction = kv.Value;
                            break;
                        }
                        if (bestIt == null || Report.CompareScore(r.Score(), bestRep.Score()) < 0)
                        {
                            bestIt = cand; bestRep = r; bestSig = sig; bestAction = kv.Value;
                        }
                    }
                }
                if (bestIt == null) break;
                it = bestIt; rep = bestRep;
                visited.Add(bestSig);
                rounds++;
                trace.Add(new Dictionary<string, object>
                {
                    { "round", rounds },
                    { "action", bestAction },
                    { "hard", rep.HardFailed },
                    { "soft", rep.SoftFailed },
                    { "csr", Num.Round4(rep.Csr) },
                    { "codes", rep.Violations.Select(v => v.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList() },
                });
            }
            return Tuple.Create(it, rep, rounds, trace);
        }
    }
}