"""实测 Lunar 在 3072MB 下启动：确认日志里「内存：3072 MB」且游戏无 OOM。

步骤：启动 Release 构建 -> 截图主界面 -> （后续手动点 Lunar 入口与启动按钮）。
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
os.environ.setdefault("PCL_FLAVOR", "release")
import server  # noqa: E402

OUT = r"E:/DESKTOP/project/Pcl-lite/PCL-CE/.mcp-out"
os.makedirs(OUT, exist_ok=True)

print("kill:", server.pcl_app_kill())
time.sleep(1.5)
print("launch:", server.pcl_app_launch(flavor="release", wait_seconds=25))

res = server.pcl_app_screenshot(save_as="lunar-3072-01-main.png")
print("shot main:", res)
print("windows:", server.pcl_app_windows())
