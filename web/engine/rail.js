// 高铁班次推算（对应 Python agent/rail.py）：确定性伪车次，不是实时余票。
import { round1 } from "./util.js";

export const FIRST_DEP = 6 * 60 + 40;
export const LAST_DEP = 21 * 60 + 30;
export const HUB_MINUTES = 45;
const CHECK_IN = 25;
const STATION_SUFFIX = ["", "东", "西", "南", "北"];

export function seed(text) {
  let s = 0;
  for (const ch of text) {
    s = (s * 31 + ch.codePointAt(0)) & 0x7fffffff;
  }
  return s;
}

function step(km) {
  if (km < 180) return 30;
  if (km < 500) return 45;
  if (km < 1000) return 60;
  return 75;
}

function station(D, key, prefix = "") {
  const name = D.cityName(key);
  const suf = STATION_SUFFIX[seed(prefix + key) % STATION_SUFFIX.length];
  return name + suf + "站";
}

export function trip(D, aKey, bKey, notBefore) {
  if (!aKey || !bKey || aKey === bKey) return null;
  const [minutes, fare, km] = D.intercity(aKey, bKey);
  const run = Math.max(35, Math.trunc(minutes) - HUB_MINUTES);
  const st = step(km);
  const earliest = Math.trunc(notBefore) + CHECK_IN;
  const first = FIRST_DEP + (seed(aKey + ">" + bKey) % st);
  let dep;
  if (earliest <= first) {
    dep = first;
  } else {
    const k = Math.ceil((earliest - first) / st);
    dep = first + k * st;
  }
  let nextDay = false;
  if (dep > LAST_DEP) {
    dep = first;
    nextDay = true;
  }
  const code = "G" + (100 + (seed(bKey + "<" + aKey) % 8900));
  return {
    from: aKey, from_name: D.cityName(aKey),
    to: bKey, to_name: D.cityName(bKey),
    code,
    dep_station: station(D, aKey, "dep"),
    arr_station: station(D, bKey, "arr"),
    dep: Math.trunc(dep), arr: Math.trunc(dep + run), run: Math.trunc(run),
    km: round1(km), fare: Math.trunc(fare),
    hub_minutes: HUB_MINUTES, next_day: nextDay,
  };
}

export function firstDeparture(D, aKey, bKey) {
  return trip(D, aKey, bKey, FIRST_DEP - CHECK_IN);
}

export function lastDeparture(D, aKey, bKey) {
  const t = trip(D, aKey, bKey, LAST_DEP - CHECK_IN);
  if (t && t.dep <= LAST_DEP) return t;
  return trip(D, aKey, bKey, FIRST_DEP - CHECK_IN);
}