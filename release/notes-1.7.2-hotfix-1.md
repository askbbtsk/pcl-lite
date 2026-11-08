# PCL Lite 1.7.2 hotfix 1

修一个「关掉『全部允许』之后就什么都下不动」的问题：网络授权白名单里登记的域名，
和代码真正会去请求的域名对不上。

## 网络授权白名单

- 更新检查原先登记的是三个根本不会被请求的域名，真正在用的
  `raw.githubusercontent.com`、`cdn.jsdelivr.net`、`api.github.com` 全都没登记。
  现在按实际链路登记，反代清单仍由 `DownloadProxies` 单一持有，启动时派生注册进白名单。
- 补上漏登的下载域名：`zkitefly.github.io`、`mirrors.cloud.tencent.com`；
  加载器相关的 `maven.minecraftforge.net`、`jenkins.liteloader.com`、`releases.labymod.net`、`api.github.com`。
- 删掉两个幽灵域名（`s3.pysio.online`、`staticassets.naids.com`）和那个从未被请求过的
  `update.pcl-lite.dev` 常量——它们只会让用户为不存在的连接授权。
- 新增 11 个用例守住这张表：内置域名必须能反查回自己的用途、不能出现幽灵域名、
  动态登记必须幂等、空输入必须被忽略。

## 其它

- 清理 `tools/` 下 11 个一次性调试脚本（截图、Lunar 内存、更新检查那批），
  需要时从 git 历史里取。
- 文档对齐现状：目录树改成本仓库真实的 `pcl-lite/` 结构并补上插件相关目录；
  版本号说明改成「只改 `metadata.json` 一处，csproj 用宏推导」；
  发版流程改走 GitHub Actions 的 `release-publish.yml`，发布说明优先用
  `release/notes-<版本>.md`。

验证结果：Debug 构建 0 警告、0 错误；647 个 .NET 测试通过；
`tools/verify-update-chain.py` 确认 v1.7.2 仍可被未登录用户发现并下载，SHA-256 与 PE 头正常。
