# -*- coding: utf-8 -*-
"""单段出行的「最佳方案」推荐。

给定起点/终点（景点或住宿区）与用户偏好，比较四种走法并挑一个：

* 纯步行              —— 短距离最省钱
* 公交/地铁直达        —— 中等距离最省钱
* 打车接驳 + 轨道 + 步行 —— 中长距离的时间与花费折中
* 打车直达             —— 最贵最快

评分 = 分钟数 + 花费 × TIME_COST（默认 1.82 分钟/元，即 33 元/小时的时间价值），
所以短途会推荐走路、逛街会推荐地铁、赶时间才推荐打车，并且会把组合方案拆成
「打车 8 分 → 地铁 40 分 → 步行 4 分」这样的分段说明。
"""
from __future__ import annotations

from typing import Dict, List, Optional

from . import data

TIME_COST = 1.82          # 每分钟等待/乘车折合的花费（元）的倒数：1 元 ≈ 1.82 分钟
COMBO_MIN_KM = 3.5        # 组合方案至少这个距离才划算
FEEDER_MINUTES = 7        # 打车到轨道站的平均时间
FEEDER_COST = 12          # 起步价
LAST_WALK = 4             # 出站步行到景点


def _seg(mode: str, text: str, minutes: int, cost: int) -> dict:
    return {"mode": mode, "text": text, "minutes": int(minutes), "cost": int(cost)}


def _single(mode: str, km: float, minutes: int) -> dict:
    if mode == "walk":
        segs = [_seg("walk", f"步行 {minutes} 分钟", minutes, 0)]
        return _wrap(segs, "步行 · 免费", "步行距离内，不用等车")
    if mode == "taxi":
        fare = data.taxi_fare(km)
        segs = [_seg("taxi", f"打车 {minutes} 分钟", minutes, fare)]
        return _wrap(segs, f"打车 {minutes} 分钟 · ¥{fare}", "最快，费用较高")
    fare = data.metro_fare(km)
    segs = [_seg("transit", f"公交/地铁 {minutes} 分钟", minutes, fare)]
    return _wrap(segs, f"公交/地铁 {minutes} 分钟 · ¥{fare}", "不用打车，时间较稳")


def _wrap(segs: List[dict], headline: str, why: str) -> dict:
    mins = sum(s["minutes"] for s in segs)
    cost = sum(s["cost"] for s in segs)
    return {"segments": segs, "minutes": int(mins), "cost": int(cost),
            "headline": headline, "why": why}


def _combo(km: float, transit_min: int) -> dict:
    """打车到站 + 轨道 + 步行到景点。"""
    ride = max(6, int(round((transit_min - 10) * 0.78)))
    fare = data.metro_fare(km)
    segs = [
        _seg("taxi", f"打车到地铁站 {FEEDER_MINUTES} 分钟", FEEDER_MINUTES, FEEDER_COST),
        _seg("metro", f"地铁/轨道 {ride} 分钟", ride, fare),
        _seg("walk", f"出站步行 {LAST_WALK} 分钟", LAST_WALK, 0),
    ]
    return _wrap(segs, f"打车 {FEEDER_MINUTES} 分 + 轨道 {ride} 分 + 步行 {LAST_WALK} 分 · ¥{FEEDER_COST + fare}",
                 "打车接驳省掉等车，轨道避开路面拥堵")


def _score(plan: dict) -> float:
    return plan["minutes"] + plan["cost"] * TIME_COST


def plan(a_id: str, b_id: str, mode: str = "auto") -> dict:
    """返回该段的最佳出行方案；mode 固定为 walk/transit/taxi 时不比较。"""
    if a_id == b_id:
        return _wrap([], "原地", "")
    km = data.id_km(a_id, b_id)
    if mode in ("walk", "transit", "taxi"):
        return _single(mode, km, data.id_minutes(a_id, b_id, mode))
    walk_min = data.id_minutes(a_id, b_id, "walk")
    bus_min = data.id_minutes(a_id, b_id, "transit")
    taxi_min = data.id_minutes(a_id, b_id, "taxi")
    options: List[dict] = []
    if km <= 3.0 or walk_min <= 35:
        options.append(_single("walk", km, walk_min))
    options.append(_single("transit", km, bus_min))
    options.append(_single("taxi", km, taxi_min))
    if km >= COMBO_MIN_KM and bus_min > 20:
        options.append(_combo(km, bus_min))
    best = options[0]
    for o in options[1:]:
        if _score(o) < _score(best) - 1e-9:
            best = o
    # 说明为什么是它：和次优方案比，省了多少
    rest = [o for o in options if o is not best]
    if rest:
        alt = min(rest, key=_score)
        dm = best["minutes"] - alt["minutes"]
        dc = best["cost"] - alt["cost"]
        bits = []
        if dm < 0:
            bits.append(f"比「{alt['headline']}」快 {abs(dm)} 分钟")
        elif dm > 0:
            bits.append(f"比「{alt['headline']}」慢 {dm} 分钟")
        if dc < 0:
            bits.append(f"省 ¥{abs(dc)}")
        elif dc > 0:
            bits.append(f"多花 ¥{dc}")
        if bits:
            best = dict(best)
            best["why"] = (best["why"] + "；" if best["why"] else "") + "、".join(bits)
    return best


def total_cost(itinerary) -> int:
    """行程中所有“市内段”的花费合计（元）。"""
    return sum((s.via or {}).get("cost", 0) for d in itinerary.days for s in d.stops)


if __name__ == "__main__":  # pragma: no cover
    data.use_city("chengdu")
    for a, b in (("kuanzhai", "wuhouci"), ("kuanzhai", "panda"),
                 ("hotel:春熙路", "dujiangyan")):
        p = plan(a, b)
        print(a, "->", b, "|", p["headline"], "|", p["minutes"], "分钟 ¥", p["cost"])
        print("   ", p["why"])