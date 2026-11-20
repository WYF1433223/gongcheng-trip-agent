# -*- coding: utf-8 -*-
"""逐字段对比 unity/golden.json（Python 结果）与 unity/out_csharp.json（C# 结果）。

用法（项目根目录）：
    python unity/diff_golden.py
退出码：0 完全一致；1 存在差异。
"""
from __future__ import annotations

import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOL = 1e-6


def load(name: str):
    path = os.path.join(HERE, name)
    if not os.path.exists(path):
        raise SystemExit(f"缺少文件：{path}")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def walk(path: str, a, b, out: list) -> None:
    if isinstance(a, dict) and isinstance(b, dict):
        for k in sorted(set(a) | set(b)):
            if k not in a:
                out.append((f"{path}.{k}", "<缺失>", b[k]))
            elif k not in b:
                out.append((f"{path}.{k}", a[k], "<缺失>"))
            else:
                walk(f"{path}.{k}", a[k], b[k], out)
        return
    if isinstance(a, list) and isinstance(b, list):
        if len(a) != len(b):
            out.append((f"{path}.length", len(a), len(b)))
        for i in range(min(len(a), len(b))):
            walk(f"{path}[{i}]", a[i], b[i], out)
        return
    if isinstance(a, (int, float)) and isinstance(b, (int, float)) and not isinstance(a, bool):
        if abs(float(a) - float(b)) > TOL:
            out.append((path, a, b))
        return
    if a != b:
        out.append((path, a, b))


def main() -> int:
    py = load("golden.json")
    cs = load("out_csharp.json")
    diffs: list = []
    walk("", py, cs, diffs)
    total_cases = len(py.get("cases", []))
    if not diffs:
        print(f"一致：{total_cases} 组档案 × 2 模式，全部字段相同。")
        return 0
    print(f"发现 {len(diffs)} 处差异（{total_cases} 组档案）：")
    for path, a, b in diffs[:60]:
        print(f"  {path}\n    Python={a!r}\n    C#    ={b!r}")
    if len(diffs) > 60:
        print(f"  … 其余 {len(diffs) - 60} 处略")
    return 1


if __name__ == "__main__":
    sys.exit(main())