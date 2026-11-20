// 编排层：一次生成 / 闭环修复（对应 Python 的 agent/engine.py）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace TravelCore
{
    public static class Engine
    {
        public static readonly string[] Modes = { "single_pass", "closed_loop" };

        public static RunResult Run(Profile profile, string mode = "closed_loop", int maxRounds = 15,
            bool dedup = true, string strategy = "first", bool cache = true)
        {
            var keys = profile.Cities.Count > 0
                ? profile.Cities
                : new List<string> { Data.CityKeys.Count > 0 ? Data.CityKeys[0] : Data.City };
            Data.UseCities(keys);

            var sw = Stopwatch.StartNew();
            var it = Planner.PlanOnce(profile);
            Verifier.Schedule(it, origin: profile.Origin, returnHome: profile.ReturnHome);
            var rep = Verifier.Verify(it, profile);

            int toolCalls = 2 + profile.Days;
            int rounds = 0;
            var trace = new List<Dictionary<string, object>>();
            trace.Add(TraceRow(0, "一次性生成初稿，未做外部校验", rep));

            if (mode == "closed_loop")
            {
                var r = Repair.Run(it, profile, rep, maxRounds, dedup, strategy);
                it = r.Item1;
                rep = r.Item2;
                rounds = r.Item3;
                trace.AddRange(r.Item4);
                if (cache)
                {
                    toolCalls += 2 * rounds + 1;
                }
                else
                {
                    int stopsNow = it.Ids().Count;
                    toolCalls += rounds * (2 + stopsNow) + 1;
                }
                Verifier.Schedule(it, origin: profile.Origin, returnHome: profile.ReturnHome);
                rep = Verifier.Verify(it, profile);
            }

            Verifier.Schedule(it, origin: profile.Origin, returnHome: profile.ReturnHome);
            sw.Stop();
            var metrics = new Metrics
            {
                Cost = Verifier.CostOf(it),
                PrefScore = Verifier.PrefScore(it, profile),
                ToolCalls = toolCalls,
                RepairRounds = rounds,
                LatencyMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1, MidpointRounding.AwayFromZero),
                Stops = it.Ids().Count,
            };
            return new RunResult
            {
                Mode = mode,
                Profile = profile,
                Itinerary = it,
                Report = rep,
                Metrics = metrics,
                Trace = trace,
            };
        }

        private static Dictionary<string, object> TraceRow(int round, string action, Report rep)
        {
            return new Dictionary<string, object>
            {
                { "round", round },
                { "action", action },
                { "hard", rep.HardFailed },
                { "soft", rep.SoftFailed },
                { "csr", Num.Round4(rep.Csr) },
                { "codes", rep.Violations.Select(v => v.Code).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToList() },
            };
        }
    }
}