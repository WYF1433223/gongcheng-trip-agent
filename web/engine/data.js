// 数据层（对应 Python agent/data.py）：城市规格、POI、住宿区、路网矩阵、通勤时间。
import { iround, round1 } from "./util.js";

const DAY_MIN = 1440;

export const TRANSIT_CITY_KM = 10.0;
export const TRANSIT_CITY_SPEED = 20.0;
export const TRANSIT_FAST_SPEED = 32.0;
const MODE_FACTOR = { walk: 1.7, taxi: 0.7, transit: 1.0 };

export const PREF_TAGS = ["历史人文", "自然风光", "美食", "购物", "艺术展馆", "夜景", "亲子"];
export const TRANSPORTS = { walk: "步行", transit: "公交/地铁", taxi: "打车", auto: "自动推荐" };

export class Data {
  constructor() {
    this.specs = new Map();      // key -> raw spec
    this.routes = new Map();     // key -> {index, foot, drive, dist}
    this.directory = [];         // [{name, cities:[{key,name,province,lat,lng,has_data}]}]
    this.dirPos = new Map();     // key/中文名 -> [lat, lng]
    this.dirName = new Map();    // key -> 中文名
    this.weatherByCity = new Map();
    this.weatherSrcByCity = new Map();

    this.pois = [];
    this.poiById = new Map();
    this.poiCity = new Map();
    this.cityMeta = new Map();
    this.keys = [];
    this.weatherGlobal = [];
    this.weatherOverride = null;
    this.weatherSource = "内置演示数据";

    // 兼容旧全局变量：远郊参数只跟首个城市同步（与 Python 实现一致）
    this.farIds = new Set();
    this.farMinutes = 0;
    this.farBoth = 0;
    this.farKm = null;
  }

  setDirectory(dir) {
    this.directory = dir || [];
    for (const prov of this.directory) {
      for (const c of prov.cities) {
        if (c.key) {
          this.dirPos.set(c.key, [c.lat, c.lng]);
          this.dirName.set(c.key, c.name);
        }
        this.dirPos.set(c.name, [c.lat, c.lng]);
        this.dirName.set(c.name, c.name);
      }
    }
  }

  addCity(key, spec, route) {
    if (spec) this.specs.set(key, spec);
    if (route) this.routes.set(key, this._routeTable(route));
  }

  _routeTable(raw) {
    const index = new Map();
    raw.nodes.forEach((n, i) => index.set(n, i));
    return {
      index,
      foot: raw.foot.duration,
      drive: raw.drive.duration,
      dist: raw.drive.distance,
    };
  }

  hasCity(key) {
    return this.specs.has(key);
  }

  useCities(keys) {
    const ks = (keys || []).filter(Boolean);
    if (!ks.length) throw new Error("没有可用城市");
    this.keys = ks.slice();
    this.pois = [];
    this.poiById.clear();
    this.poiCity.clear();
    this.cityMeta.clear();
    for (const key of ks) {
      const spec = this.specs.get(key);
      if (!spec) throw new Error("未收录的城市：" + key);
      const hotels = {};
      for (const [zone, ll] of Object.entries(spec.hotels || {})) hotels[zone] = ll.slice();
      const cityPois = (spec.pois || []).map((p) => ({
        id: p.id, name: p.name, district: p.district,
        lat: p.lat, lng: p.lng,
        open_min: p.open_min, close_min: p.close_min,
        duration: p.duration, price: p.price, rating: p.rating,
        tags: p.tags || [], outdoor: !!p.outdoor,
        closed_weekday: p.closed_weekday ?? null,
      }));
      this.pois.push(...cityPois);
      for (const p of cityPois) this.poiCity.set(p.id, key);
      this.cityMeta.set(key, {
        key,
        name: spec.name,
        center: (spec.center || [0, 0]).slice(),
        hotels,
        default_hotel: Object.keys(hotels)[0] || "市中心",
        weather: (spec.weather || []).slice(),
        far_ids: new Set(spec.far_ids || []),
        far_minutes: Number(spec.far_minutes || 0),
        far_both_minutes: Number(spec.far_both_minutes || 0),
        far_km: spec.far_km ?? null,
      });
    }
    for (const p of this.pois) this.poiById.set(p.id, p);
    const meta = this.cityMeta.get(ks[0]);
    this.weatherGlobal = meta.weather;
    this._syncFar(ks[0]);
    return ks;
  }

  _syncFar(key) {
    const m = this.cityMeta.get(key) || {};
    this.farIds = new Set(m.far_ids || []);
    this.farMinutes = Number(m.far_minutes || 0);
    this.farBoth = Number(m.far_both_minutes || 0);
    this.farKm = m.far_km ?? null;
  }

  cityOfPoi(pid) {
    return this.poiCity.get(pid) || this.keys[0];
  }

  hotelOf(city) {
    const m = this.cityMeta.get(city);
    return m ? m.default_hotel : "";
  }

  hotelsOf(city) {
    const m = this.cityMeta.get(city);
    return m ? m.hotels : {};
  }

  cityName(key) {
    const m = this.cityMeta.get(key);
    if (m) return m.name;
    if (this.dirName.has(key)) return this.dirName.get(key);
    const spec = this.specs.get(key);
    if (spec) return spec.name || key;
    return key;
  }

  cityPosition(key) {
    const m = this.cityMeta.get(key);
    if (m) return m.center.slice();
    if (this.dirPos.has(key)) return this.dirPos.get(key).slice();
    const spec = this.specs.get(key);
    if (spec && spec.center) return spec.center.slice();
    return null;
  }

  // ---- 天气 ----
  setWeatherFor(key, days, source = "真实预报") {
    if (!days || !days.length) return;
    this.weatherByCity.set(key, days.map((w) => ({ ...w })));
    this.weatherSrcByCity.set(key, source);
    if (this.keys.length && key === this.keys[0]) {
      this.weatherOverride = days.map((w) => ({ ...w }));
      this.weatherSource = source;
    }
  }

  clearWeather() {
    this.weatherByCity.clear();
    this.weatherSrcByCity.clear();
    this.weatherOverride = null;
  }

  weatherList() {
    return this.weatherOverride || this.weatherGlobal;
  }

  weatherAt(key, idx) {
    const first = this.keys[0];
    let seq = this.weatherByCity.get(key);
    if (!seq && key === first && this.weatherOverride) seq = this.weatherOverride;
    if (!seq) {
      const m = this.cityMeta.get(key);
      seq = (m && m.weather && m.weather.length ? m.weather : this.weatherGlobal);
    }
    if (!seq || !seq.length) return {};
    const item = { ...seq[idx % seq.length] };
    item.day = "第" + (idx + 1) + "天";
    return item;
  }

  // ---- 几何 ----
  haversine(a, b) {
    const r = 6371.0;
    const rad = Math.PI / 180;
    const lat1 = a[0] * rad, lng1 = a[1] * rad;
    const lat2 = b[0] * rad, lng2 = b[1] * rad;
    const dlat = lat2 - lat1, dlng = lng2 - lng1;
    const h = Math.sin(dlat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dlng / 2) ** 2;
    return 2 * r * Math.asin(Math.sqrt(h));
  }

  intercity(aKey, bKey) {
    if (aKey === bKey) return [0, 0, 0.0];
    const ca = this.cityPosition(aKey);
    const cb = this.cityPosition(bKey);
    if (!ca || !cb) return [120, 120, 300.0];
    const km = this.haversine(ca, cb);
    const minutes = iround(45 + km / 2.6);
    const fare = iround(km * 0.45 + 10);
    return [minutes, fare, km];
  }

  routeTable(cityKey) {
    return this.routes.get(cityKey) || null;
  }

  routeKm(cityKey, aId, bId) {
    const tab = this.routeTable(cityKey);
    if (!tab) return null;
    const i = tab.index.get(aId), j = tab.index.get(bId);
    if (i === undefined || j === undefined) return null;
    const m = tab.dist[i][j];
    return m === null || m === undefined ? null : round1(m / 1000.0);
  }

  _matrixMinutes(cityKey, aId, bId, mode) {
    const tab = this.routeTable(cityKey);
    if (!tab) return null;
    const i = tab.index.get(aId), j = tab.index.get(bId);
    if (i === undefined || j === undefined || i === j) return null;
    if (mode === "walk") {
      const sec = tab.foot[i][j];
      if (sec === null || sec === undefined) return null;
      return Math.max(2, iround(sec / 60.0));
    }
    const dist = tab.dist[i][j];
    if (dist === null || dist === undefined) return null;
    const km = dist / 1000.0;
    if (mode === "taxi") {
      const sec = tab.drive[i][j] || (km / 26.0) * 3600.0;
      return Math.max(4, iround((sec * 1.35) / 60.0 + 3));
    }
    const ride = this._transitRide(km);
    return Math.max(8, iround(8 + ride + 2));
  }

  _transitRide(km) {
    const cityPart = (Math.min(km, TRANSIT_CITY_KM) / TRANSIT_CITY_SPEED) * 60.0;
    if (km <= TRANSIT_CITY_KM) return cityPart;
    return cityPart + ((km - TRANSIT_CITY_KM) / TRANSIT_FAST_SPEED) * 60.0;
  }

  _scale(minutes, mode) {
    return Math.max(1, iround(minutes * (MODE_FACTOR[mode] ?? 1.0)));
  }

  _rideMinutes(km, mode = "transit") {
    if (mode === "walk") return Math.max(3, iround(km * 12.5 + 1));
    if (mode === "taxi") return Math.max(5, iround(6 + km * 2.6));
    return Math.max(9, iround(14 + km * 3.1));
  }

  travelMinutes(aId, bId, mode = "transit") {
    if (aId === bId) return 0;
    const a = this.poiById.get(aId);
    const b = this.poiById.get(bId);
    const cityA = this.poiCity.get(aId) || this.keys[0];
    const cityB = this.poiCity.get(bId) || this.keys[0];
    if (cityA !== cityB) {
      return this.intercity(cityA, cityB)[0];
    }
    const real = this._matrixMinutes(cityA, aId, bId, mode);
    if (real !== null) return real;
    if (this.farIds.size) {
      const af = this.farIds.has(a.id), bf = this.farIds.has(b.id);
      if (af !== bf) return this._scale(this.farMinutes, mode);
      if (af && bf) return this._scale(this.farBoth, mode);
    }
    const km = this.haversine([a.lat, a.lng], [b.lat, b.lng]);
    if (this.farKm && km > this.farKm) return this._scale(iround(20 + km * 1.4), mode);
    return this._rideMinutes(km, mode);
  }

  hotelTravelMinutes(zone, poiId, mode = "transit") {
    const city = this.poiCity.get(poiId) || this.keys[0];
    const zones = this.hotelsOf(city);
    const z = Object.prototype.hasOwnProperty.call(zones, zone) ? zone : this.hotelOf(city);
    const ll = zones[z] || Object.values(zones)[0];
    const p = this.poiById.get(poiId);
    const real = this._matrixMinutes(city, "hotel:" + z, p.id, mode);
    if (real !== null) return real;
    const km = this.haversine([ll[0], ll[1]], [p.lat, p.lng]);
    if (this.farIds.has(p.id)) return this._scale(this.farMinutes, mode);
    if (this.farKm && km > this.farKm) return this._scale(iround(20 + km * 1.4), mode);
    return this._rideMinutes(km + 0.2, mode);
  }

  metroFare(km) {
    if (km <= 6) return 2;
    return Math.trunc(Math.min(8, 2 + Math.floor((km - 6) / 8) + 1));
  }

  taxiFare(km) {
    return Math.trunc(Math.max(13, iround(10 + Math.max(0.0, km - 3) * 2.6)));
  }

  coords(id) {
    if (id.startsWith("hotel:")) {
      const zone = id.slice(6);
      for (const key of this.keys) {
        const zones = this.hotelsOf(key);
        if (Object.prototype.hasOwnProperty.call(zones, zone)) {
          return [key, zones[zone][0], zones[zone][1], zone + "住宿区"];
        }
      }
      return [this.keys[0], 0.0, 0.0, zone];
    }
    const p = this.poiById.get(id);
    return [this.poiCity.get(id) || this.keys[0], p.lat, p.lng, p.name];
  }

  idKm(aId, bId) {
    const [ca, la, lna] = this.coords(aId);
    const [cb, lb, lnb] = this.coords(bId);
    if (ca !== cb) {
      const km = this.intercity(ca, cb)[2];
      return round1(km);
    }
    if (!aId.startsWith("hotel:") && !bId.startsWith("hotel:")) {
      const k = this.routeKm(ca, aId, bId);
      if (k !== null) return k;
    }
    return round1(this.haversine([la, lna], [lb, lnb]));
  }

  idMinutes(aId, bId, mode = "transit") {
    if (aId === bId) return 0;
    if (aId.startsWith("hotel:")) return this.hotelTravelMinutes(aId.slice(6), bId, mode);
    if (bId.startsWith("hotel:")) return this.hotelTravelMinutes(bId.slice(6), aId, mode);
    return this.travelMinutes(aId, bId, mode);
  }
}

export { DAY_MIN };