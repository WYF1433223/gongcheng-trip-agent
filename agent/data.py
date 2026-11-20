# -*- coding: utf-8 -*-
"""城市数据加载：POI、天气、住宿区域与通勤时间估算。

单城市数据放在 agent/cities/<city>.json；多城市行程时用 use_cities() 把
若干城市的 POI 合并到同一份视图里，并用 POI_CITY 记录每个点属于哪个城市。
省市目录见 geo_seed.py（全国 34 个省级行政区、339 个地级市）。
"""
from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple


@dataclass(frozen=True)
class Poi:
    id: str
    name: str
    district: str
    lat: float
    lng: float
    open_min: int          # 开放时间（分钟，自 00:00）
    close_min: int
    duration: int          # 建议游览时长（分钟）
    price: int             # 门票（元）
    rating: float
    tags: List[str]
    outdoor: bool = False
    closed_weekday: Optional[int] = None  # 0=周一


def hm(h: int, m: int = 0) -> int:
    return h * 60 + m


HERE = os.path.dirname(os.path.abspath(__file__))
CITY_DIR = os.path.join(HERE, "cities")
ROUTE_DIR = os.path.join(HERE, "routes")     # OSM/OSRM 生成的真实路网矩阵
CITY = os.environ.get("CITY", "chengdu").strip().lower() or "chengdu"

PREF_TAGS = ["历史人文", "自然风光", "美食", "购物", "艺术展馆", "夜景", "亲子"]

# 运行时容器（保持引用稳定，多城市时原地替换内容）
POIS: List[Poi] = []
POI_BY_ID: Dict[str, Poi] = {}
WEATHER: List[dict] = []
HOTEL_ZONES: Dict[str, tuple] = {}
DEFAULT_HOTEL = "市中心"
CITY_KEYS: List[str] = [CITY]
CITY_META: Dict[str, dict] = {}
POI_CITY: Dict[str, str] = {}

_FAR_IDS: set = set()
_FAR_MINUTES = 0
_FAR_BOTH = 0
_FAR_KM = None
_WEATHER_OVERRIDE: Optional[List[dict]] = None
WEATHER_SOURCE = "内置演示数据"

FOOD_PER_DAY = 120     # 人均餐饮估算
TRANSIT_PER_DAY = 40   # 市内交通估算（默认公交）
TRANSIT_COST = {"walk": 10, "transit": 40, "taxi": 140}   # 每日市内交通预算（元）

# 默认休息时段：白天小憩 90 分钟、傍晚 60 分钟（用户可在界面里改）
DEFAULT_RESTS = [
    {"label": "午间休息", "start": 11 * 60 + 30, "end": 14 * 60, "minutes": 90},
    {"label": "傍晚休息", "start": 18 * 60, "end": 20 * 60, "minutes": 60},
]
REST_LIMITS = {"minutes": (0, 180), "start": (6 * 60, 22 * 60), "end": (6 * 60, 23 * 60)}


def normalize_rests(raw) -> list:
    """把外部传入的休息时段整理成合法列表；非法项丢弃，缺省用内置默认值。"""
    out = []
    for item in (raw or []):
        try:
            st = int(item.get("start", 0))
            en = int(item.get("end", 0))
            mi = int(item.get("minutes", 0))
        except (TypeError, ValueError, AttributeError):
            continue
        if mi <= 0 or en <= st:
            continue
        lo, hi = REST_LIMITS["start"][0], REST_LIMITS["end"][1]
        st = max(lo, min(hi, st))
        en = max(lo, min(hi, en))
        if en <= st:
            continue
        mi = max(10, min(en - st, mi))
        out.append({"label": str(item.get("label", "休息")), "start": st,
                    "end": en, "minutes": mi})
    out.sort(key=lambda r: r["start"])
    return out or [dict(r) for r in DEFAULT_RESTS]
TRANSPORTS = {"walk": "步行", "transit": "公交/地铁", "taxi": "打车"}

CITY_FILE = os.path.join(CITY_DIR, f"{CITY}.json")


def _spec_path(key: str) -> str:
    return os.path.join(CITY_DIR, f"{key}.json")


_DIR_INDEX: Dict[str, tuple] = {}


def _dir_index() -> Dict[str, tuple]:
    """省—市目录索引：城市中文名 -> (纬度, 经度)。"""
    global _DIR_INDEX
    if not _DIR_INDEX:
        try:
            from .geo_seed import PROVINCES
            for prov in PROVINCES:
                for c in prov["cities"]:
                    _DIR_INDEX.setdefault(c["name"], (float(c["lat"]), float(c["lng"])))
        except Exception:  # pragma: no cover
            pass
    return _DIR_INDEX


def _load_spec(key: str) -> dict:
    """读城市数据；种子城市首次访问时落盘成 JSON，目录内其它城市走通用模板。"""
    path = _spec_path(key)
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    try:
        from .seed_cities import SEED, city_json
    except Exception:  # pragma: no cover
        SEED = {}
    if key not in SEED:
        pos = _dir_index().get(key)
        if pos:  # 通用模板城市：不落盘，按目录经纬度现场生成
            from .synth import build
            return build(key, key, pos[0], pos[1])
        raise ValueError(f"未收录的城市：{key}")
    spec = city_json(key)
    os.makedirs(CITY_DIR, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(spec, f, ensure_ascii=False, indent=1)
    return spec


def available_cities() -> List[str]:
    keys = set()
    if os.path.isdir(CITY_DIR):
        keys |= {f[:-5] for f in os.listdir(CITY_DIR)
                 if f.endswith(".json") and not f.startswith((".", "_"))}
    try:
        from .seed_cities import SEED
        keys |= set(SEED)
    except Exception:  # pragma: no cover
        pass
    return sorted(keys)


def _apply(specs: List[dict], keys: List[str]) -> None:
    """把若干城市的数据合并进模块级容器（就地替换，保持引用有效）。"""
    global CITY, CITY_NAME, CITY_CENTER, DEFAULT_HOTEL, CITY_KEYS
    pois: List[Poi] = []
    CITY_META.clear()
    POI_CITY.clear()
    for spec, key in zip(specs, keys):
        city_pois = [Poi(**item) for item in spec["pois"]]
        pois.extend(city_pois)
        for p in city_pois:
            POI_CITY[p.id] = key
        hotels = {k: tuple(v) for k, v in spec["hotels"].items()}
        CITY_META[key] = {
            "key": key,
            "name": spec["name"],
            "center": tuple(spec.get("center", [0.0, 0.0])),
            "hotels": hotels,
            "default_hotel": next(iter(hotels)),
            "weather": spec.get("weather") or [],
            "far_ids": set(spec.get("far_ids") or []),
            "far_minutes": int(spec.get("far_minutes") or 0),
            "far_both_minutes": int(spec.get("far_both_minutes") or 0),
            "far_km": spec.get("far_km"),
        }
    CITY_KEYS = list(keys)
    CITY = keys[0]
    CITY_NAME = CITY_META[keys[0]]["name"]
    CITY_CENTER = CITY_META[keys[0]]["center"]
    POIS[:] = pois
    POI_BY_ID.clear()
    POI_BY_ID.update({p.id: p for p in pois})
    HOTEL_ZONES.clear()
    HOTEL_ZONES.update(CITY_META[keys[0]]["hotels"])
    DEFAULT_HOTEL = CITY_META[keys[0]]["default_hotel"]
    WEATHER[:] = CITY_META[keys[0]]["weather"]
    _sync_legacy(keys[0])


def _sync_legacy(key: str) -> None:
    """把当前城市的远郊参数同步到旧模块级变量。"""
    global _FAR_IDS, _FAR_MINUTES, _FAR_BOTH, _FAR_KM
    meta = CITY_META.get(key) or {}
    _FAR_IDS = set(meta.get("far_ids") or [])
    _FAR_MINUTES = int(meta.get("far_minutes") or 0)
    _FAR_BOTH = int(meta.get("far_both_minutes") or 0)
    _FAR_KM = meta.get("far_km")


def use_cities(keys: List[str]) -> List[str]:
    """激活一组城市（顺序即行程顺序）。"""
    keys = [k for k in (keys or [CITY]) if k]
    if not keys:
        keys = ["chengdu"]
    specs = [_load_spec(k) for k in keys]
    _apply(specs, keys)
    return list(keys)


def use_city(name: str) -> str:
    """单城市切换（兼容旧接口）。"""
    global CITY_FILE
    name = (name or "").strip().lower()
    _apply([_load_spec(name)], [name])
    CITY_FILE = _spec_path(name)
    return name


def city_of_poi(pid: str) -> str:
    return POI_CITY.get(pid, CITY)


def hotel_of(city: str) -> str:
    meta = CITY_META.get(city)
    return meta["default_hotel"] if meta else DEFAULT_HOTEL


def hotels_of(city: str) -> Dict[str, tuple]:
    meta = CITY_META.get(city)
    return meta["hotels"] if meta else HOTEL_ZONES


_NAME_CACHE: Dict[str, str] = {}
_POS_CACHE: Dict[str, Optional[tuple]] = {}


def _spec_meta(key: str) -> dict:
    """未载入内存的城市，直接读它的 json 拿 name / center（带缓存）。"""
    path = _spec_path(key)
    if not os.path.exists(path):
        return {}
    try:
        with open(path, encoding="utf-8") as f:
            raw = json.load(f)
    except (OSError, ValueError):
        return {}
    _NAME_CACHE[key] = raw.get("name") or key
    c = raw.get("center")
    _POS_CACHE[key] = tuple(c) if c else None
    return raw


def city_name(key: str) -> str:
    meta = CITY_META.get(key)
    if meta:
        return meta["name"]
    if key in _NAME_CACHE:
        return _NAME_CACHE[key]
    try:
        from .seed_cities import SEED
        if key in SEED:
            return SEED[key][0]
    except Exception:  # pragma: no cover
        pass
    spec = _spec_meta(key)
    if spec:
        return spec.get("name") or key
    return key


WEATHER_BY_CITY: Dict[str, List[dict]] = {}
WEATHER_SRC_BY_CITY: Dict[str, str] = {}


def set_weather(days: List[dict], source: str = "真实预报") -> None:
    """用真实预报替换内置天气；daily 结构同 WEATHER。"""
    global _WEATHER_OVERRIDE, WEATHER_SOURCE
    if days:
        _WEATHER_OVERRIDE = list(days)
        WEATHER_SOURCE = source


def set_weather_for(key: str, days: List[dict], source: str = "真实预报") -> None:
    """给某个城市单独设预报（多城市行程时每个城市各取各的）。"""
    if not days:
        return
    WEATHER_BY_CITY[key] = [dict(w) for w in days]
    WEATHER_SRC_BY_CITY[key] = source
    if CITY_KEYS and key == CITY_KEYS[0]:
        set_weather(days, source)


def clear_weather() -> None:
    global _WEATHER_OVERRIDE
    WEATHER_BY_CITY.clear()
    WEATHER_SRC_BY_CITY.clear()
    _WEATHER_OVERRIDE = None


def weather_list() -> List[dict]:
    return _WEATHER_OVERRIDE or WEATHER


def weather_at(key: str, idx: int) -> dict:
    """取某城市第 idx 天的天气：优先该城市的真实预报，其次其内置模板。"""
    first = CITY_KEYS[0] if CITY_KEYS else CITY
    seq = WEATHER_BY_CITY.get(key) or (_WEATHER_OVERRIDE if key == first else None)
    if not seq:
        seq = (CITY_META.get(key) or {}).get("weather") or WEATHER
    if not seq:
        return {}
    item = dict(seq[idx % len(seq)])
    item["day"] = f"第{idx + 1}天"
    return item


def _haversine(a: tuple, b: tuple) -> float:
    r = 6371.0
    lat1, lng1 = math.radians(a[0]), math.radians(a[1])
    lat2, lng2 = math.radians(b[0]), math.radians(b[1])
    dlat, dlng = lat2 - lat1, lng2 - lng1
    h = math.sin(dlat / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin(dlng / 2) ** 2
    return 2 * r * math.asin(math.sqrt(h))


def intercity(a_key: str, b_key: str) -> Tuple[int, int, float]:
    """城际交通估算：返回（分钟，票价，公里数）。按高铁 + 市区接驳近似。"""
    if a_key == b_key:
        return 0, 0, 0.0
    ca = city_position(a_key)
    cb = city_position(b_key)
    if not ca or not cb:
        return 120, 120, 300.0
    km = _haversine(ca, cb)
    minutes = int(round(45 + km / 2.6))       # 45 分钟候车/接驳 + 平均 156km/h
    fare = int(round(km * 0.45 + 10))
    return minutes, fare, km


_ROUTE_TABLES: Dict[str, Optional[dict]] = {}


def _route_table(city_key: str) -> Optional[dict]:
    """读取某城市的真实路网矩阵（OSM/OSRM 生成）；没有则返回 None。"""
    if city_key in _ROUTE_TABLES:
        return _ROUTE_TABLES[city_key]
    tab = None
    path = os.path.join(ROUTE_DIR, f"{city_key}.json")
    if os.path.exists(path):
        try:
            with open(path, encoding="utf-8") as f:
                raw = json.load(f)
            tab = {
                "index": {n: i for i, n in enumerate(raw["nodes"])},
                "foot": raw["foot"]["duration"],
                "drive": raw["drive"]["duration"],
                "dist": raw["drive"]["distance"],
            }
        except (OSError, ValueError, KeyError):
            tab = None
    _ROUTE_TABLES[city_key] = tab
    return tab


def route_km(city_key: str, a_id: str, b_id: str) -> Optional[float]:
    """真实驾车距离（公里），来自路网矩阵。"""
    tab = _route_table(city_key)
    if not tab:
        return None
    i, j = tab["index"].get(a_id), tab["index"].get(b_id)
    if i is None or j is None:
        return None
    m = tab["dist"][i][j]
    return None if m is None else round(m / 1000.0, 1)


def _matrix_minutes(city_key: str, a_id: str, b_id: str, mode: str) -> Optional[int]:
    """用真实路网求解同城出行分钟数；缺数据返回 None 由估算兜底。

    * 步行：OSM 步行路网实测耗时
    * 打车：OSM 驾车路网自由流耗时 ×1.35（城市拥堵系数）+ 3 分钟叫车
    * 公交/地铁：驾车距离 / 20km/h（含停站）+ 8 分钟步行到站与候车 + 2 分钟换乘
    """
    tab = _route_table(city_key)
    if not tab:
        return None
    i, j = tab["index"].get(a_id), tab["index"].get(b_id)
    if i is None or j is None or i == j:
        return None
    if mode == "walk":
        sec = tab["foot"][i][j]
        if sec is None:
            return None
        return max(2, int(round(sec / 60.0)))
    dist = tab["dist"][i][j]
    if dist is None:
        return None
    km = dist / 1000.0
    if mode == "taxi":
        sec = tab["drive"][i][j] or (km / 26.0 * 3600.0)
        return max(4, int(round(sec * 1.35 / 60.0 + 3)))
    # 公交/地铁：市区段按 20km/h，超过 10km 后按 32km/h（快速路、地铁、近郊线）
    ride = _transit_ride(km)
    return max(8, int(round(8 + ride + 2)))


_MODE_FACTOR = {"walk": 1.70, "taxi": 0.70, "transit": 1.00}


def _scale(minutes: int, mode: str) -> int:
    """远郊/长途段的固定分钟数按交通方式缩放（步行更慢、打车更快）。"""
    return max(1, int(round(minutes * _MODE_FACTOR.get(mode, 1.0))))


TRANSIT_CITY_KM = 10.0     # 市区段
TRANSIT_CITY_SPEED = 20.0  # km/h，含停站
TRANSIT_FAST_SPEED = 32.0  # km/h，超过 10km 后的快速段


def _transit_ride(km: float) -> float:
    """公交/地铁在车时间：市区 20km/h，长途段 32km/h。"""
    city_part = min(km, TRANSIT_CITY_KM) / TRANSIT_CITY_SPEED * 60.0
    if km <= TRANSIT_CITY_KM:
        return city_part
    return city_part + (km - TRANSIT_CITY_KM) / TRANSIT_FAST_SPEED * 60.0


def _ride_minutes(km: float, mode: str = "transit") -> int:
    """按交通方式估算同城移动时间，已计入等车、换乘与步行接驳。"""
    if mode == "walk":
        return max(3, int(round(km * 12.5 + 1)))      # 4.8 km/h
    if mode == "taxi":
        return max(5, int(round(6 + km * 2.6)))       # 等车 6 分钟 + 23 km/h
    return max(9, int(round(14 + km * 3.1)))          # 候车换乘 14 分钟 + 19 km/h


def travel_minutes(a_id: str, b_id: str, mode: str = "transit") -> int:
    """mock 地图 API：按直线距离换算地铁/步行时间，远郊点按跨城交通估算。"""
    if a_id == b_id:
        return 0
    a = POI_BY_ID[a_id]
    b = POI_BY_ID[b_id]
    if POI_CITY.get(a_id) != POI_CITY.get(b_id):
        m, _fare, _km = intercity(POI_CITY.get(a_id, CITY), POI_CITY.get(b_id, CITY))
        return m
    real = _matrix_minutes(POI_CITY.get(a_id, CITY), a_id, b_id, mode)
    if real is not None:
        return real
    far = _FAR_IDS
    if far:
        if (a.id in far) ^ (b.id in far):
            return _scale(_FAR_MINUTES, mode)
        if a.id in far and b.id in far:
            return _scale(_FAR_BOTH, mode)
    km = _haversine((a.lat, a.lng), (b.lat, b.lng))
    if _FAR_KM and km > _FAR_KM:
        return _scale(int(round(20 + km * 1.4)), mode)
    return _ride_minutes(km, mode)


def hotel_travel_minutes(zone: str, poi_id: str, mode: str = "transit") -> int:
    city = POI_CITY.get(poi_id, CITY)
    zones = hotels_of(city)
    zone = zone if zone in zones else hotel_of(city)
    lat, lng = zones.get(zone, next(iter(zones.values())))
    p = POI_BY_ID[poi_id]
    real = _matrix_minutes(city, f"hotel:{zone}", p.id, mode)
    if real is not None:
        return real
    km = _haversine((lat, lng), (p.lat, p.lng))
    if p.id in _FAR_IDS:
        return _scale(_FAR_MINUTES, mode)
    if _FAR_KM and km > _FAR_KM:
        return _scale(int(round(20 + km * 1.4)), mode)
    return _ride_minutes(km + 0.2, mode)


def metro_fare(km: float) -> int:
    """公交/地铁票价（元）：2 元起步，按里程阶梯，上限 8 元。"""
    if km <= 6:
        return 2
    return int(min(8, 2 + (km - 6) // 8 + 1))


def taxi_fare(km: float) -> int:
    """出租车费（元）：10 元起步（含 3km）+ 2.6 元/km。"""
    return int(max(13, round(10 + max(0.0, km - 3) * 2.6)))


def _coords(a_id: str):
    """POI id 或 hotel:<区名> → (城市 key, lat, lng, 显示名)。"""
    if a_id.startswith("hotel:"):
        zone = a_id[6:]
        for key in CITY_KEYS or [CITY]:
            zones = hotels_of(key)
            if zone in zones:
                lat, lng = zones[zone]
                return key, lat, lng, f"{zone}住宿区"
        return CITY, 0.0, 0.0, zone
    p = POI_BY_ID[a_id]
    return POI_CITY.get(a_id, CITY), p.lat, p.lng, p.name


def id_km(a_id: str, b_id: str) -> float:
    """两点之间的真实驾车距离（公里）；跨城返回城际直线距离。"""
    ca, la, ln_a, _ = _coords(a_id)
    cb, lb, ln_b, _ = _coords(b_id)
    if ca != cb:
        _m, _f, km = intercity(ca, cb)
        return round(km, 1)
    if not a_id.startswith("hotel:") and not b_id.startswith("hotel:"):
        k = route_km(ca, a_id, b_id)
        if k is not None:
            return k
    return round(_haversine((la, ln_a), (lb, ln_b)), 1)


def id_minutes(a_id: str, b_id: str, mode: str = "transit") -> int:
    """任意两点（含住宿区）按交通方式的移动分钟数。"""
    if a_id == b_id:
        return 0
    if a_id.startswith("hotel:"):
        return hotel_travel_minutes(a_id[6:], b_id, mode)
    if b_id.startswith("hotel:"):
        return hotel_travel_minutes(b_id[6:], a_id, mode)
    return travel_minutes(a_id, b_id, mode)


_NAME_TO_KEY: Dict[str, str] = {}
_FILE_INDEX: Dict[str, str] = {}


def _file_index() -> Dict[str, str]:
    """`{中文城市名: 城市文件 slug}`，由 cities/_index.json 生成（抓取的城市文件用拼音命名）。"""
    global _FILE_INDEX
    if not _FILE_INDEX:
        path = os.path.join(CITY_DIR, "_index.json")
        try:
            with open(path, encoding="utf-8") as f:
                _FILE_INDEX = json.load(f)
        except Exception:  # pragma: no cover
            _FILE_INDEX = {}
    return _FILE_INDEX


def _key_of(name: str, province: str) -> Optional[str]:
    global _NAME_TO_KEY
    if not _NAME_TO_KEY:
        try:
            from .seed_cities import SEED, PROVINCE_OF
            for key, (cn, _rows) in SEED.items():
                _NAME_TO_KEY[f"{PROVINCE_OF.get(key, '')}/{cn}"] = key
        except Exception:  # pragma: no cover
            pass
        _NAME_TO_KEY.setdefault("四川/成都", "chengdu")
        _NAME_TO_KEY.setdefault("江西/南昌", "nanchang")
        _NAME_TO_KEY.setdefault("江西/抚州", "fuzhou_jx")
    hit = _NAME_TO_KEY.get(f"{province}/{name}")
    idx = _file_index().get(name)
    # 优先用「真的有城市文件」的 key（SEED 里的种子城市可能已被抓取文件取代，
    # 例如宜春的 yichun.json 已改名为 yichun_jx.json）
    if idx and os.path.exists(_spec_path(idx)):
        return idx
    return hit or idx


def city_directory() -> List[dict]:
    """省市目录：每个城市都有可用 key；标注是否手工收录（has_data）。"""
    from .geo_seed import PROVINCES
    have = set(available_cities())
    out = []
    for prov in PROVINCES:
        cities = []
        for c in prov["cities"]:
            key = _key_of(c["name"], prov["name"]) or c["name"]
            cities.append({
                "key": key,
                "name": c["name"],
                "province": prov["name"],
                "lat": c["lat"],
                "lng": c["lng"],
                "has_data": key in have,
            })
        out.append({"name": prov["name"], "cities": cities})
    return out


def resolve_city(name: str) -> Optional[str]:
    """把用户输入（key 或中文城市名）解析成 _load_spec 能吃的 key。"""
    name = (name or "").strip()
    if not name:
        return None
    if os.path.exists(_spec_path(name)):
        return name
    hit = _file_index().get(name)  # 中文名 → 抓取的城市文件 slug
    if hit and os.path.exists(_spec_path(hit)):
        return hit
    if name in _dir_index():
        return name
    try:
        from .seed_cities import SEED
        if name in SEED:
            return name
    except Exception:  # pragma: no cover
        pass
    return None


def city_position(key: str) -> Optional[tuple]:
    """城市中心坐标：已收录城市读 meta，其余读省—市目录或城市文件。"""
    meta = CITY_META.get(key)
    if meta:
        return tuple(meta["center"])
    if key in _POS_CACHE:
        return _POS_CACHE[key]
    p = _dir_index().get(key)
    if p is None:
        _spec_meta(key)
        p = _POS_CACHE.get(key)
    _POS_CACHE[key] = p
    return p


# 初始化：默认城市
use_city(CITY)