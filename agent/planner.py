# -*- coding: utf-8 -*-
"""一次性生成（baseline）：按偏好排序后顺序切片，不做空间与时间校验。"""
from __future__ import annotations

from typing import Dict, List

from . import data
from .data import POIS, POI_BY_ID
from .model import Day, Itinerary, Profile, Stop


def _score(poi, profile: Profile) -> float:
    s = poi.rating * 2
    if set(profile.prefs) & set(poi.tags):
        s += 6
    if poi.id in profile.must_visit:
        s += 20
    if profile.party == "家庭" and "亲子" in poi.tags:
        s += 4
    if profile.party == "朋友" and "夜景" in poi.tags:
        s += 2
    if profile.budget < 1500 and poi.price > 0:
        s -= poi.price / 20
    return s


def rank_pois(profile: Profile) -> List[str]:
    return [p.id for p in sorted(POIS, key=lambda p: (-_score(p, profile), p.name))]


def plan_once(profile: Profile) -> Itinerary:
    """把排序结果按「每日站数 × 各城市停留天数」切片，得到未校验的初稿。"""
    ranked = rank_pois(profile)
    n = profile.stops_per_day()
    used = set()
    days: List[Day] = []
    must = [m for m in profile.must_visit if m in POI_BY_ID]
    by_city: Dict[str, List[str]] = {}
    for pid in ranked:
        by_city.setdefault(data.city_of_poi(pid), []).append(pid)

    for city, nd in profile.city_plan():
        pool = by_city.get(city, [])
        must_here = [m for m in must if data.city_of_poi(m) == city]
        for _ in range(nd):
            picks: List[str] = []
            # 必去点优先塞进所在城市的第一天
            for mid in must_here:
                if mid not in used and len(picks) < n:
                    picks.append(mid)
                    used.add(mid)
            for pid in pool:
                if len(picks) >= n:
                    break
                if pid not in used:
                    picks.append(pid)
                    used.add(pid)
            days.append(Day(stops=[Stop(pid) for pid in picks], city=city))
    return Itinerary(days=days, hotel=profile.hotel, transport=profile.transport,
                    rests=[dict(r) for r in (profile.rests or data.DEFAULT_RESTS)])