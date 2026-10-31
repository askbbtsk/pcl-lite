# 贡献

欢迎提 PR 和 Issue。

**动手之前先看 [docs/CONTRIBUTE.md](docs/CONTRIBUTE.md)** —— 从 fork、构建、跑起来，到代码规矩和 PR 清单都在里面。

三个最容易踩的点，先记住：

1. **不要改 `update.json`**。客户端读的是锚点仓库那份，你改自己仓库这份对你构建出来的 exe 没有任何影响。
   想让自己构建查自己的 Release，用 `update.local.json`（已 gitignore），见 CONTRIBUTE.md 第 4 节。
2. **不要改 `metadata.json` 的版本号**。合并后统一发版，你改了后面每次合并都会冲突。
3. **加文案要四种语言一起加**（zh-CN / zh-TW / en-US / en-GB），缺了会悄悄回退成中文。
