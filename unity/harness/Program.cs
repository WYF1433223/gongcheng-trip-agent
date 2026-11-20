// 一致性验收：读 unity/golden.json，用 C# 引擎重跑 20 组档案 + 跨市档案，
// 结果写到 unity/out_csharp.json，供 unity/diff_golden.py 与 Python 端逐字段对比。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TravelCore;

namespace TravelHarness
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string goldenPath = Path.GetFullPath(args.Length > 0 ? args[0] : "../golden.json");
            string citiesDir = Path.GetFullPath(args.Length > 1 ? args[1] : "../../agent/cities");
            string outPath = Path.GetFullPath(args.Length > 2 ? args[2] : "../out_csharp.json");

            Data.DataDir = citiesDir;
            Console.OutputEncoding = Encoding.UTF8;

            var root = MiniJson.Parse(File.ReadAllText(goldenPath, Encoding.UTF8));
            string startDate = root["start_date"].AsStr();
            var cases = root["cases"].Items();

            var w = new JsonWriter();
            w.BeginObject();
            w.Key("start_date"); w.Str(startDate);
            w.Key("cases"); w.BeginArray();

            foreach (var c in cases)
            {
                string name = c["name"].AsStr();
                var profile = ToProfile(c["profile"], startDate);
                w.BeginObject();
                w.Key("name"); w.Str(name);
                w.Key("profile"); WriteProfile(w, profile);
                w.Key("modes"); w.BeginObject();
                foreach (var mode in Engine.Modes)
                {
                    var res = Engine.Run(profile, mode);
                    w.Key(mode); WriteSnapshot(w, res);
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0} {1,-11} CSR={2} 硬={3} 软={4} 花费={5} 轮={6} 调用={7} 站点={8}",
                        name, mode, res.Report.Csr.ToString("P2", CultureInfo.InvariantCulture),
                        res.Report.HardFailed, res.Report.SoftFailed, res.Metrics.Cost,
                        res.Metrics.RepairRounds, res.Metrics.ToolCalls, res.Metrics.Stops));
                }
                w.EndObject();
                w.EndObject();
            }

            w.EndArray();
            w.EndObject();
            File.WriteAllText(outPath, w.ToString(), new UTF8Encoding(false));
            Console.WriteLine("写出 " + outPath);
            return 0;
        }

        private static List<string> StrList(JVal v)
        {
            return v.Items().Select(x => x.AsStr()).ToList();
        }

        private static List<int> IntList(JVal v)
        {
            return v.Items().Select(x => x.AsInt()).ToList();
        }

        private static List<RestSpec> ToRests(JVal v)
        {
            var outp = new List<RestSpec>();
            if (v == null || v.Type != JType.Arr) return Data.DefaultRests();
            foreach (var item in v.Items())
            {
                outp.Add(new RestSpec
                {
                    Label = item["label"].AsStr(),
                    Start = item["start"].AsInt(),
                    End = item["end"].AsInt(),
                    Minutes = item["minutes"].AsInt(),
                });
            }
            return outp.Count > 0 ? outp : Data.DefaultRests();
        }

        private static string StrOr(JVal v, string dflt)
        {
            if (v == null || v.Type == JType.Null) return dflt;
            if (v.Type == JType.Str) return v.AsStr();
            return dflt;
        }

        private static bool BoolOr(JVal v, bool dflt)
        {
            if (v == null || v.Type == JType.Null) return dflt;
            return v.Type == JType.Bool ? v.AsBool() : dflt;
        }

        private static Profile ToProfile(JVal p, string startDate)
        {
            return new Profile
            {
                Days = p["days"].AsInt(),
                Budget = p["budget"].AsInt(),
                Party = p["party"].AsStr(),
                Pace = p["pace"].AsStr(),
                Transport = p["transport"].AsStr(),
                Prefs = StrList(p["prefs"]),
                MustVisit = StrList(p["must_visit"]),
                Hotel = p["hotel"].AsStr(),
                StartDate = startDate,
                Cities = StrList(p["cities"]),
                CityDays = IntList(p["city_days"]),
                Rests = ToRests(p["rests"]),
                Origin = StrOr(p["origin"], ""),
                ReturnHome = BoolOr(p["return_home"], true),
            };
        }

        private static void WriteProfile(JsonWriter w, Profile p)
        {
            w.BeginObject();
            w.Key("days"); w.Int(p.Days);
            w.Key("budget"); w.Int(p.Budget);
            w.Key("party"); w.Str(p.Party);
            w.Key("pace"); w.Str(p.Pace);
            w.Key("transport"); w.Str(p.Transport);
            w.Key("prefs"); WriteStrings(w, p.Prefs);
            w.Key("must_visit"); WriteStrings(w, p.MustVisit);
            w.Key("hotel"); w.Str(p.Hotel);
            w.Key("cities"); WriteStrings(w, p.Cities);
            w.Key("city_days"); w.BeginArray(); foreach (var d in p.CityDays) w.Int(d); w.EndArray();
            w.Key("origin"); w.Str(p.Origin);
            w.Key("return_home"); w.Bool(p.ReturnHome);
            w.Key("rests"); w.BeginArray();
            foreach (var r in p.Rests)
            {
                w.BeginObject();
                w.Key("label"); w.Str(r.Label);
                w.Key("start"); w.Int(r.Start);
                w.Key("end"); w.Int(r.End);
                w.Key("minutes"); w.Int(r.Minutes);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }

        private static void WriteStrings(JsonWriter w, List<string> xs)
        {
            w.BeginArray();
            foreach (var x in xs) w.Str(x);
            w.EndArray();
        }

        private static void WriteLeg(JsonWriter w, Leg leg, bool sparse = false)
        {
            bool empty = leg == null || string.IsNullOrEmpty(leg.Code);
            w.BeginObject();
            if (empty && sparse)
            {
                w.EndObject();
                return;
            }
            if (leg == null) leg = new Leg();
            if (!sparse)
            {
                w.Key("to"); w.Str(leg.To);
                w.Key("minutes"); w.Int(leg.Run);
                w.Key("fare"); w.Int(leg.Fare);
                w.Key("km"); w.Num(leg.Km);
                w.Key("code"); w.Str(leg.Code);
                w.Key("dep"); w.Int(leg.Dep);
                w.Key("arr"); w.Int(leg.Arr);
                w.Key("dep_station"); w.Str(leg.DepStation);
                w.Key("arr_station"); w.Str(leg.ArrStation);
            }
            else
            {
                w.Key("code"); w.Str(leg.Code);
                w.Key("dep"); w.Int(leg.Dep);
                w.Key("arr"); w.Int(leg.Arr);
                w.Key("run"); w.Int(leg.Run);
                w.Key("fare"); w.Int(leg.Fare);
                w.Key("dep_station"); w.Str(leg.DepStation);
                w.Key("arr_station"); w.Str(leg.ArrStation);
                w.Key("km"); w.Num(leg.Km);
            }
            w.EndObject();
        }

        private static void WriteSnapshot(JsonWriter w, RunResult res)
        {
            var rep = res.Report;
            var m = res.Metrics;
            w.BeginObject();
            w.Key("csr"); w.Num(Num.Round4(rep.Csr));
            w.Key("hard_total"); w.Int(rep.HardTotal);
            w.Key("hard_failed"); w.Int(rep.HardFailed);
            w.Key("soft_total"); w.Int(rep.SoftTotal);
            w.Key("soft_failed"); w.Int(rep.SoftFailed);
            w.Key("cost"); w.Int(m.Cost);
            w.Key("pref_score"); w.Num(Num.Round4(m.PrefScore));
            w.Key("tool_calls"); w.Int(m.ToolCalls);
            w.Key("rounds"); w.Int(m.RepairRounds);
            w.Key("stops"); w.Int(m.Stops);
            w.Key("origin_leg"); WriteLeg(w, res.Itinerary.OriginLeg, true);
            w.Key("return_leg"); WriteLeg(w, res.Itinerary.ReturnLeg, true);
            w.Key("days"); w.BeginArray();
            foreach (var d in res.Itinerary.Days)
            {
                w.BeginObject();
                w.Key("city"); w.Str(d.City);
                w.Key("rain"); w.Int(d.Weather == null ? 0 : d.Weather.Rain);
                w.Key("rest_done"); w.BeginArray();
                foreach (var rd in d.RestDone) { w.Bool(rd); }
                w.EndArray();
                w.Key("last_leave"); w.Int(d.LastLeave);
                w.Key("leg"); WriteLeg(w, d.Leg);
                w.Key("stops"); w.BeginArray();
                foreach (var s in d.Stops)
                {
                    w.BeginObject();
                    w.Key("id"); w.Str(s.PoiId);
                    w.Key("arrive"); w.Int(s.Arrive);
                    w.Key("depart"); w.Int(s.Depart);
                    w.Key("from_prev"); w.Int(s.FromPrev);
                    w.Key("wait"); w.Int(s.Wait);
                    w.Key("via_minutes"); w.Int(s.Via == null ? 0 : s.Via.Minutes);
                    w.Key("via_cost"); w.Int(s.Via == null ? 0 : s.Via.Cost);
                    w.Key("via_headline"); w.Str(s.Via == null ? "" : s.Via.Headline);
                    w.EndObject();
                }
                w.EndArray();
                w.EndObject();
            }
            w.EndArray();
            w.Key("violations"); w.BeginArray();
            foreach (var v in rep.Violations)
            {
                w.BeginObject();
                w.Key("code"); w.Str(v.Code);
                w.Key("severity"); w.Str(v.Severity);
                w.Key("day"); if (v.Day.HasValue) w.Int(v.Day.Value); else w.Null();
                w.Key("poi_id"); if (v.PoiId == null) w.Null(); else w.Str(v.PoiId);
                w.EndObject();
            }
            w.EndArray();
            w.EndObject();
        }
    }
}