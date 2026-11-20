// 编排层（对应 Python agent/engine.py）。
import * as repair from "./repair.js";
import * as verifier from "./verifier.js";
import { Metrics, RunResult, cmpScore } from "./model.js";
import { planOnce } from "./planner.js";
import { round4 } from "./util.js";

export const MODES = ["single_pass", "closed_loop"];
export const DEFAULT_CITY = "chengdu";

export function run(D, profile, mode = "closed_loop", opts = {}) {
  const maxRounds = opts.max_rounds ?? 15;
  const dedup = opts.dedup ?? true;
  const strategy = opts.strategy ?? "first";
  const cache = opts.cache ?? true;

  const keys = profile.cities.length ? profile.cities : [D.keys.length ? D.keys[0] : DEFAULT_CITY];
  D.useCities(keys);

  const t0 = now();
  let it = planOnce(D, profile);
  verifier.schedule(D, it, null, profile.origin, profile.return_home);
  let rep = verifier.verify(D, it, profile);

  let toolCalls = 2 + profile.days;
  let rounds = 0;
  const trace = [{
    round: 0,
    action: "一次性生成初稿，未做外部校验",
    hard: rep.hard_failed,
    soft: rep.soft_failed,
    csr: round4(rep.csr),
    codes: [...new Set(rep.violations.map((v) => v.code))].sort(),
  }];

  if (mode === "closed_loop") {
    const out = repair.run(D, it, profile, rep, maxRounds, dedup, strategy);
    it = out.itinerary;
    rep = out.report;
    rounds = out.rounds;
    trace.push(...out.trace);
    if (cache) toolCalls += 2 * rounds + 1;
    else toolCalls += rounds * (2 + it.ids().length) + 1;
    verifier.schedule(D, it, null, profile.origin, profile.return_home);
    rep = verifier.verify(D, it, profile);
  }

  verifier.schedule(D, it, null, profile.origin, profile.return_home);
  const latency = Math.round((now() - t0) * 10) / 10;
  const metrics = new Metrics({
    cost: verifier.costOf(D, it),
    pref_score: verifier.prefScore(D, it, profile),
    tool_calls: toolCalls,
    repair_rounds: rounds,
    latency_ms: latency,
    stops: it.ids().length,
  });
  return new RunResult({ mode, profile, itinerary: it, report: rep, metrics, trace });
}

function now() {
  if (typeof performance !== "undefined" && performance.now) return performance.now();
  return Date.now();
}

export function runCompare(D, profile) {
  return {
    profile: profile.toDict(),
    single_pass: summarize(D, run(D, profile, "single_pass")),
    closed_loop: summarize(D, run(D, profile, "closed_loop")),
  };
}

export function summarize(D, res) {
  const rep = res.report;
  return {
    mode: res.mode,
    feasible: rep.feasible,
    csr: round4(rep.csr),
    hard_failed: rep.hard_failed,
    soft_failed: rep.soft_failed,
    cost: res.metrics.cost,
    pref_score: round4(res.metrics.pref_score),
    tool_calls: res.metrics.tool_calls,
    rounds: res.metrics.repair_rounds,
    stops: res.metrics.stops,
    violations: rep.violations.map((v) => v.toDict()),
    trace: res.trace,
    itinerary: res.itinerary.toDict((k) => D.cityName(k)),
  };
}

export { cmpScore };