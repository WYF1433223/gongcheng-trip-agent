# -*- coding: utf-8 -*-
"""真实天气适配器：Open-Meteo 免费接口（无需 API Key），带本地缓存与离线兜底。

用法：
    forecast = ensure("nanchang", "2026-11-20", 3)   # 返回写入 data 的逐日天气

返回结构与 data.WEATHER 一致：
    {"day": "第N天", "date": "2026-11-20", "text": "小雨", "low": 9, "high": 15, "rain": 70}
"""
from __future__ import annotations

import json
import os
import time
import urllib.parse
import urllib.request
from datetime import date, timedelta
from typing import List

from . import data

CACHE_TTL = 3600  # 秒；天气一天更新几次即可，避免频繁请求
HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, "cities", ".weather_cache.json")

# 接口可用范围：过去 92 天 ~ 未来 16 天
MAX_PAST, MAX_FUTURE = 92, 16

# WMO 天气代码 -> (中文描述, 默认降水概率)
WMO = {
    0: ("晴", 0), 1: ("晴间多云", 5), 2: ("多云", 10), 3: ("阴", 20),
    45: ("雾", 20), 48: ("雾凇", 20),
    51: ("毛毛雨", 55), 53: ("毛毛雨", 60), 55: ("毛毛雨", 70),
    56: ("冻雨", 65), 57: ("冻雨", 75),
    61: ("小雨", 70), 63: ("中雨", 80), 65: ("大雨", 90),
    66: ("冻雨", 80), 67: ("冻雨", 90),
    71: ("小雪", 70), 73: ("中雪", 80), 75: ("大雪", 90), 77: ("米雪", 70),
    80: ("阵雨", 70), 81: ("阵雨", 80), 82: ("强阵雨", 90),
    85: ("阵雪", 75), 86: ("强阵雪", 85),
    95: ("雷阵雨", 85), 96: ("雷阵雨伴冰雹", 90), 99: ("强雷暴", 95),
}


def _parse(start_date: str) -> date:
    try:
        return date.fromisoformat(start_date[:10]) if start_date else date.today()
    except ValueError:
        return date.today()


def _fetch(lat: float, lng: float, start: date, end: date) -> List[dict]:
    q = urllib.parse.urlencode({
        "latitude": f"{lat:.4f}", "longitude": f"{lng:.4f}",
        "daily": "weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max",
        "timezone": "Asia/Shanghai",
        "start_date": start.isoformat(), "end_date": end.isoformat(),
    }, safe=",")
    url = f"https://api.open-meteo.com/v1/forecast?{q}"
    req = urllib.request.Request(url, headers={"User-Agent": "travel-agent-demo/1.0"})
    last = None
    for attempt in range(2):  # 偶发 TLS 握手超时，重试一次
        try:
            with urllib.request.urlopen(req, timeout=8) as r:
                raw = json.load(r)
            break
        except Exception as exc:
            last = exc
            time.sleep(0.6)
    else:
        raise last
    daily = raw["daily"]
    out = []
    for i, d in enumerate(daily["time"]):
        code = int(daily["weather_code"][i])
        text, rain_default = WMO.get(code, ("多云", 30))
        rain = daily["precipitation_probability_max"][i]
        out.append({
            "day": f"第{i + 1}天",
            "date": d,
            "text": text,
            "low": int(round(daily["temperature_2m_min"][i])),
            "high": int(round(daily["temperature_2m_max"][i])),
            "rain": int(rain) if rain is not None else rain_default,
        })
    return out


def _fallback(start: date, days: int, template=None) -> List[dict]:
    """离线兜底：沿用内置的天气形态，补上真实日期。"""
    base = template or data.WEATHER or WEATHER_TEMPLATE
    out = []
    for i in range(days):
        w = dict(base[min(i, len(base) - 1)])
        w["day"] = f"第{i + 1}天"
        w["date"] = (start + timedelta(days=i)).isoformat()
        out.append(w)
    return out


WEATHER_TEMPLATE = [
    {"day": "第1天", "text": "多云", "low": 12, "high": 21, "rain": 15},
    {"day": "第2天", "text": "晴", "low": 13, "high": 23, "rain": 5},
    {"day": "第3天", "text": "小雨", "low": 11, "high": 17, "rain": 70},
]


def forecast(lat: float, lng: float, start_date: str = "", days: int = 5,
             enabled: bool = True, template=None, cache_key: str = "") -> tuple:
    """取某坐标的逐日预报，返回 (days, 来源说明)。任何失败都回退内置形态。"""
    start = _parse(start_date)
    days = max(1, min(16, days))
    end = start + timedelta(days=days - 1)
    if not enabled:
        return _fallback(start, days, template), "内置演示数据（离线模式）"

    key = cache_key or f"{lat:.3f},{lng:.3f}:{start.isoformat()}:{days}"
    try:
        if os.path.exists(CACHE):
            cache = json.load(open(CACHE, encoding="utf-8"))
            hit = cache.get(key)
            if hit and time.time() - hit.get("ts", 0) < CACHE_TTL:
                return hit["days"], f"Open-Meteo 缓存（{start.isoformat()} 起）"
    except Exception:
        pass

    today = date.today()
    if (today - start).days > MAX_PAST or (start - today).days > MAX_FUTURE:
        return _fallback(start, days, template), f"超出预报范围（{start.isoformat()}），用内置天气形态"

    try:
        out = _fetch(lat, lng, start, end)
        if not out:
            raise ValueError("空预报")
        try:
            cache = {}
            if os.path.exists(CACHE):
                cache = json.load(open(CACHE, encoding="utf-8"))
            cache[key] = {"ts": time.time(), "days": out}
            with open(CACHE, "w", encoding="utf-8") as f:
                json.dump(cache, f, ensure_ascii=False)
        except OSError:
            pass
        return out, f"Open-Meteo 实时预报（{start.isoformat()} 起）"
    except Exception as exc:  # 断网/超时/解析失败都不影响演示
        return _fallback(start, days, template), f"内置演示数据（在线获取失败：{type(exc).__name__}）"


def ensure(city: str, start_date: str = "", days: int = 5, enabled: bool = True) -> str:
    """取首城预报并写入 data；任何失败都回退内置数据。返回数据来源说明。"""
    days_list, src = forecast(*data.CITY_CENTER, start_date, days, enabled,
                              template=data.WEATHER, cache_key=f"{city}:{start_date[:10]}:{days}")
    data.set_weather(days_list, src)
    return data.WEATHER_SOURCE


if __name__ == "__main__":  # 手动试一下：python -m agent.weather [YYYY-MM-DD]
    import sys
    sd = sys.argv[1] if len(sys.argv) > 1 else ""
    print(ensure(os.environ.get("CITY", "chengdu"), sd, 5))
    for w in data.weather_list():
        print(w)