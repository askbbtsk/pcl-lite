"""Validate only the v2 service manifest. No legacy update.json requests or fallback.

Usage:
  python tools/verify-manifest-v2.py --v2 release/updatev2.json
  python tools/verify-manifest-v2.py
"""
import argparse
import json
import re
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed

SERVICE = "bedkillerspacex-boop/PCL-Lite-service"


def parse(body):
    root = json.loads(body.lstrip("\ufeff"))
    entries = root if isinstance(root, list) else next(
        (root[key] for key in ("sources", "repositories", "repos", "mirrors", "servers", "nodes")
         if isinstance(root, dict) and isinstance(root.get(key), list)), None)
    if entries is None:
        raise ValueError("v2 source list is required; v1 pointers are not supported")
    sources = []
    for entry in entries:
        pointer = entry if isinstance(entry, str) else entry.get("repo") if isinstance(entry, dict) else None
        if not isinstance(pointer, str) or not re.fullmatch(r"[\w.-]+/[\w.-]+", pointer):
            raise ValueError(f"invalid repository entry: {entry!r}")
        sources.append(pointer)
    if not sources:
        raise ValueError("v2 source list is empty")
    return sources


def fetch(url, api=False):
    request = urllib.request.Request(url, headers={"User-Agent": "PCL-Lite-v2-check",
        "Accept": "application/vnd.github.raw+json" if api else "application/json"})
    with urllib.request.urlopen(request, timeout=10) as response:
        body = response.read(1024 * 1024).decode("utf-8-sig")
    return url, parse(body)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--v2", help="validate a local v2 file without network access")
    args = parser.parse_args()
    if args.v2:
        with open(args.v2, encoding="utf-8-sig") as source:
            print("v2 OK:", ", ".join(parse(source.read())))
        return
    channels = [
        (f"https://api.github.com/repos/{SERVICE}/contents/updatev2.json", True),
        (f"https://raw.githubusercontent.com/{SERVICE}/main/updatev2.json", False),
        (f"https://cdn.jsdelivr.net/gh/{SERVICE}@main/updatev2.json", False),
    ]
    errors = []
    success = False
    with ThreadPoolExecutor(max_workers=len(channels)) as pool:
        for future in as_completed([pool.submit(fetch, *channel) for channel in channels]):
            try:
                url, sources = future.result()
                success = True
                print(f"v2 OK: {url}: {', '.join(sources)}")
            except Exception as error:
                errors.append(str(error))
    if not success:
        raise SystemExit("All v2 channels failed; no v1 request made: " + "; ".join(errors))


if __name__ == "__main__":
    main()
