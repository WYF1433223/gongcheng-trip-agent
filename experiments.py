# -*- coding: utf-8 -*-
"""实验脚本：跑 E1~E5 五组实验，输出 CSV / Markdown / 图表。

用法（项目根目录）：
    python experiments.py            # 跑全部实验并出图
    python experiments.py --no-fig   # 只出表不出图

设计说明：
- 所有档案固定写在 PROFILES 里，不用随机数，保证任何机器上结果一致。
- 指标口径与报告表 7-1 一致：CSR、ER、PREF、CALL、RND、T 均由引擎计算。
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import statistics as st
import time

from agent import data, engine
from agent.model import Profile

if data.CITY != "chengdu":
    raise SystemExit("experiments.py 的 20 组档案是按成都数据写的；"
                     "换城市请先改 PROFILES，或设置 CITY=chengdu 再跑。")

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "results")
MAX_ROUNDS = 15

# ---------------------------------------------------------------- 20 组档案
PROFILES = [
    # 天数 预算 同行人 节奏 偏好 必去 住宿
    dict(days=3, budget=3000, party="情侣", pace="normal",
         prefs=["历史人文", "美食"], must=["panda"], hotel="春熙路"),
    dict(days=3, budget=3000, party="情侣", pace="relaxed",
         prefs=["艺术展馆", "夜景"], must=[], hotel="宽窄巷子"),
    dict(days=3, budget=2500, party="同学", pace="intense",
         prefs=["购物", "美食"], must=["taikoo"], hotel="春熙路"),
    dict(days=2, budget=1200, party="独自", pace="relaxed",
         prefs=["历史人文"], must=["wuhouci"], hotel="武侯祠"),
    dict(days=2, budget=1500, party="情侣", pace="normal",
         prefs=["夜景", "美食"], must=["jiuyanqiao"], hotel="春熙路"),
    dict(days=4, budget=5000, party="家人", pace="normal",
         prefs=["亲子", "自然风光"], must=["panda"], hotel="春熙路"),
    dict(days=4, budget=4000, party="家人", pace="relaxed",
         prefs=["亲子", "历史人文"], must=["panda", "jinsha"], hotel="宽窄巷子"),
    dict(days=5, budget=2000, party="家人", pace="intense",
         prefs=["亲子"], must=["qingcheng", "panda"], hotel="武侯祠"),
    dict(days=5, budget=8000, party="情侣", pace="normal",
         prefs=["自然风光", "艺术展馆"], must=["dujiangyan"], hotel="春熙路"),
    dict(days=5, budget=6000, party="同学", pace="normal",
         prefs=["历史人文", "夜景"], must=["jinli", "jiuyanqiao"], hotel="春熙路"),
    dict(days=3, budget=2000, party="同学", pace="intense",
         prefs=["美食", "购物"], must=[], hotel="春熙路"),
    dict(days=2, budget=900, party="独自", pace="normal",
         prefs=["美食"], must=[], hotel="春熙路"),
    dict(days=3, budget=6000, party="情侣", pace="relaxed",
         prefs=["艺术展馆", "夜景"], must=["tianfuart"], hotel="宽窄巷子"),
    dict(days=4, budget=3500, party="同学", pace="normal",
         prefs=["历史人文", "购物"], must=["scmuseum"], hotel="春熙路"),
    dict(days=4, budget=3000, party="家人", pace="relaxed",
         prefs=["亲子", "夜景"], must=["pandatower"], hotel="春熙路"),
    dict(days=3, budget=1800, party="情侣", pace="normal",
         prefs=["自然风光", "美食"], must=["qingcheng"], hotel="武侯祠"),
    dict(days=5, budget=4000, party="家人", pace="normal",
         prefs=["历史人文", "亲子"], must=["dufu", "panda"], hotel="武侯祠"),
    dict(days=2, budget=2200, party="情侣", pace="intense",
         prefs=["购物", "艺术展馆"], must=["global"], hotel="春熙路"),
    dict(days=4, budget=5500, party="同学", pace="normal",
         prefs=["夜景", "美食", "购物"], must=["taikoo", "jiuyanqiao"], hotel="春熙路"),
    dict(days=3, budget=2500, party="独自", pace="normal",
         prefs=["历史人文", "艺术展馆"], must=["scmuseum"], hotel="宽窄巷子"),
]

CROSS_CITY = ["dujiangyan", "qingcheng"]
SAME_CITY = ["panda", "wuhouci", "kuanzhai", "jinsha"]


def P(**kw) -> Profile:
    if "must" in kw:
        kw["must_visit"] = kw.pop("must")
    return Profile(**kw)


def run_one(profile: Profile, **kw):
    r = engine.run(profile, **kw)
    m, rep = r.metrics, r.report
    return {
        "csr": round(rep.csr, 4),
        "feasible": 1 if rep.feasible else 0,
        "hard": rep.hard_failed,
        "soft": rep.soft_failed,
        "pref": round(m.pref_score, 4),
        "calls": m.tool_calls,
        "rounds": m.repair_rounds,
        "cost": m.cost,
        "stops": m.stops,
        "ms": m.latency_ms,
    }


def mean(rows, key):
    return round(st.mean(r[key] for r in rows), 4) if rows else 0.0


def agg(rows):
    return {
        "n": len(rows),
        "csr": mean(rows, "csr"),
        "er": mean(rows, "feasible"),
        "pref": mean(rows, "pref"),
        "calls": mean(rows, "calls"),
        "rounds": mean(rows, "rounds"),
        "ms": mean(rows, "ms"),
        "cost": mean(rows, "cost"),
        "stops": mean(rows, "stops"),
    }


# ---------------------------------------------------------------- E1
def e1(profiles):
    base, loop = [], []
    for p in profiles:
        base.append(run_one(p, mode="single_pass"))
        loop.append(run_one(p, mode="closed_loop", max_rounds=MAX_ROUNDS))
    return base, loop


# ---------------------------------------------------------------- E2 / E3
def e2_e3(profiles):
    full, nodedup, bestimp = [], [], []
    for p in profiles:
        full.append(run_one(p, mode="closed_loop", max_rounds=MAX_ROUNDS))
        nodedup.append(run_one(p, mode="closed_loop", max_rounds=MAX_ROUNDS, dedup=False))
        bestimp.append(run_one(p, mode="closed_loop", max_rounds=MAX_ROUNDS, strategy="best"))
    return full, nodedup, bestimp


# ---------------------------------------------------------------- E4
def e4(base_profiles):
    same, cross = [], []
    for i, p0 in enumerate(base_profiles[:10]):
        kw = dict(p0)
        kw["must"] = [SAME_CITY[i % len(SAME_CITY)], SAME_CITY[(i + 1) % len(SAME_CITY)]]
        same.append(run_one(P(**kw), mode="closed_loop", max_rounds=MAX_ROUNDS))
        kw2 = dict(p0)
        kw2["must"] = CROSS_CITY
        cross.append(run_one(P(**kw2), mode="closed_loop", max_rounds=MAX_ROUNDS))
    return same, cross


# ---------------------------------------------------------------- E5
def e5(profiles):
    on, off = [], []
    for p in profiles:
        on.append(run_one(p, mode="closed_loop", max_rounds=MAX_ROUNDS, cache=True))
        off.append(run_one(p, mode="closed_loop", max_rounds=MAX_ROUNDS, cache=False))
    return on, off


# ---------------------------------------------------------------- 输出
def write_csv(name, profiles, base, loop):
    path = os.path.join(OUT, name)
    with open(path, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f)
        w.writerow(["档案", "天数", "预算", "同行人", "节奏", "偏好", "必去",
                    "模式", "CSR", "硬冲突", "软冲突", "PREF", "CALL", "RND", "花费", "站点", "耗时ms"])
        for i, (p, b, c) in enumerate(zip(profiles, base, loop), 1):
            for tag, r in (("一次性生成", b), ("闭环修复", c)):
                w.writerow([f"P{i:02d}", p.days, p.budget, p.party, p.pace,
                            "/".join(p.prefs), "/".join(p.must_visit), tag,
                            r["csr"], r["hard"], r["soft"], r["pref"],
                            r["calls"], r["rounds"], r["cost"], r["stops"], r["ms"]])
    return path


def fmt(x):
    return f"{x:.4f}" if isinstance(x, float) else str(x)


def write_md(res):
    L = []
    A = res["e1"]["base"]
    B = res["e1"]["loop"]
    L.append("# 实验结果（真实运行数据）\n")
    L.append(f"- 生成时间：{res['meta']['time']}")
    L.append(f"- 档案数：{A['n']}，最大修复轮数 {MAX_ROUNDS}，Python {res['meta']['python']}")
    L.append(f"- 单次规划耗时中位数（闭环）：{res['meta']['median_ms']} ms\n")

    L.append("## E1 主实验：一次性生成 vs 闭环修复\n")
    L.append("| 指标 | 一次性生成 | 闭环修复 | 变化 |")
    L.append("| --- | --- | --- | --- |")
    rows = [
        ("CSR 均值", A["csr"], B["csr"], "pct"),
        ("可执行率 ER", A["er"], B["er"], "pct"),
        ("偏好匹配率 PREF", A["pref"], B["pref"], "pct"),
        ("平均工具调用 CALL", A["calls"], B["calls"], "num"),
        ("平均修复轮次 RND", A["rounds"], B["rounds"], "num"),
        ("平均站点数", A["stops"], B["stops"], "num"),
        ("平均耗时 T（ms）", A["ms"], B["ms"], "num"),
    ]
    for name, a, b, kind in rows:
        d = b - a
        if kind == "pct":
            L.append(f"| {name} | {a*100:.2f}% | {b*100:.2f}% | {d*100:+.2f} pp |")
        else:
            L.append(f"| {name} | {a:.2f} | {b:.2f} | {d:+.2f} |")

    L.append(f"\n- 一次性生成中硬冲突全员均有的档案：{res['e1']['base_bad']}/{A['n']}")
    L.append(f"- 闭环修复后硬冲突清零的档案：{res['e1']['loop_ok']}/{B['n']}\n")

    L.append("## E2 消融：去掉访问去重\n")
    C = res["e2"]
    L.append("| 指标 | 完整修复 | 无去重 |")
    L.append("| --- | --- | --- |")
    L.append(f"| 平均修复轮次 | {C['full']['rounds']:.2f} | {C['nodup']['rounds']:.2f} |")
    L.append(f"| 平均 CSR | {C['full']['csr']*100:.2f}% | {C['nodup']['csr']*100:.2f}% |")
    L.append(f"| 平均耗时（ms） | {C['full']['ms']:.2f} | {C['nodup']['ms']:.2f} |")
    L.append(f"| 触顶档案数（轮次=max） | {C['full']['hit_cap']} | {C['nodup']['hit_cap']} |\n")

    L.append("## E3 消融：最优改进 vs 首改进\n")
    D = res["e3"]
    L.append("| 指标 | 首改进（默认） | 最优改进 |")
    L.append("| --- | --- | --- |")
    L.append(f"| 平均 CSR | {D['first']['csr']*100:.2f}% | {D['best']['csr']*100:.2f}% |")
    L.append(f"| 可执行率 | {D['first']['er']*100:.2f}% | {D['best']['er']*100:.2f}% |")
    L.append(f"| 平均耗时（ms） | {D['first']['ms']:.2f} | {D['best']['ms']:.2f} |")
    L.append(f"| 平均偏好 | {D['first']['pref']*100:.2f}% | {D['best']['pref']*100:.2f}% |\n")

    L.append("## E4 鲁棒性：同城 vs 跨城必去点\n")
    E = res["e4"]
    L.append("| 指标 | 同城必去 | 跨城必去（都江堰/青城山） |")
    L.append("| --- | --- | --- |")
    L.append(f"| 可执行率 | {E['same']['er']*100:.2f}% | {E['cross']['er']*100:.2f}% |")
    L.append(f"| 平均 CSR | {E['same']['csr']*100:.2f}% | {E['cross']['csr']*100:.2f}% |")
    L.append(f"| 平均修复轮次 | {E['same']['rounds']:.2f} | {E['cross']['rounds']:.2f} |\n")

    L.append("## E5 成本：缓存开关\n")
    F = res["e5"]
    L.append("| 指标 | 有缓存 | 无缓存 |")
    L.append("| --- | --- | --- |")
    L.append(f"| 平均工具调用 | {F['on']['calls']:.2f} | {F['off']['calls']:.2f} |")
    L.append(f"| 平均耗时（ms） | {F['on']['ms']:.2f} | {F['off']['ms']:.2f} |\n")
    open(os.path.join(OUT, "summary.md"), "w", encoding="utf-8").write("\n".join(L))


def figures(res):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.pyplot as plt
        from matplotlib import font_manager as fm
    except Exception as e:  # pragma: no cover
        print("跳过绘图：", e)
        return []
    fp = "C:/Windows/Fonts/simhei.ttf"
    if os.path.exists(fp):
        fm.fontManager.addfont(fp)
        plt.rcParams["font.sans-serif"] = ["SimHei"]
    plt.rcParams["axes.unicode_minus"] = False
    made = []

    # 图1：E1 逐档案 CSR 对比
    base = res["e1"]["rows_base"]
    loop = res["e1"]["rows_loop"]
    xs = list(range(1, len(base) + 1))
    fig, ax = plt.subplots(figsize=(7.2, 3.2), dpi=200)
    w = 0.38
    ax.bar([x - w / 2 for x in xs], [r["csr"] * 100 for r in base], w,
           label="一次性生成", color="#c0504d")
    ax.bar([x + w / 2 for x in xs], [r["csr"] * 100 for r in loop], w,
           label="闭环修复", color="#4f81bd")
    ax.set_xlabel("档案编号")
    ax.set_ylabel("约束满足率 CSR（%）")
    ax.set_ylim(55, 104)
    ax.set_xticks(xs)
    ax.legend(frameon=False, ncol=2, loc="lower center", bbox_to_anchor=(0.5, 1.0))
    ax.spines[["top", "right"]].set_visible(False)
    fig.tight_layout()
    p = os.path.join(OUT, "fig_e1_csr.png")
    fig.savefig(p)
    plt.close(fig)
    made.append(p)

    # 图2：E2/E3 消融
    fig, axes = plt.subplots(1, 2, figsize=(7.2, 2.9), dpi=200)
    labels = ["完整", "无去重"]
    vals = [res["e2"]["full"]["rounds"], res["e2"]["nodup"]["rounds"]]
    axes[0].bar(labels, vals, color=["#4f81bd", "#c0504d"])
    axes[0].set_title("E2 访问去重（平均轮次）")
    axes[0].set_ylabel("轮次")
    labels2 = ["首改进", "最优改进"]
    vals2 = [res["e3"]["first"]["ms"], res["e3"]["best"]["ms"]]
    axes[1].bar(labels2, vals2, color=["#4f81bd", "#9bbb59"])
    axes[1].set_title("E3 接受策略（平均耗时 ms）")
    axes[1].set_ylabel("毫秒")
    for ax in axes:
        ax.spines[["top", "right"]].set_visible(False)
    fig.tight_layout()
    p = os.path.join(OUT, "fig_ablation.png")
    fig.savefig(p)
    plt.close(fig)
    made.append(p)

    # 图3：E5 成本
    fig, ax = plt.subplots(figsize=(4.6, 2.9), dpi=200)
    ax.bar(["有缓存", "无缓存"],
           [res["e5"]["on"]["calls"], res["e5"]["off"]["calls"]],
           color=["#4f81bd", "#f79646"])
    ax.set_ylabel("平均工具调用次数")
    ax.set_title("E5 缓存对调用成本的影响")
    ax.spines[["top", "right"]].set_visible(False)
    fig.tight_layout()
    p = os.path.join(OUT, "fig_cost.png")
    fig.savefig(p)
    plt.close(fig)
    made.append(p)
    return made


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--no-fig", action="store_true")
    args = ap.parse_args()
    os.makedirs(OUT, exist_ok=True)

    t0 = time.time()
    profiles = [P(**d) for d in PROFILES]
    base, loop = e1(profiles)
    full, nodup, bestimp = e2_e3(profiles)
    same, cross = e4(PROFILES)
    on, off = e5(profiles)

    res = {
        "meta": {
            "time": time.strftime("%Y-%m-%d %H:%M"),
            "python": __import__("sys").version.split()[0],
            "median_ms": round(st.median(r["ms"] for r in loop), 2),
            "elapsed_s": round(time.time() - t0, 2),
        },
        "e1": {
            "base": agg(base), "loop": agg(loop),
            "rows_base": base, "rows_loop": loop,
            "base_bad": sum(1 for r in base if r["hard"] > 0),
            "loop_ok": sum(1 for r in loop if r["hard"] == 0),
        },
        "e2": {
            "full": {**agg(full), "hit_cap": sum(1 for r in full if r["rounds"] >= MAX_ROUNDS)},
            "nodup": {**agg(nodup), "hit_cap": sum(1 for r in nodup if r["rounds"] >= MAX_ROUNDS)},
        },
        "e3": {"first": agg(full), "best": agg(bestimp)},
        "e4": {"same": agg(same), "cross": agg(cross)},
        "e5": {"on": agg(on), "off": agg(off)},
    }
    with open(os.path.join(OUT, "summary.json"), "w", encoding="utf-8") as f:
        json.dump(res, f, ensure_ascii=False, indent=1)
    write_csv("e1_per_profile.csv", profiles, base, loop)
    write_md(res)
    made = [] if args.no_fig else figures(res)

    print(f"E1 一次性生成: CSR={res['e1']['base']['csr']:.4f} ER={res['e1']['base']['er']:.2f} PREF={res['e1']['base']['pref']:.4f}")
    print(f"E1 闭环修复  : CSR={res['e1']['loop']['csr']:.4f} ER={res['e1']['loop']['er']:.2f} PREF={res['e1']['loop']['pref']:.4f} RND={res['e1']['loop']['rounds']:.2f}")
    print(f"E2 轮次 完整={res['e2']['full']['rounds']:.2f} 无去重={res['e2']['nodup']['rounds']:.2f} 触顶={res['e2']['nodup']['hit_cap']}")
    print(f"E3 CSR 首改进={res['e3']['first']['csr']:.4f} 最优改进={res['e3']['best']['csr']:.4f} 耗时={res['e3']['first']['ms']:.2f}/{res['e3']['best']['ms']:.2f}ms")
    print(f"E4 ER 同城={res['e4']['same']['er']:.2f} 跨城={res['e4']['cross']['er']:.2f}")
    print(f"E5 CALL 有缓存={res['e5']['on']['calls']:.2f} 无缓存={res['e5']['off']['calls']:.2f}")
    print(f"输出目录: {OUT}")
    for p in made:
        print("  图:", os.path.basename(p))


if __name__ == "__main__":
    main()