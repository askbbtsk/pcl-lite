"""在设置页左侧列表里扫一遍纵坐标，找出「关于」项的命中位置。

沙箱会在 bash 调用结束时回收子进程，所以启动、点击、截图必须在同一次调用内完成。
"""
import os
import subprocess
import sys
import time

sys.path.insert(0, r"E:/DESKTOP/project/Pcl-lite/PCL-CE/tools/PCL.DebugMcp")
os.environ.setdefault("PCL_FLAVOR", "release")
import server  # noqa: E402

EXE = (r"E:\DESKTOP\project\Pcl-lite\PCL-CE\Plain Craft Launcher 2"
       r"\bin\Release\net10.0-windows\win-x64\Plain Craft Launcher 2.exe")

proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x00000008)
win = None
for _ in range(50):
    time.sleep(1)
    win = server._find_main_window()
    if win:
        break
if not win:
    sys.exit("窗口没出来")
time.sleep(2.5)

hwnd = win["hwnd"]
server._click_at(hwnd, 595, 38)   # 顶部「设置」
time.sleep(1.6)
server._click_at(hwnd, 595, 38)
time.sleep(1.4)

out_dir = server._ensure_out()
for y in range(450, 512, 6):
    server._click_at(hwnd, 65, y)
    time.sleep(1.2)
    png, w, h = server._grab_window(hwnd)
    (out_dir / f"scan-{y}.png").write_bytes(png)
    print("scan", y, "->", out_dir / f"scan-{y}.png")

subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
