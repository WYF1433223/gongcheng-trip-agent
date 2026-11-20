// 一次性生成（baseline）：按偏好排序后顺序切片，不做空间与时间校验。
import { Day, Itinerary, Stop } from "./model.js";

function score(poi, profile) {
  let s = poi.rating * 2;
  const prefs = new Set(profile.prefs);
  if (poi.tags.some((t) => prefs.has(t))) s += 6;
  if (profile.must_visit.includes(poi.id)) s += 20;
  if (profile.party === "家庭" && poi.tags.includes("亲子")) s += 4;
  if (profile.party === "朋友" && poi.tags.includes("夜景")) s += 2;
  if (profile.budget < 1500 && poi.price > 0) s -= poi.price / 20;
  return s;
}

export function rankPois(D, profile) {
  return D.pois
    .slice()
    .sort((a, b) => {
      const sa = score(a, profile), sb = score(b, profile);
      if (sa !== sb) return sb - sa;
      if (a.name === b.name) return 0;
      return a.name < b.name ? -1 : 1;
    })
    .map((p) => p.id);
}

export function planOnce(D, profile) {
  const ranked = rankPois(D, profile);
  const n = profile.stopsPerDay();
  const used = new Set();
  const days = [];
  const must = profile.must_visit.filter((m) => D.poiById.has(m));
  const byCity = new Map();
  for (const pid of ranked) {
    const c = D.cityOfPoi(pid);
    if (!byCity.has(c)) byCity.set(c, []);
    byCity.get(c).push(pid);
  }

  for (const [city, nd] of profile.cityPlan(D.keys[0])) {
    const pool = byCity.get(city) || [];
    const mustHere = must.filter((m) => D.cityOfPoi(m) === city);
    for (let d = 0; d < nd; d++) {
      const picks = [];
      for (const mid of mustHere) {
        if (!used.has(mid) && picks.length < n) {
          picks.push(mid);
          used.add(mid);
        }
      }
      for (const pid of pool) {
        if (picks.length >= n) break;
        if (!used.has(pid)) {
          picks.push(pid);
          used.add(pid);
        }
      }
      days.push(new Day(picks.map((pid) => new Stop(pid)), {}, city, {}, [], 0));
    }
  }
  return new Itinerary({
    days,
    hotel: profile.hotel,
    transport: profile.transport,
    rests: profile.rests,
  });
}