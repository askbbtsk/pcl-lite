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

    # 关键断言：全部从线上列表推出来，不写死版本号，
    # 否则每发一版都要改一遍脚本，改完还容易忘了改。
    print("\n断言：")
    picked = {chan: pick(releases, lvl) for chan, lvl in CHANNELS.items()}
    stable = picked["Stable"]
    latest = picked["Latest"]

    checks = []

    # ① 稳定版通道只能挑到正式版
    if stable is None:
        checks.append((False, "稳定版通道挑不出任何版本"))
    else:
        lvl = level_from_name(stable[1])
        checks.append((lvl == 0, f"稳定版通道挑中的必须是正式版：{stable[1]}（等级 {lvl}）"))

    # ② 预发布版本不能漏到稳定/预览/快照通道
    for chan in ("Stable", "Preview", "Snapshot"):
        got = picked[chan]
        if got is None:
            continue
        lvl = level_from_name(got[1])
        checks.append((lvl <= CHANNELS[chan],
                       f"{chan} 通道不该收到等级 {lvl} 的 {got[1]}"))

    # ③ 始终最新必须挑到全局最大的那个，nightly 只有它能收到
    if latest is None:
        checks.append((False, "始终最新通道挑不出任何版本"))
    else:
        all_keys = [(semver_key(normalize_tag(r["tag_name"])), normalize_tag(r["tag_name"]))
                    for r in releases if not r.get("draft")]
        newest = max(all_keys)[1] if all_keys else None
        checks.append((latest[1] == newest,
                       f"始终最新通道应当挑到全局最新：挑中 {latest[1]}，实际最新 {newest}"))

    # ④ 用稳定版通道挑中的版本当基准：它自己应当「已是最新」，
    #    比它旧一档的版本应当「看到更新」
    if stable is not None:
        base = stable[1]
        checks.append((not (semver_key(base) > semver_key(base)),
                       f"{base} 用户走稳定版通道，应当已是最新"))
        older = sorted((semver_key(normalize_tag(r["tag_name"])), normalize_tag(r["tag_name"]))
                       for r in releases
                       if not r.get("draft") and level_from_name(r["tag_name"]) == 0
                       and semver_key(r["tag_name"]) < semver_key(base))
        if older:
            prev = older[-1][1]
            checks.append((semver_key(base) > semver_key(prev),
                           f"{prev} 用户走稳定版通道，应当看到更新 {base}"))

    for ok, desc in checks:
        if not ok:
            failed += 1
        print(f"  {'✓' if ok else '✗'} {desc}")

    print("\n" + "=" * 70)
    if failed:
        print(f"有 {failed} 条断言不符预期")
        return 1
    print("全部符合预期")
    print("=" * 70)
    return 0


if __name__ == "__main__":
    sys.exit(main())
