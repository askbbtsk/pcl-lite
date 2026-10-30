"""实测多源清单（updatev2.json）的加载与「v2 失败回退 v1」。

只读线上数据，不改任何东西。复刻客户端 `UpdatesManifestModel._Load` 的决策：

    1. 多源清单 updatev2.json（所有通道并发抢答）
    2. 拿不到 / 解析不了  -> 回退 update.json 单指针
    3. 两份都不行 -> 这次检查更新失败

用法：

    # 用线上真实数据跑（updatev2.json 还没推上去时，应当走回退分支）
    python tools/verify-manifest-fallback.py

    # 用本地文件模拟「v2 已经推上去了」，验证多源解析与选优
    python tools/verify-manifest-fallback.py --v2 release/updatev2.json

    # 走代理
    PCL_TEST_PROXY=http://127.0.0.1:7890 python tools/verify-manifest-fallback.py

退出码 0 表示链路符合预期。
"""

import argparse
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

PROXY = os.environ.get("PCL_TEST_PROXY", "")
TIMEOUT = 8

ANCHOR_OWNER = "bedkillerspacex-boop"
ANCHOR_REPO = "PCL-Lite-service"

PROXIES = ["https://cors.isteed.cc/", "https://gh-proxy.com/", "https://gitproxy.click/"]

V1_FILE = "update.json"
V2_FILE = "updatev2.json"

# 与 UpdateManifestV2 保持一致的等级权重
TIER_WEIGHTS = {
    "official": 100,
    "primary": 100,
    "main": 100,
    "mirror": 70,
    "cdn": 70,
    "backup": 40,
    "fallback": 40,
    "community": 25,
    "thirdparty": 25,
}
DEFAULT_WEIGHT = 50

POINTER_KEYS = ("repo", "repository", "update", "target", "url", "latest", "pointer")
TIER_KEYS = ("tier", "level", "kind", "role", "class")
WEIGHT_KEYS = ("weight", "priority", "score", "rank")
LIST_KEYS = ("sources", "repositories", "repos", "mirrors", "servers", "nodes")


def _opener(use_proxy):
    handlers = []
    if use_proxy:
        handlers.append(urllib.request.ProxyHandler({"http": use_proxy, "https": use_proxy}))
    else:
        handlers.append(urllib.request.ProxyHandler({}))  # 显式禁用环境变量代理
    return urllib.request.build_opener(*handlers)


def fetch(url, accept=None, use_proxy=None):
    req = urllib.request.Request(url, headers={"User-Agent": "pcl-lite-verify/1.0"})
    if accept:
        req.add_header("Accept", accept)
    t0 = time.time()
    with _opener(use_proxy).open(req, timeout=TIMEOUT) as resp:
        return resp.status, resp.read().decode("utf-8", "replace"), time.time() - t0


def channels(file_name, owner=ANCHOR_OWNER, repo=ANCHOR_REPO):
    """与客户端 _ManifestChannels 一致：每个锚点 × {API, raw, 反代, jsDelivr}。"""
    raw = f"https://raw.githubusercontent.com/{owner}/{repo}/main/{file_name}"
    yield "Contents API", (
        f"https://api.github.com/repos/{owner}/{repo}/contents/{file_name}",
        "application/vnd.github.raw",
    )
    yield "raw 直连", (raw, None)
    for p in PROXIES:
        yield p.split("//")[1].split("/")[0], (p + raw, None)
    yield "jsDelivr", (
        f"https://cdn.jsdelivr.net/gh/{owner}/{repo}@main/{file_name}",
        None,
    )


def fetch_manifest(file_name, proxy, local_override=None, owner=ANCHOR_OWNER, repo=ANCHOR_REPO):
    """并发抢答，返回 (body, winner, results)。全失败时 body 为 None。"""
    if local_override:
        with open(local_override, encoding="utf-8") as f:
            return f.read(), "(本地文件)", {"(本地文件)": ("OK", local_override)}

    results = {}
    winner = None
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = {
            pool.submit(fetch, url, acc, proxy): name
            for name, (url, acc) in channels(file_name, owner, repo)
        }
        for fut in as_completed(futures):
            name = futures[fut]
            try:
                status, body, dt = fut.result()
                if status != 200:
                    raise ValueError(f"HTTP {status}")
                results[name] = ("OK", f"{dt * 1000:.0f}ms")
                if winner is None:
                    winner = (name, body)
            except Exception as e:
                results[name] = ("FAIL", f"{type(e).__name__}: {e}")
    return (winner[1] if winner else None), (winner[0] if winner else None), results


# ─────────────────────────────────────────────────────────────
#  解析（与 UpdateManifestV2 / UpdateManifestPointer 同构）
# ─────────────────────────────────────────────────────────────


def looks_like_pointer(text):
    if not text or len(text) > 200:
        return False
    if any(c.isspace() or c in "<>{}[]\\?#" for c in text):
        return False
    t = text.strip().strip("\"'").strip().strip("/")
    if t.lower().startswith("github.com/"):
        t = t[10:]
    parts = [p for p in t.split("/") if p]
    if len(parts) < 2:
        return False
    return all(p not in (".", "..") and ":" not in p for p in parts[:2])


def parse_pointer(text):
    t = text.strip().strip("\"'").strip()
    if t.lower().startswith("http"):
        t = re.sub(r"^https?://[^/]+/", "", t)
    t = t.strip("/")
    if t.lower().startswith("github.com/"):
        t = t[10:]
    parts = [p for p in t.split("/") if p]
    if len(parts) < 2:
        raise ValueError(f"指针看不懂: {text!r}")
    repo = parts[1][:-4] if parts[1].lower().endswith(".git") else parts[1]
    return parts[0], repo


def extract_v1(body):
    text = body.strip().lstrip("\ufeff").strip()
    if not text:
        raise ValueError("清单是空的")
    if text[0] == "<":
        raise ValueError("返回的是网页而不是清单")
    if text[0] in "{[\"":
        node = json.loads(text)
        if isinstance(node, str):
            cand = node.strip()
        elif isinstance(node, dict):
            cand = ""
            for k in POINTER_KEYS:
                if isinstance(node.get(k), str) and node[k].strip():
                    cand = node[k].strip()
                    break
            if not cand:
                vals = [v for v in node.values() if isinstance(v, str) and v.strip()]
                cand = vals[0].strip() if len(vals) == 1 else ""
        else:
            cand = ""
        if not cand:
            raise ValueError("清单里没有写仓库地址")
    else:
        cand = text
    if not looks_like_pointer(cand):
        raise ValueError(f"清单内容「{cand[:60]}」不是仓库指针")
    return cand


def weight_of_tier(tier):
    return TIER_WEIGHTS.get((tier or "").strip().lower(), DEFAULT_WEIGHT)


def parse_v2(body):
    text = body.strip().lstrip("\ufeff").strip()
    if not text:
        raise ValueError("多源清单是空的")
    if text[0] == "<":
        raise ValueError("返回的是网页而不是多源清单")
    root = json.loads(text)

    problems = []
    if isinstance(root, list):
        entries = list(root)
    elif isinstance(root, dict):
        entries = None
        for k in LIST_KEYS:
            if isinstance(root.get(k), list):
                entries = list(root[k])
                break
        if entries is None:
            entries = [extract_v1(json.dumps(root, ensure_ascii=False))]
    elif isinstance(root, str):
        entries = [root]
    else:
        raise ValueError("清单顶层既不是数组也不是对象")

    sources = []
    for item in entries:
        try:
            if isinstance(item, str):
                pointer, tier, weight = item, None, None
            elif isinstance(item, dict):
                pointer = ""
                for k in POINTER_KEYS:
                    if isinstance(item.get(k), str) and item[k].strip():
                        pointer = item[k].strip()
                        break
                if not pointer:
                    vals = [v for v in item.values() if isinstance(v, str) and v.strip()]
                    pointer = vals[0].strip() if len(vals) == 1 else ""
                tier = next((item[k] for k in TIER_KEYS if isinstance(item.get(k), str)), None)
                weight = next(
                    (int(item[k]) for k in WEIGHT_KEYS
                     if isinstance(item.get(k), (int, float))
                     or (isinstance(item.get(k), str) and item[k].strip().isdigit())),
                    None,
                )
            else:
                problems.append(f"无法识别的条目 {item!r}")
                continue
        except Exception as e:
            problems.append(f"条目 {item!r} 解析失败: {e}")
            continue

        if not pointer or not looks_like_pointer(pointer):
            problems.append(f"「{str(pointer)[:60]}」不像仓库地址，已跳过")
            continue

        try:
            owner, repo = parse_pointer(pointer)
        except Exception as e:
            problems.append(f"「{pointer[:60]}」解析失败: {e}")
            continue

        tier = (tier or "mirror").strip().lower()
        sources.append(
            {
                "owner": owner,
                "repo": repo,
                "tier": tier,
                "weight": weight if weight is not None else weight_of_tier(tier),
            }
        )

    if not sources:
        raise ValueError("多源清单里没有一个可用的仓库地址")

    # 去重（大小写不敏感，取权重高的）+ 按权重降序
    dedup = {}
    for s in sources:
        key = (s["owner"].lower(), s["repo"].lower())
        if key not in dedup or dedup[key]["weight"] < s["weight"]:
            dedup[key] = s
    return sorted(dedup.values(), key=lambda x: (-x["weight"], f"{x['owner']}/{x['repo']}"))


def plan(v2_body, v1_body):
    """返回 (sources, used_v2, v2_failure)。"""
    v2_failure = None
    if not v2_body:
        v2_failure = "多源清单没拉到"
    else:
        try:
            return parse_v2(v2_body), True, None
        except Exception as e:
            v2_failure = str(e)

    if not v1_body:
        raise ValueError(f"两份清单都不可用：多源清单（{v2_failure}）；单指针清单也没拉到")

    pointer = extract_v1(v1_body)
    owner, repo = parse_pointer(pointer)
    return (
        [{"owner": owner, "repo": repo, "tier": "official", "weight": weight_of_tier("official")}],
        False,
        v2_failure,
    )


# ─────────────────────────────────────────────────────────────


def latest_release(owner, repo, proxy):
    url = f"https://api.github.com/repos/{owner}/{repo}/releases?per_page=20"
    status, body, _ = fetch(url, "application/vnd.github+json", proxy)
    if status != 200:
        raise ValueError(f"HTTP {status}")
    return json.loads(body)


def semver_key(tag):
    """粗排：把 tag 拆成 (major, minor, patch, 是否预发布)。"""
    t = tag.strip().lstrip("vV")
    core = re.split(r"[-+]", t, maxsplit=1)[0]
    parts = [int(x) if x.isdigit() else 0 for x in core.split(".")]
    while len(parts) < 3:
        parts.append(0)
    return (parts[0], parts[1], parts[2], 0 if "-" in t else 1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--v2", help="用本地文件冒充线上的 updatev2.json")
    ap.add_argument("--v1", help="用本地文件冒充线上的 update.json")
    args = ap.parse_args()

    proxy = PROXY or None
    print("=" * 70)
    print("多源清单加载与回退实测")
    print(f"代理: {proxy or '直连（已禁用环境变量代理）'}")
    print(f"v2 来源: {args.v2 or '线上'}   v1 来源: {args.v1 or '线上'}")
    print("=" * 70)

    # --- 1. 拉 v2 ---
    print(f"\n[1] 拉多源清单 {V2_FILE}（并发抢答）")
    v2_body, v2_winner, v2_results = fetch_manifest(V2_FILE, proxy, args.v2)
    for name, (state, detail) in sorted(v2_results.items()):
        print(f"  {'✓' if state == 'OK' else '✗'} {name:<22} {detail}")
    print(f"  结果: {'拿到（' + str(v2_winner) + '）' if v2_body else '全部失败，将回退'}")

    # --- 2. 拉 v1（只在需要时） ---
    v1_body = None
    need_v1 = True
    if v2_body:
        try:
            parse_v2(v2_body)
            need_v1 = False
        except Exception as e:
            print(f"  v2 内容不可用: {e}")

    if need_v1:
        print(f"\n[2] 回退：拉单指针清单 {V1_FILE}（并发抢答）")
        v1_body, v1_winner, v1_results = fetch_manifest(V1_FILE, proxy, args.v1)
        for name, (state, detail) in sorted(v1_results.items()):
            print(f"  {'✓' if state == 'OK' else '✗'} {name:<22} {detail}")
        if not v1_body:
            print("\n  两份清单都拉不到，链路断在这里（客户端会去问 GitHub Releases 兜底源）")
            return 1
        print(f"  结果: 拿到（{v1_winner}）")
    else:
        print(f"\n[2] 跳过：v2 可用，不需要回退")

    # --- 3. 决策 ---
    print("\n[3] 决策")
    sources, used_v2, v2_failure = plan(v2_body, v1_body)
    if used_v2:
        print(f"  ✓ 使用多源清单，共 {len(sources)} 个源：")
        for s in sources:
            print(f"      {s['owner']}/{s['repo']:<28} tier={s['tier']:<9} weight={s['weight']}")
    else:
        print(f"  ✓ 已回退到单指针清单")
        print(f"      原因: {v2_failure}")
        print(f"      指向: {sources[0]['owner']}/{sources[0]['repo']} (weight={sources[0]['weight']})")

    # --- 4. 分档 ---
    # 客户端是「按权重分档、从最高档开始问，最高档回来了就根本不再问低权重档」。
    # 这里把每档都问一遍只是为了把清单里每个源的健康状况都看清楚，
    # 但选优严格按分档走：第一档只要有结果，后面的档直接忽略。
    print("\n[4] 逐档取版本（客户端档内并发；高权重档有结果就不再问低权重档）")
    tiers = {}
    for s in sources:
        tiers.setdefault(s["weight"], []).append(s)

    found = []          # (semver_key, weight, tag, source)
    winner_tier = None
    for weight in sorted(tiers, reverse=True):
        tier_found = []
        for s in tiers[weight]:
            try:
                releases = latest_release(s["owner"], s["repo"], proxy)
                usable = [r for r in releases if not r.get("draft")]
                if not usable:
                    print(f"  ✗ [{weight}] {s['owner']}/{s['repo']:<26} 没有可用 Release")
                    continue
                best = max(usable, key=lambda r: semver_key(r["tag_name"]))
                tier_found.append((semver_key(best["tag_name"]), s["weight"], best["tag_name"], s))
                print(f"  ✓ [{weight}] {s['owner']}/{s['repo']:<26} 最新 {best['tag_name']}"
                      f"  (prerelease={best['prerelease']})")
            except Exception as e:
                print(f"  ✗ [{weight}] {s['owner']}/{s['repo']:<26} {type(e).__name__}: {e}")

        if tier_found:
            found = tier_found
            winner_tier = weight
            lower = [w for w in sorted(tiers, reverse=True) if w < weight]
            if lower:
                print(f"  权重 {weight} 档已有结果 → 低权重档 {lower} 不再参与（客户端连请求都不发）")
            break
        print(f"  权重 {weight} 档全军覆没 → 退到下一档")

    if not found:
        print("\n  所有源都取不到版本，客户端会报「获取版本信息失败」")
        return 1

    # --- 5. 选优 ---
    print(f"\n[5] 选优（只在权重 {winner_tier} 这一档内比版本号）")

    found.sort(key=lambda x: (x[0], x[1]), reverse=True)
    ver, weight, tag, src = found[0]
    print(f"  ✓ 取 {tag}（来自 {src['owner']}/{src['repo']}，weight={weight}, tier={src['tier']}）")
    if len(found) > 1:
        others = "、".join(f"{t}@{s['owner']}/{s['repo']}" for _, _, t, s in found[1:])
        print(f"    同档其余: {others}")

    # --- 6. 身份校验（官方源免校验，其余必须有 update.json） ---
    print("\n[6] 身份校验")
    if src["tier"] == "official":
        print(f"  ✓ {src['owner']}/{src['repo']} 是官方源（tier=official），免校验、零额外请求")
    else:
        try:
            body, channel, _ = fetch_manifest(V1_FILE, proxy, None, owner=src["owner"], repo=src["repo"])
            if not body:
                print(f"  ✗ 取不到 {src['owner']}/{src['repo']} 的 update.json，客户端会跳过该源")
                return 1
            pointer = extract_v1(body)
            print(f"  ✓ {src['owner']}/{src['repo']} 仓库内有 update.json（{channel}）→ 指向 {pointer}")
        except Exception as e:
            print(f"  ✗ 身份校验失败：{type(e).__name__}: {e}")
            return 1

    print("\n" + "=" * 70)
    print("结论：", end="")
    if used_v2:
        print(f"多源清单可用，按「权重分档优先，同档比版本号」选优（取 {tag}）")
    else:
        print("v2 不可用时正确回退到 v1，链路仍然通")
    print("=" * 70)
    return 0


if __name__ == "__main__":
    sys.exit(main())
