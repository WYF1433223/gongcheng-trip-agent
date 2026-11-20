// 校验驱动的定向修复（对应 Python agent/repair.py）。
import * as verifier from "./verifier.js";
import { Day, Itinerary, Stop, cmpScore } from "./model.js";
import { round4 } from "./util.js";

export const MAX_EVALS_PER_ROUND = 40;

export function mealIds(D) {
  return D.pois.filter((p) => p.tags.includes("美食") || p.tags.includes("夜市")).map((p) => p.id);
}

export function toIds(it) {
  return it.days.map((d) => d.stops.map((s) => s.poi_id));
}

export function build(ids, hotel = null, cities = null, transport = "transit", rests = null) {
  const cs = cities || [];
  return new Itinerary({
    days: ids.map((day, i) => new Day(day.map((p) => new Stop(p)), {}, i < cs.length ? cs[i] : "", {}, [], 0)),
    hotel: hotel || "",
    transport,
    rests: rests || null,
  });
}

export function cityOk(D, ids, cities) {
  if (!cities || !cities.length) return true;
  for (let i = 0; i < ids.length; i++) {
    const c = cities[i];
    for (const pid of ids[i]) {
      if (D.cityOfPoi(pid) !== c) return false;
    }
  }
  return true;
}

function insertSorted(D, day, pid) {
  let pos = day.length;
  for (let i = 0; i < day.length; i++) {
    if (D.poiById.get(day[i]).open_min > D.poiById.get(pid).open_min) {
      pos = i;
      break;
    }
  }
  day.splice(pos, 0, pid);
}

const copyIds = (ids) => ids.map((d) => d.slice());
const sortDay = (D, day) =>
  day.slice().sort((a, b) => {
    const oa = D.poiById.get(a).open_min, ob = D.poiById.get(b).open_min;
    if (oa !== ob) return oa - ob;
    const na = D.poiById.get(a).name, nb = D.poiById.get(b).name;
    if (na === nb) return 0;
    return na < nb ? -1 : 1;
  });

export function opReorder(D, ids, di) {
  const next = copyIds(ids);
  next[di] = sortDay(D, next[di]);
  return next;
}

export function opMove(D, ids, di, si, ti) {
  const next = copyIds(ids);
  const [pid] = next[di].splice(si, 1);
  insertSorted(D, next[ti], pid);
  return next;
}

export function opRemove(ids, di, si) {
  const next = copyIds(ids);
  next[di].splice(si, 1);
  return next;
}

export function opReplace(ids, di, si, pid) {
  const next = copyIds(ids);
  next[di][si] = pid;
  return next;
}

export function opInsert(D, ids, di, pid) {
  const next = copyIds(ids);
  insertSorted(D, next[di], pid);
  return next;
}

export function opSwap(D, ids, d1, i1, d2, i2) {
  const next = copyIds(ids);
  const tmp = next[d1][i1];
  next[d1][i1] = next[d2][i2];
  next[d2][i2] = tmp;
  for (const d of [d1, d2]) next[d] = sortDay(D, next[d]);
  return next;
}

function cmpTuple(a, b) {
  for (let i = 0; i < a.length; i++) {
    const x = a[i], y = b[i];
    if (typeof x === "string" || typeof y === "string") {
      if (x === y) continue;
      return x < y ? -1 : 1;
    }
    if (x !== y) return x < y ? -1 : 1;
  }
  return 0;
}

export function alts(D, pid, used, profile = null, indoorOnly = false, limit = 3, city = null) {
  const base = D.poiById.get(pid);
  const baseTags = new Set(base.tags);
  const prefs = profile ? new Set(profile.prefs) : new Set();
  const scored = [];
  for (const p of D.pois) {
    if (used.has(p.id) || p.id === pid) continue;
    if (city && D.cityOfPoi(p.id) !== city) continue;
    let overlap = 0;
    for (const t of p.tags) if (baseTags.has(t)) overlap += 1;
    if (!overlap) continue;
    if (indoorOnly && p.outdoor) continue;
    if (p.price > base.price + 60) continue;
    const prefHit = p.tags.some((t) => prefs.has(t)) ? 1 : 0;
    scored.push([prefHit, overlap, -p.price, p.rating, p.id]);
  }
  // reverse=True：全部降序（含 id 降序），与 Python 一致
  scored.sort((a, b) => cmpTuple(b, a));
  const out = [];
  const seen = new Set();
  for (const row of scored) {
    const p = row[row.length - 1];
    if (seen.has(p)) continue;
    seen.add(p);
    out.push(p);
    if (out.length >= limit) break;
  }
  return out;
}

const hasSpace = (ids, ti, limit) => ids[ti].length < limit;

export function candidates(D, it, rep, profile) {
  const ids = toIds(it);
  const used = new Set();
  for (const day of ids) for (const p of day) used.add(p);
  const cap = profile.stopsPerDay();
  const out = [];
  const daysN = ids.length;

  const same = (a, b) => {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i++) {
      if (a[i].length !== b[i].length) return false;
      for (let j = 0; j < a[i].length; j++) if (a[i][j] !== b[i][j]) return false;
    }
    return true;
  };

  const add = (newIds, action) => {
    if (!newIds) return;
    if (same(newIds, ids)) return;
    out.push({ ids: newIds, action });
  };

  const dayOf = (pid) => {
    for (let di = 0; di < ids.length; di++) if (ids[di].includes(pid)) return di;
    return null;
  };

  const otherDayWithSpace = (di) => {
    const res = [];
    for (let ti = 0; ti < daysN; ti++) if (ti !== di && hasSpace(ids, ti, cap)) res.push(ti);
    return res;
  };

  for (const v of [...rep.hard, ...rep.soft]) {
    if (["OUT_OF_WINDOW", "CLOSED_WEEKDAY", "LONG_WAIT", "DUPLICATE"].includes(v.code) &&
        v.poi_id && v.day !== null && v.day !== undefined) {
      let di = v.day;
      const pid = v.poi_id;
      if (!ids[di] || !ids[di].includes(pid)) {
        di = dayOf(pid);
        if (di === null) continue;
      }
      const si = ids[di].indexOf(pid);
      const name = D.poiById.get(pid).name;
      const isMust = profile.must_visit.includes(pid);

      if (isMust) {
        ids[di].forEach((q, qi) => {
          if (q === pid) return;
          for (const ti of otherDayWithSpace(di)) {
            add(opMove(D, ids, di, qi, ti),
              "为必去点 " + name + " 让位：" + D.poiById.get(q).name + " 第" + (di + 1) + "天 → 第" + (ti + 1) + "天");
          }
        });
        let swaps = 0;
        outer:
        for (let ti = 0; ti < daysN; ti++) {
          if (ti === di) continue;
          for (let si2 = 0; si2 < ids[ti].length; si2++) {
            if (swaps >= 4) break outer;
            swaps += 1;
            const q = ids[ti][si2];
            add(opSwap(D, ids, di, si, ti, si2),
              "互换腾挪：" + name + " 与第" + (ti + 1) + "天 " + D.poiById.get(q).name + " 对调");
          }
        }
      }

      if (isMust) {
        ids[di].forEach((q, qi) => {
          if (q === pid || profile.must_visit.includes(q)) return;
          add(opRemove(ids, di, qi),
            "为必去点 " + name + " 腾出第" + (di + 1) + "天：删除 " + D.poiById.get(q).name);
        });
      }

      add(opReorder(D, ids, di), "重排第" + (di + 1) + "天游览顺序（" + name + "）");

      for (const ti of otherDayWithSpace(di)) {
        add(opMove(D, ids, di, si, ti), name + " 第" + (di + 1) + "天 → 第" + (ti + 1) + "天");
      }

      for (const alt of alts(D, pid, used, profile)) {
        add(opReplace(ids, di, si, alt), name + " 替换为 " + D.poiById.get(alt).name);
      }

      if (!isMust) add(opRemove(ids, di, si), "删除 " + name);
    } else if (["PACE_EXCESS", "DAY_OVERRUN"].includes(v.code) && v.day !== null && v.day !== undefined) {
      const di = v.day;
      if (!ids[di] || !ids[di].length) continue;
      const si = ids[di].length - 1;
      const pid = ids[di][si];
      for (const ti of otherDayWithSpace(di)) {
        add(opMove(D, ids, di, si, ti), D.poiById.get(pid).name + " 第" + (di + 1) + "天 → 第" + (ti + 1) + "天");
      }
      if (!profile.must_visit.includes(pid)) {
        add(opRemove(ids, di, si), "删除 " + D.poiById.get(pid).name + "，压缩第" + (di + 1) + "天行程");
      }
    } else if (v.code === "BUDGET_EXCEEDED") {
      const paid = [...new Set(ids.flat().filter((p) => D.poiById.get(p).price > 0))];
      paid.sort((a, b) => {
        const pa = D.poiById.get(a).price, pb = D.poiById.get(b).price;
        if (pa !== pb) return pb - pa;
        return a < b ? -1 : a > b ? 1 : 0;
      });
      for (const pid of paid.slice(0, 2)) {
        const di = dayOf(pid);
        if (di === null) continue;
        const si = ids[di].indexOf(pid);
        for (const alt of alts(D, pid, used, profile)) {
          if (D.poiById.get(alt).price < D.poiById.get(pid).price) {
            add(opReplace(ids, di, si, alt),
              D.poiById.get(pid).name + " 替换为更便宜的 " + D.poiById.get(alt).name);
          }
        }
        if (!profile.must_visit.includes(pid)) {
          add(opRemove(ids, di, si), "删除付费点 " + D.poiById.get(pid).name + " 以控制预算");
        }
      }
    } else if (v.code === "MISSING_MUST_VISIT" && v.poi_id) {
      const pid = v.poi_id;
      for (let ti = 0; ti < daysN; ti++) {
        if (hasSpace(ids, ti, cap)) {
          add(opInsert(D, ids, ti, pid), "插入必去点 " + D.poiById.get(pid).name + " 到第" + (ti + 1) + "天");
        }
      }
    } else if (v.code === "RAIN_OUTDOOR" && v.poi_id && v.day !== null && v.day !== undefined) {
      const di = v.day, pid = v.poi_id;
      if (!ids[di] || !ids[di].includes(pid)) continue;
      const si = ids[di].indexOf(pid);
      for (const alt of alts(D, pid, used, profile, true)) {
        add(opReplace(ids, di, si, alt),
          "雨天将 " + D.poiById.get(pid).name + " 替换为室内 " + D.poiById.get(alt).name);
      }
      for (let ti = 0; ti < daysN; ti++) {
        if (ti !== di && ((it.days[ti].weather || {}).rain || 0) < 60 && hasSpace(ids, ti, cap)) {
          add(opMove(D, ids, di, si, ti), D.poiById.get(pid).name + " 移到第" + (ti + 1) + "天（无雨）");
        }
      }
      if (!profile.must_visit.includes(pid)) {
        add(opRemove(ids, di, si), "删除雨天户外点 " + D.poiById.get(pid).name);
      }
    } else if (v.code === "REST_MISSING" && v.day !== null && v.day !== undefined) {
      const di = v.day;
      for (const mid of mealIds(D)) {
        if (used.has(mid)) continue;
        if (hasSpace(ids, di, cap)) {
          add(opInsert(D, ids, di, mid), "第" + (di + 1) + "天插入餐饮节点 " + D.poiById.get(mid).name);
        }
        ids[di].forEach((pid, si) => {
          if (!profile.must_visit.includes(pid)) {
            add(opReplace(ids, di, si, mid),
              "第" + (di + 1) + "天以 " + D.poiById.get(mid).name + " 替换 " + D.poiById.get(pid).name);
          }
        });
      }
    }
  }

  // 5b) 跨天对调
  const seenHard = new Set();
  let swaps = 0;
  for (const v of rep.hard) {
    if (swaps >= 8) break;
    if (!v.poi_id || v.day === null || v.day === undefined) continue;
    const di = ids[v.day] && ids[v.day].includes(v.poi_id) ? v.day : dayOf(v.poi_id);
    if (di === null || !ids[di].includes(v.poi_id)) continue;
    const si = ids[di].indexOf(v.poi_id);
    const key = di + ":" + si;
    if (seenHard.has(key)) continue;
    seenHard.add(key);
    for (let ti = 0; ti < daysN; ti++) {
      if (ti === di) continue;
      for (let si2 = 0; si2 < ids[ti].length; si2++) {
        if (swaps >= 8) break;
        swaps += 1;
        add(opSwap(D, ids, di, si, ti, si2),
          "跨天对调：" + D.poiById.get(v.poi_id).name + " 与第" + (ti + 1) + "天 " + D.poiById.get(ids[ti][si2]).name + " 互换");
      }
    }
  }

  // 偏好补强
  if (rep.soft.some((v) => v.code === "PREF_LOW")) {
    const prefs = new Set(profile.prefs);
    const pool = D.pois.filter((p) => !used.has(p.id) && p.tags.some((t) => prefs.has(t))).map((p) => p.id);
    pool.sort((a, b) => D.poiById.get(b).rating - D.poiById.get(a).rating);
    ids.forEach((day, di) => {
      day.forEach((pid, si) => {
        const tags = D.poiById.get(pid).tags;
        if (profile.must_visit.includes(pid) || tags.some((t) => prefs.has(t))) return;
        for (const alt of pool.slice(0, 2)) {
          add(opReplace(ids, di, si, alt),
            "偏好补强：" + D.poiById.get(pid).name + " → " + D.poiById.get(alt).name);
        }
      });
    });
  }
  return out;
}

export function sig(ids) {
  return ids.map((d) => d.join(",")).join("|");
}

export function run(D, it, profile, rep = null, maxRounds = 15, dedup = true, strategy = "first") {
  let cur = it;
  let curRep = rep || verifier.verify(D, cur, profile);
  const cities = cur.days.map((d) => d.city);
  const trace = [];
  const visited = new Set([sig(toIds(cur))]);
  let rounds = 0;

  while (rounds < maxRounds && !(curRep.feasible && curRep.soft_failed === 0)) {
    let best = null;
    let evals = 0;
    for (const cand of candidates(D, cur, curRep, profile)) {
      evals += 1;
      if (evals > MAX_EVALS_PER_ROUND) break;
      if (!cityOk(D, cand.ids, cities)) continue;
      const s = sig(cand.ids);
      if (dedup && visited.has(s)) continue;
      const next = build(cand.ids, null, cities, cur.transport, cur.rests);
      verifier.schedule(D, next, null, profile.origin, profile.return_home);
      const r = verifier.verify(D, next, profile);
      if (cmpScore(r.score(), curRep.score()) < 0) {
        if (strategy === "first") {
          best = { it: next, rep: r, sig: s, action: cand.action };
          break;
        }
        if (!best || cmpScore(r.score(), best.rep.score()) < 0) {
          best = { it: next, rep: r, sig: s, action: cand.action };
        }
      }
    }
    if (!best) break;
    cur = best.it;
    curRep = best.rep;
    visited.add(best.sig);
    rounds += 1;
    trace.push({
      round: rounds,
      action: best.action,
      hard: curRep.hard_failed,
      soft: curRep.soft_failed,
      csr: round4(curRep.csr),
      codes: [...new Set(curRep.violations.map((v) => v.code))].sort(),
    });
  }
  return { itinerary: cur, report: curRep, rounds, trace };
}