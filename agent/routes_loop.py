# -*- coding: utf-8 -*-
"""持续重试生成真实路网矩阵（公开服务很抖，所以慢慢磨）。

按优先级轮询：先 16 个手工数据城市，再热门旅游城市，最后其余城市。
每轮把所有缺的城市都试一遍，成功后落盘；全部完成或连续 3 轮零成功就退出。

用法：
    python -m agent.routes_loop            # 一直磨到手/热门城市齐了
    python -m agent.routes_loop all        # 磨全部 339 城
"""
from __future__ import annotations

import json
import os
import sys
import time

from . import build_routes as br
from .routes_batch import HAND, HOT


def pending(keys):
    return [k for k in keys if not os.path.exists(os.path.join(br.OUT_DIR, f"{k}.json"))]


def run(keys, max_rounds=40, empty_stop=3):
    os.makedirs(br.OUT_DIR, exist_ok=True)
    keys = [k for k in keys if os.path.exists(os.path.join(br.CITY_DIR, f"{k}.json"))]
    empty = 0
    for rnd in range(1, max_rounds + 1):
        todo = pending(keys)
        if not todo:
            print(f"全部完成，共 {len(keys)} 城", flush=True)
            return 0
        ok = 0
        print(f"--- 第 {rnd} 轮，待建 {len(todo)} 城", flush=True)
        for key in todo:
            t0 = time.time()
            try:
                data = br.build_city(key)
            except Exception as exc:  # noqa: BLE001
                print(f"[fail] {key}: {str(exc)[-60:]}", flush=True)
                continue
            with open(os.path.join(br.OUT_DIR, f"{key}.json"), "w", encoding="utf-8") as fp:
                json.dump(data, fp, ensure_ascii=False, separators=(",", ":"))
            ok += 1
            print(f"[ok] {key}: {len(data['nodes'])} 节点 · {time.time() - t0:.1f}s", flush=True)
            time.sleep(2.0)
        empty = 0 if ok else empty + 1
        if empty >= empty_stop:
            print("连续多轮零成功，服务不可用，先停下", flush=True)
            return 0
        time.sleep(6.0)
    print("达到轮次上限，先停下", flush=True)
    return 0


def main(argv=None):
    argv = list(argv or sys.argv[1:])
    scope = argv[0] if argv else "popular"
    if scope == "all":
        allkeys = sorted(f[:-5] for f in os.listdir(br.CITY_DIR) if f.endswith(".json"))
        keys = HAND + HOT + [k for k in allkeys if k not in HAND and k not in HOT]
    else:
        keys = HAND + [k for k in HOT if k not in HAND]
    return run(keys)


if __name__ == "__main__":
    raise SystemExit(main())