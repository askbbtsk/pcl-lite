# Issue 提交与分类指南

PCL Lite 的反馈统一提交到 [本仓库的 Issue 入口](https://github.com/askbbtsk/pcl-lite/issues/new/choose)。不需要去上游仓库，也不需要先安装其他启动器。

## 选择分类

| 入口 | 适用场景 | 自动标签 | 查看未关闭的问题 |
| --- | --- | --- | --- |
| Bug 反馈 | 启动器报错、界面错位、功能异常 | `bug` | [Bugs](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Abug) |
| 游戏崩溃或无法启动 | Java、基岩版、Lunar 启动失败或异常退出 | `bug`、`crash` | [崩溃](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Acrash) |
| 性能与体验优化 | 卡顿、索引慢、占用高、下载速度 | `enhancement`、`performance` | [性能](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Aperformance) |
| 功能建议 | 新功能或现有功能扩展 | `enhancement` | [建议与优化](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Aenhancement) |
| 使用提问 | 不会操作或不确定是否是 Bug | `question` | [提问](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Aquestion) |
| 文档与翻译问题 | README、文档、插件 API 文档或翻译错误 | `documentation` | [文档](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Adocumentation) |

所有新模板还会带上 `needs-triage`，表示尚未完成分类核对，不表示问题已确认。

## 提交时写什么

1. [搜索已有 Issue](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue)。同一问题已有记录时，在原记录补充版本、步骤和附件。
2. 一个 Issue 只记录一个问题，标题说明具体操作与异常，例如「开启实时桌面折射后说明文字变黑」。
3. 写出完整启动器版本、x64 / ARM64、Windows 版本；图形问题补充显卡、缩放、多屏和玻璃设置。游戏问题补充游戏与加载器版本、Java（如适用）和模组信息。
4. 对比实际结果和预期结果，附操作步骤。偶发、只遇到一次或无法稳定复现也可反馈，如实注明即可。
5. 功能建议说明使用场景和希望发生的行为；提交建议不代表已经排期或承诺实现。

## 日志和截图

- 问题发生后，在启动器「设置 → 查看日志」导出日志。已经关闭或崩溃后重启的，请导出全部日志，保留发生问题时的记录。
- 游戏崩溃可补充 `latest.log` 和崩溃报告。无法取得日志时写明原因，不要求编造附件或为了排查删除模组、实例与存档。
- 可将截图、录屏和导出的日志拖入 Issue 文本框。日志不能代替问题描述。
- 上传前检查附件，遮盖账号令牌、密码、邮箱、服务器地址等敏感信息。公开 Issue 的正文和附件会被其他人看到。

## 维护者怎么整理

- 日常先看 [待分类](https://github.com/askbbtsk/pcl-lite/issues?q=is%3Aissue%20is%3Aopen%20label%3Aneeds-triage)，确认分类正确后移除 `needs-triage`。这一步手动处理，不会自动宣布已复现。
- 用 `bug`、`crash`、`performance`、`enhancement`、`question`、`documentation` 作为分类。它们是标签筛选入口，不会把 Issue 搬到其他仓库。
- 需要补材料时添加 `question` 并留言说明具体缺什么；重复反馈标记 `duplicate` 并链接原记录，保留原有讨论。
- 确认修复后关联对应 PR / 提交，注明首次包含修复的版本，再关闭 Issue。暂时没有复现不要写成已经修复。
- 现有 Issue 不会被本次模板改动自动重分类；可选中需要整理的记录后批量添加标签。

无需提交方手动添加标签，选择模板提交后由 GitHub 自动附加分类。已有标签沿用，不创建 `bugs` 和 `bug` 两套同义标签。
