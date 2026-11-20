// 一致性验收：浏览器引擎（JS）与 Python 的 golden 快照逐字段比对。
// 用法： node test_golden.mjs <repo-root> <golden.json>
import fs from "node:fs";
import path from "node:path";
import { Data } from "./data.js";
import { Profile } from "./model.js";
import * as engine from "./engine.js";

const root = process.argv[2];
const goldenPath = process.argv[3];
if (!root || !goldenPath) {
  console.error("用法: node test_golden.mjs <repo-root> <golden.json>");
  process.exit(2);
}

const golden = JSON.parse(fs.readFileSync(goldenPath, "utf8"));
const D = new Data();
D.setDirectory(JSON.parse(fs.readFileSync(path.join(root, "agent/directory.json"), "utf8")));

function loadCity(key) {
  if (D.hasCity(key)) return;
  const p = path.join(root, "agent/cities", key + ".json");
  if (!fs.existsSync(p)) return;
  const spec = JSON.parse(fs.readFileSync(p, "utf8"));
  const rp = path.join(root, "agent/routes", key + ".json");
  const route = fs.existsSync(rp) ? JSON.parse(fs.readFileSync(rp, "utf8")) : null;
  D.addCity(key, spec, route);
}

function snapshot(res) {
  const it = res.itinerary, rep = res.report, m = res.metrics;
  const legObj = (leg) => (leg && leg.code ? {
    code: leg.code, dep: leg.dep, arr: leg.arr, run: leg.run, fare: leg.fare,
    dep_station: leg.dep_station, arr_station: leg.arr_station, km: leg.km,
  } : {});
  return {
    csr: Math.round(rep.csr * 1e4) / 1e4,
    hard_total: rep.hard_total,
    hard_failed: rep.hard_failed,
    soft_total: rep.soft_total,
    soft_failed: rep.soft_failed,
    cost: m.cost,
    pref_score: Math.round(m.pref_score * 1e4) / 1e4,
    tool_calls: m.tool_calls,
    rounds: m.repair_rounds,
    stops: m.stops,
    origin_leg: legObj(it.origin_leg),
    return_leg: legObj(it.return_leg),
    days: it.days.map((d) => ({
      city: d.city,
      rest_done: d.rest_done.slice(),
      rain: Math.trunc((d.weather || {}).rain || 0),
      leg: {
        to: (d.leg || {}).to || "",
        minutes: (d.leg || {}).run || 0,
        fare: (d.leg || {}).fare || 0,
        km: (d.leg || {}).km || 0,
        code: (d.leg || {}).code || "",
        dep: (d.leg || {}).dep || 0,
        arr: (d.leg || {}).arr || 0,
        dep_station: (d.leg || {}).dep_station || "",
        arr_station: (d.leg || {}).arr_station || "",
      },
      last_leave: d.last_leave,
      stops: d.stops.map((s) => ({
        id: s.poi_id, arrive: s.arrive, depart: s.depart,
        from_prev: s.from_prev, wait: s.wait,
        via_minutes: (s.via || {}).minutes || 0,
        via_cost: (s.via || {}).cost || 0,
        via_headline: (s.via || {}).headline || "",
      })),
    })),
    violations: rep.violations.map((v) => ({ code: v.code, severity: v.severity, day: v.day, poi_id: v.poi_id })),
  };
}

const diffs = [];
function cmp(actual, expected, where) {
  if (expected === null || expected === undefined) {
    if (actual !== expected) diffs.push(`${where}: 实际 ${JSON.stringify(actual)} 期望 ${JSON.stringify(expected)}`);
    return;
  }
  if (Array.isArray(expected)) {
    if (!Array.isArray(actual)) { diffs.push(`${where}: 不是数组`); return; }
    if (actual.length !== expected.length) {
      diffs.push(`${where}: 长度 ${actual.length} != ${expected.length}`);
    }
    const n = Math.min(actual.length, expected.length);
    for (let i = 0; i < n; i++) cmp(actual[i], expected[i], `${where}[${i}]`);
    return;
  }
  if (typeof expected === "object") {
    if (typeof actual !== "object" || actual === null) { diffs.push(`${where}: 不是对象`); return; }
    for (const k of Object.keys(expected)) cmp(actual[k], expected[k], `${where}.${k}`);
    return;
  }
  if (typeof expected === "number") {
    if (typeof actual !== "number" || Math.abs(actual - expected) > 1e-6) {
      diffs.push(`${where}: 实际 ${JSON.stringify(actual)} 期望 ${expected}`);
    }
    return;
  }
  if (actual !== expected) diffs.push(`${where}: 实际 ${JSON.stringify(actual)} 期望 ${JSON.stringify(expected)}`);
}

let sameCases = 0;
for (const c of golden.cases) {
  const p = c.profile;
  const keys = (p.cities && p.cities.length) ? p.cities.slice() : ["chengdu"];
  const prof = new Profile({
    days: p.days, budget: p.budget, party: p.party, pace: p.pace,
    transport: p.transport || "transit",
    prefs: p.prefs || [], must_visit: p.must_visit || [],
    hotel: p.hotel || "", start_date: golden.start_date,
    cities: p.cities || [], city_days: p.city_days || [],
    origin: p.origin || "", return_home: p.return_home !== false,
    rests: p.rests || [],
  });
  for (const k of keys) loadCity(k);
  if (prof.origin) loadCity(prof.origin);
  let caseOk = true;
  for (const mode of ["single_pass", "closed_loop"]) {
    const before = diffs.length;
    const res = engine.run(D, prof, mode);
    cmp(snapshot(res), c.modes[mode], `${c.name}/${mode}`);
    if (diffs.length > before) caseOk = false;
  }
  if (caseOk) sameCases += 1;
  const sp = c.modes.single_pass, cl = c.modes.closed_loop;
  console.log(`${c.name}: 一次性 ${(sp.csr * 100).toFixed(2)}% 硬${sp.hard_failed} | 闭环 ${(cl.csr * 100).toFixed(2)}% 硬${cl.hard_failed} 轮${cl.rounds} → ${caseOk ? "一致" : "有差异"}`);
}

if (diffs.length) {
  console.log(`\n共 ${diffs.length} 处差异（前 60 条）：`);
  for (const d of diffs.slice(0, 60)) console.log("  " + d);
  process.exit(1);
}
console.log(`\n一致：${sameCases} 组档案 × 2 模式，全部字段相同。`);