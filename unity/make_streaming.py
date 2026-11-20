"""把 cities/*.json 与省—市目录导出到 Unity 的 StreamingAssets。

用法：python unity/make_streaming.py [目标目录]
默认目标：unity/AndroidApp/Assets/StreamingAssets
"""
import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)

from agent import data  # noqa: E402


def main():
    target = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "AndroidApp", "Assets", "StreamingAssets")
    cities_out = os.path.join(target, "cities")
    os.makedirs(cities_out, exist_ok=True)

    src = os.path.join(ROOT, "agent", "cities")
    n = 0
    for fn in sorted(os.listdir(src)):
        if not fn.endswith(".json") or fn.startswith((".", "_")):
            continue
        with open(os.path.join(src, fn), encoding="utf-8") as f:
            spec = json.load(f)
        with open(os.path.join(cities_out, fn), "w", encoding="utf-8") as f:
            json.dump(spec, f, ensure_ascii=False, indent=1)
        n += 1

    routes_src = os.path.join(ROOT, "agent", "routes")
    routes_out = os.path.join(target, "routes")
    r = 0
    if os.path.isdir(routes_src):
        os.makedirs(routes_out, exist_ok=True)
        for fn in sorted(os.listdir(routes_src)):
            if not fn.endswith(".json") or fn.startswith((".", "_")):
                continue
            shutil.copyfile(os.path.join(routes_src, fn), os.path.join(routes_out, fn))
            r += 1

    directory = data.city_directory()
    with open(os.path.join(target, "directory.json"), "w", encoding="utf-8") as f:
        json.dump(directory, f, ensure_ascii=False, indent=1)

    print(f"cities: {n} -> {cities_out}")
    print(f"directory: {len(directory)} provinces -> {os.path.join(target, 'directory.json')}")
    print(f"routes: {r} -> {routes_out}")


if __name__ == "__main__":
    main()