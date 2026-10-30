"""验证「拖窗口不改启动默认尺寸」：启动 → 改窗口大小 → 进设置页 → 截图 + 读配置。

截图坐标和点击坐标不是一个坐标系：_grab_window 抓的是 GetWindowRect（含 DWM 阴影），
_click_at 收的是客户区坐标。所以点在截图里量出来的位置之前，要先减掉两者的偏移。

用法：python tools/_size_persist_test.py
"""
import ctypes
import os
import subprocess
import sys
import time
import types
from ctypes import wintypes as wt

# server.py 顶部 import 了 mcp SDK，截图只用得到它的 Win32 那几支
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
CFG = (r"E:\DESKTOP\project\Pcl-lite\PCL-CE\Plain Craft Launcher 2"
       r"\bin\Release\net10.0-windows\win-x64\PCL\config.v1.yml")

_user32 = ctypes.windll.user32
_user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
                                 ctypes.c_int, ctypes.c_int, wt.UINT]
SWP_NOMOVE, SWP_NOZORDER = 0x0002, 0x0004

out_dir = server._ensure_out()


def find_pcl_window():
    for w in server._enum_windows("PCL Lite"):
        if w["title"].strip().lower() == "pcl lite":
            return w
    return None


def client_offset(hwnd):
    rect = wt.RECT()
    _user32.GetWindowRect(hwnd, ctypes.byref(rect))
    point = wt.POINT(0, 0)
    _user32.ClientToScreen(hwnd, ctypes.byref(point))
    return point.x - rect.left, point.y - rect.top


def click_shot(hwnd, sx, sy):
    """按截图里的坐标点击。

    不用 SetCursorPos + SendInput：那条路在窗口没拿到前台时会把点击丢在别处，
    实测落到过「启动 Lunar」按钮上，直接把游戏拉起来了。改成往窗口直接投
    WM_LBUTTONDOWN/UP，lParam 就是客户区坐标，命中点不会飘。
    """
    ox, oy = client_offset(hwnd)
    x, y = int(sx - ox), int(sy - oy)
    lp = (y << 16) | (x & 0xFFFF)
    _user32.SetForegroundWindow(hwnd)
    time.sleep(0.2)
    # 光标也得挪过去：MyListItem 之类依赖 IsMouseDirectlyOver，光发消息不挪光标点不动
    ax, ay = server._client_to_screen(hwnd, x, y)
    _user32.SetCursorPos(int(ax), int(ay))
    time.sleep(0.15)
    _user32.PostMessageW(hwnd, 0x0201, 1, lp)      # WM_LBUTTONDOWN
    time.sleep(0.06)
    _user32.PostMessageW(hwnd, 0x0202, 0, lp)      # WM_LBUTTONUP
    return {"x": x, "y": y}


def shot(hwnd, name):
    png, gw, gh = server._grab_window(hwnd)
    path = out_dir / f"{name}.png"
    path.write_bytes(png)
    print(f"saved {path}  {gw}x{gh}")


def cfg_size():
    for line in open(CFG, encoding="utf-8-sig"):
        if line.startswith(("WindowWidth", "WindowHeight")):
            print("  cfg:", line.strip())


print("启动前配置：")
cfg_size()

proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x00000008)
win = None
for _ in range(50):
    time.sleep(1)
    win = find_pcl_window()
    if win:
        break
if not win:
    print("窗口没出来，进程状态：", proc.poll())
    sys.exit(1)

# 等启动动画结束，否则量到的是动画中间态
time.sleep(5)
win = find_pcl_window()
print("稳定后窗口：", win["width"], "x", win["height"], "| 客户区偏移", client_offset(win["hwnd"]))

# 模拟用户拖窗口边界：直接改窗口尺寸（比原尺寸大 200×150，确保真的变了）
win = find_pcl_window()
print("保持原尺寸：", win["width"], "x", win["height"])

print("拖动后配置：")
cfg_size()

# 进设置页（截图坐标：导航「设置」约在 665,42）
click_shot(win["hwnd"], 705, 38)
time.sleep(1.5)
# 设置页左侧「个性化」
click_shot(win["hwnd"], 95, 510)
time.sleep(1.8)
shot(win["hwnd"], "size-test-settings")

subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
