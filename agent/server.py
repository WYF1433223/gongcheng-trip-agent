# -*- coding: utf-8 -*-
"""演示服务：标准库 HTTP 服务器，提供静态页面与 /api/plan 接口。

启动： python -m agent.server [--port 8823] [--city nanchang] [--no-live-weather]

页面里可以选省份 → 勾选该省多个城市 → 生成跨城市行程；也可直接搜索城市。
所有城市数据都从 agent/cities/*.json 读取，缺数据的城市在目录里灰显。
"""
from __future__ import annotations

import argparse
import json
import os
import posixpath
import threading
from datetime import date
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from . import data, engine
from .model import Profile

ROOT = os.path.dirname(os.path.abspath(__file__))
WEB = os.path.join(ROOT, "web")

MIME = {".html": "text/html; charset=utf-8", ".css": "text/css; charset=utf-8",
        ".js": "application/javascript; charset=utf-8", ".png": "image/png", ".svg": "image/svg+xml"}

LIVE_WEATHER = True
# 引擎会就地切换 data 模块里的城市视图，多线程下必须串行化
PLAN_LOCK = threading.Lock()
MAX_DAYS = 12


def meta() -> dict:
    """页面初始化信息：把全部内置城市都装进视图，前端据此做省市级联与必去点。"""
    with PLAN_LOCK:
        keys = data.available_cities()
        data.use_cities(keys)
        try:
            apply_weather([keys[0]], "", 5)
        except Exception:
            pass
        must = []
        for k in keys:
            pool = sorted([p for p in data.POIS if data.city_of_poi(p.id) == k],
                          key=lambda p: -p.rating)[:4]
            must += [{"id": p.id, "name": p.name, "city": k} for p in pool]
        return {
            "city": data.CITY_NAME,
            "city_keys": keys,
            "city_names": [data.city_name(k) for k in keys],
            "weather_source": data.WEATHER_SOURCE,
            "weather": data.weather_list(),
            "today": date.today().isoformat(),
            "max_days": MAX_DAYS,
            "directory": data.city_directory(),
            "available": [{"key": k, "name": data.city_name(k)} for k in keys],
            "pois": {p.id: {"name": p.name, "tags": p.tags, "price": p.price,
                            "duration": p.duration, "outdoor": p.outdoor,
                            "district": p.district, "city": data.city_of_poi(p.id)}
                     for p in data.POIS},
            "prefs": data.PREF_TAGS,
            "routes": sorted(k for k in keys if data._route_table(k)),
            "must": must,
            "days_default": 3,
        }


def safe_date(text) -> str:
    try:
        return date.fromisoformat(str(text)[:10]).isoformat()
    except (ValueError, TypeError):
        return ""


def _clean_cities(raw) -> list:
    keys = []
    for c in raw or []:
        k = data.resolve_city(str(c))
        if k and k not in keys:
            keys.append(k)
    if not keys:
        keys = [data.CITY_KEYS[0]]
    return keys[:4]


def build_profile(payload: dict) -> Profile:
    cities = _clean_cities(payload.get("cities"))
    try:
        city_days = [max(0, min(MAX_DAYS, int(x))) for x in (payload.get("city_days") or [])][:len(cities)]
    except (TypeError, ValueError):
        city_days = []
    if len(city_days) != len(cities) or sum(city_days) <= 0:
        total = max(1, min(MAX_DAYS, int(payload.get("days", 3) or 3)))
        base, rem = divmod(total, len(cities))
        city_days = [base + (1 if i < rem else 0) for i in range(len(cities))]
        city_days = [max(1, d) for d in city_days]
    days = sum(city_days)
    budget = max(300, min(30000, int(payload.get("budget", 3000))))
    prefs = [p for p in payload.get("prefs", []) if p in data.PREF_TAGS]
    must = [m for m in payload.get("must", [])
            if m in data.POI_BY_ID and data.city_of_poi(m) in cities]
    party = payload.get("party", "情侣")
    pace = payload.get("pace", "normal")
    if pace not in ("relaxed", "normal", "intense"):
        pace = "normal"
    transport = payload.get("transport", "auto")
    if transport not in ("auto", "walk", "transit", "taxi"):
        transport = "auto"
    origin = data.resolve_city(payload.get("origin", "") or "") or ""
    if origin in cities:
        origin = ""
    return Profile(days=days, budget=budget, party=party, pace=pace,
                   prefs=prefs, must_visit=must, hotel="",
                   start_date=safe_date(payload.get("start_date", "")),
                   cities=cities, city_days=city_days,
                   transport=transport, origin=origin,
                   return_home=bool(origin))


def apply_weather(keys, start_date: str, days: int, city_days=None) -> str:
    """按城市逐个取预报：每个城市用自己的坐标与停留天数（失败自动回退内置模板）。"""
    from . import weather
    notes = []
    city_days = city_days or []
    for i, key in enumerate(keys):
        pos = data.city_position(key)
        if not pos:
            continue
        nd = city_days[i] if i < len(city_days) and city_days[i] > 0 else max(1, days)
        template = (data.CITY_META.get(key) or {}).get("weather")
        live_days, src = weather.forecast(pos[0], pos[1], start_date, nd, enabled=LIVE_WEATHER,
                                          template=template,
                                          cache_key=f"{key}:{start_date}:{nd}")
        data.set_weather_for(key, live_days, src)
        notes.append(src)
    if not notes:
        return data.WEATHER_SOURCE
    sources = set(notes)
    if len(sources) == 1:
        return notes[0]
    return "、".join(sorted(sources))


class Handler(BaseHTTPRequestHandler):
    server_version = "TravelAgentDemo/0.3"

    def log_message(self, fmt, *args):  # 静默访问日志
        pass

    def _send(self, code: int, payload, ctype: str = "application/json; charset=utf-8") -> None:
        body = payload if isinstance(payload, bytes) else json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        parsed = urlparse(self.path)
        path, qs = parsed.path, parse_qs(parsed.query)
        if path == "/api/meta":
            self._send(200, meta())
            return
        if path == "/api/weather":
            key = (qs.get("city") or [data.CITY_KEYS[0]])[0]
            sd = safe_date((qs.get("start_date") or [""])[0])
            try:
                days = max(1, min(MAX_DAYS, int((qs.get("days") or ["5"])[0])))
            except ValueError:
                days = 5
            with PLAN_LOCK:
                src = apply_weather([key], sd, days)
                self._send(200, {"city": key, "city_name": data.city_name(key),
                                 "weather_source": src, "weather": data.weather_list()})
            return
        if path == "/":
            path = "/index.html"
        rel = posixpath.normpath(path).lstrip("/")
        if rel.startswith("..") or os.path.isabs(rel):
            self._send(403, {"error": "forbidden"})
            return
        full = os.path.join(WEB, rel)
        if not os.path.isfile(full):
            self._send(404, {"error": "not found"})
            return
        ext = os.path.splitext(full)[1].lower()
        with open(full, "rb") as f:
            self._send(200, f.read(), MIME.get(ext, "application/octet-stream"))

    def _payload(self) -> dict:
        length = int(self.headers.get("Content-Length", "0"))
        raw = self.rfile.read(length) or b"{}"
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:  # 兼容非 UTF-8 终端发来的请求
            text = raw.decode("gbk", "replace")
        return json.loads(text)

    def do_POST(self) -> None:
        path = urlparse(self.path).path
        if path != "/api/plan":
            self._send(404, {"error": "not found"})
            return
        try:
            payload = self._payload()
            profile = build_profile(payload)
            with PLAN_LOCK:
                data.use_cities(profile.cities)
                src = apply_weather(profile.cities, profile.start_date, max(5, profile.days),
                                    profile.city_days)
                result = engine.run_compare(profile)
            result["weather_source"] = src
            result["cities"] = [{"key": k, "name": data.city_name(k)} for k in profile.cities]
            self._send(200, result)
        except ValueError as exc:
            self._send(400, {"error": str(exc)})
        except Exception as exc:  # 演示服务：把异常回传前端便于排查
            self._send(500, {"error": f"{type(exc).__name__}: {exc}"})


def serve(port: int = 8823, host: str = "127.0.0.1", live_weather: bool = True) -> None:
    global LIVE_WEATHER
    LIVE_WEATHER = live_weather
    keys = list(data.CITY_KEYS)
    try:
        apply_weather(keys, "", 5)
    except Exception:
        pass
    print(f"旅行行程规划演示已启动: http://{host}:{port}")
    print(f"  当前城市={data.CITY_NAME}  天气源={data.WEATHER_SOURCE}")
    print(f"  有数据的城市：{', '.join(data.available_cities())}")
    httpd = ThreadingHTTPServer((host, port), Handler)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8823)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--city", default=None, help="启动时选中的城市，如 chengdu / nanchang")
    ap.add_argument("--no-live-weather", action="store_true", help="只用内置天气，不联网")
    a = ap.parse_args()
    if a.city:
        data.use_city(a.city)
    serve(a.port, a.host, live_weather=not a.no_live_weather)