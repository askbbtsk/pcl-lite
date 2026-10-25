"""模拟 PCL Lite 客户端更新链路，端到端验证 1.3.2 可下载。

链路：
  锚点 update.json（多通道并发抢答）
    -> 解析出 owner/repo
    -> GitHub Releases API 取 latest
    -> 挑资产、读 digest
    -> 通过下载反代拿文件、算 SHA-256 比对

用法：python tools/verify-update-chain.py
"""

import hashlib
import json
import os
import re
import socket
import ssl
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

PROXY = os.environ.get("PCL_TEST_PROXY", "")  # 空 = 直连（--noproxy 语义）
TIMEOUT = 8

ANCHOR_OWNER = "bedkillerspacex-boop"
ANCHOR_REPO = "PCL-Lite-service"
ANCHOR_RAW = f"https://raw.githubusercontent.com/{ANCHOR_OWNER}/{ANCHOR_REPO}/main/update.json"

PROXIES = ["https://cors.isteed.cc/", "https://gh-proxy.com/", "https://gitproxy.click/"]


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
        body = resp.read()
        return resp.status, body, time.time() - t0


def channels():
    """与客户端 _ManifestUrls() 一致：raw 直连 + 每个反代 + jsDelivr，外加 Contents API。"""
    yield "Contents API", (
        f"https://api.github.com/repos/{ANCHOR_OWNER}/{ANCHOR_REPO}/contents/update.json",
        "application/vnd.github.raw",
    )
    yield "raw 直连", (ANCHOR_RAW, None)
    for p in PROXIES:
        yield p.split("//")[1].split("/")[0], (p + ANCHOR_RAW, None)
    yield "jsDelivr", (
        f"https://cdn.jsdelivr.net/gh/{ANCHOR_OWNER}/{ANCHOR_REPO}@main/update.json",
        None,
    )


def extract_pointer(body: str) -> str:
    text = body.strip().lstrip("\ufeff").strip()
    if text.startswith("{"):
        obj = json.loads(text)
        for k in ("update", "repository", "repo", "target", "url", "latest"):
            if isinstance(obj.get(k), str):
                return obj[k].strip()
        vals = [v for v in obj.values() if isinstance(v, str)]
        return vals[0].strip() if len(vals) == 1 else ""
    return text.strip().strip('"')


def parse_pointer(pointer: str):
    t = pointer.strip()
    if t.lower().startswith("http"):
        t = re.sub(r"^https?://[^/]+/", "", t)
    t = t.strip("/")
    if t.lower().startswith("github.com/"):
        t = t[10:]
    parts = [p for p in t.split("/") if p]
    if len(parts) < 2:
        raise ValueError(f"指针看不懂: {pointer!r}")
    return parts[0], parts[1]


def main():
    proxy = PROXY or None
    print("=" * 66)
    print("PCL Lite 更新链路端到端验证")
    print(f"代理: {proxy or '直连（已禁用环境变量代理）'}")
    print("=" * 66)

    # --- 1. 锚点：并发抢答 ---
    print("\n[1] 拉取锚点 update.json（并发抢答）")
    winner = None
    results = {}
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = {
            pool.submit(fetch, url, acc, proxy): name
            for name, (url, acc) in channels()
        }
        for fut in as_completed(futures):
            name = futures[fut]
            try:
                status, body, dt = fut.result()
                text = body.decode("utf-8", "replace")
                if status != 200:
                    raise ValueError(f"HTTP {status}")
                ptr = extract_pointer(text)
                if not ptr:
                    raise ValueError("内容为空")
                results[name] = ("OK", f"{dt*1000:.0f}ms -> {ptr}", ptr)
                if winner is None:
                    winner = (name, ptr)
            except Exception as e:
                results[name] = ("FAIL", f"{type(e).__name__}: {e}", None)

    for name, (state, detail, _) in sorted(results.items()):
        mark = "✓" if state == "OK" else "✗"
        print(f"  {mark} {name:<22} {detail}")

    if winner is None:
        print("\n所有锚点通道都失败，链路断在这里。")
        return 1
    print(f"\n  抢答赢家: {winner[0]} -> {winner[1]}")

    # --- 2. 解析指针 ---
    owner, repo = parse_pointer(winner[1])
    print(f"\n[2] 指针解析: owner={owner}  repo={repo}")

    # --- 3. 取 latest ---
    print("\n[3] GitHub Releases API 取 latest")
    api = f"https://api.github.com/repos/{owner}/{repo}/releases/latest"
    try:
        status, body, dt = fetch(api, use_proxy=proxy)
        rel = json.loads(body)
    except Exception as e:
        print(f"  ✗ {type(e).__name__}: {e}")
        return 1
    print(f"  ✓ HTTP {status}  {dt*1000:.0f}ms")
    print(f"    tag         : {rel['tag_name']}")
    print(f"    prerelease  : {rel['prerelease']}   draft: {rel['draft']}")
    print(f"    published   : {rel['published_at']}")

    # --- 4. 挑资产 ---
    exe = [a for a in rel["assets"] if a["name"].lower().endswith(".exe")]
    if not exe:
        print("  ✗ 这个 Release 没有 exe 资产")
        return 1
    asset = exe[0]
    digest = (asset.get("digest") or "").removeprefix("sha256:")
    print(f"\n[4] 资产: {asset['name']}  {asset['size']:,} bytes")
    print(f"    digest: sha256:{digest or '(未提供)'}")

    # --- 5. 下载 ---
    print("\n[5] 下载（直连 + 反代各试一次，取最先成功的）")
    url = asset["browser_download_url"]
    candidates = [("直连", url)] + [(p.split("//")[1].split("/")[0], p + url) for p in PROXIES]

    data = None
    for name, u in candidates:
        try:
            t0 = time.time()
            req = urllib.request.Request(u, headers={"User-Agent": "pcl-lite-verify/1.0"})
            with _opener(proxy).open(req, timeout=60) as resp:
                data = resp.read()
            dt = time.time() - t0
            speed = len(data) / dt / 1024 / 1024
            print(f"  ✓ {name:<22} {len(data):,} bytes  {dt:.1f}s  {speed:.2f} MB/s")
            break
        except Exception as e:
            print(f"  ✗ {name:<22} {type(e).__name__}: {e}")

    if data is None:
        print("\n  所有下载通道都失败。")
        return 1

    # --- 6. 校验 ---
    actual = hashlib.sha256(data).hexdigest()
    print(f"\n[6] 校验 SHA-256")
    print(f"    实际: {actual}")
    print(f"    期望: {digest}")
    if digest and actual != digest:
        print("  ✗ 哈希不一致！")
        return 1
    print("  ✓ 一致")

    # 顺带确认 PE 头（不是 HTML 错误页伪装成 exe）
    if data[:2] != b"MZ":
        print(f"  ✗ 文件头不是 MZ（前两字节 {data[:2]!r}），下到的不是 exe")
        return 1
    print("  ✓ PE 头正常（MZ）")

    print("\n" + "=" * 66)
    print(f"链路完整可用：{rel['tag_name']} 可被未登录用户发现并下载")
    print("=" * 66)
    return 0


if __name__ == "__main__":
    sys.exit(main())
