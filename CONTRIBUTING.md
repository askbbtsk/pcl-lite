# 贡献

欢迎提 PR 和 Issue。

**动手之前先看 [docs/CONTRIBUTE.md](docs/CONTRIBUTE.md)** —— 从 fork、构建、跑起来，到代码规矩和 PR 清单都在里面。

三个最容易踩的点，先记住：

1. **`update.json` 不用改**。它的内容是 `{"update": "self"}`，所有仓库里都一样，合并不会冲突。
   客户端也不读你自己仓库这份（它读锚点仓库），改了对你构建出来的 exe 没有任何影响。
   想让自己发的版本被认出来，改 `PCL.Core/App/ProjectLinks.cs` 里的 `GitHubOwner` / `GitHubRepo` 两个常量。
2. **不要改 `metadata.json` 的版本号**。合并后统一发版，你改了后面每次合并都会冲突。
3. **加文案要四种语言一起加**（zh-CN / zh-TW / en-US / en-GB），缺了会悄悄回退成中文。
