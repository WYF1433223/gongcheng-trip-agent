# -*- coding: utf-8 -*-
"""通用城市模板：给没有手工数据的城市现场生成一份"可演示"的数据。

目录里 339 个地级市不可能都有手工整理的景点，凡是没有 cities/<key>.json 的
城市，都按同一套模板 + 该市经纬度生成 6 个通用点位（博物馆 / 老街 / 公园 /
观景台 / 文创园 / 夜市）。这些点是**演示用的合成数据**，不是真实 POI，
界面上会标注"通用模板"，实验与报告里也只用 16 个手工城市的数据。
"""
from __future__ import annotations

from typing import List

# (名称, 片区, 开门, 关门, 时长, 门票, 评分, 标签, 户外, 固定闭馆星期)
TEMPLATE = [
    ("城市博物馆", "中心城区", 9, 17, 90, 0, 4.6, ["历史人文", "艺术展馆"], False, 0),
    ("老城历史街区", "老城区", 8, 21, 90, 0, 4.4, ["历史人文", "美食"], True, None),
    ("城市公园", "城中", 6, 21, 75, 0, 4.3, ["自然风光", "亲子"], True, None),
    ("滨江观景平台", "滨江", 10, 21, 60, 30, 4.2, ["夜景"], True, None),
    ("文创艺术园区", "新区", 10, 20, 75, 0, 4.1, ["艺术展馆", "购物"], False, None),
    ("夜市小吃街", "老城区", 17, 23, 60, 0, 4.5, ["美食", "夜景"], True, None),
]

# 相对市中心的位置（单位：度，约 ±2km 量级），保证同一城市每次生成结果一致
OFFSETS = [
    (0.006, -0.004), (-0.010, 0.008), (0.004, 0.011),
    (0.013, 0.002), (-0.006, -0.012), (-0.012, 0.001),
]

HOTEL_OFFSETS = {"市中心": (0.0, 0.0), "老城区": (-0.008, 0.004), "滨江": (0.010, 0.003)}

WEATHER_TEMPLATE = [
    {"day": "第1天", "text": "多云", "low": 12, "high": 21, "rain": 15},
    {"day": "第2天", "text": "晴", "low": 13, "high": 23, "rain": 5},
    {"day": "第3天", "text": "小雨", "low": 11, "high": 17, "rain": 70},
    {"day": "第4天", "text": "阴", "low": 10, "high": 18, "rain": 30},
    {"day": "第5天", "text": "晴间多云", "low": 12, "high": 22, "rain": 10},
]


def build(key: str, name: str, lat: float, lng: float) -> dict:
    """按模板生成一份与 cities/*.json 同结构的数据。key 用城市中文名。"""
    pois: List[dict] = []
    for i, (tname, district, oh, ch, dur, price, rating, tags, outdoor, closed) in enumerate(TEMPLATE):
        dlat, dlng = OFFSETS[i % len(OFFSETS)]
        pois.append({
            "id": "%s_%02d" % (key, i + 1),
            "name": tname,
            "district": district,
            "lat": round(lat + dlat, 4),
            "lng": round(lng + dlng, 4),
            "open_min": oh * 60,
            "close_min": ch * 60,
            "duration": dur,
            "price": price,
            "rating": rating,
            "tags": tags,
            "outdoor": outdoor,
            "closed_weekday": closed,
        })
    hotels = {z: [round(lat + a, 4), round(lng + b, 4)] for z, (a, b) in HOTEL_OFFSETS.items()}
    return {
        "name": name,
        "center": [lat, lng],
        "hotels": hotels,
        "default_hotel": "市中心",
        "weather": [dict(w) for w in WEATHER_TEMPLATE],
        "far_ids": [],
        "far_minutes": 60,
        "far_both_minutes": 90,
        "far_km": None,
        "synthetic": True,
        "pois": pois,
    }