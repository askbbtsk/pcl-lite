"""验证「Lunar 默认版本」与「更新检查」两处修复。

启动 Release 构建（metadata 里 suffix 留空 = 1.6.1），进 Lunar 板块截图看版本下拉框
默认显示什么，再翻日志看自动检查更新的结论。

用法：python tools/_lunar_default_test.py [--keep]
"""
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
os.environ.setdefault("PCL_FLAVOR", "release")
import server  # noqa: E402

LOG_DIR = (r"E:\DESKTOP\project\Pcl-lite\PCL-CE\Plain Craft Launcher 2"
           r"\bin\Release\net10.0-windows\win-x64\PCL\Log")

print("kill:", server.pcl_app_kill())
time.sleep(1)
print("launch:", server.pcl_app_launch(flavor="release", wait_seconds=22))

res = server.pcl_app_screenshot(save_as="lunar-default-01-main.png")
print("shot:", res)

# 日志里的更新结论
log = server._latest_log(LOG_DIR)
print("日志:", log)
if log:
    for e in server._parse_log(log):
        msg = e.get("message") or ""
        if any(k in msg for k in ("Update", "更新", "版本")):
            print(" ", e.get("time"), "|", e.get("level"), "|", msg[:160])

if "--keep" not in sys.argv:
    pass
