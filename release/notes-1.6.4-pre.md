<summary>1.6.4 的预览版：更新清单不再写死仓库名——源码仓库里的 update.json 统一写成 self，别人复刻这个仓库再也不用每次手动忽略它。顺带补了贡献指南，修好了 README 里那两张挂掉的图。稳定版通道不受影响，仍是 1.6.2-hotfix-2。</summary>

PCL Lite 1.6.4 预览版（pre）。

> **这是预览版，不是正式版。** 把更新通道选成「预览体验」就能收到；
> 「快照尝鲜」「始终最新」也会收到，「稳定版」不会。

> 1.6.3 的全部内容都在这一版里（Lunar IPC、好友私聊、聊天界面重做、版本号兜底）。
> 已经在 `1.6.3-pre` 的话，这一版是它的增量。

## 一、`update.json` 不再写死仓库名

以前仓库根目录的 `update.json` 里写的是 `askbbtsk/pcl-lite/releases/latest`。
看起来像个"改一下就能指向自己仓库"的开关，其实不是——客户端读的是**锚点仓库**
（`PCL-Lite-service`）里那份，改自己仓库这份对构建出来的 exe 没有任何影响。

但它的副作用是真的：**每个复刻仓库里的内容都不一样**，每次从上游同步都会在这份 json 上
撞一次冲突，只能 `git update-index --skip-worktree` 或者手动丢弃。

现在指针支持 `self`：

```json
{
  "update": "self"
}
```

`self` 会展开成客户端自己编译进去的 `GitHubOwner` / `GitHubRepo`，也就是
`owner/repo/releases/latest`。于是：

- **所有仓库里的 `update.json` 内容逐字节相同**，合并永远不会冲突
- 复刻的人什么都不用改，原样保留即可
- 想发自己版本的人只改 `PCL.Core/App/ProjectLinks.cs` 里的 `GitHubOwner` / `GitHubRepo`
  两个常量，`update.json` 一个字都不用动
- 锚点仓库里那份**仍然写死具体指针**——那层间接是换发布仓库时唯一的救命绳，不能丢

客户端侧只动了 `UpdateManifestPointer`（`Extract` / `Parse` / `LooksLikePointer` 三处都认 `self`），
多源清单里的 `"repo": "self"` 走同一条路。补了 5 个单元测试。

**对现网没有影响**：锚点清单里 `askbbtsk/pcl-lite` 是 `official` 档、免身份校验，
所以源码仓库这份 `update.json` 本来就不参与更新链路。

## 二、新增贡献指南

`docs/CONTRIBUTE.md`，从 fork 到 PR 全流程：环境、构建、Debug/Release 数据目录分离、
单例锁、`update.json` 到底该怎么理解、版本号规则、代码地图、XAML 事件签名对照表、
深色主题注意事项、四语言文案、测试里哪些红是环境性的、PR 提交前清单、常见坑。

根目录的 `CONTRIBUTING.md` 从一句话改成指路。

## 三、README 致谢的两张图修好了

那两张图当初是传到**私有仓**里的附件，仓库转公开之后匿名访问全是 404，
README 里只剩 alt 文字。现在图片搬进了仓库（`docs/assets/thanks/`），
三份 README 都改成相对路径，以后换仓库、换账号都不会再掉。

## 四、许可证补充

`LICENSE` 第 4 条补充了 **Moonsworth**（Lunar Client 的运营方）——本项目接了 Lunar 的
本地 IPC 与社交协议，需要明确与它也没有隶属关系。

## 校验值

```
SHA-256  69FA6AFC125E685EE83888B9AA1CBA401D1AAE02877B0FF2CCC111F36FB1CD0F
```

## 已知问题

- 好友/私聊依赖 Lunar 后端的私有协议，官方随时可能改字段；改了要重新逆向
- 头像来自 `skins.mcstats.com`，如果哪天这个域名挂了，会退回默认图标
- 服务端下限会随官方启动器更新继续往上抬；PCL 被拒时会自动升版本重连，能自愈
- 结束游戏是直接杀进程，不保证存档落盘（与官方行为一致）
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 游戏只认 `~/.lunarclient/settings/game/accounts.json` 里的 `accessToken`，PCL 续不了
  Lunar 自己的续期；可行路径是用 PCL 登录的微软账号刷新后写回该文件

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
