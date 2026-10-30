"""一次性完成：启动 → 还原窗口 → 截图 → 点设置 → 截图。

窗口最小化时 `_click_at` 会把坐标算成 -32000 附近，点不到东西，
所以每次操作前先 SW_RESTORE 一次。
"""
import os
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
sys.path.insert(0, r"E:/DESKTOP/project/Pcl-lite/PCL-CE/tools")
import server  # noqa: E402
from _pcl_shot_by_pid import pids, shoot, windows_of  # noqa: E402

# MCP 里 debug 产物指向 bin/Debug/net10.0-windows（AnyCPU，陈旧），
# 带 -p:Platform=x64 编出来的实际在 win-x64 子目录
server.FLAVORS["debug"]["bin"] = "PCL-CE/Plain Craft Launcher 2/bin/Debug/net10.0-windows/win-x64"


def restore() -> int:
    """还原主窗口，返回 hwnd。"""
    wins = [w for w in windows_of(pids()) if w[1].strip().lower() == "pcl lite"]
    if not wins:
        raise SystemExit(f"没找到 PCL 主窗口，当前：{windows_of(pids())}")
    hwnd = wins[0][0]
    server._user32.ShowWindow(hwnd, 9)  # SW_RESTORE
    server._user32.SetForegroundWindow(hwnd)
    time.sleep(1.0)
    return hwnd


if __name__ == "__main__":
    server.pcl_app_kill()
    time.sleep(1.2)
    info = server.pcl_app_launch(flavor="debug", wait_seconds=22)
    print("launch:", info.get("pid"), info.get("exe"))
    time.sleep(2)

    restore()
    print("shot main:", shoot("lunar-mem-10-main.png"))

    for label, x, y, name in [
        ("设置", 722, 40, "lunar-mem-11-setup.png"),
        ("LunarClient", 98, 232, "lunar-mem-12-lunar.png"),
    ]:
        restore()
        server.pcl_ui_click(x=x, y=y, settle_seconds=1.5)
        time.sleep(0.8)
        restore()
        print(f"shot {label}:", shoot(name))
