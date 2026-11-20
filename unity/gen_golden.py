# -*- coding: utf-8 -*-
"""生成 C# 端一致性验收用的 golden 数据。

用法（项目根目录）：
    python unity/gen_golden.py            # 写出 unity/golden.json
"""
from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from agent import engine  # noqa: E402
from agent.data import DEFAULT_RESTS  # noqa: E402
from agent.model import Profile  # noqa: E402
from experiments import PROFILES  # noqa: E402

START_DATE = "2026-11-16"   # 固定周一，闭馆日判定与真实日历对齐


def profile_from(d: dict) -> Profile:
    return Profile(
        days=d["days"], budget=d["budget"], party=d["party"], pace=d["pace"],
        transport=d.get("transport", "transit"),
        prefs=list(d.get("prefs") or []), must_visit=list(d.get("must") or []),
        hotel=d.get("hotel", ""), start_date=START_DATE,
        cities=list(d.get("cities") or []), city_days=list(d.get("city_days") or []),
        origin=d.get("origin", ""), return_home=d.get("return_home", True),
        rests=[dict(r) for r in (d.get("rests") or [])] or [dict(r) for r in DEFAULT_RESTS],
    )


def snapshot(res) -> dict:
    it, rep, m = res.itinerary, res.report, res.metrics
    return {
        "csr": round(rep.csr, 4),
        "hard_total": rep.hard_total,
        "hard_failed": rep.hard_failed,
        "soft_total": rep.soft_total,
        "soft_failed": rep.soft_failed,
        "cost": m.cost,
        "pref_score": round(m.pref_score, 4),
        "tool_calls": m.tool_calls,
        "rounds": m.repair_rounds,
        "stops": m.stops,
        "origin_leg": (it.origin_leg or {}).get("code", "") and {
            "code": it.origin_leg["code"], "dep": it.origin_leg["dep"],
            "arr": it.origin_leg["arr"], "run": it.origin_leg["run"],
            "fare": it.origin_leg["fare"], "dep_station": it.origin_leg["dep_station"],
            "arr_station": it.origin_leg["arr_station"], "km": it.origin_leg["km"],
        } or {},
        "return_leg": (it.return_leg or {}).get("code", "") and {
            "code": it.return_leg["code"], "dep": it.return_leg["dep"],
            "arr": it.return_leg["arr"], "run": it.return_leg["run"],
            "fare": it.return_leg["fare"], "dep_station": it.return_leg["dep_station"],
            "arr_station": it.return_leg["arr_station"], "km": it.return_leg["km"],
        } or {},
        "days": [
            {
                "city": d.city, "rest_done": list(d.rest_done),
                "rain": int(d.weather.get("rain", 0)),
                "leg": {"to": (d.leg or {}).get("to", ""),
                        "minutes": (d.leg or {}).get("run", 0),
                        "fare": (d.leg or {}).get("fare", 0),
                        "km": (d.leg or {}).get("km", 0),
                        "code": (d.leg or {}).get("code", ""),
                        "dep": (d.leg or {}).get("dep", 0),
                        "arr": (d.leg or {}).get("arr", 0),
                        "dep_station": (d.leg or {}).get("dep_station", ""),
                        "arr_station": (d.leg or {}).get("arr_station", "")},
                "last_leave": d.last_leave,
                "stops": [{"id": s.poi_id, "arrive": s.arrive, "depart": s.depart,
                           "from_prev": s.from_prev, "wait": s.wait,
                           "via_minutes": (s.via or {}).get("minutes", 0),
                           "via_cost": (s.via or {}).get("cost", 0),
                           "via_headline": (s.via or {}).get("headline", "")}
                          for s in d.stops],
            }
            for d in it.days
        ],
        "violations": [
            {"code": v.code, "severity": v.severity, "day": v.day, "poi_id": v.poi_id}
            for v in rep.violations
        ],
    }


def main() -> None:
    cases = []
    for i, d in enumerate(PROFILES, start=1):
        prof = profile_from(d)
        case = {"name": f"P{i:02d}", "profile": prof.to_dict(), "modes": {}}
        for mode in ("single_pass", "closed_loop"):
            case["modes"][mode] = snapshot(engine.run(prof, mode))
        cases.append(case)

    # 跨市档案：南昌 → 九江 → 景德镇
    extras = [
        ("T3", {"days": 6, "budget": 4500, "party": "情侣", "pace": "normal",
                "prefs": ["历史人文", "美食"], "must": [], "hotel": "",
                "cities": ["nanchang", "jiujiang", "jingdezhen"], "city_days": [2, 2, 2]}),
        # 预算吃紧：强制触发 BUDGET_EXCEEDED 分支（含价格并列的取舍）
        ("T4", {"days": 2, "budget": 300, "party": "情侣", "pace": "normal",
                "prefs": ["历史人文"], "must": ["panda"], "hotel": "春熙路",
                "cities": ["chengdu"]}),
        # 雨天户外：成都第 3 天小雨 75%，触发 RAIN_OUTDOOR 的替换/移日算子
        ("T5", {"days": 3, "budget": 2500, "party": "家人", "pace": "normal",
                "prefs": ["自然风光"], "must": ["qingcheng"], "hotel": "春熙路",
                "cities": ["chengdu"]}),
        # 出发地 + 自动推荐出行方式 + 跨城高铁：长沙 → 南昌 → 九江 → 景德镇 → 长沙
        ("T6", {"days": 6, "budget": 4500, "party": "情侣", "pace": "normal",
                "transport": "auto", "origin": "changsha", "return_home": True,
                "prefs": ["历史人文", "美食"], "must": [], "hotel": "",
                "cities": ["nanchang", "jiujiang", "jingdezhen"], "city_days": [2, 2, 2]}),
        # 单城 + 自动推荐：检验长距离段是否给出“打车+轨道+步行”组合
        ("T7", {"days": 3, "budget": 3000, "party": "情侣", "pace": "normal",
                "transport": "auto", "prefs": ["自然风光", "历史人文"],
                "must": ["qingcheng"], "hotel": "春熙路", "cities": ["chengdu"]}),
    ]
    for name, d in extras:
        prof = profile_from(d)
        case = {"name": name, "profile": prof.to_dict(), "modes": {}}
        for mode in ("single_pass", "closed_loop"):
            case["modes"][mode] = snapshot(engine.run(prof, mode))
        cases.append(case)

    out = {"start_date": START_DATE, "cases": cases}
    path = os.path.join(HERE, "golden.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print(f"写出 {path}：{len(cases)} 组档案")
    for c in cases:
        sp, cl = c["modes"]["single_pass"], c["modes"]["closed_loop"]
        print(f"  {c['name']}: 一次性 CSR={sp['csr']:.2%} 硬={sp['hard_failed']} | "
              f"闭环 CSR={cl['csr']:.2%} 硬={cl['hard_failed']} 轮={cl['rounds']} 调用={cl['tool_calls']}")


if __name__ == "__main__":
    main()