"""按 PCL Lite 的拼装逻辑真实拉起一个 Lunar 子版本，用于验证多版本支持。

用法: python lunar_launch_test.py 1.21.4 [--keep]
"""
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request
import zipfile
from concurrent.futures import ThreadPoolExecutor

PROXY = "http://127.0.0.1:7890"
OPENER = urllib.request.build_opener(
    urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}))
API = "https://api.lunarclientprod.com/launcher"
ROOT = os.path.expanduser("~/.lunarclient")
MULTIVER = os.path.join(ROOT, "offline", "multiver")
NATIVES = os.path.join(MULTIVER, "natives")
INST = "8f2a1c4e-3b7d-4a91-9c25-6d1e0f3a5b78"


def launch(sub, tries=4):
    body = {
        "installation_id": INST, "os": "win32", "os_release": "10.0.19045",
        "arch": "x64", "launcher_version": "3.3.0", "version": sub,
        "branch": "master", "args": [], "module": "lunar",
        "canary_preference": "NEUTRAL",
        "profile": {"id": "00000000-0000-0000-0000-000000000000", "name": "Player"},
    }
    last = None
    for _ in range(tries):
        try:
            req = urllib.request.Request(
                f"{API}/launch", data=json.dumps(body).encode(),
                headers={"Content-Type": "application/json",
                         "User-Agent": "Lunar Client Launcher v3.3.0"})
            with OPENER.open(req, timeout=40) as r:
                return json.loads(r.read())
        except Exception as exc:  # noqa: BLE001
            last = exc
            time.sleep(1.5)
    raise last


def metadata(sub, tries=4):
    url = (f"{API}/metadata/versions/lunar?branch=master"
           f"&installation_id={INST}")
    last = None
    for _ in range(tries):
        try:
            req = urllib.request.Request(url, headers={
                "User-Agent": "Lunar Client Launcher v3.3.0"})
            with OPENER.open(req, timeout=40) as r:
                data = json.loads(r.read())
            for version in data["versions"]:
                for sv in version.get("subversions") or []:
                    if sv["id"] == sub:
                        return sv
            return None
        except Exception as exc:  # noqa: BLE001
            last = exc
            time.sleep(1.5)
    raise last


def fetch(url, dest):
    tmp = dest + ".tmp"
    req = urllib.request.Request(url, headers={"User-Agent": "PCL-Lite-Test"})
    with OPENER.open(req, timeout=120) as r, open(tmp, "wb") as f:
        while True:
            chunk = r.read(1 << 20)
            if not chunk:
                break
            f.write(chunk)
    os.replace(tmp, dest)


def sha1(path):
    import hashlib
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def ensure(artifacts, target, verify=True):
    def stale(a):
        dest = os.path.join(target, a["name"])
        if not os.path.exists(dest):
            return True
        if not verify or not a.get("sha1"):
            return False
        return sha1(dest) != a["sha1"]

    missing = [a for a in artifacts if stale(a)]
    if not missing:
        print(f"  物料齐了：{len(artifacts)} 个（SHA-1 全部匹配）")
        return
    print(f"  需要下载 {len(missing)} 个物料…")
    total = [0]

    def work(a):
        dest = os.path.join(target, a["name"])
        fetch(a["url"], dest)
        total[0] += 1
        print(f"    [{total[0]}/{len(missing)}] {a['name']} ({a.get('size', 0)//1024} KB)")

    with ThreadPoolExecutor(max_workers=8) as pool:
        list(pool.map(work, missing))


def main():
    sub = sys.argv[1] if len(sys.argv) > 1 else "1.21.4"
    data = launch(sub)
    if not data.get("success"):
        print("启动接口拒绝:", json.dumps(data)[:300])
        return 1

    ltd = data["launchTypeData"]
    arts = ltd["artifacts"]
    classpath = [a for a in arts if a["type"] == "CLASS_PATH"]
    external = [a for a in arts if a["type"] not in ("CLASS_PATH", "NATIVES")]
    natives = [a for a in arts if a["type"] == "NATIVES"]

    print(f"版本 {sub}: 主类 {ltd['mainClass']}, 附件 {len(arts)} "
          f"(classpath {len(classpath)} / external {len(external)} / natives {len(natives)})")

    os.makedirs(MULTIVER, exist_ok=True)
    os.makedirs(NATIVES, exist_ok=True)
    ensure(arts, MULTIVER)

    for a in natives:
        archive = os.path.join(MULTIVER, a["name"])
        if os.path.exists(archive):
            try:
                zipfile.ZipFile(archive).extractall(NATIVES)
            except Exception as exc:  # noqa: BLE001
                print("  解压失败", a["name"], exc)

    jre = data["jre"]
    java = os.path.join(ROOT, "jre", jre["folderChecksum"],
                        *jre["executablePathInArchive"])
    java = java.replace("javaw.exe", "java.exe")
    if not os.path.exists(java):
        print("缺 JRE:", java)
        return 1

    meta = metadata(sub) or {}
    asset_index = (meta.get("assets") or {}).get("id", "")
    ui_sha1 = (data.get("ui") or {}).get("sourceSha1", "")
    game_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "lunar-test-game")
    os.makedirs(game_dir, exist_ok=True)

    args = [
        "-Xmx2048m",
        f"-Djava.library.path={NATIVES}",
        "-Dichor.usingIsolatedProfiles=true",
        "-XX:+DisableAttachMechanism",
        "-XX:-CreateCoredumpOnCrash",
        "-XX:-CreateMinidumpOnCrash",
    ]
    args += jre.get("extraArguments") or []
    args += ["-cp", ";".join(os.path.join(MULTIVER, a["name"]) for a in classpath),
             ltd["mainClass"]]
    args += ["--ichorClassPath", ",".join(a["name"] for a in classpath)]
    args += ["--ichorExternalFiles", ",".join(a["name"] for a in external)]
    args += ["--version", sub]
    args += ["--launcherVersion", "3.3.0"]
    args += ["--launcherFeatureFlags", '{"enabled":[],"disabled":[]}']
    args += ["--installationId", INST]
    args += ["--overwolfMuid", "not specified"]
    args += ["--sentryTraceId", "not specified"]
    args += ["--launchId", "11111111-2222-3333-4444-555555555555"]
    args += ["--canaryToken", data.get("canaryToken", "")]
    args += ["--username", "TestPlayer"]
    args += ["--uuid", "00000000000000000000000000000000"]
    args += ["--xuid", "0"]
    args += ["--accessToken", "0" * 32]
    args += ["--userProperties", "{}"]
    if asset_index:
        args += ["--assetIndex", asset_index]
    args += ["--gameDir", game_dir]
    args += ["--assetsDir", os.path.join(ROOT, "shared", "assets")]
    args += ["--texturesDir", os.path.join(ROOT, "textures")]
    args += ["--uiDir", os.path.join(ROOT, "ui", ui_sha1) if ui_sha1 else os.path.join(ROOT, "ui")]
    args += ["--webosrDir", NATIVES]
    args += ["--workingDirectory", "."]
    args += ["--classpathDir", MULTIVER]
    args += ["--width", "854", "--height", "480"]
    args += ["--ipcPort", "0"]

    log_path = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                            f"lunar-{sub}.log")
    print("启动中，日志:", log_path)
    with open(log_path, "w", encoding="utf-8", errors="replace") as log:
        proc = subprocess.Popen([java, *args], cwd=game_dir,
                                stdout=log, stderr=subprocess.STDOUT)
        deadline = time.time() + 70
        seen_main = False
        while time.time() < deadline:
            if proc.poll() is not None:
                break
            time.sleep(2)
            try:
                with open(log_path, encoding="utf-8", errors="replace") as f:
                    text = f.read()
                if "Starting game!" in text or "Setting user" in text or "main" in text.lower():
                    seen_main = True
            except OSError:
                pass
        alive = proc.poll() is None
        if alive:
            proc.kill()
        print(f"存活={alive} 退出码={proc.poll()}")

    with open(log_path, encoding="utf-8", errors="replace") as f:
        text = f.read()
    for key in ["Exception", "Error", "Failed", "Starting game", "Mixin", "Setting user"]:
        hits = [ln for ln in text.splitlines() if key in ln]
        if hits:
            print(f"--- {key} ({len(hits)}) ---")
            for ln in hits[:6]:
                print("   ", ln[:180])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
