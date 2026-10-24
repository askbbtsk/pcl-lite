"""启动新构建 → 进设置 → 点关于 → 截图，全程在一次进程生命周期内完成。

沙箱会在 bash 调用结束时回收子进程，所以不能分多次调用操作同一个实例。
用法：python tools/_about_shot.py <关于项纵坐标>
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

about_y = int(sys.argv[1]) if len(sys.argv) > 1 else 462
tag = sys.argv[2] if len(sys.argv) > 2 else "shot"

proc = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x00000008)

win = None
for _ in range(50):
    time.sleep(1)
    win = server._find_main_window()
    if win:
        break
if not win:
    print("窗口没出来，进程状态：", proc.poll())
    sys.exit(1)
print("window:", win["width"], "x", win["height"], "after launch")

time.sleep(2.5)

# 顶部导航「设置」
server._click_at(win["hwnd"], 595, 38)
time.sleep(1.5)
server._click_at(win["hwnd"], 595, 38)
time.sleep(1.2)

# 设置页左侧「关于」
server._click_at(win["hwnd"], 65, about_y)
time.sleep(1.5)

png, w, h = server._grab_window(win["hwnd"])
out = server._ensure_out() / f"{tag}.png"
out.write_bytes(png)
print("saved", out, w, h)

subprocess.run(["taskkill", "/F", "/PID", str(proc.pid)], capture_output=True)
