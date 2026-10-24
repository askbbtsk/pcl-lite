"""用调试 MCP 的底层函数启动启动器并截图，供人工核对关于页改动。"""
import os
import sys

os.environ.setdefault("PCL_FLAVOR", "release")
sys.path.insert(0, r"E:/DESKTOP/project/Pcl-lite/PCL-CE/tools/PCL.DebugMcp")

import server  # noqa: E402

print("kill:", server.pcl_app_kill())
print("launch:", server.pcl_app_launch(flavor="release", wait_seconds=25))
res = server.pcl_app_screenshot(save_as="about-00-main.png")
print("shot:", res[1] if isinstance(res, list) else res)
