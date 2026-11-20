// 领域模型（对应 Python agent/model.py）
import { round4 } from "./util.js";

export const DEFAULT_RESTS = [
  { label: "午间休息", start: 11 * 60 + 30, end: 14 * 60, minutes: 90 },
  { label: "傍晚休息", start: 18 * 60, end: 20 * 60, minutes: 60 },
];

export const FOOD_PER_DAY = 120;

export class Profile {
  constructor(o = {}) {
    this.days = o.days ?? 3;
    this.budget = o.budget ?? 3000;
    this.party = o.party ?? "情侣";
    this.pace = o.pace ?? "normal";
    this.transport = o.transport ?? "transit";
    this.prefs = o.prefs ?? [];
    this.must_visit = o.must_visit ?? [];
    this.hotel = o.hotel ?? "";
    this.start_date = o.start_date ?? "";
    this.cities = o.cities ?? [];
    this.city_days = o.city_days ?? [];
    this.rests = (o.rests ?? DEFAULT_RESTS).map((r) => ({ ...r }));
    this.origin = o.origin ?? "";
    this.return_home = o.return_home ?? true;
  }

  stopsPerDay() {
    return { relaxed: 3, normal: 4, intense: 5 }[this.pace] ?? 4;
  }

  cityPlan(defaultKey) {
    if (!this.cities.length) return [[defaultKey, this.days]];
    const keys = this.cities.slice();
    let days = this.city_days.slice();
    if (days.length !== keys.length || days.reduce((a, b) => a + b, 0) !== this.days) {
      days = distribute(this.days, keys.length);
    }
    return keys.map((k, i) => [k, days[i]]).filter(([, d]) => d > 0);
  }

  startDt() {
    if (this.start_date) {
      const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(this.start_date);
      if (m) return Date.UTC(+m[1], +m[2] - 1, +m[3]);
    }
    const n = new Date();
    return Date.UTC(n.getFullYear(), n.getMonth(), n.getDate());
  }

  // Python weekday(): 周一=0
  weekdayOf(i) {
    const d = new Date(this.startDt() + i * 86400000);
    return (d.getUTCDay() + 6) % 7;
  }

  dateOf(i) {
    const d = new Date(this.startDt() + i * 86400000);
    return d.toISOString().slice(0, 10);
  }

  toDict() {
    return {
      days: this.days, budget: this.budget, party: this.party, pace: this.pace,
      transport: this.transport, prefs: this.prefs, must_visit: this.must_visit,
      hotel: this.hotel, cities: this.cities.slice(), city_days: this.city_days.slice(),
      rests: this.rests.map((r) => ({ ...r })), origin: this.origin,
      return_home: this.return_home,
    };
  }
}

export function distribute(total, n) {
  if (n <= 0) return [];
  const base = Math.floor(total / n);
  const rem = total % n;
  let days = [];
  for (let i = 0; i < n; i++) days.push(base + (i < rem ? 1 : 0));
  if (base === 0) {
    days = [];
    for (let i = 0; i < total; i++) days.push(1);
    for (let i = 0; i < n - total; i++) days.push(0);
  }
  return days;
}

export class Stop {
  constructor(poiId) {
    this.poi_id = poiId;
    this.arrive = 0;
    this.depart = 0;
    this.from_prev = 0;
    this.wait = 0;
    this.via = {};
  }
  clone() {
    const s = new Stop(this.poi_id);
    s.arrive = this.arrive;
    s.depart = this.depart;
    s.from_prev = this.from_prev;
    s.wait = this.wait;
    s.via = { ...this.via };
    return s;
  }
  toDict() {
    return {
      poi_id: this.poi_id, arrive: this.arrive, depart: this.depart,
      from_prev: this.from_prev, wait: this.wait, via: this.via,
    };
  }
}

export class Day {
  constructor(stops = [], weather = {}, city = "", leg = {}, restDone = [], lastLeave = 0) {
    this.stops = stops;
    this.weather = weather;
    this.city = city;
    this.leg = leg;
    this.rest_done = restDone;
    this.last_leave = lastLeave;
  }
  restTotal(rests) {
    let sum = 0;
    rests.forEach((r, i) => {
      if (i < this.rest_done.length && this.rest_done[i]) sum += r.minutes;
    });
    return sum;
  }
  clone() {
    return new Day(
      this.stops.map((s) => s.clone()),
      { ...this.weather },
      this.city,
      { ...this.leg },
      this.rest_done.slice(),
      this.last_leave
    );
  }
  toDict(cityName) {
    return {
      weather: this.weather, city: this.city, leg: this.leg,
      rest_done: this.rest_done.slice(), city_name: cityName ?? this.city,
      stops: this.stops.map((s) => s.toDict()),
    };
  }
}

export class Itinerary {
  constructor(o = {}) {
    this.days = o.days ?? [];
    this.hotel = o.hotel ?? "";
    this.transport = o.transport ?? "transit";
    this.rests = (o.rests ?? DEFAULT_RESTS).map((r) => ({ ...r }));
    this.origin_leg = o.origin_leg ?? {};
    this.return_leg = o.return_leg ?? {};
  }
  clone() {
    return new Itinerary({
      days: this.days.map((d) => d.clone()),
      hotel: this.hotel,
      transport: this.transport,
      rests: this.rests,
      origin_leg: { ...this.origin_leg },
      return_leg: { ...this.return_leg },
    });
  }
  ids() {
    const out = [];
    for (const d of this.days) for (const s of d.stops) out.push(s.poi_id);
    return out;
  }
  toDict(cityNameOf) {
    return {
      hotel: this.hotel, transport: this.transport,
      origin_leg: this.origin_leg, return_leg: this.return_leg,
      days: this.days.map((d) => d.toDict(cityNameOf ? cityNameOf(d.city) : d.city)),
    };
  }
}

export class Violation {
  constructor(code, severity, day, poiId, message, fix = "") {
    this.code = code;
    this.severity = severity;
    this.day = day;
    this.poi_id = poiId;
    this.message = message;
    this.fix = fix;
  }
  key() {
    return `${this.code}|${this.day}|${this.poi_id}`;
  }
  toDict() {
    return {
      code: this.code, severity: this.severity, day: this.day,
      poi_id: this.poi_id, message: this.message, fix: this.fix,
    };
  }
}

export class Report {
  constructor(o = {}) {
    this.violations = o.violations ?? [];
    this.hard_total = o.hard_total ?? 0;
    this.hard_failed = o.hard_failed ?? 0;
    this.soft_total = o.soft_total ?? 0;
    this.soft_failed = o.soft_failed ?? 0;
  }
  get hard() {
    return this.violations.filter((v) => v.severity === "hard");
  }
  get soft() {
    return this.violations.filter((v) => v.severity === "soft");
  }
  get csr() {
    if (this.hard_total === 0) return 1.0;
    return Math.max(0.0, 1 - this.hard_failed / this.hard_total);
  }
  get feasible() {
    return this.hard_failed === 0;
  }
  score() {
    return [this.hard_failed, this.soft_failed, this.violations.length];
  }
  toDict() {
    return {
      violations: this.violations.map((v) => v.toDict()),
      hard_total: this.hard_total, hard_failed: this.hard_failed,
      soft_total: this.soft_total, soft_failed: this.soft_failed,
      csr: round4(this.csr), feasible: this.feasible,
    };
  }
}

export function cmpScore(a, b) {
  // 字典序比较，返回 -1/0/1
  for (let i = 0; i < a.length; i++) {
    if (a[i] !== b[i]) return a[i] < b[i] ? -1 : 1;
  }
  return 0;
}

export class Metrics {
  constructor(o = {}) {
    this.cost = o.cost ?? 0;
    this.pref_score = o.pref_score ?? 0;
    this.tool_calls = o.tool_calls ?? 0;
    this.repair_rounds = o.repair_rounds ?? 0;
    this.latency_ms = o.latency_ms ?? 0;
    this.stops = o.stops ?? 0;
  }
  toDict() {
    return {
      cost: this.cost, pref_score: round4(this.pref_score),
      tool_calls: this.tool_calls, repair_rounds: this.repair_rounds,
      latency_ms: this.latency_ms, stops: this.stops,
    };
  }
}

export class RunResult {
  constructor(o = {}) {
    this.mode = o.mode;
    this.profile = o.profile;
    this.itinerary = o.itinerary;
    this.report = o.report;
    this.metrics = o.metrics;
    this.trace = o.trace ?? [];
  }
}