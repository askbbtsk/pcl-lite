#!/usr/bin/env python3
"""
验证 Azure 应用的 client_id 能否用于 Minecraft 正版登录（设备代码流）。

用途：注册完 Azure 应用后，先跑这个脚本确认配置正确，再去启动器里试。
它会把容易踩的坑直接翻译成人话，而不是甩一串 AADSTS 错误码。

用法：
    python verify-ms-client-id.py <client_id>
    python verify-ms-client-id.py                 # 读环境变量 PCL_MS_CLIENT_ID
    python verify-ms-client-id.py <client_id> --full   # 一路走到 Minecraft 档案

仅依赖标准库。
"""

import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

# 全部直连，避免系统代理劫持导致误判
_OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))

DEVICE_ENDPOINT = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode"
TOKEN_ENDPOINT = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token"
XBL_ENDPOINT = "https://user.auth.xboxlive.com/user/authenticate"
XSTS_ENDPOINT = "https://xsts.auth.xboxlive.com/xsts/authorize"
MC_LOGIN_ENDPOINT = "https://api.minecraftservices.com/authentication/login_with_xbox"
MC_PROFILE_ENDPOINT = "https://api.minecraftservices.com/minecraft/profile"

SCOPE = "XboxLive.signin offline_access"

ERROR_HINTS = {
    "AADSTS700016": (
        "client_id 不存在，或者应用的账户类型选错了。\n"
        "    这个流程走 /consumers/ 端点，Azure 里必须选「仅限个人帐户」。\n"
        "    选「任何 Entra ID 租户 + 个人 Microsoft 帐户」也会报这个。"
    ),
    "AADSTS7000218": (
        "应用没有开启「允许公共客户端流」。\n"
        "    Azure 门户 → 应用 → 身份验证 → 拉到底 → 高级设置 → 允许公共客户端流 → 是 → 保存"
    ),
    "AADSTS65005": (
        "应用缺少 Xbox Live 权限。\n"
        "    API 权限 → 添加权限 → 我的组织使用的 API → 搜 Xbox Live → XboxLive.signin → 添加"
    ),
    "AADSTS50020": "用了工作/学校账号登录。这个应用只收个人微软账号。",
    "AADSTS50034": "账号不在该目录里。同上，换个人账号。",
    "AADSTS90002": "租户不存在。检查 client_id 是否抄错。",
    "AADSTS900971": "client_id 为空或格式不对。",
    "invalid_grant": "授权码已失效（一般 15 分钟过期）。重跑一次脚本。",
    "expired_token": "设备代码已过期。重跑一次脚本。",
    "authorization_declined": "你在浏览器里点了拒绝。重跑并选择同意。",
    "bad_verification_code": "验证码输错了。",
}

XERR_HINTS = {
    "2148916233": (
        "这个微软账号还没有 Xbox 档案。\n"
        "    去 https://xbox.com 用同一账号登录一次，它会自动创建。"
    ),
    "2148916235": "Xbox Live 在你所在地区不可用。",
    "2148916236": "账号需要完成成人验证。",
    "2148916237": "账号需要完成成人验证。",
    "2148916238": (
        "账号是未成年人，且没有加入 Microsoft Family 家庭组。\n"
        "    需要用家长账号把你加进去。"
    ),
}


def _request(url, method="GET", data=None, json_body=False, token=None):
    body = None
    if data is not None:
        body = json.dumps(data).encode() if json_body else urllib.parse.urlencode(data).encode()

    req = urllib.request.Request(url, data=body, method=method)
    req.add_header("Accept", "application/json")
    if data is not None:
        req.add_header(
            "Content-Type",
            "application/json" if json_body else "application/x-www-form-urlencoded",
        )
    if token:
        req.add_header("Authorization", f"Bearer {token}")

    try:
        with _OPENER.open(req, timeout=30) as resp:
            return resp.status, json.loads(resp.read().decode())
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(raw)
        except json.JSONDecodeError:
            return e.code, {"_raw": raw}


def _explain(payload):
    code = str(payload.get("error") or "")
    desc = str(payload.get("error_description") or payload.get("errorMessage") or "")
    blob = f"{code} {desc}"

    for key, hint in ERROR_HINTS.items():
        if key in blob:
            return hint

    if desc:
        return desc.split("\r\n")[0][:300]
    if code:
        return code
    return json.dumps(payload, ensure_ascii=False)[:300]


def _xerr_hint(payload):
    xerr = str(payload.get("XErr") or "")
    return xerr, XERR_HINTS.get(xerr)


def step(msg):
    print(f"\n\u001b[36m\u25b6 {msg}\u001b[0m")


def ok(msg):
    print(f"  \u001b[32m\u2713\u001b[0m {msg}")


def bad(msg):
    print(f"  \u001b[31m\u2717\u001b[0m {msg}")


def main():
    plain = [a for a in sys.argv[1:] if not a.startswith("--")]
    full = "--full" in sys.argv

    client_id = (plain[0] if plain else os.environ.get("PCL_MS_CLIENT_ID", "")).strip()

    if not client_id:
        print("用法: python verify-ms-client-id.py <client_id>")
        print("      或者先设置环境变量 PCL_MS_CLIENT_ID")
        return 2

    print(f"client_id : {client_id}")
    print(f"验证范围  : {'完整流程（含 Minecraft 档案）' if full else '仅微软授权'}")

    # ── 1. 申请设备代码 ──────────────────────────────────────────
    step("申请设备代码")
    status, data = _request(DEVICE_ENDPOINT, "POST", {"client_id": client_id, "scope": SCOPE})

    if status != 200 or "device_code" not in data:
        bad(f"失败（HTTP {status}）")
        print(f"    {_explain(data)}")
        return 1

    device_code = data["device_code"]
    user_code = data["user_code"]
    verify_uri = data.get("verification_uri") or "https://microsoft.com/link"
    interval = max(int(data.get("interval", 5)), 1)
    expires_in = int(data.get("expires_in", 900))

    ok("微软接受了这个 client_id，配置没问题")
    print()
    print("  ─────────────────────────────────────────────")
    print(f"   浏览器打开 : {verify_uri}")
    print(f"   输入代码   : {user_code}")
    print("  ─────────────────────────────────────────────")
    print(f"\n  代码 {expires_in // 60} 分钟内有效，等待登录中...")

    # ── 2. 轮询换 token ─────────────────────────────────────────
    step("等待授权")
    deadline = time.time() + expires_in
    oauth = None

    while time.time() < deadline:
        status, data = _request(TOKEN_ENDPOINT, "POST", {
            "client_id": client_id,
            "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
            "device_code": device_code,
        })
        err = data.get("error")

        if err == "authorization_pending":
            print("  \u00b7 等待中...", end="\r", flush=True)
            time.sleep(interval)
            continue
        if err == "slow_down":
            interval += 5
            time.sleep(interval)
            continue
        if err:
            print()
            bad("授权失败")
            print(f"    {_explain(data)}")
            return 1
        if data.get("access_token"):
            print()
            ok("拿到微软 access_token")
            oauth = data
            break

    if not oauth:
        bad("超时，设备代码已过期")
        return 1

    ms_token = oauth["access_token"]
    if oauth.get("refresh_token"):
        ok("拿到 refresh_token（可免密刷新）")

    if not full:
        print("\n\u001b[32m配置完全正确。\u001b[0m 设置环境变量后重启启动器即可正版登录：")
        print(f'\n    setx PCL_MS_CLIENT_ID "{client_id}"\n')
        print("想验证到 Minecraft 档案，加 --full 重跑。")
        return 0

    # ── 3. Xbox Live ────────────────────────────────────────────
    step("Xbox Live 认证")
    status, data = _request(XBL_ENDPOINT, "POST", {
        "Properties": {"RpsTicket": f"d={ms_token}", "SiteName": "user.auth.xboxlive.com"},
        "RelyingParty": "http://auth.xboxlive.com",
        "TokenType": "JWT",
    }, json_body=True)

    if status != 200 or "Token" not in data:
        bad(f"失败（HTTP {status}）")
        xerr, hint = _xerr_hint(data)
        print(f"    {hint or _explain(data)}")
        return 1

    xbl_token = data["Token"]
    uhs = data["DisplayClaims"]["xui"][0]["uhs"]
    ok(f"XBL token 到手，user hash = {uhs}")

    # ── 4. XSTS ─────────────────────────────────────────────────
    step("XSTS 授权")
    status, data = _request(XSTS_ENDPOINT, "POST", {
        "Properties": {"SandboxId": "RETAIL", "UserTokens": [xbl_token]},
        "RelyingParty": "rp://api.minecraftservices.com/",
        "TokenType": "JWT",
    }, json_body=True)

    if status != 200 or "Token" not in data:
        bad(f"失败（HTTP {status}）")
        xerr, hint = _xerr_hint(data)
        print(f"    {('XErr ' + xerr + ': ') if xerr else ''}{hint or _explain(data)}")
        return 1

    xsts_token = data["Token"]
    ok("XSTS token 到手")

    # ── 5. 换 Minecraft token ───────────────────────────────────
    step("换取 Minecraft token")
    status, data = _request(MC_LOGIN_ENDPOINT, "POST", {
        "identityToken": f"XBL3.0 x={uhs};{xsts_token}",
    }, json_body=True)

    if status != 200 or "access_token" not in data:
        bad(f"失败（HTTP {status}）")
        print(f"    {_explain(data)}")
        print("    如果是 401，多半是这个账号没买 Minecraft。")
        return 1

    mc_token = data["access_token"]
    ok("Minecraft token 到手")

    # ── 6. 读档案 ───────────────────────────────────────────────
    step("读取玩家档案")
    status, data = _request(MC_PROFILE_ENDPOINT, "GET", token=mc_token)

    if status == 404:
        bad("这个账号没有 Minecraft 档案")
        print("    说明账号没买游戏。登录流程本身是通的。")
        return 1
    if status != 200:
        bad(f"失败（HTTP {status}）")
        print(f"    {_explain(data)}")
        return 1

    skins = data.get("skins") or []
    print()
    print("  ─────────────────────────────────────────────")
    print(f"   玩家名 : {data.get('name')}")
    print(f"   UUID   : {data.get('id')}")
    print(f"   皮肤数 : {len(skins)}")
    print("  ─────────────────────────────────────────────")
    print("\n\u001b[32m全链路通过。\u001b[0m 启动器里可以直接用这个 client_id 了。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
