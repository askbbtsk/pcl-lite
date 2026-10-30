"""按指定窗口尺寸启动 → 截主页 → 进设置 → 截左侧列表 → 点「界面」→ 截界面页。

沙箱会在 bash 调用结束时回收子进程，所以整条链路必须在一次调用里走完。
用法：python tools/_classic_shot.py [宽] [高] [tag]
"""
import os
import subprocess
import sys
import time
import types

# server.py 顶部 import 了 mcp SDK，但截图只用得到它的 Win32 那几支。
# 本机 python 没装 mcp，塞几个空壳进去，别为了截图去拉一整个 SDK。
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

w = int(sys.argv[1]) if len(sys.argv) > 1 else 850
h = int(sys.argv[2]) if len(sys.argv) > 2 else 500
tag = sys.argv[3] if len(sys.argv) > 3 else "classic"
ui_y = int(sys.argv[4]) if len(sys.argv) > 4 else 330

out_dir = server._ensure_out()


def find_pcl_window():
    """只认标题正好是 "PCL Lite " 的那个窗口。

    server._find_main_window() 是「标题包含 PCL Lite，取面积最大」，浏览器标签页
    标题里也带这个词，结果抓到的是 GitHub 页面（1920×1029）。必须精确匹配。
    """
    for w in server._enum_windows("PCL Lite"):
        if w["title"].strip().lower() == "pcl lite":
            return w
    return None


def shot(hwnd, name):
    png, gw, gh = server._grab_window(hwnd)
    path = out_dir / f"{name}.png"
    path.write_bytes(png)
    print(f"saved {path}  {gw}x{gh}")


proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x00000008)

win = None
for _ in range(50):
    time.sleep(1)
    win = find_pcl_window()
    if win:
        break
if not win:
    print("窗口没出来，进程状态：", proc.poll())
    print("候选：", server._enum_windows("PCL Lite"))
    sys.exit(1)
print("window:", win["width"], "x", win["height"], "title", repr(win["title"]))

time.sleep(4)
shot(win["hwnd"], f"{tag}-1-home")

# 顶部导航「设置」
server._click_at(win["hwnd"], 595, 38)
time.sleep(1.5)
server._click_at(win["hwnd"], 595, 38)
time.sleep(1.5)
shot(win["hwnd"], f"{tag}-2-settings")

# 设置页左侧「界面」
server._click_at(win["hwnd"], 65, ui_y)
time.sleep(1.8)
shot(win["hwnd"], f"{tag}-3-ui")

subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
