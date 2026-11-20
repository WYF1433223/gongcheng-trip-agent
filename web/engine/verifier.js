// 排程与约束校验（对应 Python agent/verifier.py）。
import * as multimodal from "./multimodal.js";
import * as rail from "./rail.js";
import { Day, Itinerary, Report, Stop, Violation, FOOD_PER_DAY } from "./model.js";
import { fmt } from "./util.js";

export const DAY_START = 8 * 60 + 30;
export const REST_EARLY = 30;
export const DAY_END = 22 * 60;
export const MAX_WAIT = 90;

const WEEKDAY_CN = ["一", "二", "三", "四", "五", "六", "日"];

export function windowOf(poi) {
  let open = poi.open_min;
  let close = poi.close_min;
  if (close <= open) close += 1440;
  return [open, close];
}

export function scheduleDay(D, day, hotel, dayIndex, start = DAY_START, mode = "transit", rests = null) {
  const rs = rests || [{ label: "午间休息", start: 11 * 60 + 30, end: 14 * 60, minutes: 90 },
                       { label: "傍晚休息", start: 18 * 60, end: 20 * 60, minutes: 60 }];
  let t = start;
  day.rest_done = new Array(rs.length).fill(false);
  let prev = null;

  const takeRest = (now) => {
    for (let i = 0; i < rs.length; i++) {
      if (day.rest_done[i]) continue;
      if (rs[i].start - REST_EARLY <= now && now <= rs[i].end) {
        day.rest_done[i] = true;
        return rs[i].minutes;
      }
    }
    return 0;
  };

  for (const stop of day.stops) {
    const poi = D.poiById.get(stop.poi_id);
    const startId = prev === null ? "hotel:" + hotel : prev;
    const plan = multimodal.plan(D, startId, poi.id, mode);
    stop.via = plan;
    const travel = plan.minutes;
    let arrive = t + travel;
    const [open] = windowOf(poi);
    let wait = 0;
    if (open > DAY_START && arrive < open && open - arrive <= MAX_WAIT) {
      wait = open - arrive;
      arrive = open;
    }
    arrive += takeRest(arrive);
    stop.from_prev = travel;
    stop.wait = wait;
    stop.arrive = arrive;
    stop.depart = arrive + poi.duration;
    t = stop.depart;
    prev = poi.id;
  }

  for (let i = 0; i < rs.length; i++) {
    if (day.rest_done[i]) continue;
    if (t < rs[i].start - REST_EARLY) day.rest_done[i] = true;
    else if (rs[i].start - REST_EARLY <= t && t <= rs[i].end) {
      t += rs[i].minutes;
      day.rest_done[i] = true;
    }
  }
  day.last_leave = t;
  const ws = D.weatherList();
  if (ws && ws.length) day.weather = { ...ws[Math.min(dayIndex, ws.length - 1)] };
}

export function schedule(D, itinerary, hotel = null, origin = "", returnHome = true) {
  let prevCity = null;
  const seen = new Map();
  let firstCity = null;

  itinerary.days.forEach((day, i) => {
    let city = day.city || (prevCity !== null ? prevCity : (itinerary.days[0] ? itinerary.days[0].city : D.keys[0]));
    city = city || D.keys[0];
    day.city = city;
    if (firstCity === null) firstCity = city;
    let start;
    if (prevCity !== null && city !== prevCity) {
      const leg = rail.trip(D, prevCity, city, DAY_START);
      leg.hub_minutes = 30;
      day.leg = leg;
      start = leg.arr + 30;
    } else {
      day.leg = {};
      start = DAY_START;
    }
    const k = seen.get(city) || 0;
    seen.set(city, k + 1);
    day.weather = D.weatherAt(city, k);
    scheduleDay(D, day, hotel || D.hotelOf(city), i, start, itinerary.transport, itinerary.rests);
    prevCity = city;
  });

  itinerary.origin_leg = {};
  if (origin && firstCity && origin !== firstCity) {
    const go = rail.trip(D, origin, firstCity, 6 * 60 + 30);
    go.hub_minutes = 30;
    itinerary.origin_leg = go;
    if (itinerary.days.length) {
      const d0 = itinerary.days[0];
      d0.city = firstCity;
      d0.weather = D.weatherAt(firstCity, 0);
      scheduleDay(D, d0, hotel || D.hotelOf(firstCity), 0,
        Math.max(DAY_START, go.arr + 30), itinerary.transport, itinerary.rests);
    }
  }

  itinerary.return_leg = {};
  if (origin && firstCity && origin !== firstCity && returnHome && itinerary.days.length) {
    const last = itinerary.days[itinerary.days.length - 1];
    const lastLeave = last.stops.length ? last.last_leave : DAY_START;
    const back = rail.trip(D, last.city, origin, lastLeave + 45);
    back.hub_minutes = 30;
    itinerary.return_leg = back;
  }
  if (hotel) itinerary.hotel = hotel;
  return itinerary;
}

export function costOf(D, itinerary) {
  const days = itinerary.days.length;
  let tickets = 0;
  let ride = 0;
  for (const d of itinerary.days) {
    for (const s of d.stops) {
      tickets += D.poiById.get(s.poi_id).price;
      ride += (s.via || {}).cost || 0;
    }
  }
  let legs = 0;
  for (const d of itinerary.days) legs += (d.leg || {}).fare || 0;
  legs += (itinerary.origin_leg || {}).fare || 0;
  legs += (itinerary.return_leg || {}).fare || 0;
  return tickets + FOOD_PER_DAY * days + legs + ride;
}

export function intercityKm(itinerary) {
  let s = 0;
  for (const d of itinerary.days) s += (d.leg || {}).km || 0;
  return Math.round(s * 10) / 10;
}

export function prefScore(D, itinerary, profile) {
  const ids = itinerary.ids();
  if (!ids.length) return 0.0;
  const prefs = new Set(profile.prefs);
  if (!prefs.size) return 0.75;
  let hits = 0;
  for (const pid of ids) {
    const poi = D.poiById.get(pid);
    if (poi && poi.tags.some((t) => prefs.has(t))) hits += 1;
  }
  return hits / ids.length;
}

export function verify(D, itinerary, profile, startWeekday = null) {
  const vs = [];
  let hardTotal = 2 + 2 * itinerary.days.length;
  const seen = new Map();
  const baseWd = startWeekday !== null ? startWeekday : profile.weekdayOf(0);

  itinerary.days.forEach((day, di) => {
    const wd = (baseWd + di) % 7;
    if (!day.stops.length) {
      vs.push(new Violation("DAY_EMPTY", "hard", di, null, "第" + (di + 1) + "天没有任何行程"));
    }
    for (const stop of day.stops) {
      const poi = D.poiById.get(stop.poi_id);
      hardTotal += 3;
      if (seen.has(poi.id)) {
        vs.push(new Violation("DUPLICATE", "hard", di, poi.id,
          poi.name + " 在第" + (seen.get(poi.id) + 1) + "天已安排，第" + (di + 1) + "天重复", "删除重复项"));
      } else {
        seen.set(poi.id, di);
      }
      if (poi.closed_weekday !== null && poi.closed_weekday !== undefined && poi.closed_weekday === wd) {
        vs.push(new Violation("CLOSED_WEEKDAY", "hard", di, poi.id,
          poi.name + " 周" + WEEKDAY_CN[wd] + "闭馆", "换日或替换同类景点"));
      }
      const [open, close] = windowOf(poi);
      if (stop.arrive < open || stop.depart > close) {
        vs.push(new Violation("OUT_OF_WINDOW", "hard", di, poi.id,
          poi.name + " 安排 " + fmt(stop.arrive) + "–" + fmt(stop.depart) +
          "，开放 " + fmt(open) + "–" + fmt(close), "平移时段或换到适合的日期"));
      }
      if (stop.wait > 60) {
        vs.push(new Violation("LONG_WAIT", "soft", di, poi.id,
          poi.name + " 早到需等待 " + stop.wait + " 分钟", "调整游览顺序"));
      }
      const rain = (day.weather || {}).rain || 0;
      if (rain >= 60 && poi.outdoor) {
        vs.push(new Violation("RAIN_OUTDOOR", "soft", di, poi.id,
          poi.name + " 为户外景点，当天降雨概率 " + day.weather.rain + "%", "替换为室内景点或改到晴天"));
      }
    }
    if (day.stops.length > profile.stopsPerDay()) {
      vs.push(new Violation("PACE_EXCESS", "hard", di, null,
        "第" + (di + 1) + "天安排 " + day.stops.length + " 站，超过「" + profile.pace + "」节奏上限 " +
        profile.stopsPerDay() + " 站", "移出或删除部分站点"));
    }
    if (day.stops.length && day.stops[day.stops.length - 1].depart > DAY_END) {
      const last = day.stops[day.stops.length - 1];
      vs.push(new Violation("DAY_OVERRUN", "hard", di, last.poi_id,
        "第" + (di + 1) + "天 " + fmt(last.depart) + " 才结束，超过 22:00", "减少站点或前移"));
    }
    if (day.stops.length) {
      itinerary.rests.forEach((r, ri) => {
        const done = ri < day.rest_done.length && day.rest_done[ri];
        if (done) return;
        vs.push(new Violation("REST_MISSING", "soft", di, null,
          "第" + (di + 1) + "天未安排" + r.label + "（" + fmt(r.start) + "–" + fmt(r.end) +
          " 至少 " + r.minutes + " 分钟）", "在休息窗口内留出空档，或减少当天景点"));
      });
    }
  });

  const back = itinerary.return_leg || {};
  const last = itinerary.days.length ? itinerary.days[itinerary.days.length - 1] : null;
  if (back && back.dep !== undefined) {
    const leave = last && last.stops.length ? last.last_leave : DAY_START;
    if (back.next_day || back.dep <= leave) {
      vs.push(new Violation("RETURN_LATE", "soft", itinerary.days.length - 1, null,
        "第" + itinerary.days.length + "天 " + fmt(leave) + " 才结束，赶不上当天返程，改乘次日 " +
        fmt(back.dep) + " 的车", "提前收尾或减少末日的景点"));
    } else if (back.arr > 23 * 60) {
      vs.push(new Violation("RETURN_LATE", "soft", itinerary.days.length - 1, null,
        "返程 " + fmt(back.arr) + " 才到" + back.to_name + "，已过 23:00", "选更早的返程车次"));
    }
  }

  const cost = costOf(D, itinerary);
  if (cost > profile.budget) {
    vs.push(new Violation("BUDGET_EXCEEDED", "hard", null, null,
      "预计花费 " + cost + " 元，超出预算 " + profile.budget + " 元", "替换高价门票或减少付费景点"));
  }

  const ids = new Set(itinerary.ids());
  for (const mid of profile.must_visit) {
    if (!ids.has(mid)) {
      const poi = D.poiById.get(mid);
      vs.push(new Violation("MISSING_MUST_VISIT", "hard", null, mid,
        "必去点 " + (poi ? poi.name : mid) + " 未安排", "插入行程"));
    }
  }

  if (profile.prefs.length && prefScore(D, itinerary, profile) < 0.4) {
    vs.push(new Violation("PREF_LOW", "soft", null, null,
      "偏好匹配率仅 " + Math.round(prefScore(D, itinerary, profile) * 100) + "%", "增加偏好标签匹配的景点"));
  }

  return new Report({
    violations: vs,
    hard_total: hardTotal,
    hard_failed: vs.filter((v) => v.severity === "hard").length,
    soft_total: vs.filter((v) => v.severity === "soft").length,
    soft_failed: vs.filter((v) => v.severity === "soft").length,
  });
}

export { Day, Itinerary, Stop };