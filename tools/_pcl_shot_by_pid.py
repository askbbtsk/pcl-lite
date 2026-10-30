"""按 PID 定位 PCL 主窗口，还原后截图。

MCP 自带的 `_find_main_window` 按标题子串匹配，浏览器标签页（GitHub Release 页标题里
也有 "PCL Lite"）会被误抓；这里改成按进程 PID 匹配。
"""
import ctypes
import os
import subprocess
import sys
import time
import types

if "mcp" not in sys.modules:
    _mcp = types.ModuleType("mcp")
    _mcp_server = types.ModuleType("mcp.server")
    _mcp_mcpserver = types.ModuleType("mcp.server.mcpserver")

    class _StubServer:
        def __init__(self, *a, **kw):
            pass

        def tool(self, *a, **kw):
            return lambda f: f

        prompt = resource = tool

        def run(self, *a, **kw):
            pass

    _mcp_mcpserver.MCPServer = _StubServer
    _mcp_mcpserver.Image = type("Image", (), {"__init__": lambda self, **kw: None})
    _mcp.server = _mcp_server
    _mcp_server.mcpserver = _mcp_mcpserver
    sys.modules["mcp"] = _mcp
    sys.modules["mcp.server"] = _mcp_server
    sys.modules["mcp.server.mcpserver"] = _mcp_mcpserver

sys.path.insert(0, r"E:/DESKTOP/project/Pcl-lite/PCL-CE/tools/PCL.DebugMcp")
import server  # noqa: E402

OUT = r"E:/DESKTOP/project/Pcl-lite/.mcp-out"
os.makedirs(OUT, exist_ok=True)


def pids() -> set[int]:
    rows = subprocess.run(
        ["tasklist", "/FI", "IMAGENAME eq Plain Craft Launcher 2.exe", "/FO", "CSV", "/NH"],
        capture_output=True, text=True, encoding="gbk", errors="replace").stdout.strip()
    found = set()
    for line in rows.splitlines():
        parts = line.split('","')
        if len(parts) > 1:
            try:
                found.add(int(parts[1]))
            except ValueError:
                pass
    return found


def windows_of(wanted: set[int]) -> list[tuple[int, str]]:
    found: list[tuple[int, str]] = []

    @ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
    def _cb(hwnd, _lparam):
        pid = ctypes.c_ulong()
        server._user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in wanted:
            n = server._user32.GetWindowTextLengthW(hwnd)
            buf = ctypes.create_unicode_buffer(n + 1)
            server._user32.GetWindowTextW(hwnd, buf, n + 1)
            found.append((int(hwnd), buf.value))
        return True

    server._user32.EnumWindows(_cb, 0)
    return found


def shoot(name: str) -> str:
    wins = windows_of(pids())
    if not wins:
        raise SystemExit("没找到 PCL 窗口")
    hwnd = wins[0][0]
    server._user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    server._user32.SetForegroundWindow(hwnd)
    time.sleep(1.2)
    png, w, h = server._grab_window(hwnd)
    path = os.path.join(OUT, name)
    with open(path, "wb") as f:
        f.write(png)
    return f"{path} ({w}x{h})"


if __name__ == "__main__":
    print("pids:", pids())
    print("windows:", windows_of(pids()))
    print("saved:", shoot(sys.argv[1] if len(sys.argv) > 1 else "shot.png"))
