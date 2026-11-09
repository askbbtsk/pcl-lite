# PCL Lite 1.7.5-snapshot-1

本次为整合包制作功能的预览版本，包含 1.7.5-nightly-2 的修复。

## 新增与改进

- 导出页提供“不附带启动器 / 附带完整 PCL Lite / 附带精简 PCL Light”三种互斥选项。
- 选择 Light 时，在导出任务中从 `bedkillerspacex-boop/Pcl-Light` 最新正式 Release 下载 x64 或 ARM64 程序，默认 x64；Light 不内嵌到 Lite 程序中。
- Light EXE 和 SHA256SUMS.txt 支持 GitHub 直链优先、现有 PROXY 列表依次回退；下载后必须通过 SHA-256 校验。
- 支持打包实例目录内的 Java，展示版本和架构，不下载或复制实例外的 Java。Lite 使用实例相对路径，Light 包使用其支持的启动器相对路径。
- 支持设置安装后的版本隔离，默认开启；Lite 导入关闭隔离的包时，将游戏内容放入共享目录，遇到不同内容的同名文件时停止并保留源文件。
- 兼容旧 IncludeLauncher 配置，以及 LF 的 IncludeJava、VersionIsolation 等制作配置字段；精简模式字段提示忽略，不改变完整 Lite 界面。
- 导出时排除内部备份、回收记录、来源记录、事务暂存和账号缓存，避免分发作者的原始 Setup.ini。
- 最终压缩包先写临时文件，完成后替换目标；取消或失败不覆盖已有导出包。
- 恢复原来的 PCL Lite 欢迎页，性能状态改为独立主页预设，补齐三语选项名称。

## 已知边界

- 当前 PCL Light 发布版导入时强制开启隔离；附带 Light 与关闭隔离的组合会明确提示并停止导出。需要关闭隔离时，请使用完整 Lite 或不附带启动器。
- 自动测试覆盖配置兼容、资产和摘要解析、文件排除、Java 路径、共享隔离及内外层 ZIP；Light 首次运行的完整交互、真实 LF/Lite 整合包双跑仍需人工验收。

功能提交已通过 714 项单测及 x64 / ARM64 CI 构建。本次发布通过 GitHub Actions 注入 OAuth client ID，随包提供 Sigstore 签名文件。
