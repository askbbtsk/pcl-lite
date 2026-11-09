# 整合包制作与附带启动器

在实例设置的导出页面选择内容，设置整合包名称及版本，然后导出 ZIP 或 MRPACK。

## 附带启动器

- 不附带：默认，直接生成整合包。
- 完整 PCL Lite：复制当前运行的已发布 Lite 程序，根目录为 `PCL Lite.exe`。
- 精简 PCL Light：导出时查询 `bedkillerspacex-boop/Pcl-Light` 最新正式 Release，固定该次发布的资产和摘要，再下载所选 x64 / ARM64 程序。默认 x64。根目录为 `PCL Light.exe`。

Light 不是 Lite 的内嵌资源，不随 Lite 程序分发。附带任意启动器时，外层 ZIP 包含一个启动器和 `modpack.mrpack`。Light 的 SHA-256 必须匹配 Release digest，或同一 Release 的 SHA256SUMS.txt；无法取得唯一摘要时导出失败。网络访问仍遵循启动器的联网授权。

EXE 与 SHA256SUMS.txt 均支持 GitHub 直链优先、现有更新 PROXY 列表依次回退，候选地址去重。校验清单每个来源设有超时，避免直链失败后一直阻塞代理；GitHub Release 元信息仍从官方 API 查询。

## Java 与隔离

“打包实例目录内的 Java”默认关闭。页面查找实例内的 `bin/java.exe` 和 `lib` 目录，使用宿主 PE 解析器读取版本和架构，并优先选择该实例指定的 Java。不复制实例外的 Java，也不下载 Java。Light 的目标架构必须与所附 Java 一致。

Lite 包或无启动器包将 Java 放入 `overrides/runtime/java`，通过新的 `instance-relative` Java 偏好解析；迁移目录不改变语义。旧 `relative` 偏好继续相对于启动器目录解析。附带 Light 时运行时放在外层 `PCL/runtime/java`，沿用 Light 支持的 `relative` 偏好，不要求旧版 Light 支持新的偏好类型。

“安装后的版本隔离”默认开启。选择关闭时，Lite 导入器将包声明的游戏内容移到共享游戏目录；Java 和实例配置仍保留在实例内。共享目录有同名且内容不同的文件时停止处理并保留源文件，不覆盖玩家内容。此选项不修改作者当前实例或全局隔离默认值。

当前 PCL Light 发布版导入器会强制开启隔离，所以附带 Light 与关闭隔离的组合会明确提示并停止导出。需要关闭隔离时可附带完整 Lite，或选择不附带启动器后使用 Lite 导入；本轮不修改或重新发布外部 Light 仓库。

## 配置兼容

导出配置保留原有内容规则和追加文件段。旧 `IncludeLauncher:True` 等同于完整 Lite。新增 `LauncherType:None/Lite/Light` 和 `LightArchitecture:x64/ARM64`；新字段优先。

Java 支持 `IncludeVersionJava`，并兼容 LF 的 `IncludeJava`；隔离支持 `VersionArgumentIndie`，并兼容 LF 的 `VersionIsolation`。两者同存时优先 Lite 字段。`LivingModpackMode` 等精简模式字段不会应用于完整 Lite，导入时提示。

Modrinth 上传模式禁止附带启动器和 Java，以及直接捆绑外部资源。保留 Lite 的已有文件勾选和规则，不改变原有 ZIP/MRPACK 索引格式。

## 文件保护与验收

不导出备份、内部回收记录、来源记录、事务暂存、账号缓存或 Setup.ini 原始内容。实例 Setup.ini 由导出任务只写入隔离和 Java 选择，不携带作者机器的绝对路径和登录信息。个性化内容可包含背景图片、音乐、Custom.xaml、hints.txt 与 Logo.png，不复制全局 Setup.ini。

最终包在目标目录先写入临时文件，成功后替换目标；取消、下载失败或哈希失败不覆盖现有导出文件。失败的任务暂存目录由已有任务目录清理机制处理，不作为可交付包。

自动化测试覆盖配置兼容、资产匹配、摘要解析、路径过滤、相对 Java、共享隔离冲突以及内外层 ZIP 文件内容。真实 Windows 上的 Light 首次运行和下载授权交互仍需人工验收；首次运行应识别同目录 `modpack.mrpack`。不要求 ZIP 时间戳或启动器品牌与 LF 一致。
