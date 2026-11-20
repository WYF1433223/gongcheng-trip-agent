# -*- coding: utf-8 -*-
"""领域模型：用户需求、行程、冲突、运行结果。"""
from __future__ import annotations

from dataclasses import dataclass, field

from . import data as _data
from typing import Dict, List, Optional


@dataclass
class Profile:
    days: int = 3
    budget: int = 3000
    party: str = "情侣"
    pace: str = "normal"          # relaxed / normal / intense
    transport: str = "transit"    # walk / transit / taxi
    prefs: List[str] = field(default_factory=list)
    must_visit: List[str] = field(default_factory=list)
    hotel: str = field(default_factory=lambda: _data.DEFAULT_HOTEL)
    start_date: str = ""          # ISO 日期；空表示“从今天开始”
    cities: List[str] = field(default_factory=list)   # 多城市行程（顺序即访问顺序）
    city_days: List[int] = field(default_factory=list)  # 每个城市停留天数
    rests: List[dict] = field(default_factory=lambda: [dict(r) for r in _data.DEFAULT_RESTS])
    origin: str = ""              # 出发地（城市 key，可为空表示就在首城）
    return_home: bool = True      # 最后一天是否回到出发地

    def stops_per_day(self) -> int:
        return {"relaxed": 3, "normal": 4, "intense": 5}[self.pace]

    def city_plan(self) -> List[tuple]:
        """返回 [(城市 key, 天数)]；单城市时退化为当前城市。"""
        if not self.cities:
            key = _data.CITY_KEYS[0] if _data.CITY_KEYS else _data.CITY
            return [(key, self.days)]
        keys = list(self.cities)
        days = list(self.city_days or [])
        if len(days) != len(keys) or sum(days) != self.days:
            days = _distribute(self.days, len(keys))
        return [(k, d) for k, d in zip(keys, days) if d > 0]

    def date_of(self, i: int) -> str:
        from datetime import date, timedelta
        start = self.start_dt()
        return (start + timedelta(days=i)).isoformat()

    def start_dt(self):
        from datetime import date
        if self.start_date:
            try:
                return date.fromisoformat(self.start_date[:10])
            except ValueError:
                pass
        return date.today()

    def weekday_of(self, i: int) -> int:
        return self.start_dt().weekday() + i

    def to_dict(self) -> dict:
        return {
            "days": self.days, "budget": self.budget, "party": self.party,
            "pace": self.pace, "transport": self.transport, "prefs": self.prefs,
            "must_visit": self.must_visit, "hotel": self.hotel,
            "cities": list(self.cities), "city_days": list(self.city_days),
            "rests": [dict(r) for r in self.rests],
            "origin": self.origin, "return_home": self.return_home,
        }


@dataclass
class Stop:
    poi_id: str
    arrive: int = 0               # 到达时刻（分钟）
    depart: int = 0               # 离开时刻
    from_prev: int = 0            # 上一站到本站交通时间
    wait: int = 0                 # 早到等待时间
    via: dict = field(default_factory=dict)   # 本段推荐出行方案（multimodal.plan）

    def to_dict(self) -> dict:
        return {"poi_id": self.poi_id, "arrive": self.arrive,
                "depart": self.depart, "from_prev": self.from_prev, "wait": self.wait,
                "via": self.via}


@dataclass
class Day:
    stops: List[Stop] = field(default_factory=list)
    weather: dict = field(default_factory=dict)
    city: str = ""               # 该天所在城市 key
    leg: dict = field(default_factory=dict)   # 城际交通信息（跨市当天）
    rest_done: List[bool] = field(default_factory=list)   # 每个休息时段是否已安排
    last_leave: int = 0          # 当天最后一站离开时刻

    def rest_total(self, rests) -> int:
        return sum(r["minutes"] for r, ok in zip(rests, self.rest_done) if ok)

    def to_dict(self) -> dict:
        return {"weather": self.weather, "city": self.city, "leg": self.leg,
                "rest_done": list(self.rest_done),
                "city_name": _data.CITY_META.get(self.city, {}).get("name", self.city),
                "stops": [s.to_dict() for s in self.stops]}


@dataclass
class Itinerary:
    days: List[Day] = field(default_factory=list)
    hotel: str = field(default_factory=lambda: _data.DEFAULT_HOTEL)
    transport: str = "transit"    # auto / walk / transit / taxi
    rests: List[dict] = field(default_factory=lambda: [dict(r) for r in _data.DEFAULT_RESTS])
    origin_leg: dict = field(default_factory=dict)   # 出发地 → 首城
    return_leg: dict = field(default_factory=dict)   # 末城 → 出发地

    def clone(self) -> "Itinerary":
        return Itinerary(
            days=[Day([Stop(s.poi_id, s.arrive, s.depart, s.from_prev, s.wait, dict(s.via))
                       for s in d.stops],
                      dict(d.weather), d.city, dict(d.leg), list(d.rest_done))
                  for d in self.days],
            hotel=self.hotel,
            transport=self.transport,
            rests=[dict(r) for r in self.rests],
            origin_leg=dict(self.origin_leg),
            return_leg=dict(self.return_leg),
        )

    def ids(self) -> List[str]:
        return [s.poi_id for d in self.days for s in d.stops]

    def to_dict(self) -> dict:
        return {"hotel": self.hotel, "transport": self.transport,
                "origin_leg": self.origin_leg, "return_leg": self.return_leg,
                "days": [d.to_dict() for d in self.days]}


def _distribute(total: int, n: int) -> List[int]:
    """把总天数尽量平均分给 n 个城市，余数优先给前面的城市。"""
    if n <= 0:
        return []
    base, rem = divmod(total, n)
    days = [base + (1 if i < rem else 0) for i in range(n)]
    if base == 0:                     # 天数少于城市数：只保留前 total 个
        days = [1] * total + [0] * (n - total)
    return days


@dataclass
class Violation:
    code: str
    severity: str          # hard / soft
    day: Optional[int]     # 0-based
    poi_id: Optional[str]
    message: str
    fix: str = ""          # 建议修复方向

    def key(self) -> tuple:
        return (self.code, self.day, self.poi_id)

    def to_dict(self) -> dict:
        return {"code": self.code, "severity": self.severity, "day": self.day,
                "poi_id": self.poi_id, "message": self.message, "fix": self.fix}


@dataclass
class Report:
    violations: List[Violation] = field(default_factory=list)
    hard_total: int = 0
    hard_failed: int = 0
    soft_total: int = 0
    soft_failed: int = 0

    @property
    def hard(self) -> List[Violation]:
        return [v for v in self.violations if v.severity == "hard"]

    @property
    def soft(self) -> List[Violation]:
        return [v for v in self.violations if v.severity == "soft"]

    @property
    def csr(self) -> float:
        if self.hard_total == 0:
            return 1.0
        return max(0.0, 1 - self.hard_failed / self.hard_total)

    @property
    def feasible(self) -> bool:
        return self.hard_failed == 0

    def score(self) -> tuple:
        """字典序：硬冲突数、软冲突数，用于修复过程接受/拒绝。"""
        return (self.hard_failed, self.soft_failed, len(self.violations))

    def to_dict(self) -> dict:
        return {
            "violations": [v.to_dict() for v in self.violations],
            "hard_total": self.hard_total, "hard_failed": self.hard_failed,
            "soft_total": self.soft_total, "soft_failed": self.soft_failed,
            "csr": round(self.csr, 4), "feasible": self.feasible,
        }


@dataclass
class Metrics:
    cost: int = 0
    pref_score: float = 0.0
    tool_calls: int = 0
    repair_rounds: int = 0
    latency_ms: float = 0.0
    stops: int = 0

    def to_dict(self) -> dict:
        return {"cost": self.cost, "pref_score": round(self.pref_score, 4),
                "tool_calls": self.tool_calls, "repair_rounds": self.repair_rounds,
                "latency_ms": self.latency_ms, "stops": self.stops}


@dataclass
class RunResult:
    mode: str                    # single_pass / closed_loop
    profile: Profile
    itinerary: Itinerary
    report: Report
    metrics: Metrics
    trace: List[dict] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {
            "mode": self.mode, "profile": self.profile.to_dict(),
            "itinerary": self.itinerary.to_dict(), "report": self.report.to_dict(),
            "metrics": self.metrics.to_dict(), "trace": self.trace,
        }