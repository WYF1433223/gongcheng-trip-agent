# -*- coding: utf-8 -*-
"""校验驱动的定向修复：定位冲突 → 生成候选算子 → 字典序接受。"""
from __future__ import annotations

from typing import Dict, List, Optional, Sequence, Tuple

from . import data, verifier
from .data import POIS, POI_BY_ID
from .model import Day, Itinerary, Profile, Report, Stop, Violation

def meal_ids() -> List[str]:
    return [p.id for p in POIS if any(t in p.tags for t in ("美食", "夜市"))]
MAX_EVALS_PER_ROUND = 40


# ---------- 行程构造工具（以「每天 POI id 列表」为编辑对象）----------
def to_ids(it: Itinerary) -> List[List[str]]:
    return [[s.poi_id for s in d.stops] for d in it.days]


def build(ids: Sequence[Sequence[str]], hotel: Optional[str] = None,
          cities: Optional[Sequence[str]] = None, transport: str = "transit",
          rests: Optional[list] = None) -> Itinerary:
    cities = list(cities or [])
    return Itinerary(
        days=[Day(stops=[Stop(p) for p in day], city=(cities[i] if i < len(cities) else ""))
              for i, day in enumerate(ids)],
        hotel=hotel or "",
        transport=transport,
        rests=[dict(r) for r in (rests or data.DEFAULT_RESTS)],
    )


def city_ok(ids: Sequence[Sequence[str]], cities: Sequence[str]) -> bool:
    """候选解必须保持“某天的景点都属于该天所在城市”。"""
    if not cities:
        return True
    for day, c in zip(ids, cities):
        for pid in day:
            if data.city_of_poi(pid) != c:
                return False
    return True


def _insert_sorted(day: List[str], pid: str) -> None:
    pos = len(day)
    for i, p in enumerate(day):
        if POI_BY_ID[p].open_min > POI_BY_ID[pid].open_min:
            pos = i
            break
    day.insert(pos, pid)


def op_reorder(ids: List[List[str]], di: int) -> List[List[str]]:
    new = [list(x) for x in ids]
    new[di] = sorted(new[di], key=lambda p: (POI_BY_ID[p].open_min, POI_BY_ID[p].name))
    return new


def op_move(ids: List[List[str]], di: int, si: int, ti: int) -> List[List[str]]:
    new = [list(x) for x in ids]
    pid = new[di].pop(si)
    _insert_sorted(new[ti], pid)
    return new


def op_remove(ids: List[List[str]], di: int, si: int) -> List[List[str]]:
    new = [list(x) for x in ids]
    new[di].pop(si)
    return new


def op_replace(ids: List[List[str]], di: int, si: int, pid: str) -> List[List[str]]:
    new = [list(x) for x in ids]
    new[di][si] = pid
    return new


def op_insert(ids: List[List[str]], di: int, pid: str) -> List[List[str]]:
    new = [list(x) for x in ids]
    _insert_sorted(new[di], pid)
    return new


def op_swap(ids: List[List[str]], d1: int, i1: int, d2: int, i2: int) -> List[List[str]]:
    """两站互换（顺带按开放时间重排两天），用于各天都排满时腾挪空间。"""
    new = [list(x) for x in ids]
    new[d1][i1], new[d2][i2] = new[d2][i2], new[d1][i1]
    for d in (d1, d2):
        new[d] = sorted(new[d], key=lambda p: (POI_BY_ID[p].open_min, POI_BY_ID[p].name))
    return new


# ---------- 候选替代点 ----------
def _alts(pid: str, used: set, profile: Optional[Profile] = None,
          indoor_only: bool = False, limit: int = 3,
          city: Optional[str] = None) -> List[str]:
    base = POI_BY_ID[pid]
    base_tags = set(base.tags)
    prefs = set(profile.prefs) if profile else set()
    scored = []
    for p in POIS:
        if p.id in used or p.id == pid:
            continue
        if city and data.city_of_poi(p.id) != city:
            continue
        overlap = base_tags & set(p.tags)
        if not overlap:
            continue
        if indoor_only and p.outdoor:
            continue
        if p.price > base.price + 60:
            continue
        # 同类优先，其次匹配用户偏好，再按价格/评分
        pref_hit = 1 if (prefs & set(p.tags)) else 0
        scored.append((pref_hit, len(overlap), -p.price, p.rating, p.id))
    scored.sort(reverse=True)
    seen, out = set(), []
    for *_rest, p in scored:
        if p in seen:
            continue
        seen.add(p)
        out.append(p)
        if len(out) >= limit:
            break
    return out


def _has_space(ids: List[List[str]], ti: int, limit: int) -> bool:
    return len(ids[ti]) < limit


# ---------- 候选生成 ----------
def candidates(it: Itinerary, rep: Report, profile: Profile) -> List[Tuple[List[List[str]], str]]:
    ids = to_ids(it)
    used = {p for day in ids for p in day}
    cap = profile.stops_per_day()
    out: List[Tuple[List[List[str]], str]] = []
    days_n = len(ids)

    def add(new_ids, action):
        if new_ids is None:
            return
        if new_ids == ids:
            return
        out.append((new_ids, action))

    def day_of(pid: str) -> Optional[int]:
        for di, day in enumerate(ids):
            if pid in day:
                return di
        return None

    def other_day_with_space(di: int) -> List[int]:
        return [ti for ti in range(days_n) if ti != di and _has_space(ids, ti, cap)]

    for v in rep.hard + rep.soft:
        if v.code in ("OUT_OF_WINDOW", "CLOSED_WEEKDAY", "LONG_WAIT", "DUPLICATE") and v.poi_id and v.day is not None:
            di, pid = v.day, v.poi_id
            if pid not in ids[di]:
                di = day_of(pid)
                if di is None:
                    continue
            si = ids[di].index(pid)
            name = POI_BY_ID[pid].name
            # 0) 必去点放不下时，为它腾出整天空间
            if pid in profile.must_visit:
                for qi, q in enumerate(ids[di]):
                    if q == pid:
                        continue
                    for ti in other_day_with_space(di):
                        add(op_move(ids, di, qi, ti),
                            f"为必去点 {name} 让位：{POI_BY_ID[q].name} 第{di+1}天 → 第{ti+1}天")
                # 各天均已排满时，用互换来腾挪（最多试 4 组）
                swaps = 0
                for ti in range(days_n):
                    if ti == di:
                        continue
                    for si2, q in enumerate(ids[ti]):
                        if swaps >= 4:
                            break
                        swaps += 1
                        add(op_swap(ids, di, si, ti, si2),
                            f"互换腾挪：{name} 与第{ti+1}天 {POI_BY_ID[q].name} 对调")
            # 0b) 必去点要占大半天时，把当天其它点撤掉，让它独占这天
            if pid in profile.must_visit:
                for qi, q in enumerate(ids[di]):
                    if q == pid or q in profile.must_visit:
                        continue
                    add(op_remove(ids, di, qi),
                        f"为必去点 {name} 腾出第{di+1}天：删除 {POI_BY_ID[q].name}")
            # 1) 当天重排
            add(op_reorder(ids, di), f"重排第{di+1}天游览顺序（{name}）")
            # 2) 换日
            for ti in other_day_with_space(di):
                add(op_move(ids, di, si, ti), f"{name} 第{di+1}天 → 第{ti+1}天")
            # 3) 同类替换
            for alt in _alts(pid, used, profile):
                add(op_replace(ids, di, si, alt), f"{name} 替换为 {POI_BY_ID[alt].name}")
            # 4) 删除非必去点
            if pid not in profile.must_visit:
                add(op_remove(ids, di, si), f"删除 {name}")
        elif v.code in ("PACE_EXCESS", "DAY_OVERRUN") and v.day is not None:
            di = v.day
            if not ids[di]:
                continue
            si = len(ids[di]) - 1
            pid = ids[di][si]
            for ti in other_day_with_space(di):
                add(op_move(ids, di, si, ti), f"{POI_BY_ID[pid].name} 第{di+1}天 → 第{ti+1}天")
            if pid not in profile.must_visit:
                add(op_remove(ids, di, si), f"删除 {POI_BY_ID[pid].name}，压缩第{di+1}天行程")
        elif v.code == "BUDGET_EXCEEDED":
            # 价格相同按 id 排序，避免 set 迭代顺序带来的不确定性（同输入同输出）
            paid = sorted({p for day in ids for p in day if POI_BY_ID[p].price > 0},
                          key=lambda p: (-POI_BY_ID[p].price, p))
            for pid in paid[:2]:
                di = day_of(pid)
                if di is None:
                    continue
                si = ids[di].index(pid)
                for alt in _alts(pid, used, profile):
                    if POI_BY_ID[alt].price < POI_BY_ID[pid].price:
                        add(op_replace(ids, di, si, alt),
                            f"{POI_BY_ID[pid].name} 替换为更便宜的 {POI_BY_ID[alt].name}")
                if pid not in profile.must_visit:
                    add(op_remove(ids, di, si), f"删除付费点 {POI_BY_ID[pid].name} 以控制预算")
        elif v.code == "MISSING_MUST_VISIT" and v.poi_id:
            pid = v.poi_id
            for ti in range(days_n):
                if _has_space(ids, ti, cap):
                    add(op_insert(ids, ti, pid), f"插入必去点 {POI_BY_ID[pid].name} 到第{ti+1}天")
        elif v.code == "RAIN_OUTDOOR" and v.poi_id and v.day is not None:
            di, pid = v.day, v.poi_id
            if pid not in ids[di]:
                continue
            si = ids[di].index(pid)
            for alt in _alts(pid, used, profile, indoor_only=True):
                add(op_replace(ids, di, si, alt), f"雨天将 {POI_BY_ID[pid].name} 替换为室内 {POI_BY_ID[alt].name}")
            for ti in range(days_n):
                if ti != di and it.days[ti].weather.get("rain", 0) < 60 and _has_space(ids, ti, cap):
                    add(op_move(ids, di, si, ti), f"{POI_BY_ID[pid].name} 移到第{ti+1}天（无雨）")
            if pid not in profile.must_visit:
                add(op_remove(ids, di, si), f"删除雨天户外点 {POI_BY_ID[pid].name}")
        elif v.code == "REST_MISSING" and v.day is not None:
            di = v.day
            for mid in meal_ids():
                if mid in used:
                    continue
                if _has_space(ids, di, cap):
                    add(op_insert(ids, di, mid), f"第{di+1}天插入餐饮节点 {POI_BY_ID[mid].name}")
                for si, pid in enumerate(ids[di]):
                    if pid not in profile.must_visit:
                        add(op_replace(ids, di, si, mid), f"第{di+1}天以 {POI_BY_ID[mid].name} 替换 {POI_BY_ID[pid].name}")
    # 5b) 跨天对调：某天塞不下的点时，用别的天的点把它换出来（同城换同城，站数不变）
    seen_hard = set()
    swaps = 0
    for v in rep.hard:
        if swaps >= 8:
            break
        if not v.poi_id or v.day is None:
            continue
        di = v.day if v.poi_id in ids[v.day] else day_of(v.poi_id)
        if di is None or v.poi_id not in ids[di]:
            continue
        si = ids[di].index(v.poi_id)
        if (di, si) in seen_hard:
            continue
        seen_hard.add((di, si))
        for ti in range(days_n):
            if ti == di:
                continue
            for si2 in range(len(ids[ti])):
                if swaps >= 8:
                    break
                swaps += 1
                add(op_swap(ids, di, si, ti, si2),
                    f"跨天对调：{POI_BY_ID[v.poi_id].name} 与第{ti+1}天 {POI_BY_ID[ids[ti][si2]].name} 互换")
    # 偏好匹配不足：用偏好标签命中的闲置景点替换低匹配站点
    if any(v.code == "PREF_LOW" for v in rep.soft):
        prefs = set(profile.prefs)
        pool = [p.id for p in POIS if p.id not in used and (prefs & set(p.tags))]
        pool.sort(key=lambda p: -POI_BY_ID[p].rating)
        for di, day in enumerate(ids):
            for si, pid in enumerate(day):
                if pid in profile.must_visit or (prefs & set(POI_BY_ID[pid].tags)):
                    continue
                for alt in pool[:2]:
                    add(op_replace(ids, di, si, alt),
                        f"偏好补强：{POI_BY_ID[pid].name} → {POI_BY_ID[alt].name}")
    return out


def _sig(ids: List[List[str]]) -> str:
    return "|".join(",".join(day) for day in ids)


def run(it: Itinerary, profile: Profile, rep: Optional[Report] = None,
        max_rounds: int = 15, dedup: bool = True,
        strategy: str = "first") -> Tuple[Itinerary, Report, int, List[dict]]:
    """迭代修复，返回（行程，校验报告，修复轮数，修复轨迹）。

    dedup=False 关闭访问去重（消融 E2）；strategy="best" 改为最优改进（消融 E3）。
    """
    rep = rep or verifier.verify(it, profile)
    cities = [d.city for d in it.days]
    trace: List[dict] = []
    visited = {_sig(to_ids(it))}
    rounds = 0

    while rounds < max_rounds and not (rep.feasible and rep.soft_failed == 0):
        best = None
        evals = 0
        for ids, action in candidates(it, rep, profile):
            evals += 1
            if evals > MAX_EVALS_PER_ROUND:
                break
            if not city_ok(ids, cities):
                continue
            sig = _sig(ids)
            if dedup and sig in visited:
                continue
            cand = build(ids, None, cities, it.transport, it.rests)
            verifier.schedule(cand, origin=profile.origin, return_home=profile.return_home)
            r = verifier.verify(cand, profile)
            if r.score() < rep.score():
                if strategy == "first":
                    best = (cand, r, sig, action)
                    break
                if best is None or r.score() < best[1].score():
                    best = (cand, r, sig, action)
        if best is None:
            break
        it, rep, sig, action = best
        visited.add(sig)
        rounds += 1
        trace.append({
            "round": rounds,
            "action": action,
            "hard": rep.hard_failed,
            "soft": rep.soft_failed,
            "csr": round(rep.csr, 4),
            "codes": sorted({v.code for v in rep.violations}),
        })
    return it, rep, rounds, trace