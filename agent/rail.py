# -*- coding: utf-8 -*-
"""高铁/动车班次推算。

沙箱内拿不到 12306 之类的实时时刻数据（域名不可达），这里按公开的平均车速
与发车密度，用两城距离推出一张**确定性**的班次表：车次号、始发站/终到站、
发车与到达时刻、历时、二等座票价。

口径（全部标注为推算值，不是实时余票）：
* 车次号：由两城名称派生，成对固定，便于复现与双实现比对
* 发车密度：<180km 每 30 分钟一班，<500km 每 45 分钟，<1000km 每 60 分钟，更远 75 分钟
* 运营时段：06:40–21:30
* 历时：沿用 data.intercity() 的总时长减去 45 分钟候车接驳（含停站）
* 票价：沿用 data.intercity() 的二等座估算（约 0.45 元/km）
"""
from __future__ import annotations

from typing import Dict, Optional

from . import data

FIRST_DEP = 6 * 60 + 40        # 首班
LAST_DEP = 21 * 60 + 30        # 末班
HUB_MINUTES = 45               # 到站取票安检 + 市内接驳
_CHECK_IN = 25                 # 提前进站
_STATION_SUFFIX = ["", "东", "西", "南", "北"]


def _seed(text: str) -> int:
    """与 C# 端逐位一致的字符串散列（31 进制多项式）。"""
    s = 0
    for ch in text:
        s = (s * 31 + ord(ch)) & 0x7FFFFFFF
    return s


def _step(km: float) -> int:
    if km < 180:
        return 30
    if km < 500:
        return 45
    if km < 1000:
        return 60
    return 75


def _station(key: str, prefix: str = "") -> str:
    name = data.city_name(key)
    suf = _STATION_SUFFIX[_seed(prefix + key) % len(_STATION_SUFFIX)]
    return f"{name}{suf}站"


def trip(a_key: str, b_key: str, not_before: int) -> Optional[Dict]:
    """返回 not_before 之后可乘的第一班车；当天赶不上则记为次日首班。"""
    if not a_key or not b_key or a_key == b_key:
        return None
    minutes, fare, km = data.intercity(a_key, b_key)
    run = max(35, int(minutes) - HUB_MINUTES)
    step = _step(km)
    earliest = int(not_before) + _CHECK_IN
    first = FIRST_DEP + (_seed(a_key + ">" + b_key) % step)
    if earliest <= first:
        dep = first
    else:
        k = (earliest - first + step - 1) // step
        dep = first + k * step
    next_day = False
    if dep > LAST_DEP:
        dep = first
        next_day = True
    code = "G" + str(100 + (_seed(b_key + "<" + a_key) % 8900))
    return {
        "from": a_key, "from_name": data.city_name(a_key),
        "to": b_key, "to_name": data.city_name(b_key),
        "code": code,
        "dep_station": _station(a_key, "dep"),
        "arr_station": _station(b_key, "arr"),
        "dep": int(dep), "arr": int(dep + run), "run": int(run),
        "km": round(km, 1), "fare": int(fare),
        "hub_minutes": HUB_MINUTES, "next_day": next_day,
    }


def first_departure(a_key: str, b_key: str) -> Optional[Dict]:
    """当天首班（用于“从出发地出发”的去程）。"""
    return trip(a_key, b_key, FIRST_DEP - _CHECK_IN)


def last_departure(a_key: str, b_key: str) -> Optional[Dict]:
    """当天末班（用于返程兜底）。"""
    t = trip(a_key, b_key, LAST_DEP - _CHECK_IN)
    if t and t["dep"] <= LAST_DEP:
        return t
    return trip(a_key, b_key, FIRST_DEP - _CHECK_IN)


def fmt(minutes: int) -> str:
    m = int(minutes) % 1440
    return f"{m // 60:02d}:{m % 60:02d}"


def label(leg: Optional[Dict]) -> str:
    """「G1234 南昌西 08:12 → 上饶 09:35（¥126）」这样的单行描述。"""
    if not leg:
        return ""
    return (f"{leg['code']} {leg['dep_station']} {fmt(leg['dep'])} → "
            f"{leg['arr_station']} {fmt(leg['arr'])}")


if __name__ == "__main__":  # pragma: no cover
    for a, b in (("nanchang", "changsha"), ("luoyang", "zhengzhou")):
        t = first_departure(a, b)
        print(t["code"], t["dep_station"], fmt(t["dep"]),
              "->", t["arr_station"], fmt(t["arr"]),
              f"{t['run']}分钟 ¥{t['fare']}")