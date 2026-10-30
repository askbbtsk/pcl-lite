"""启动一次，等自动检查更新跑完，把日志里的更新相关行打出来。

用途：验证「1.6.1 能不能看到 1.6.1-hotfix-1」。构建时把 metadata 的 suffix 留空即可。

用法：python tools/_update_check_test.py [等待秒数]
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

EXE = (r"E:\DESKTOP\project\Pcl-lite\PCL-CE\Plain Craft Launcher 2"
       r"\bin\Release\net10.0-windows\win-x64\Plain Craft Launcher 2.exe")
LOG_DIR = (r"E:\DESKTOP\project\Pcl-lite\PCL-CE\Plain Craft Launcher 2"
           r"\bin\Release\net10.0-windows\win-x64\PCL\Log")

wait = int(sys.argv[1]) if len(sys.argv) > 1 else 14

before = set(os.listdir(LOG_DIR))
proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x00000008)
time.sleep(wait)

log = server._latest_log(LOG_DIR)
print("日志:", log)
if log:
    entries = server._parse_log(log)
    hits = [e for e in entries
            if any(k in (e.get("message") or "") for k in ("Update", "更新", "1.6.1"))]
    for e in hits[-25:]:
        print(" ", e.get("time"), "|", e.get("level"), "|", e.get("message"))

subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
