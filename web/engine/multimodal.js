// 单段出行的「最佳方案」推荐（对应 Python agent/multimodal.py）。
import { iround } from "./util.js";

export const TIME_COST = 1.82;
export const COMBO_MIN_KM = 3.5;
export const FEEDER_MINUTES = 7;
export const FEEDER_COST = 12;
export const LAST_WALK = 4;

function seg(mode, text, minutes, cost) {
  return { mode, text, minutes: Math.trunc(minutes), cost: Math.trunc(cost) };
}

function wrap(segs, headline, why) {
  let mins = 0, cost = 0;
  for (const s of segs) { mins += s.minutes; cost += s.cost; }
  return { segments: segs, minutes: Math.trunc(mins), cost: Math.trunc(cost), headline, why };
}

function single(D, mode, km, minutes) {
  if (mode === "walk") {
    return wrap([seg("walk", "步行 " + minutes + " 分钟", minutes, 0)], "步行 · 免费", "步行距离内，不用等车");
  }
  if (mode === "taxi") {
    const fare = D.taxiFare(km);
    return wrap([seg("taxi", "打车 " + minutes + " 分钟", minutes, fare)],
      "打车 " + minutes + " 分钟 · ¥" + fare, "最快，费用较高");
  }
  const fare = D.metroFare(km);
  return wrap([seg("transit", "公交/地铁 " + minutes + " 分钟", minutes, fare)],
    "公交/地铁 " + minutes + " 分钟 · ¥" + fare, "不用打车，时间较稳");
}

function combo(D, km, transitMin) {
  const ride = Math.max(6, iround((transitMin - 10) * 0.78));
  const fare = D.metroFare(km);
  const segs = [
    seg("taxi", "打车到地铁站 " + FEEDER_MINUTES + " 分钟", FEEDER_MINUTES, FEEDER_COST),
    seg("metro", "地铁/轨道 " + ride + " 分钟", ride, fare),
    seg("walk", "出站步行 " + LAST_WALK + " 分钟", LAST_WALK, 0),
  ];
  return wrap(segs,
    "打车 " + FEEDER_MINUTES + " 分 + 轨道 " + ride + " 分 + 步行 " + LAST_WALK + " 分 · ¥" + (FEEDER_COST + fare),
    "打车接驳省掉等车，轨道避开路面拥堵");
}

function score(plan) {
  return plan.minutes + plan.cost * TIME_COST;
}

export function plan(D, aId, bId, mode = "auto") {
  if (aId === bId) return wrap([], "原地", "");
  const km = D.idKm(aId, bId);
  if (mode === "walk" || mode === "transit" || mode === "taxi") {
    return single(D, mode, km, D.idMinutes(aId, bId, mode));
  }
  const walkMin = D.idMinutes(aId, bId, "walk");
  const busMin = D.idMinutes(aId, bId, "transit");
  const taxiMin = D.idMinutes(aId, bId, "taxi");
  const options = [];
  if (km <= 3.0 || walkMin <= 35) options.push(single(D, "walk", km, walkMin));
  options.push(single(D, "transit", km, busMin));
  options.push(single(D, "taxi", km, taxiMin));
  if (km >= COMBO_MIN_KM && busMin > 20) options.push(combo(D, km, busMin));

  let best = options[0];
  for (let i = 1; i < options.length; i++) {
    if (score(options[i]) < score(best) - 1e-9) best = options[i];
  }
  const rest = options.filter((o) => o !== best);
  if (rest.length) {
    let alt = rest[0];
    for (const o of rest) if (score(o) < score(alt)) alt = o;
    const dm = best.minutes - alt.minutes;
    const dc = best.cost - alt.cost;
    const bits = [];
    if (dm < 0) bits.push("比「" + alt.headline + "」快 " + Math.abs(dm) + " 分钟");
    else if (dm > 0) bits.push("比「" + alt.headline + "」慢 " + dm + " 分钟");
    if (dc < 0) bits.push("省 ¥" + Math.abs(dc));
    else if (dc > 0) bits.push("多花 ¥" + dc);
    if (bits.length) {
      best = { ...best };
      best.why = (best.why ? best.why + "；" : "") + bits.join("、");
    }
  }
  return best;
}