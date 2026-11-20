# -*- coding: utf-8 -*-
"""编排层：一次生成 / 闭环修复 两种模式，输出统一结构供 UI 与实验脚本使用。"""
from __future__ import annotations

import time
from typing import Dict, List

from . import data, repair, verifier
from .data import DEFAULT_HOTEL, POIS
from .model import Metrics, Profile, RunResult
from .planner import plan_once


def run(profile: Profile, mode: str = "closed_loop", max_rounds: int = 15,
        dedup: bool = True, strategy: str = "first", cache: bool = True) -> RunResult:
    keys = profile.cities or ([data.CITY_KEYS[0]] if data.CITY_KEYS else [data.CITY])
    data.use_cities(keys)

    t0 = time.perf_counter()
    it = plan_once(profile)
    verifier.schedule(it, origin=profile.origin, return_home=profile.return_home)
    rep = verifier.verify(it, profile)

    tool_calls = 2 + profile.days          # 意图解析 + POI 检索 + 逐日天气
    rounds = 0
    trace: List[Dict] = [{
        "round": 0,
        "action": "一次性生成初稿，未做外部校验",
        "hard": rep.hard_failed,
        "soft": rep.soft_failed,
        "csr": round(rep.csr, 4),
        "codes": sorted({v.code for v in rep.violations}),
    }]

    if mode == "closed_loop":
        it, rep, rounds, r_trace = repair.run(
            it, profile, rep, max_rounds, dedup=dedup, strategy=strategy)
        trace += r_trace
        if cache:
            tool_calls += 2 * rounds + 1    # 每轮：约束校验 + 地图重算；末尾一次复查
        else:
            stops_now = len(it.ids())
            tool_calls += rounds * (2 + stops_now) + 1   # 无缓存：逐站重查通勤与门票
        verifier.schedule(it, origin=profile.origin, return_home=profile.return_home)
        rep = verifier.verify(it, profile)

    verifier.schedule(it, origin=profile.origin, return_home=profile.return_home)
    latency = round((time.perf_counter() - t0) * 1000, 1)
    metrics = Metrics(
        cost=verifier.cost_of(it),
        pref_score=verifier.pref_score(it, profile),
        tool_calls=tool_calls,
        repair_rounds=rounds,
        latency_ms=latency,
        stops=len(it.ids()),
    )
    return RunResult(mode=mode, profile=profile, itinerary=it, report=rep, metrics=metrics, trace=trace)


def run_compare(profile: Profile) -> Dict[str, dict]:
    base = run(profile, "single_pass")
    loop = run(profile, "closed_loop")
    return {"profile": profile.to_dict(), "single_pass": base.to_dict(), "closed_loop": loop.to_dict()}


def summarize(result: RunResult) -> Dict[str, object]:
    rep = result.report
    return {
        "mode": result.mode,
        "feasible": rep.feasible,
        "csr": round(rep.csr, 4),
        "hard_failed": rep.hard_failed,
        "soft_failed": rep.soft_failed,
        "cost": result.metrics.cost,
        "pref_score": round(result.metrics.pref_score, 4),
        "tool_calls": result.metrics.tool_calls,
        "rounds": result.metrics.repair_rounds,
        "violations": [v.to_dict() for v in rep.violations],
    }


if __name__ == "__main__":  # 命令行快速对比：python -m agent.engine
    demo = Profile(days=3, budget=3000, party="情侣", pace="normal",
                   prefs=["历史人文", "美食"], must_visit=[POIS[0].id], hotel=DEFAULT_HOTEL)
    for mode in ("single_pass", "closed_loop"):
        res = summarize(run(demo, mode))
        print(f"{mode:12s} CSR={res['csr']:.2%} 硬冲突={res['hard_failed']} "
              f"软冲突={res['soft_failed']} 花费={res['cost']}元 "
              f"偏好={res['pref_score']:.2%} 调用={res['tool_calls']} "
              f"轮次={res['rounds']}")