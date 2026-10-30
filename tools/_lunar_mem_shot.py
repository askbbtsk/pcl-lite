"""打开 设置 → LunarClient，截图确认新的「自定义内存」区块。

注意：MCP 里 debug 产物默认指向 bin/Debug/net10.0-windows（AnyCPU，陈旧），
带 -p:Platform=x64 编出来的实际在 win-x64 子目录，这里手动改掉再启动。
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
os.environ.setdefault("PCL_FLAVOR", "debug")
import server  # noqa: E402

server.FLAVORS["debug"]["bin"] = "PCL-CE/Plain Craft Launcher 2/bin/Debug/net10.0-windows/win-x64"

OUT = r"E:/DESKTOP/project/Pcl-lite/PCL-CE/.mcp-out"
os.makedirs(OUT, exist_ok=True)

steps = sys.argv[1:]

print("kill:", server.pcl_app_kill())
time.sleep(1.2)

if not steps or steps[0] != "--no-launch":
    print("launch:", server.pcl_app_launch(flavor="debug", wait_seconds=25))
    res = server.pcl_app_screenshot(save_as="lunar-mem-01-main.png")
    print("shot:", res)
else:
    steps = steps[1:]

for raw in steps:
    kind, _, value = raw.partition(":")
    if kind == "click":
        x, y = (int(v) for v in value.split(","))
        print("click", x, y, server.pcl_ui_click(x=x, y=y, settle_seconds=1.0))
    elif kind == "shot":
        print("shot:", server.pcl_app_screenshot(save_as=value))
    elif kind == "scroll":
        x, y, n = (int(v) for v in value.split(","))
        print("scroll", server.pcl_ui_scroll(x=x, y=y, notches=n))
    elif kind == "wait":
        time.sleep(float(value))
