"""对着线上 Release 列表，复刻客户端 _PickRelease + IsLatest 的判定，验证通道语义。

镜像的规则（UpdatesGitHubModel._PickRelease / UpdateChannelLevels）：
  - draft 跳过
  - 按 tag 后缀算等级：空=0，pre/beta/alpha/rc=1，snapshot=2，nightly=3
  - tag 没后缀但标了 prerelease 的，按预览级(1)处理
  - 等级 > 通道等级的丢弃（包含式）
  - 剩下的按 SemVer 降序取第一条
"""

import json
import os
import re
import sys
import urllib.request

PROXY = os.environ.get("PCL_TEST_PROXY", "")

CHANNELS = {"Stable": 0, "Preview": 1, "Snapshot": 2, "Latest": 3}


def fetch(url):
    handlers = []
    if PROXY:
        handlers.append(urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}))
    else:
        handlers.append(urllib.request.ProxyHandler({}))
    opener = urllib.request.build_opener(*handlers)
    req = urllib.request.Request(url, headers={
        "User-Agent": "pcl-lite-verify/1.0",
        "Accept": "application/vnd.github+json",
    })
    with opener.open(req, timeout=30) as resp:
        return json.loads(resp.read())


def normalize_tag(tag):
    t = (tag or "").strip()
    if t[:1] in ("v", "V"):
        t = t[1:]
    return t


def level_from_name(name):
    t = normalize_tag(name)
    if "-" not in t:
        return 0
    suffix = t.split("-", 1)[1].split("+", 1)[0].lower()
    if "nightly" in suffix:
        return 3
    if "snapshot" in suffix:
        return 2
    return 1


SEMVER_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.\-]+))?(?:\+[0-9A-Za-z.\-]+)?$")


def semver_key(name):
    """返回可直接比较的元组。无后缀 > 有后缀（SemVer 2.0 §11.3）。"""
    m = SEMVER_RE.match(normalize_tag(name))
    if not m:
        raise ValueError(f"tag 解析不了: {name!r}")
    major, minor, patch = (int(m.group(i)) for i in (1, 2, 3))
    pre = m.group(4)
    if pre is None:
        return (major, minor, patch, 1, ())
    ids = []
    for part in pre.split("."):
        ids.append((0, int(part)) if part.isdigit() else (1, part))
    return (major, minor, patch, 0, tuple(ids))


def pick(releases, channel_level):
    cands = []
    for rel in releases:
        if rel.get("draft"):
            continue
        tag = rel.get("tag_name")
        if not tag:
            continue
        name = normalize_tag(tag)
        try:
            lvl = level_from_name(name)
        except Exception:
            continue
        if lvl == 0 and rel.get("prerelease"):
            lvl = 1
        if lvl > channel_level:
            continue
        try:
            cands.append((semver_key(name), name, rel))
        except ValueError:
            continue
    if not cands:
        return None
    return max(cands, key=lambda x: x[0])


def main():
    print("=" * 70)
    print("更新通道语义验证（对着线上 Release 列表）")
    print("=" * 70)

    releases = fetch("https://api.github.com/repos/askbbtsk/pcl-lite/releases?per_page=100")
    print(f"\n线上共 {len(releases)} 条 Release：")
    for rel in releases:
        lvl = level_from_name(rel["tag_name"])
        if lvl == 0 and rel.get("prerelease"):
            lvl = 1
        label = {0: "正式版", 1: "预览版", 2: "快照版", 3: "不稳定版"}[lvl]
        print(f"  {rel['tag_name']:<16} prerelease={str(rel.get('prerelease')):<5} -> {label}")

    failed = 0
    print("\n各通道挑中的版本：")
    for chan, lvl in CHANNELS.items():
        got = pick(releases, lvl)
        print(f"  {chan:<9} -> {got[1] if got else '(无)'}")

    # 关键断言
    print("\n断言：")
    cases = [
        ("Stable", "1.3.2", True, "1.3.2 用户走稳定版通道，应当看到更新"),
        ("Stable", "1.3.3", False, "1.3.3 用户走稳定版通道，应当已是最新"),
        ("Preview", "1.3.2", True, "1.3.2 用户走预览通道，应当看到更新"),
        ("Latest", "1.3.3", False, "1.3.3 用户走始终最新，应当已是最新"),
        ("Snapshot", "1.3.3", False, "1.3.3 用户走快照通道，应当已是最新"),
    ]
    for chan, current, expect_update, desc in cases:
        got = pick(releases, CHANNELS[chan])
        if got is None:
            print(f"  ✗ {desc}：通道挑不出任何版本")
            failed += 1
            continue
        latest_name = got[1]
        has_update = semver_key(latest_name) > semver_key(current)
        mark = "✓" if has_update == expect_update else "✗"
        if has_update != expect_update:
            failed += 1
        verdict = f"有新版本 {latest_name}" if has_update else "已是最新"
        print(f"  {mark} {desc}：{current} vs {latest_name} -> {verdict}")

    print("\n" + "=" * 70)
    if failed:
        print(f"有 {failed} 条断言不符预期")
        return 1
    print("全部符合预期")
    print("=" * 70)
    return 0


if __name__ == "__main__":
    sys.exit(main())
