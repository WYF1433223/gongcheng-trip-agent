# -*- coding: utf-8 -*-
"""排程与约束校验：把 POI 序列展开为带时刻的行程，并逐条检查硬/软约束。"""
from __future__ import annotations

from typing import Dict, List, Optional

from . import data, multimodal, rail
from .data import POI_BY_ID, FOOD_PER_DAY, TRANSIT_PER_DAY
from .model import Day, Itinerary, Profile, Report, Stop, Violation

DAY_START = 8 * 60 + 30      # 从酒店出发
REST_EARLY = 30              # 允许在休息窗口开始前 30 分钟就开休
DAY_END = 22 * 60            # 当日必须结束
MAX_WAIT = 90                # 早到超过该时长视为安排不合理


def _window(poi) -> tuple:
    open_, close_ = poi.open_min, poi.close_min
    if close_ <= open_:
        close_ += 1440
    return open_, close_


def schedule_day(day: Day, hotel: str, day_index: int, start: int = DAY_START,
                 mode: str = "transit", rests: Optional[list] = None) -> None:
    """按顺序推演时刻：酒店→第1站→…→最后一站；休息窗口内自动留出休息时间。"""
    rests = rests if rests is not None else data.DEFAULT_RESTS
    t = start
    day.rest_done = [False] * len(rests)   # 每次排程都重新计算休息时段
    prev: Optional[str] = None

    def take_rest(now: int) -> int:
        """若 now 落在某个尚未安排的休息窗口内，返回要插入的分钟数。"""
        for i, r in enumerate(rests):
            if day.rest_done[i]:
                continue
            if r["start"] - REST_EARLY <= now <= r["end"]:
                day.rest_done[i] = True
                return r["minutes"]
        return 0

    for stop in day.stops:
        poi = POI_BY_ID[stop.poi_id]
        start_id = f"hotel:{hotel}" if prev is None else prev
        plan = multimodal.plan(start_id, poi.id, mode)
        stop.via = plan
        travel = plan["minutes"]
        arrive = t + travel
        open_, _close = _window(poi)
        wait = 0
        if open_ > DAY_START and arrive < open_ and open_ - arrive <= MAX_WAIT:
            wait = open_ - arrive
            arrive = open_
        arrive += take_rest(arrive)       # 到站正好落在休息窗口：先休息
        stop.from_prev = travel
        stop.wait = wait
        stop.arrive = arrive
        stop.depart = arrive + poi.duration
        t = stop.depart
        prev = poi.id
    # 收尾：落在窗口内就补休息；当天根本没走到该时段则视为不需要
    for i, r in enumerate(rests):
        if day.rest_done[i]:
            continue
        if t < r["start"] - REST_EARLY:
            day.rest_done[i] = True
        elif r["start"] - REST_EARLY <= t <= r["end"]:
            t += r["minutes"]
            day.rest_done[i] = True
    day.last_leave = t
    ws = data.weather_list()
    day.weather = ws[min(day_index, len(ws) - 1)]


def schedule(itinerary: Itinerary, hotel: Optional[str] = None,
             origin: str = "", return_home: bool = True) -> Itinerary:
    """逐日排程；跨市当天先乘车，抵达后再排景点；含出发地去程与返程。"""
    prev_city = None
    seen: Dict[str, int] = {}
    first_city = None
    for i, day in enumerate(itinerary.days):
        city = day.city or (prev_city if i else (itinerary.days[0].city if itinerary.days else data.CITY))
        city = city or data.CITY
        day.city = city
        if first_city is None:
            first_city = city
        if prev_city is not None and city != prev_city:
            leg = rail.trip(prev_city, city, DAY_START)
            leg["hub_minutes"] = 30
            day.leg = leg
            start = leg["arr"] + 30               # 到站 + 酒店放行李
        else:
            day.leg = {}
            start = DAY_START
        k = seen.get(city, 0)
        seen[city] = k + 1
        day.weather = data.weather_at(city, k)
        schedule_day(day, hotel or data.hotel_of(city), i, start, itinerary.transport, itinerary.rests)
        prev_city = city
    # 出发地 → 首城（去程）
    itinerary.origin_leg = {}
    if origin and first_city and origin != first_city:
        go = rail.trip(origin, first_city, 6 * 60 + 30)
        go["hub_minutes"] = 30
        itinerary.origin_leg = go
        # 去程抵达后重排第一天（要等火车到站才能出门）
        if itinerary.days:
            d0 = itinerary.days[0]
            k0 = seen.get(first_city, 1) - 1
            d0.city = first_city
            d0.weather = data.weather_at(first_city, 0)
            schedule_day(d0, hotel or data.hotel_of(first_city), 0,
                         max(DAY_START, go["arr"] + 30), itinerary.transport, itinerary.rests)
    # 末城 → 出发地（返程）
    itinerary.return_leg = {}
    if origin and first_city and origin != first_city and return_home and itinerary.days:
        last = itinerary.days[-1]
        last_leave = last.last_leave if last.stops else DAY_START
        back = rail.trip(last.city, origin, last_leave + 45)
        back["hub_minutes"] = 30
        itinerary.return_leg = back
    if hotel:
        itinerary.hotel = hotel
    return itinerary


def cost_of(itinerary: Itinerary) -> int:
    days = len(itinerary.days)
    tickets = sum(POI_BY_ID[s.poi_id].price for d in itinerary.days for s in d.stops)
    legs = sum((d.leg or {}).get("fare", 0) for d in itinerary.days)
    legs += (itinerary.origin_leg or {}).get("fare", 0)
    legs += (itinerary.return_leg or {}).get("fare", 0)
    ride = sum((s.via or {}).get("cost", 0) for d in itinerary.days for s in d.stops)
    return tickets + FOOD_PER_DAY * days + legs + ride


def intercity_km(itinerary: Itinerary) -> float:
    return round(sum((d.leg or {}).get("km", 0) for d in itinerary.days), 1)


def pref_score(itinerary: Itinerary, profile: Profile) -> float:
    ids = itinerary.ids()
    if not ids:
        return 0.0
    prefs = set(profile.prefs)
    if not prefs:
        return 0.75
    hits = sum(1 for pid in ids if prefs & set(POI_BY_ID[pid].tags))
    return hits / len(ids)


def verify(itinerary: Itinerary, profile: Profile, start_weekday: int = None) -> Report:
    vs: List[Violation] = []
    hard_total = 2 + 2 * len(itinerary.days)   # 预算、必去 + 每日节奏、当日时长
    seen: dict = {}

    for di, day in enumerate(itinerary.days):
        wd = ((start_weekday if start_weekday is not None else profile.start_dt().weekday()) + di) % 7
        if not day.stops:
            vs.append(Violation("DAY_EMPTY", "hard", di, None, f"第{di+1}天没有任何行程"))
        for stop in day.stops:
            poi = POI_BY_ID[stop.poi_id]
            hard_total += 3
            # 1) 重复
            if poi.id in seen:
                vs.append(Violation("DUPLICATE", "hard", di, poi.id,
                                    f"{poi.name} 在第{seen[poi.id]+1}天已安排，第{di+1}天重复",
                                    "删除重复项"))
            else:
                seen[poi.id] = di
            # 2) 闭馆日
            if poi.closed_weekday is not None and poi.closed_weekday == wd:
                vs.append(Violation("CLOSED_WEEKDAY", "hard", di, poi.id,
                                    f"{poi.name} 周{['一','二','三','四','五','六','日'][wd]}闭馆",
                                    "换日或替换同类景点"))
            # 3) 开放时间
            open_, close_ = _window(poi)
            if stop.arrive < open_ or stop.depart > close_:
                vs.append(Violation("OUT_OF_WINDOW", "hard", di, poi.id,
                                    f"{poi.name} 安排 {_fmt(stop.arrive)}–{_fmt(stop.depart)}，"
                                    f"开放 {_fmt(open_)}–{_fmt(close_)}",
                                    "平移时段或换到适合的日期"))
            if stop.wait > 60:
                vs.append(Violation("LONG_WAIT", "soft", di, poi.id,
                                    f"{poi.name} 早到需等待 {stop.wait} 分钟", "调整游览顺序"))
            if day.weather.get("rain", 0) >= 60 and poi.outdoor:
                vs.append(Violation("RAIN_OUTDOOR", "soft", di, poi.id,
                                    f"{poi.name} 为户外景点，当天降雨概率 {day.weather['rain']}%",
                                    "替换为室内景点或改到晴天"))
        # 4) 节奏
        if len(day.stops) > profile.stops_per_day():
            vs.append(Violation("PACE_EXCESS", "hard", di, None,
                                f"第{di+1}天安排 {len(day.stops)} 站，超过「{profile.pace}」节奏上限 "
                                f"{profile.stops_per_day()} 站", "移出或删除部分站点"))
        # 5) 当日结束时间
        if day.stops and day.stops[-1].depart > DAY_END:
            vs.append(Violation("DAY_OVERRUN", "hard", di, day.stops[-1].poi_id,
                                f"第{di+1}天 {_fmt(day.stops[-1].depart)} 才结束，超过 22:00",
                                "减少站点或前移"))
        # 6) 休息时段（软）
        if day.stops:
            for ri, r in enumerate(itinerary.rests):
                done = ri < len(day.rest_done) and day.rest_done[ri]
                if done:
                    continue
                vs.append(Violation(
                    "REST_MISSING", "soft", di, None,
                    f"第{di+1}天未安排{r['label']}（{_fmt(r['start'])}–{_fmt(r['end'])} 至少 {r['minutes']} 分钟）",
                    "在休息窗口内留出空档，或减少当天景点"))

    # 6b) 返程
    back = itinerary.return_leg or {}
    last = itinerary.days[-1] if itinerary.days else None
    if back:
        leave = last.last_leave if last and last.stops else DAY_START
        if back.get("next_day") or back["dep"] and back["dep"] <= leave:
            vs.append(Violation("RETURN_LATE", "soft", len(itinerary.days) - 1, None,
                                f"第{len(itinerary.days)}天 {_fmt(leave)} 才结束，赶不上当天返程，改乘次日 {rail.fmt(back['dep'])} 的车",
                                "提前收尾或减少末日的景点"))
        elif back["arr"] > 23 * 60:
            vs.append(Violation("RETURN_LATE", "soft", len(itinerary.days) - 1, None,
                                f"返程 {_fmt(back['arr'])} 才到{back['to_name']}，已过 23:00",
                                "选更早的返程车次"))

    # 7) 预算
    cost = cost_of(itinerary)
    if cost > profile.budget:
        vs.append(Violation("BUDGET_EXCEEDED", "hard", None, None,
                            f"预计花费 {cost} 元，超出预算 {profile.budget} 元",
                            "替换高价门票或减少付费景点"))
    # 8) 必去点
    ids = set(itinerary.ids())
    for mid in profile.must_visit:
        if mid not in ids:
            vs.append(Violation("MISSING_MUST_VISIT", "hard", None, mid,
                                f"必去点 {POI_BY_ID[mid].name} 未安排", "插入行程"))
    # 9) 偏好匹配（软）
    if profile.prefs and pref_score(itinerary, profile) < 0.4:
        vs.append(Violation("PREF_LOW", "soft", None, None,
                            f"偏好匹配率仅 {pref_score(itinerary, profile):.0%}", "增加偏好标签匹配的景点"))

    rep = Report(violations=vs, hard_total=hard_total,
                 hard_failed=sum(1 for v in vs if v.severity == "hard"),
                 soft_total=sum(1 for v in vs if v.severity == "soft"),
                 soft_failed=sum(1 for v in vs if v.severity == "soft"))
    return rep


def _fmt(minutes: int) -> str:
    minutes %= 1440
    return f"{minutes // 60:02d}:{minutes % 60:02d}"