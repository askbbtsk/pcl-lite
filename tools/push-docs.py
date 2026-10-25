"""把 LICENSE / LICENSE-APACHE / 三个 README 作为单个提交推到 Pcl-Lite 的 main。

刻意不使用 git push：本地仓库的 origin 指向上游，且工作区有 66 个源码改动，
push 会把整份源码历史一起推上去。这里只走 Git Data API，只影响这 5 个文件。
"""
import base64
import json
import subprocess
import sys

OWNER = "Pcl-Lite"
REPO = "Pcl-Lite"
BRANCH = "main"
MESSAGE = "docs: 加入许可证并重写 README"

ROOT = r"E:/DESKTOP/project/Pcl-lite/PCL-CE"
FILES = ["LICENSE", "LICENSE-APACHE", "README.md", "README-EN.md", "README-ZH_TW.md"]


def gh(method, path, payload=None):
    cmd = ["gh", "api", "--method", method, path]
    if payload is not None:
        cmd += ["--input", "-"]
    proc = subprocess.run(
        cmd,
        input=json.dumps(payload).encode() if payload is not None else None,
        capture_output=True,
    )
    if proc.returncode != 0:
        print(f"[FAIL] {method} {path}\n{proc.stderr.decode('utf-8', 'ignore')}")
        sys.exit(1)
    out = proc.stdout.decode("utf-8", "ignore").strip()
    return json.loads(out) if out else {}


ref = gh("GET", f"repos/{OWNER}/{REPO}/git/ref/heads/{BRANCH}")
parent = ref["object"]["sha"]
base_tree = gh("GET", f"repos/{OWNER}/{REPO}/git/commits/{parent}")["tree"]["sha"]
print(f"parent={parent[:10]} base_tree={base_tree[:10]}")

tree = []
for name in FILES:
    with open(f"{ROOT}/{name}", "rb") as f:
        raw = f.read()
    blob = gh("POST", f"repos/{OWNER}/{REPO}/git/blobs",
              {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
    tree.append({"path": name, "mode": "100644", "type": "blob", "sha": blob["sha"]})
    print(f"  blob {name:16s} {len(raw):6d} B  {blob['sha'][:10]}")

new_tree = gh("POST", f"repos/{OWNER}/{REPO}/git/trees",
              {"base_tree": base_tree, "tree": tree})["sha"]
commit = gh("POST", f"repos/{OWNER}/{REPO}/git/commits",
            {"message": MESSAGE, "tree": new_tree, "parents": [parent]})["sha"]
gh("PATCH", f"repos/{OWNER}/{REPO}/git/refs/heads/{BRANCH}", {"sha": commit})
print(f"OK commit={commit}")
