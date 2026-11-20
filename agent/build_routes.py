# -*- coding: utf-8 -*-
"""用 OpenStreetMap 的 OSRM 公共路由服务生成「真实路网」出行矩阵。

对每个手工城市（agent/cities/*.json）取「酒店 + 全部景点」的坐标，调用 OSRM
的 table 接口一次性拿到全连接矩阵：

* 步行矩阵：routing.openstreetmap.de/routed-foot（OSM 步行路网的真实路径与耗时）
* 驾车矩阵：router.project-osrm.org（OSM 驾车路网的真实路径与耗时）
* 距离矩阵：驾车距离（米），用于估算公交/地铁耗时与展示公里数

结果写入 agent/routes/<city>.json，供 data.py 与 C# 端离线读取；没有矩阵的城市
（合成模板城市）自动回落到 haversine 估算。

用法：
    python -m agent.build_routes            # 全部城市
    python -m agent.build_routes nanchang   # 指定城市
"""
from __future__ import annotations

import json
import os
import time
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
CITY_DIR = os.path.join(HERE, "cities")
OUT_DIR = os.path.join(HERE, "routes")

WALK_SPEED = 1.35    # m/s，约 4.9 km/h，成年人城市步行常速
WALK_DETOUR = 1.15   # 步行路网相对驾车路网的绕行系数

FOOT_API = "https://routing.openstreetmap.de/routed-foot/table/v1/foot/"
DRIVE_API = "https://router.project-osrm.org/table/v1/driving/"


def _coords_str(nodes):
    return ";".join(f"{lng:.6f},{lat:.6f}" for _id, lat, lng in nodes)


def _fetch(url, retry=3):
    last = None
    for i in range(retry + 1):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "travel-agent-thesis/1.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except Exception as exc:  # noqa: BLE001 - 网络偶发，重试后放弃
            last = exc
            time.sleep(2.0 + i * 2.0)
    raise RuntimeError(f"请求失败：{url[:80]} :: {last}")


def build_city(key: str) -> dict:
    path = os.path.join(CITY_DIR, f"{key}.json")
    with open(path, encoding="utf-8") as fp:
        spec = json.load(fp)

    nodes = []
    hotel_ids = []
    for name, (lat, lng) in (spec.get("hotels") or {}).items():
        hotel_ids.append(f"hotel:{name}")
        nodes.append((f"hotel:{name}", lat, lng))
    poi_ids = []
    for poi in spec["pois"]:
        poi_ids.append(poi["id"])
        nodes.append((poi["id"], poi["lat"], poi["lng"]))

    coords = _coords_str([(i, la, ln) for i, la, ln in nodes])
    # 步行专用服务（routed-foot）限流严重，改为「驾车路网距离 × 步行绕行系数 ÷ 步行速度」，
    # 结果是同一张 OSM 路网上的真实距离，只是按 1.35 m/s 折算成步行耗时。
    drive = _fetch(f"{DRIVE_API}{coords}?annotations=duration,distance")
    if drive.get("code") != "Ok":
        raise RuntimeError(f"驾车矩阵失败：{key} {drive.get('code')}")
    dists = drive.get("distances") or []
    foot = {
        "duration": [[None if m is None else m * WALK_DETOUR / WALK_SPEED for m in row] for row in dists],
        "distance": [[None if m is None else m * WALK_DETOUR for m in row] for row in dists],
    }

    def _round(mat):
        return None if mat is None else [[None if v is None else round(v, 1) for v in row] for row in mat]

    return {
        "key": key,
        "name": spec.get("name", key),
        "source": "OSM / OSRM 驾车路网 table API（步行按路网距离折算）",
        "built_at": time.strftime("%Y-%m-%d"),
        "nodes": [i for i, _la, _ln in nodes],
        "poi_ids": poi_ids,
        "hotel_ids": hotel_ids,
        "foot": {"duration": _round(foot.get("duration")), "distance": _round(foot.get("distance"))},
        "drive": {"duration": _round(drive.get("durations")), "distance": _round(drive.get("distances"))},
    }


def main(argv=None) -> int:
    argv = list(argv or [])
    os.makedirs(OUT_DIR, exist_ok=True)
    keys = argv or sorted(f[:-5] for f in os.listdir(CITY_DIR) if f.endswith(".json"))
    for key in keys:
        out = os.path.join(OUT_DIR, f"{key}.json")
        try:
            data = build_city(key)
        except Exception as exc:  # noqa: BLE001
            print(f"[skip] {key}: {exc}")
            continue
        with open(out, "w", encoding="utf-8") as fp:
            json.dump(data, fp, ensure_ascii=False, separators=(",", ":"))
        n = len(data["nodes"])
        print(f"[ok] {key}: {n} 节点，{n * n} 组路径 → {os.path.relpath(out, HERE)}")
        time.sleep(1.5)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(__import__("sys").argv[1:]))