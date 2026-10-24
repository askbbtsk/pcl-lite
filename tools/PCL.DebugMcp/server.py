#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
PCL Lite 调试 MCP 服务。

把「跑起来 → 看日志 → 截图 → 改配置 → 重新构建」这条调试闭环暴露成 MCP 工具，
让 AI 可以在没有人类点鼠标的情况下自己把问题定位出来。

设计取舍：
  * 全部基于「外部观察」（进程、窗口、日志文件、配置文件、源码），
    不往启动器里塞 IPC 通道，因此不需要改一行产品代码，Debug/Release/dist 三种产物都能调。
  * 启动进程用 DETACHED_PROCESS，父进程退出后启动器继续活着，
    这样 MCP 的一次工具调用结束不会把被测程序一起带走。
  * 所有路径都可以用环境变量覆盖，默认值按仓库结构自动推断。

环境变量：
  PCL_REPO_ROOT      仓库根目录，默认由本文件位置推断
  PCL_FLAVOR         默认产物：debug / release / dist，默认 debug
  PCL_MCP_OUT        截图等临时产物的输出目录，默认 <repo>/.mcp-out
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wt
import io
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable, Literal

from mcp.server.mcpserver import Image, MCPServer

# ---------------------------------------------------------------------------
# 一、环境与路径
# ---------------------------------------------------------------------------

REPO_ROOT = Path(
    os.environ.get("PCL_REPO_ROOT")
    or Path(__file__).resolve().parents[3]
).resolve()

DEFAULT_FLAVOR = os.environ.get("PCL_FLAVOR", "debug").lower()
OUT_DIR = Path(os.environ.get("PCL_MCP_OUT") or (REPO_ROOT / ".mcp-out")).resolve()

#: 每种产物：可执行文件目录、对应的漫游配置目录名
FLAVORS: dict[str, dict[str, str]] = {
    "debug": {
        "bin": "PCL-CE/Plain Craft Launcher 2/bin/Debug/net10.0-windows",
        "roaming": "PCLCE_Debug",
        "note": "调试版。启动时会弹「Debug 版本」提示，已用 PCL_DISABLE_DEBUG_HINT 关掉。",
    },
    "release": {
        "bin": "PCL-CE/Plain Craft Launcher 2/bin/Release/net10.0-windows",
        "roaming": "PCLCE",
        "note": "发布版，行为与用户实际运行一致，排查性能/渲染问题优先用它。",
    },
    "dist": {
        "bin": "dist/PCL Lite",
        "roaming": "PCLCE",
        "note": "打包产物，用于验证「用户拿到手的那份」是否正常。",
    },
}

EXE_NAME = "Plain Craft Launcher 2.exe"
CSPROJ = "PCL-CE/Plain Craft Launcher 2/Plain Craft Launcher 2.csproj"
WINDOW_TITLE_KEY = "PCL Lite"


def _flavor(name: str | None = None) -> dict[str, str]:
    key = (name or DEFAULT_FLAVOR or "debug").lower()
    if key not in FLAVORS:
        raise ValueError(f"未知产物 {key!r}，可选：{', '.join(FLAVORS)}")
    info = dict(FLAVORS[key])
    info["name"] = key
    info["dir"] = str(REPO_ROOT / info["bin"])
    info["exe"] = str(REPO_ROOT / info["bin"] / EXE_NAME)
    info["local_config"] = str(REPO_ROOT / info["bin"] / "PCL" / "config.v1.yml")
    info["log_dir"] = str(REPO_ROOT / info["bin"] / "PCL" / "Log")
    info["roaming_dir"] = str(Path(os.environ.get("APPDATA", "")) / info["roaming"])
    info["shared_config"] = str(Path(info["roaming_dir"]) / "config.v1.json")
    return info


def _ensure_out() -> Path:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    return OUT_DIR


def _ok(**kw: Any) -> dict[str, Any]:
    return {"ok": True, **kw}


def _err(message: str, **kw: Any) -> dict[str, Any]:
    return {"ok": False, "error": message, **kw}


# ---------------------------------------------------------------------------
# 二、日志解析
# ---------------------------------------------------------------------------

#: [12:07:34.021] [INFO] [TSK#11] [动画|animation] 正文
_LINE_RE = re.compile(
    r"^\[(?P<time>\d{2}:\d{2}:\d{2}\.\d{3})\]\s*"
    r"\[(?P<level>[A-Z]+)\]\s*"
    r"\[(?P<thread>[^\]]*)\]\s*"
    r"\[(?P<module>[^\]]*)\]\s?"
    r"(?P<message>.*)$"
)

_LEVEL_ALIASES = {
    "TRACE": "TRA",
    "DEBUG": "DBG",
    "INFO": "INF",
    "WARN": "WRN",
    "WARNING": "WRN",
    "ERROR": "ERR",
    "FATAL": "FTL",
}


def _normalize_level(level: str | None) -> set[str] | None:
    """把用户写的级别（大小写随意、全称缩写随意）归一成日志里的三字母缩写集合。"""
    if not level or level.strip().lower() in {"", "all", "*"}:
        return None
    wanted: set[str] = set()
    for part in re.split(r"[,\s|]+", level.strip().upper()):
        if not part:
            continue
        if part in {"WARN+", "WARNING+"}:
            wanted |= {"WRN", "ERR", "FTL"}
        elif part in {"ERROR+", "ERR+"}:
            wanted |= {"ERR", "FTL"}
        else:
            wanted.add(_LEVEL_ALIASES.get(part, part[:3]))
    return wanted or None


_LEVEL_ORDER = ["TRA", "DBG", "INF", "WRN", "ERR", "FTL"]


def _rank(level: str) -> int:
    """级别 → 序号（越大越严重）。未知级别当 TRACE 处理，永不抛异常。"""
    try:
        return _LEVEL_ORDER.index(level)
    except ValueError:
        return 0


def _parse_log(path: Path) -> list[dict[str, Any]]:
    """把一个日志文件解析成结构化条目；续行（堆栈）挂到上一条的 detail 上。"""
    entries: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            line = raw.rstrip("\r\n")
            m = _LINE_RE.match(line)
            if m:
                module = m.group("module")
                zh, _, en = module.partition("|")
                raw_level = m.group("level")
                entries.append(
                    {
                        "time": m.group("time"),
                        # 统一成三字母缩写：PCL 源码里写的是 INFO/WARN/DEBUG 全称，
                        # 但所有过滤/排序逻辑都按 TRA/DBG/INF/WRN/ERR/FTL 走。
                        "level": _LEVEL_ALIASES.get(raw_level, raw_level[:3]),
                        "level_raw": raw_level,
                        "thread": m.group("thread"),
                        "module": zh or module,
                        "module_id": en or zh or module,
                        "message": m.group("message"),
                        "detail": "",
                        "line_no": len(entries) + 1,
                    }
                )
            elif entries and line.strip():
                entries[-1]["detail"] += ("\n" if entries[-1]["detail"] else "") + line
    return entries


def _latest_log(log_dir: str | Path) -> Path | None:
    d = Path(log_dir)
    if not d.is_dir():
        return None
    files = sorted(d.glob("*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
    return files[0] if files else None


def _fmt_entry(e: dict[str, Any], with_detail: bool = True) -> str:
    head = f"[{e['time']}] [{e['level']}] [{e['module']}] {e['message']}"
    if with_detail and e["detail"]:
        return head + "\n" + e["detail"]
    return head


# ---------------------------------------------------------------------------
# 三、配置文件读写
# ---------------------------------------------------------------------------

#: 启动器写盘的顺序是「先写 .bak 再写正式文件」，所以读之前要确认文件不是半截的。
def _read_text(path: str | Path) -> str:
    p = Path(path)
    if not p.is_file():
        return ""
    return p.read_text(encoding="utf-8-sig", errors="replace")


def _parse_flat_yaml(text: str) -> dict[str, str]:
    """config.v1.yml 是扁平的 `键: 值`，没有嵌套，按行解析就够了。"""
    out: dict[str, str] = {}
    for line in text.splitlines():
        if not line or line.lstrip().startswith("#"):
            continue
        key, sep, value = line.partition(":")
        if not sep:
            continue
        out[key.strip()] = value.strip()
    return out


def _write_flat_yaml(path: Path, data: dict[str, str]) -> None:
    lines = [f"{k}: {v}" if v != "" else f"{k}: " for k, v in data.items()]
    path.write_text("\n".join(lines) + "\n", encoding="utf-8-sig")


def _local_config_path(flavor: str | None = None) -> Path:
    return Path(_flavor(flavor)["local_config"])


def _shared_config_path(flavor: str | None = None) -> Path:
    return Path(_flavor(flavor)["shared_config"])


def _config_get(key: str, store: str = "auto", flavor: str | None = None) -> dict[str, Any]:
    hits = []
    if store in {"auto", "local"}:
        data = _parse_flat_yaml(_read_text(_local_config_path(flavor)))
        if key in data:
            hits.append({"store": "local", "value": data[key], "path": str(_local_config_path(flavor))})
    if store in {"auto", "shared"}:
        p = _shared_config_path(flavor)
        if p.is_file():
            try:
                data = json.loads(_read_text(p) or "{}")
            except json.JSONDecodeError:
                data = {}
            if key in data:
                hits.append({"store": "shared", "value": data[key], "path": str(p)})
    return hits[0] if hits else {}


def _coerce(value: str) -> Any:
    low = value.strip().lower()
    if low in {"true", "false"}:
        return low == "true"
    if re.fullmatch(r"-?\d+", value.strip()):
        return int(value.strip())
    if re.fullmatch(r"-?\d*\.\d+", value.strip()):
        return float(value.strip())
    return value


def _config_set(key: str, value: Any, store: str = "auto", flavor: str | None = None) -> dict[str, Any]:
    local_p, shared_p = _local_config_path(flavor), _shared_config_path(flavor)
    if store == "auto":
        store = "local" if key in _parse_flat_yaml(_read_text(local_p)) or not shared_p.is_file() else "shared"

    if store == "local":
        data = _parse_flat_yaml(_read_text(local_p))
        if not data:
            return _err(f"本地配置不存在或为空：{local_p}")
        data[key] = "" if value is None else str(value)
        _write_flat_yaml(local_p, data)
        return _ok(store="local", path=str(local_p), key=key, value=data[key])

    if not shared_p.is_file():
        return _err(f"漫游配置不存在：{shared_p}")
    try:
        data = json.loads(_read_text(shared_p) or "{}")
    except json.JSONDecodeError as ex:
        return _err(f"漫游配置不是合法 JSON：{ex}")
    data[key] = value if not isinstance(value, str) else _coerce(value)
    shared_p.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
    return _ok(store="shared", path=str(shared_p), key=key, value=data[key])


# ---------------------------------------------------------------------------
# 四、Win32：窗口枚举 / 截图 / 输入
# ---------------------------------------------------------------------------

_user32 = ctypes.windll.user32
_gdi32 = ctypes.windll.gdi32
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:  # noqa: BLE001 - 老系统没有这个 API
    pass


def _enum_windows(title_part: str = WINDOW_TITLE_KEY) -> list[dict[str, Any]]:
    found: list[dict[str, Any]] = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def _cb(hwnd, _lparam):
        if not _user32.IsWindowVisible(hwnd):
            return True
        n = _user32.GetWindowTextLengthW(hwnd)
        if n == 0:
            return True
        buf = ctypes.create_unicode_buffer(n + 1)
        _user32.GetWindowTextW(hwnd, buf, n + 1)
        if title_part.lower() in buf.value.lower():
            rect = wt.RECT()
            _user32.GetWindowRect(hwnd, ctypes.byref(rect))
            found.append(
                {
                    "hwnd": int(hwnd),
                    "title": buf.value,
                    "rect": [rect.left, rect.top, rect.right, rect.bottom],
                    "width": rect.right - rect.left,
                    "height": rect.bottom - rect.top,
                }
            )
        return True

    _user32.EnumWindows(_cb, 0)
    return found


def _find_main_window() -> dict[str, Any] | None:
    wins = _enum_windows()
    if not wins:
        return None
    # 主窗口标题形如 "PCL Lite "，弹窗另有标题；取面积最大的那个最稳。
    return max(wins, key=lambda w: w["width"] * w["height"])


def _grab_window(hwnd: int) -> tuple[bytes, int, int]:
    """用 PrintWindow 抓窗口位图，返回 (PNG 字节, 宽, 高)。"""
    from PIL import Image as PILImage

    rect = wt.RECT()
    _user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w, h = rect.right - rect.left, rect.bottom - rect.top
    if w <= 0 or h <= 0:
        raise RuntimeError("窗口尺寸为 0，可能已经最小化")

    hdc = _user32.GetWindowDC(hwnd)
    mdc = _gdi32.CreateCompatibleDC(hdc)
    bmp = _gdi32.CreateCompatibleBitmap(hdc, w, h)
    _gdi32.SelectObject(mdc, bmp)

    class _BMIH(ctypes.Structure):
        _fields_ = [
            ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
            ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
            ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG), ("biYPelsPerMeter", wt.LONG),
            ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
        ]

    bi = _BMIH()
    bi.biSize = ctypes.sizeof(_BMIH)
    bi.biWidth = w
    bi.biHeight = -h  # 负数 = 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0

    if not _user32.PrintWindow(hwnd, mdc, 2):
        _gdi32.BitBlt(mdc, 0, 0, w, h, hdc, 0, 0, 0x00CC0020)

    buf = ctypes.create_string_buffer(w * h * 4)
    _gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bi), 0)

    img = PILImage.frombuffer("RGBA", (w, h), buf.raw, "raw", "BGRA", 0, 1).convert("RGB")
    out = io.BytesIO()
    img.save(out, format="PNG")

    _gdi32.DeleteObject(bmp)
    _gdi32.DeleteDC(mdc)
    _user32.ReleaseDC(hwnd, hdc)
    return out.getvalue(), w, h


class _MOUSEINPUT(ctypes.Structure):
    _fields_ = [
        ("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
        ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG)),
    ]


class _KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
        ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG)),
    ]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("mi", _MOUSEINPUT), ("ki", _KEYBDINPUT)]


class _INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", wt.DWORD), ("u", _INPUTUNION)]


def _send_mouse(flags: int, data: int = 0) -> None:
    ev = (_INPUT * 1)()
    ev[0].type = 0
    ev[0].mi.dwFlags = flags
    ev[0].mi.mouseData = data & 0xFFFFFFFF
    _user32.SendInput(1, ctypes.byref(ev), ctypes.sizeof(_INPUT))


def _client_to_screen(hwnd: int, x: int, y: int) -> tuple[int, int]:
    """
    客户区坐标 → 屏幕坐标。

    必须走 ClientToScreen，不能拿 GetWindowRect 的 left/top 直接加：
    PCL 的窗口带 DWM 阴影边框，GetWindowRect 比实际可见客户区大一圈，
    直接加会让点击整体偏左偏上（实测偏 40 px 左右，点按钮会落到旁边的输入框）。
    """
    point = wt.POINT(int(x), int(y))
    _user32.ClientToScreen(hwnd, ctypes.byref(point))
    return point.x, point.y


def _click_at(hwnd: int, x: int, y: int) -> dict[str, Any]:
    ax, ay = _client_to_screen(hwnd, x, y)
    _user32.SetForegroundWindow(hwnd)
    time.sleep(0.25)
    _user32.SetCursorPos(ax, ay)
    time.sleep(0.08)
    _send_mouse(0x0002)  # LEFTDOWN
    time.sleep(0.03)
    _send_mouse(0x0004)  # LEFTUP
    return {"x": ax, "y": ay}


def _scroll_at(hwnd: int, x: int, y: int, notches: int) -> None:
    ax, ay = _client_to_screen(hwnd, x, y)
    _user32.SetForegroundWindow(hwnd)
    time.sleep(0.2)
    _user32.SetCursorPos(ax, ay)
    time.sleep(0.1)
    step = 120 if notches > 0 else -120
    for _ in range(abs(notches)):
        _send_mouse(0x0800, step)
        time.sleep(0.08)


_VK = {
    "enter": 0x0D, "return": 0x0D, "esc": 0x1B, "escape": 0x1B, "tab": 0x09,
    "space": 0x20, "backspace": 0x08, "delete": 0x2E, "up": 0x26, "down": 0x28,
    "left": 0x25, "right": 0x27, "home": 0x24, "end": 0x23, "pageup": 0x21,
    "pagedown": 0x22, "f5": 0x74, "f12": 0x7B,
}


def _type_text(text: str) -> None:
    """用 KEYEVENTF_UNICODE 直接发字符，绕开输入法。"""
    for ch in text:
        ev = (_INPUT * 2)()
        for i, flag in enumerate((0x0004, 0x0004 | 0x0002)):  # KEYUP / KEYUP|KEYDOWN
            ev[i].type = 1
            ev[i].ki.wVk = 0
            ev[i].ki.wScan = ord(ch)
            ev[i].ki.dwFlags = flag
        _user32.SendInput(2, ctypes.byref(ev), ctypes.sizeof(_INPUT))
        time.sleep(0.01)


def _press_key(name: str) -> None:
    vk = _VK.get(name.strip().lower())
    if vk is None:
        raise ValueError(f"不支持的按键 {name!r}，可用：{', '.join(sorted(_VK))}")
    _user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.03)
    _user32.keybd_event(vk, 0, 2, 0)


# ---------------------------------------------------------------------------
# 五、进程控制
# ---------------------------------------------------------------------------

#: DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP
_DETACHED = 0x00000008 | 0x00000200
#: CREATE_BREAKAWAY_FROM_JOB —— 让子进程跳出父进程所在的 Job Object。
#: 没有这一位时，MCP 服务一退出（编辑器重启、工具调用结束被回收），
#: Windows 会把 Job 里所有进程一起杀掉，启动器跟着凭空消失。
_BREAKAWAY = 0x01000000


def _run_text(cmd: list[str], timeout: float = 20) -> str:
    """
    跑一个外部命令并返回文本。

    统一按字节收，再自己解码：Windows 上 tasklist / taskkill 用的是 OEM 代码页（简体中文是 936），
    直接 text=True 会让 Python 按 UTF-8 解，抛 UnicodeDecodeError 之后 stdout 变成 None。
    """
    try:
        proc = subprocess.run(cmd, capture_output=True, timeout=timeout)
    except Exception:  # noqa: BLE001
        return ""
    raw = proc.stdout or b""
    for enc in ("utf-8", "mbcs", "gbk"):
        try:
            return raw.decode(enc)
        except (UnicodeDecodeError, LookupError):
            continue
    return raw.decode("utf-8", errors="replace")


def _pids() -> list[int]:
    out = _run_text(["tasklist", "/FI", f"IMAGENAME eq {EXE_NAME}", "/FO", "CSV", "/NH"], 15)
    pids: list[int] = []
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 2 and parts[0].lower() == EXE_NAME.lower():
            try:
                pids.append(int(parts[1]))
            except ValueError:
                pass
    return pids


def _launch(flavor: str | None, args: list[str] | None, wait_seconds: float) -> dict[str, Any]:
    info = _flavor(flavor)
    exe = Path(info["exe"])
    if not exe.is_file():
        return _err(
            f"找不到可执行文件：{exe}",
            hint="先跑一次 pcl_build，或把 flavor 换成已经构建过的产物。",
        )

    env = dict(os.environ)
    env.setdefault("PCL_DISABLE_DEBUG_HINT", "1")  # 免掉 Debug 版的「特殊版本提示」弹窗
    cmd = [str(exe), *(args or [])]

    def _spawn(flags: int) -> subprocess.Popen:
        return subprocess.Popen(
            cmd, cwd=str(exe.parent), env=env,
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            creationflags=flags,
        )

    try:
        proc = _spawn(_DETACHED | _BREAKAWAY)
    except OSError:
        # 当前进程不在允许 breakaway 的 Job 里，退化成普通脱离
        proc = _spawn(_DETACHED)

    deadline = time.time() + max(wait_seconds, 0)
    win = None
    while time.time() < deadline:
        win = _find_main_window()
        if win:
            break
        time.sleep(0.4)

    return _ok(
        pid=proc.pid,
        flavor=info["name"],
        exe=str(exe),
        window_found=bool(win),
        window=win,
        waited_seconds=round(max(wait_seconds, 0), 1),
    )


def _kill() -> dict[str, Any]:
    pids = _pids()
    if not pids:
        return _ok(killed=[], note="本来就没在跑")
    _run_text(["taskkill", "/F", "/PID", *[str(p) for p in pids]], 20)
    time.sleep(0.6)
    return _ok(killed=pids, still_running=_pids())


# ---------------------------------------------------------------------------
# 六、MCP 服务与工具
# ---------------------------------------------------------------------------

mcp = MCPServer(
    name="pcl-debug",
    title="PCL Lite 调试台",
    version="1.0.0",
    instructions=(
        "这是 PCL Lite（C# / WPF 启动器）的调试服务。它不修改产品代码，"
        "而是从外部观察和驱动启动器：启动/结束进程、抓窗口截图、解析日志、"
        "读写配置、检查网络授权状态、编译、检索源码。\n\n"
        "典型用法：\n"
        "1. pcl_build 确认能编译；\n"
        "2. pcl_app_launch 起进程，pcl_app_screenshot 看界面；\n"
        "3. 出问题就 pcl_log_errors / pcl_log_search 定位，配合 pcl_repo_grep 找代码；\n"
        "4. 改完 pcl_app_kill → pcl_build → pcl_app_launch 复验。\n\n"
        "注意：启动器退出时会把内存里的配置整体写回文件，"
        "所以 pcl_config_set 必须在进程关闭状态下做，否则会被覆盖。"
    ),
)


# --------------------------------- 进程 --------------------------------- #


@mcp.tool(
    name="pcl_app_launch",
    description=(
        "启动 PCL Lite 并等待主窗口出现。\n\n"
        "进程以 DETACHED_PROCESS 启动，父进程退出后它继续存活，"
        "所以调用结束不会把启动器带走，后续工具还能继续操作它。\n"
        "默认会设置 PCL_DISABLE_DEBUG_HINT=1 来跳过 Debug 版的「特殊版本提示」弹窗。\n\n"
        "返回值里的 window_found=false 表示进程起来了但窗口没出来——"
        "通常意味着启动过程中崩了，接着用 pcl_log_errors 看原因。"
    ),
)
def pcl_app_launch(
    flavor: Literal["debug", "release", "dist"] | None = None,
    args: list[str] | None = None,
    wait_seconds: float = 20,
) -> dict[str, Any]:
    """
    Args:
        flavor: 要启动哪份产物。debug 便于调试，release 最接近用户实际体验，dist 用于验收打包产物。
        args: 追加的命令行参数（一般不需要）。
        wait_seconds: 等待主窗口出现的秒数，超时也返回，只是 window_found=false。
    """
    if _pids():
        return _err("已经有一个 PCL Lite 在跑了，先 pcl_app_kill 再启动。", pids=_pids())
    return _launch(flavor, args, wait_seconds)


@mcp.tool(
    name="pcl_app_kill",
    description=(
        "结束所有 PCL Lite 进程。\n\n"
        "⚠️ 启动器退出时会把内存里的配置写回磁盘，"
        "如果你刚用 pcl_config_set 改过配置，先确认改动已经被启动器读到，否则会被覆盖。"
    ),
)
def pcl_app_kill() -> dict[str, Any]:
    return _kill()


@mcp.tool(
    name="pcl_app_status",
    description=(
        "查询 PCL Lite 当前是否在运行：进程号、主窗口句柄与位置尺寸、启动时长。\n\n"
        "排查「点了没反应」这类问题时先看这里——"
        "进程在但窗口不在，说明卡在启动阶段；窗口在但尺寸异常，说明布局出了问题。"
    ),
)
def pcl_app_status() -> dict[str, Any]:
    pids = _pids()
    win = _find_main_window()
    out: dict[str, Any] = {"running": bool(pids), "pids": pids, "window": win}

    if pids:
        line = _run_text(["tasklist", "/FI", f"PID eq {pids[0]}", "/FO", "CSV", "/NH"], 15)
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) >= 5:
            try:
                out["memory_kb"] = int(parts[4].replace(",", "").replace("K", "").strip())
            except ValueError:
                pass

    log = _latest_log(_flavor()["log_dir"])
    if log:
        out["latest_log"] = {"name": log.name, "mtime": _ts(log.stat().st_mtime), "size": log.stat().st_size}
    return out


@mcp.tool(
    name="pcl_app_screenshot",
    description=(
        "截取 PCL Lite 主窗口，返回 PNG 图片本体（可以直接看）。\n\n"
        "排查界面问题的第一步：先看整体，再用 crop 参数放大局部。"
        "crop 用窗口内坐标 [x, y, 宽, 高]，例如 [330, 20, 450, 48] 就是顶部导航条。\n\n"
        "图片同时会落盘一份，路径在返回值的 path 里，方便多次对比。"
    ),
)
def pcl_app_screenshot(
    crop: list[int] | None = None,
    scale: float = 1.0,
    save_as: str | None = None,
) -> Any:
    """
    Args:
        crop: 可选裁剪区域 [x, y, width, height]，窗口内坐标（物理像素）。
        scale: 放大倍数，看细节时用 2~4。
        save_as: 保存文件名，默认按时间戳生成。
    """
    win = _find_main_window()
    if not win:
        return _err("没找到 PCL Lite 窗口，先 pcl_app_launch。")

    try:
        png, w, h = _grab_window(win["hwnd"])
    except Exception as ex:  # noqa: BLE001
        return _err(f"截图失败：{ex}")

    from PIL import Image as PILImage

    img = PILImage.open(io.BytesIO(png))
    if crop and len(crop) == 4:
        x, y, cw, ch = crop
        img = img.crop((x, y, x + cw, y + ch))
    if scale and scale != 1.0:
        img = img.resize((int(img.width * scale), int(img.height * scale)), PILImage.LANCZOS)

    name = save_as or f"shot-{datetime.now():%H%M%S}.png"
    path = _ensure_out() / name
    img.save(path, format="PNG")

    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return [
        Image(data=buf.getvalue(), format="png"),
        {"ok": True, "path": str(path), "width": img.width, "height": img.height, "window": [w, h]},
    ]


@mcp.tool(
    name="pcl_app_windows",
    description=(
        "列出当前所有标题含「PCL Lite」的可见窗口（含弹窗、对话框），带句柄和位置尺寸。\n\n"
        "授权弹窗、错误提示这类东西会以独立窗口出现，"
        "主窗口截图看不到它们时用这个确认是不是被挡住了。"
    ),
)
def pcl_app_windows() -> dict[str, Any]:
    return _ok(windows=_enum_windows())


# --------------------------------- UI --------------------------------- #


@mcp.tool(
    name="pcl_ui_click",
    description=(
        "在 PCL Lite 窗口内点击指定坐标（窗口内坐标，不是屏幕坐标）。\n\n"
        "坐标可以从 pcl_app_screenshot 的图上量：截图左上角就是 (0,0)。"
        "点之前建议先截图确认位置，点完再截一次确认结果。\n\n"
        "注意 PCL 的复选框命中区比视觉框略大，点在文字上也能生效。"
    ),
)
def pcl_ui_click(x: int, y: int, settle_seconds: float = 0.6) -> dict[str, Any]:
    """
    Args:
        x: 窗口内横坐标。
        y: 窗口内纵坐标。
        settle_seconds: 点击后等待动画稳定的秒数。
    """
    win = _find_main_window()
    if not win:
        return _err("没找到 PCL Lite 窗口。")
    pos = _click_at(win["hwnd"], x, y)
    time.sleep(max(settle_seconds, 0))
    return _ok(**pos)


@mcp.tool(
    name="pcl_ui_scroll",
    description=(
        "在 PCL Lite 窗口内滚动鼠标滚轮。正数向上、负数向下，每格 120。\n\n"
        "设置页很长，很多选项（比如网络授权、液态玻璃）在需要滚动才能看到的位置。"
    ),
)
def pcl_ui_scroll(x: int, y: int, notches: int = 3, settle_seconds: float = 0.5) -> dict[str, Any]:
    win = _find_main_window()
    if not win:
        return _err("没找到 PCL Lite 窗口。")
    _scroll_at(win["hwnd"], x, y, notches)
    time.sleep(max(settle_seconds, 0))
    return _ok(notches=notches)


@mcp.tool(
    name="pcl_ui_type",
    description=(
        "向当前前台窗口输入文本，或按一个功能键。\n\n"
        "text 用 KEYEVENTF_UNICODE 逐字符发送，不经过输入法，中文也能打进去。\n"
        "key 支持 enter / esc / tab / space / backspace / delete / up / down / left / right "
        "/ home / end / pageup / pagedown / f5 / f12。\n\n"
        "调用前先用 pcl_ui_click 把焦点点到目标输入框。"
    ),
)
def pcl_ui_type(text: str | None = None, key: str | None = None) -> dict[str, Any]:
    if not text and not key:
        return _err("text 和 key 至少要给一个。")
    try:
        if text:
            _type_text(text)
        if key:
            _press_key(key)
    except Exception as ex:  # noqa: BLE001
        return _err(str(ex))
    return _ok(typed=bool(text), key=key)


# --------------------------------- 日志 --------------------------------- #


@mcp.tool(
    name="pcl_log_list",
    description=(
        "列出某个产物的日志文件，按修改时间倒序。\n\n"
        "每次启动都会生成一个 Launch-<日期>-<时间>.log，"
        "排查问题时要认准「本次运行」的那份，别读到上一次的。"
    ),
)
def pcl_log_list(flavor: Literal["debug", "release", "dist"] | None = None, limit: int = 10) -> dict[str, Any]:
    info = _flavor(flavor)
    d = Path(info["log_dir"])
    if not d.is_dir():
        return _err(f"日志目录不存在：{d}", hint="这个产物可能还没运行过。")
    files = sorted(d.glob("*.log"), key=lambda p: p.stat().st_mtime, reverse=True)[: max(limit, 1)]
    return _ok(
        dir=str(d),
        files=[
            {
                "name": f.name,
                "size": f.stat().st_size,
                "mtime": _ts(f.stat().st_mtime),
                "age_seconds": round(time.time() - f.stat().st_mtime, 1),
            }
            for f in files
        ],
    )


@mcp.tool(
    name="pcl_log_tail",
    description=(
        "读取日志末尾 N 条。默认读最新的那份日志。\n\n"
        "level 可以过滤级别：'WRN' / 'ERR' / 'ERR+'（错误及以上）/ 'WRN+'（警告及以上），"
        "不传就是全部。写 'ERR,FTL' 也行。\n\n"
        "日志格式：[时间] [级别] [线程] [模块] 正文，异常堆栈会挂在对应条目的下面一起返回。"
    ),
)
def pcl_log_tail(
    lines: int = 80,
    level: str | None = None,
    file: str | None = None,
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    """
    Args:
        lines: 返回最后多少条（过滤之后的数量）。
        level: 级别过滤，如 'WRN+'。
        file: 指定日志文件名，默认最新。
        flavor: 读哪个产物的日志。
    """
    info = _flavor(flavor)
    path = Path(info["log_dir"]) / file if file else _latest_log(info["log_dir"])
    if not path or not path.is_file():
        return _err("没有找到日志文件。", dir=info["log_dir"])

    entries = _parse_log(path)
    wanted = _normalize_level(level)
    if wanted:
        entries = [e for e in entries if e["level"] in wanted]
    picked = entries[-max(lines, 1):]
    return _ok(
        file=path.name,
        total_matched=len(entries),
        returned=len(picked),
        entries=picked,
        text="\n".join(_fmt_entry(e) for e in picked),
    )


@mcp.tool(
    name="pcl_log_search",
    description=(
        "在日志里按正则搜索，返回命中行及其上下文。\n\n"
        "这是定位问题的主力工具。常用模式：\n"
        "  - 找异常：pattern='Exception|错误|失败'\n"
        "  - 找某个模块：pattern='Network|网络'\n"
        "  - 找某个方法：pattern='BuildConsentList'\n\n"
        "大小写默认不敏感。context 是每条命中前后各带几条，方便看因果。"
    ),
)
def pcl_log_search(
    pattern: str,
    context: int = 2,
    limit: int = 40,
    file: str | None = None,
    flavor: Literal["debug", "release", "dist"] | None = None,
    ignore_case: bool = True,
) -> dict[str, Any]:
    info = _flavor(flavor)
    path = Path(info["log_dir"]) / file if file else _latest_log(info["log_dir"])
    if not path or not path.is_file():
        return _err("没有找到日志文件。")

    flags = re.IGNORECASE if ignore_case else 0
    try:
        rx = re.compile(pattern, flags)
    except re.error as ex:
        return _err(f"正则不合法：{ex}")

    entries = _parse_log(path)
    hit_idx = [i for i, e in enumerate(entries) if rx.search(_fmt_entry(e))]
    if not hit_idx:
        return _ok(file=path.name, matches=0, text="（没有命中）")

    blocks: list[str] = []
    seen: set[int] = set()
    for i in hit_idx[: max(limit, 1)]:
        lo, hi = max(0, i - context), min(len(entries), i + context + 1)
        for j in range(lo, hi):
            if j in seen:
                continue
            seen.add(j)
            mark = ">>" if j == i else "  "
            blocks.append(f"{mark} {_fmt_entry(entries[j])}")
        blocks.append("")

    return _ok(file=path.name, matches=len(hit_idx), text="\n".join(blocks))


@mcp.tool(
    name="pcl_log_errors",
    description=(
        "把所有警告/错误/致命日志抽出来，按「模块 + 消息首行」聚合成问题清单。\n\n"
        "这是判断「到底哪里坏了」最快的一步："
        "同一个异常被触发 20 次只会占一行，带出现次数和首次出现的行号。\n\n"
        "返回里每条都带完整堆栈，可以直接拿去对照源码。"
    ),
)
def pcl_log_errors(
    min_level: Literal["WRN", "ERR", "FTL"] = "WRN",
    limit: int = 30,
    file: str | None = None,
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    info = _flavor(flavor)
    path = Path(info["log_dir"]) / file if file else _latest_log(info["log_dir"])
    if not path or not path.is_file():
        return _err("没有找到日志文件。")

    floor = _rank(min_level)
    entries = [e for e in _parse_log(path) if _rank(e["level"]) >= floor]

    groups: dict[tuple[str, str], dict[str, Any]] = {}
    for e in entries:
        key = (e["module"], e["message"][:80])
        g = groups.setdefault(
            key,
            {
                "level": e["level"],
                "module": e["module"],
                "message": e["message"],
                "count": 0,
                "first_line": e["line_no"],
                "first_time": e["time"],
                "detail": e["detail"],
            },
        )
        g["count"] += 1
        if _rank(e["level"]) > _rank(g["level"]):
            g["level"] = e["level"]

    ranked = sorted(groups.values(), key=lambda g: (_rank(g["level"]), g["count"]), reverse=True)
    return _ok(
        file=path.name,
        total=len(entries),
        unique=len(ranked),
        groups=ranked[: max(limit, 1)],
    )


@mcp.tool(
    name="pcl_log_stats",
    description=(
        "日志统计：按级别、按模块各有多少条，以及时间跨度。\n\n"
        "用来快速判断这次运行「健康不健康」——"
        "错误数 0 但功能不正常，说明问题在别处（网络被拦、配置错、UI 没渲染）。"
    ),
)
def pcl_log_stats(
    file: str | None = None,
    flavor: Literal["debug", "release", "dist"] | None = None,
    top_modules: int = 15,
) -> dict[str, Any]:
    info = _flavor(flavor)
    path = Path(info["log_dir"]) / file if file else _latest_log(info["log_dir"])
    if not path or not path.is_file():
        return _err("没有找到日志文件。")

    entries = _parse_log(path)
    by_level: dict[str, int] = {}
    by_module: dict[str, int] = {}
    for e in entries:
        by_level[e["level"]] = by_level.get(e["level"], 0) + 1
        by_module[e["module"]] = by_module.get(e["module"], 0) + 1

    return _ok(
        file=path.name,
        entries=len(entries),
        first_time=entries[0]["time"] if entries else None,
        last_time=entries[-1]["time"] if entries else None,
        by_level=dict(sorted(by_level.items(), key=lambda kv: -kv[1])),
        top_modules=dict(sorted(by_module.items(), key=lambda kv: -kv[1])[: max(top_modules, 1)]),
    )


# --------------------------------- 配置 --------------------------------- #


@mcp.tool(
    name="pcl_config_get",
    description=(
        "读一个配置项。PCL 的配置分两处存：\n"
        "  - local：<产物目录>/PCL/config.v1.yml，便携模式下的扁平 `键: 值`\n"
        "  - shared：%APPDATA%/<PCLCE|PCLCE_Debug>/config.v1.json，JSON\n\n"
        "store='auto' 会先找 local 再找 shared。键名就是配置文件里的原始键，"
        "比如 UiLiquidGlass、NetworkConsentAllowAll、SystemPrivacyMode。"
    ),
)
def pcl_config_get(
    key: str,
    store: Literal["auto", "local", "shared"] = "auto",
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    hit = _config_get(key, store, flavor)
    if not hit:
        return _err(f"没找到配置项 {key!r}", hint="用 pcl_config_dump 看有哪些键。")
    return _ok(**hit)


@mcp.tool(
    name="pcl_config_set",
    description=(
        "写一个配置项。\n\n"
        "⚠️ 必须在启动器关闭时调用。启动器退出时会把内存里的整份配置写回文件，"
        "覆盖掉你在这里做的修改。正确顺序是 pcl_app_kill → pcl_config_set → pcl_app_launch。\n\n"
        "store='auto' 时，键已存在于 local 就写 local，否则写 shared。"
    ),
)
def pcl_config_set(
    key: str,
    value: str | int | float | bool,
    store: Literal["auto", "local", "shared"] = "auto",
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    if _pids():
        return _err(
            "启动器还在运行，写配置会被它在退出时覆盖。",
            hint="先 pcl_app_kill。",
        )
    return _config_set(key, value, store, flavor)


@mcp.tool(
    name="pcl_config_dump",
    description=(
        "导出某份产物的全部配置（local 与 shared 两处），可选按关键字过滤。\n\n"
        "不确定某个功能对应哪个配置键时，先 dump 再 grep 关键字，"
        "比翻源码快得多。"
    ),
)
def pcl_config_dump(
    contains: str | None = None,
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    info = _flavor(flavor)
    local_p, shared_p = Path(info["local_config"]), Path(info["shared_config"])
    local = _parse_flat_yaml(_read_text(local_p))

    shared: dict[str, Any] = {}
    if shared_p.is_file():
        try:
            shared = json.loads(_read_text(shared_p) or "{}")
        except json.JSONDecodeError:
            shared = {"__parse_error__": "config.v1.json 不是合法 JSON"}

    if contains:
        needle = contains.lower()
        local = {k: v for k, v in local.items() if needle in k.lower() or needle in str(v).lower()}
        shared = {k: v for k, v in shared.items() if needle in k.lower() or needle in str(v).lower()}

    return _ok(
        local_path=str(local_p),
        local_exists=local_p.is_file(),
        local=local,
        shared_path=str(shared_p),
        shared_exists=shared_p.is_file(),
        shared=shared,
    )


# --------------------------------- 网络授权 --------------------------------- #


@mcp.tool(
    name="pcl_net_consent_state",
    description=(
        "查看网络授权当前状态：是否「授权全部连接」、隐私模式、永久白名单里有哪些用途，"
        "以及完整的用途登记表（每个用途依赖哪些域名）。\n\n"
        "排查「功能莫名失败但日志没报错」时优先看这里——"
        "PCL Lite 的出站请求会被按用途拦截，拦截是静默的。"
    ),
)
def pcl_net_consent_state(flavor: Literal["debug", "release", "dist"] | None = None) -> dict[str, Any]:
    allow_all = _config_get("NetworkConsentAllowAll", "auto", flavor).get("value", True)
    privacy = _config_get("SystemPrivacyMode", "auto", flavor).get("value", False)
    allowed_raw = _config_get("NetworkAllowedPurposes", "auto", flavor).get("value", "") or ""
    seeded = _config_get("NetworkConsentSeeded", "auto", flavor).get("value", False)
    allowed = [x for x in str(allowed_raw).split("|") if x]

    catalog = _read_catalog()
    return _ok(
        allow_all=bool(allow_all),
        privacy_mode=bool(privacy),
        seeded=bool(seeded),
        persisted_allowed=allowed,
        purposes=catalog,
        summary=(
            "全部放行（不拦截任何请求）"
            if allow_all
            else ("隐私模式：未列入白名单的用途一律拒绝" if privacy else "按用途逐个确认")
        ),
    )


@mcp.tool(
    name="pcl_net_consent_set",
    description=(
        "修改网络授权状态。\n\n"
        "action 取值：\n"
        "  - allow_all / deny_all：打开或关闭「授权全部连接」\n"
        "  - privacy_on / privacy_off：开关隐私模式\n"
        "  - allow：把 purpose 加入永久白名单（需要 purpose 参数）\n"
        "  - revoke：从永久白名单移除（需要 purpose 参数）\n"
        "  - reset：清空白名单\n\n"
        "⚠️ 和 pcl_config_set 一样，必须在启动器关闭时调用。"
    ),
)
def pcl_net_consent_set(
    action: Literal[
        "allow_all", "deny_all", "privacy_on", "privacy_off", "allow", "revoke", "reset"
    ],
    purpose: str | None = None,
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    if _pids():
        return _err("启动器还在运行，改完会被覆盖。先 pcl_app_kill。")

    if action in {"allow_all", "deny_all"}:
        return _config_set("NetworkConsentAllowAll", action == "allow_all", "auto", flavor)
    if action in {"privacy_on", "privacy_off"}:
        return _config_set("SystemPrivacyMode", action == "privacy_on", "auto", flavor)

    if action == "reset":
        return _config_set("NetworkAllowedPurposes", "", "auto", flavor)

    if not purpose:
        return _err("allow / revoke 需要 purpose 参数（用途 Id，如 mod-repo）。")

    valid = {p["id"] for p in _read_catalog()}
    if purpose not in valid:
        return _err(f"未知用途 {purpose!r}", valid=sorted(valid))

    current = _config_get("NetworkAllowedPurposes", "auto", flavor).get("value", "") or ""
    items = [x for x in str(current).split("|") if x]
    if action == "allow" and purpose not in items:
        items.append(purpose)
    elif action == "revoke" and purpose in items:
        items.remove(purpose)
    return _config_set("NetworkAllowedPurposes", "|".join(items), "auto", flavor)


@mcp.tool(
    name="pcl_net_log",
    description=(
        "从日志里捞最近的网络相关记录：授权决策、拦截、请求失败。\n\n"
        "关键词覆盖 Network / 网络用途 / 授权结果 / 隐私模式 / 拒绝 / http / DNS。"
    ),
)
def pcl_net_log(
    limit: int = 40,
    flavor: Literal["debug", "release", "dist"] | None = None,
) -> dict[str, Any]:
    info = _flavor(flavor)
    path = _latest_log(info["log_dir"])
    if not path:
        return _err("没有找到日志文件。")

    rx = re.compile(
        r"network|网络用途|授权结果|隐私模式|拒绝|request|http|dns|proxy|超时|timeout",
        re.IGNORECASE,
    )
    entries = [e for e in _parse_log(path) if rx.search(e["module"] + " " + e["message"])]
    picked = entries[-max(limit, 1):]
    return _ok(
        file=path.name,
        total=len(entries),
        text="\n".join(_fmt_entry(e, with_detail=False) for e in picked),
    )


def _read_catalog() -> list[dict[str, Any]]:
    """从 NetworkPurpose.cs 里把用途登记表抠出来，避免在 MCP 里维护第二份副本。"""
    src = REPO_ROOT / "PCL-CE/PCL.Core/IO/Net/Consent/NetworkPurpose.cs"
    text = _read_text(src)
    if not text:
        return []

    out: list[dict[str, Any]] = []
    # new( NetworkPurpose.X, "id", "中文", "English", ... , [ "host", ... ] )
    for m in re.finditer(
        r'new\(\s*NetworkPurpose\.(?P<enum>\w+),\s*"(?P<id>[^"]+)",\s*'
        r'"(?P<zh>[^"]+)",\s*"(?P<en>[^"]+)"',
        text,
    ):
        out.append({"enum": m.group("enum"), "id": m.group("id"), "title": m.group("zh")})

    # 补上每个用途的域名列表
    for item in out:
        block = re.search(
            r'NetworkPurpose\.' + re.escape(item["enum"]) + r'[\s\S]*?\]\s*\)',
            text,
        )
        if block:
            hosts = re.findall(r'"([^"]+\.[^"]+)"', block.group(0))
            item["hosts"] = [h for h in hosts if h not in {item["id"]}]
        else:
            item["hosts"] = []
    return out


# --------------------------------- 构建 --------------------------------- #


_LAST_BUILD: dict[str, Any] = {}


@mcp.tool(
    name="pcl_build",
    description=(
        "编译启动器，返回结构化结果：错误列表、警告数、耗时。\n\n"
        "改完代码必跑。Debug 用于调试（带 Debug 提示、启动更快），"
        "Release 用于验收（行为和用户一致）。\n\n"
        "编译前会自动结束正在运行的进程，否则 PCL.Core.dll 被占用会报 MSB3027。\n"
        "结果同时缓存在服务里，pcl_build_last 可以取回上一次的完整输出。"
    ),
)
def pcl_build(
    config: Literal["Debug", "Release"] = "Debug",
    timeout_seconds: int = 600,
) -> dict[str, Any]:
    global _LAST_BUILD
    if _pids():
        _kill()

    started = time.time()
    try:
        proc = subprocess.run(
            ["dotnet", "build", CSPROJ, "-c", config, "--nologo"],
            cwd=str(REPO_ROOT), capture_output=True, timeout=timeout_seconds,
        )
    except subprocess.TimeoutExpired:
        return _err(f"编译超过 {timeout_seconds}s 未结束，已放弃。")

    raw = (proc.stdout or b"") + (proc.stderr or b"")
    try:
        out = raw.decode("utf-8")
    except UnicodeDecodeError:
        out = raw.decode("mbcs", errors="replace")
    errors = []
    warnings = 0
    for line in out.splitlines():
        m = re.search(r"(?P<file>[^:\n]+)\((?P<line>\d+),(?P<col>\d+)\):\s*error\s+(?P<code>\w+):\s*(?P<msg>.*)", line)
        if m:
            errors.append(
                {
                    "file": m.group("file").strip(),
                    "line": int(m.group("line")),
                    "col": int(m.group("col")),
                    "code": m.group("code"),
                    "message": m.group("msg").strip(),
                }
            )
        elif re.search(r":\s*warning\s+\w+:", line):
            warnings += 1

    deduped = {(e["file"], e["line"], e["code"], e["message"]): e for e in errors}
    result = _ok(
        config=config,
        exit_code=proc.returncode,
        success=proc.returncode == 0,
        seconds=round(time.time() - started, 1),
        error_count=len(deduped),
        warning_count=warnings,
        errors=list(deduped.values())[:40],
        output_tail="\n".join(out.splitlines()[-40:]),
    )
    _LAST_BUILD = result
    return result


@mcp.tool(
    name="pcl_build_last",
    description="取回上一次 pcl_build 的完整结果（含输出尾部），用来回看已经滚过去的编译信息。",
)
def pcl_build_last() -> dict[str, Any]:
    if not _LAST_BUILD:
        return _err("还没编译过。")
    return _LAST_BUILD


# --------------------------------- 源码 --------------------------------- #


@mcp.tool(
    name="pcl_repo_grep",
    description=(
        "在仓库里按正则搜索源码，返回文件、行号和整行内容。\n\n"
        "默认只搜 .cs / .xaml / .csproj，避免把 bin/obj 里的东西翻出来。\n"
        "找「这个字符串从哪来的」「这个配置项谁在用」这类问题时用它，比逐个读文件快得多。"
    ),
)
def pcl_repo_grep(
    pattern: str,
    glob: str = "*.{cs,xaml,csproj}",
    limit: int = 60,
    ignore_case: bool = True,
) -> dict[str, Any]:
    try:
        rx = re.compile(pattern, re.IGNORECASE if ignore_case else 0)
    except re.error as ex:
        return _err(f"正则不合法：{ex}")

    exts = re.findall(r"\*?\.?([a-zA-Z]+)", glob)
    allowed = {("." + e).lower() for e in exts if e}
    skip = {"bin", "obj", ".git", ".vs", "node_modules", ".mcp-out"}

    hits = []
    for root, dirs, files in os.walk(REPO_ROOT):
        dirs[:] = [d for d in dirs if d.lower() not in skip]
        for fn in files:
            if allowed and Path(fn).suffix.lower() not in allowed:
                continue
            p = Path(root) / fn
            try:
                text = p.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            for i, line in enumerate(text.splitlines(), 1):
                if rx.search(line):
                    hits.append(
                        {
                            "file": str(p.relative_to(REPO_ROOT)).replace("\\", "/"),
                            "line": i,
                            "text": line.strip()[:300],
                        }
                    )
                    if len(hits) >= limit:
                        return _ok(matches=len(hits), truncated=True, hits=hits)
    return _ok(matches=len(hits), truncated=False, hits=hits)


@mcp.tool(
    name="pcl_repo_read",
    description=(
        "读仓库里的一个文件，可以只取一段行。路径用相对仓库根目录的形式，比如 "
        "'PCL-CE/PCL.Core/IO/Net/Consent/NetworkConsentService.cs'。\n\n"
        "配合 pcl_repo_grep 用：先 grep 定位行号，再 read 那一段。"
    ),
)
def pcl_repo_read(path: str, start: int = 1, lines: int = 200) -> dict[str, Any]:
    p = (REPO_ROOT / path).resolve()
    if not str(p).startswith(str(REPO_ROOT)):
        return _err("路径越界。")
    if not p.is_file():
        return _err(f"文件不存在：{path}")
    text = p.read_text(encoding="utf-8", errors="replace")
    all_lines = text.splitlines()
    start = max(start, 1)
    chunk = all_lines[start - 1: start - 1 + max(lines, 1)]
    return _ok(
        path=path,
        total_lines=len(all_lines),
        start=start,
        end=start + len(chunk) - 1,
        text="\n".join(f"{start + i:>5}  {ln}" for i, ln in enumerate(chunk)),
    )


@mcp.tool(
    name="pcl_repo_tree",
    description=(
        "列出仓库的目录结构，默认两层深。\n\n"
        "不熟悉这个项目时先用它建立整体印象，再决定去哪里 grep。"
    ),
)
def pcl_repo_tree(path: str = "", depth: int = 2) -> dict[str, Any]:
    base = (REPO_ROOT / path).resolve()
    if not str(base).startswith(str(REPO_ROOT)):
        return _err("路径越界。")
    if not base.is_dir():
        return _err(f"目录不存在：{path}")

    skip = {"bin", "obj", ".git", ".vs", "node_modules", ".mcp-out", "packages"}
    lines: list[str] = []

    def walk(d: Path, prefix: str, level: int) -> None:
        if level > depth:
            return
        try:
            items = sorted(d.iterdir(), key=lambda x: (x.is_file(), x.name.lower()))
        except OSError:
            return
        items = [i for i in items if i.name.lower() not in skip]
        for item in items[:60]:
            lines.append(f"{prefix}{'[D] ' if item.is_dir() else '    '}{item.name}")
            if item.is_dir():
                walk(item, prefix + "    ", level + 1)

    walk(base, "", 1)
    return _ok(root=str(REPO_ROOT), path=path or ".", text="\n".join(lines))


# ---------------------------------------------------------------------------
# 七、MCP prompts：把排查套路固化成可复用的话术
# ---------------------------------------------------------------------------


@mcp.prompt(
    name="debug-startup-crash",
    description="启动器起不来 / 一起就崩：从进程状态到异常堆栈的完整排查流程。",
)
def _p_debug_startup_crash(symptom: str = "") -> str:
    return f"""你要排查 PCL Lite 启动阶段的问题。现象：{symptom or '（未提供，先自己确认）'}

按这个顺序来，每一步都基于观察而不是猜测：

1. `pcl_app_status` —— 现在有没有进程？有没有窗口？
   - 有进程没窗口 → 卡在窗口创建之前，大概率是 XAML 解析或初始化异常
   - 进程都没有 → 启动即崩，或根本没起来
2. `pcl_app_launch` 重新拉一次，注意返回的 window_found。
3. `pcl_log_errors` min_level='ERR' —— 先看有没有 FTL/ERR。
4. 如果日志是空的，`pcl_log_tail` lines=120 看最后到哪一步停住：
   日志停在哪个模块，问题就在那个模块之后的代码里。
5. 找到堆栈后，`pcl_repo_grep` 搜方法名定位源码，`pcl_repo_read` 读上下文。

注意：Debug 版启动时会弹「特殊版本提示」，我们已经用 PCL_DISABLE_DEBUG_HINT 关掉了；
如果你看到这个弹窗，说明环境变量没生效，先确认。

最后给出：根因、涉及文件与行号、最小修复方案。不要在没有日志证据的情况下下结论。"""


@mcp.prompt(
    name="debug-visual-glitch",
    description="界面显示不对：用截图 + 像素级对比定位视觉问题。",
)
def _p_debug_visual_glitch(area: str = "整个窗口", expectation: str = "") -> str:
    return f"""你要排查 PCL Lite 的界面问题。关注区域：{area}。期望表现：{expectation or '（未提供）'}

方法：**先看，再量，最后才改**。

1. `pcl_app_screenshot` 拿整体图，肉眼确认问题在哪。
2. `pcl_app_screenshot` 带 crop=[x,y,w,h] 和 scale=3~4 放大可疑区域。
   坐标用窗口内坐标，从整图上量。
3. 如果要对比「开关前 / 开关后」，用 `pcl_ui_click` 切状态，各截一张，
   然后自己逐像素比较（可以写脚本读两张 PNG 求差）。
   **不要凭印象说「看起来一样」**，要给出具体像素值。
4. 涉及布局时，`pcl_repo_grep` 找对应的 XAML，`pcl_repo_read` 看模板。
5. 涉及绘制（Effect / ShaderEffect / 自定义控件）时，重点怀疑：
   - 中间位图重采样导致的边缘发虚（WPF 加 Effect 的经典坑）
   - 非整数像素布局
   - DynamicResource 没刷到

结论要写成：现象（附像素证据）→ 根因 → 改哪个文件的哪一段 → 改完怎么验证。"""


@mcp.prompt(
    name="debug-network-blocked",
    description="功能静默失败、疑似被网络授权拦截：从授权状态查到具体请求。",
)
def _p_debug_network_blocked(feature: str = "") -> str:
    return f"""有个功能联网失败或者干脆没反应。涉及功能：{feature or '（未提供）'}

PCL Lite 的所有出站请求都会先过 `NetworkGuardHandler`，按「用途」授权。
拦截是**静默**的——日志里可能一条错误都没有，所以这个顺序很重要：

1. `pcl_net_consent_state` —— 看 allow_all / privacy_mode / 白名单。
   - allow_all=true 说明不是授权问题，往网络本身查
   - privacy_mode=true 且用途不在白名单 → 就是它
2. `pcl_net_log` —— 捞最近的授权决策与拦截记录。
3. `pcl_log_search` pattern='拒绝|deny|blocked|Network'。
4. 确认是授权问题后，`pcl_net_consent_set` action='allow_all' 或
   action='allow' purpose='<用途Id>' 打开，再 `pcl_app_launch` 复验。

如果 allow_all 已经是 true 还是失败，那问题在更下层：
检查代理配置（`pcl_config_dump contains='Proxy'`）、DNS（EnableDoH）、
以及目标域名是否真的可达。

结论要区分清楚：是「被自己的授权拦了」还是「外面真的连不上」。"""


@mcp.prompt(
    name="debug-build-failure",
    description="编译失败：从错误码到源码的定位与修复。",
)
def _p_debug_build_failure(error_hint: str = "") -> str:
    return f"""编译不过。已知线索：{error_hint or '（未提供，先跑一次编译）'}

1. `pcl_build` config='Debug' —— 拿结构化错误列表（文件/行/列/错误码）。
   编译前它会自动 kill 掉在跑的进程，避免 MSB3027 文件占用。
2. 按错误码分类处理：
   - `CS0246 / CS0103` 找不到类型或成员 → 多半是 using 缺失或命名空间写错
   - `CS0121` 调用有歧义 → 显式转换参数类型
   - `CS0117` 类型没有该成员 → API 用错了，去 grep 真实定义
   - `MC####` / `XLS####` XAML 错误 → 资源键拼错、x:Name 重复、命名空间没声明
   - `MSB3027 / MSB3021` 文件被占用 → 还有进程没退，`pcl_app_kill`
3. `pcl_repo_read` 读报错行前后 30 行再动手改。
4. 改完 `pcl_build` 复验，直到 error_count=0。

不要用「加个 try/catch」或「注释掉」来让编译过。要修根因。"""


@mcp.prompt(
    name="implement-feature",
    description="新增一个功能：先摸清现状，再动手，最后自测。",
)
def _p_implement_feature(requirement: str) -> str:
    return f"""要在 PCL Lite 里实现这个需求：

{requirement}

按这个节奏走，别一上来就写代码：

1. **摸底**：`pcl_repo_tree` 看结构，`pcl_repo_grep` 找相关的现有实现
   （同类功能、同类控件、同类配置项）。PCL 的约定很多，抄现成的比自己发明稳。
2. **定位置**：说清楚要改哪几个文件、每个文件改什么。UI 在 `Plain Craft Launcher 2`，
   底层能力在 `PCL.Core`，配置项在 `PCL.Core/App/Config.cs`。
3. **注意项目约定**：
   - 配置项用 `[ConfigItem<T>("键名", 默认值, ConfigSource.Local)]`，`ConfigSource.Local`
     会写进便携的 config.v1.yml
   - 设置页的控件如果用 `Tag` + `SetByTag` 写配置，部分键会**静默失败**，
     稳妥做法是写专用事件直接赋值给强类型属性
   - 多语言要同时改 7 个语言文件，缺键会显示成 key 本身
   - 只加功能不要顺手重构无关代码
4. **实现**，然后 `pcl_build`。
5. **自测**：`pcl_app_launch` → `pcl_app_screenshot` 确认界面 →
   `pcl_ui_click` 操作 → 再截图 → `pcl_log_errors` 确认没引入新错误。
6. 汇报：改了哪些文件、为什么这么改、验证结果（附截图或日志证据）。"""


@mcp.prompt(
    name="verify-change",
    description="改完之后的回归自检：确认改动生效且没有副作用。",
)
def _p_verify_change(change: str = "") -> str:
    return f"""刚做了改动：{change or '（未提供）'}。现在做回归自检。

1. `pcl_app_kill` → `pcl_build` config='Release' —— Release 能过才算真的过。
2. `pcl_app_launch` flavor='release'，等窗口出来。
3. `pcl_app_screenshot` 看整体，`crop` 放大改动涉及的区域。
4. `pcl_log_errors` min_level='WRN' —— 有没有新冒出来的警告/错误，
   和改动前的日志比一比（`pcl_log_list` 可以找到旧日志）。
5. 走一遍受影响的相邻功能，确认没被带坏。
6. 如果涉及配置项，`pcl_config_get` 确认键和值都对。

输出一份检查表：每项写「通过 / 不通过 / 未覆盖」，不通过的要给证据。"""


# ---------------------------------------------------------------------------
# 八、MCP resources
# ---------------------------------------------------------------------------


@mcp.resource(
    "pcl://log/latest",
    name="最新日志",
    description="当前产物最新一份运行日志的完整内容。",
    mime_type="text/plain",
)
def _res_latest_log() -> str:
    path = _latest_log(_flavor()["log_dir"])
    if not path:
        return "（没有日志文件）"
    return _read_text(path)


@mcp.resource(
    "pcl://config/local",
    name="本地配置",
    description="便携模式的 config.v1.yml 原文。",
    mime_type="text/plain",
)
def _res_local_config() -> str:
    p = _local_config_path()
    return _read_text(p) if p.is_file() else "（配置文件不存在）"


@mcp.resource(
    "pcl://project/overview",
    name="项目概览",
    description="仓库结构、产物路径、构建方式的一份速查表。",
    mime_type="text/plain",
)
def _res_project_overview() -> str:
    lines = [
        f"仓库根目录：{REPO_ROOT}",
        f"默认产物：{DEFAULT_FLAVOR}",
        f"截图输出：{OUT_DIR}",
        "",
        "产物（flavor）：",
    ]
    for key in FLAVORS:
        info = _flavor(key)
        exists = "存在" if Path(info["exe"]).is_file() else "未构建"
        lines.append(f"  - {key:8s} {info['bin']}  [{exists}]  {info['note']}")
    lines += [
        "",
        "构建：dotnet build " + CSPROJ + " -c Debug|Release",
        "",
        "配置文件：",
        "  local  = <产物目录>/PCL/config.v1.yml",
        "  shared = %APPDATA%/PCLCE(_Debug)/config.v1.json",
        "",
        "日志：<产物目录>/PCL/Log/Launch-YYYY-MM-DD-HHmmssfff.log",
    ]
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# 九、入口
# ---------------------------------------------------------------------------


def _ts(epoch: float) -> str:
    return datetime.fromtimestamp(epoch).strftime("%Y-%m-%d %H:%M:%S")


if __name__ == "__main__":
    if "--selftest" in sys.argv:
        # 不经过 MCP 协议，直接把关键路径跑一遍，方便本地验证
        print("repo:", REPO_ROOT)
        for key in FLAVORS:
            info = _flavor(key)
            print(f"  {key}: exe={Path(info['exe']).is_file()} log={Path(info['log_dir']).is_dir()}")
        print("windows:", _enum_windows())
        print("pids:", _pids())
        print("catalog:", len(_read_catalog()), "purposes")
        print("latest log:", _latest_log(_flavor()["log_dir"]))
        sys.exit(0)

    mcp.run(transport="stdio")
