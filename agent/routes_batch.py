# -*- coding: utf-8 -*-
"""批量生成真实路网矩阵（分优先级、可断点续跑）。

公开 OSRM 服务对长列表和并发都不友好，所以：
* 每城 2 个请求（步行、驾车各一次 table 调用）
* 单请求 18 秒超时，失败重试试 2 次，仍失败就跳过（该城回落估算）
* 已存在的 matrix 默认跳过，方便分批跑

用法：
    python -m agent.routes_batch hand      # 16 个手工数据城市
    python -m agent.routes_batch hot       # 热门旅游城市
    python -m agent.routes_batch all       # 全部 339 城（很久）
"""
from __future__ import annotations

import json
import os
import sys
import time
import urllib.request

from . import build_routes as br

HAND = [
    "nanchang", "jiujiang", "jingdezhen", "pingxiang", "xinyu", "yingtan",
    "ganzhou", "jian", "yichun", "fuzhou_jx", "shangrao",
    "chengdu", "leshan", "mianyang", "yibin", "zigong",
]

HOT = [
    "beijing", "shanghai", "guangzhou", "shenzhen", "hangzhou", "nanjing",
    "suzhou", "xian", "chongqing", "wuhan", "changsha", "xiamen", "qingdao",
    "kunming", "guiyang", "nanning", "haerbin", "dalian", "tianjin", "luoyang",
    "zhengzhou", "jinan", "hefei", "fuzhou", "ningbo", "wuxi", "sanya",
    "guilin", "lhasa", "urumqi", "hohhot", "lanzhou", "taiyuan", "shijiazhuang",
    "changchun", "shenyang", "nanchangxian",
]


def run(keys, force=False):
    os.makedirs(br.OUT_DIR, exist_ok=True)
    ok = skip = fail = 0
    for key in keys:
        out = os.path.join(br.OUT_DIR, f"{key}.json")
        if os.path.exists(out) and not force:
            skip += 1
            continue
        if not os.path.exists(os.path.join(br.CITY_DIR, f"{key}.json")):
            print(f"[miss] {key}: 无城市数据文件")
            fail += 1
            continue
        t0 = time.time()
        try:
            data = br.build_city(key)
        except Exception as exc:  # noqa: BLE001
            print(f"[fail] {key}: {type(exc).__name__} {str(exc)[:70]}")
            fail += 1
            continue
        with open(out, "w", encoding="utf-8") as fp:
            json.dump(data, fp, ensure_ascii=False, separators=(",", ":"))
        print(f"[ok] {key}: {len(data['nodes'])} 节点 · {time.time() - t0:.1f}s")
        ok += 1
        time.sleep(0.8)
    print(f"完成：新增 {ok}，跳过 {skip}，失败 {fail}")
    return 0


def main(argv=None):
    argv = list(argv or sys.argv[1:])
    scope = argv[0] if argv else "hand"
    force = "--force" in argv
    if scope == "hand":
        keys = HAND
    elif scope == "hot":
        keys = HOT
    elif scope == "all":
        keys = sorted(f[:-5] for f in os.listdir(br.CITY_DIR) if f.endswith(".json"))
    else:
        keys = [k for k in argv if not k.startswith("--")]
    return run(keys, force=force)


if __name__ == "__main__":
    raise SystemExit(main())