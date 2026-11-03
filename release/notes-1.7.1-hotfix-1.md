<summary>修掉一个把自己锁死的门禁：没装过官方 Lunar Client 的机器，点「启动」只会弹「请先安装 Lunar Client 并至少启动一次」，而物料本来就该由 PCL 自己下 —— 结果永远启动不了，也永远触发不了下载。另外进 Lunar 板块时就开始预热令牌，聊天不用再等。</summary>

PCL Lite 1.7.1 热修复 1。

## ① 「请先安装 Lunar Client」是个死循环

点启动时有一道从早期版本留下来的门禁：

```csharp
if (!ModLunar.IsInstalled) {
    MyMsgBox("没有找到 <物料根>。请先安装 Lunar Client 并至少启动一次。");
    return;
}
```

`IsInstalled` 判断的是「物料根下有没有 `offline/multiver` 或 `shared/assets`」。这两个目录以前要靠官方 Lunar Client 装出来，但物料下载早就改成 PCL 自己做了（`LunarInstaller.EnsureInstalledAsync`），判断却没跟着改，于是成了闭环：

- 没装过官方 Lunar Client → 物料目录不存在
- 点启动 → 被门禁拦住
- 启动不了 → 永远不会触发下载 → 目录永远不存在

页面上的文案还写着「不用装 Lunar 启动器，点启动会自己下（首次约 300 MB）」，和弹窗正好相反。

现在这道门禁整段删掉，点启动直接进 `ModLunar.Launch()`，由安装编排从零把物料下齐。顺带清掉了四份语言文件里已经没人引用的 `Lunar.Msg.NotInstalled` / `Lunar.Msg.NotInstalled.Title`。

## ② 进 Lunar 页面就开始刷新令牌

聊天（Lunar 后端）和启动用的是同一个 accessToken。以前只在「点启动」和「点连接」时才顺手刷一次，先看到的就是好友列表空着、私聊发不出去；连上之后令牌到期，消息也会断。

现在打开 Lunar 板块就在后台预热一次：

| 行为 | 说明 |
| --- | --- |
| 触发时机 | 页面 `Loaded`，不阻塞界面 |
| 判定顺序 | Lunar 身份账号 → Lunar 账户库，和启动时一致 |
| 刷新阈值 | 离到期 **不到 10 分钟**就刷，不等真过期（聊天是长连接） |
| 并发 | 单飞，同时只会有一个刷新在跑 |
| 节流 | 60 秒内不重复尝试，来回切页面不会空转微软的接口 |
| 失败 | 只写日志不弹窗，真正要用时会再试并给出明确提示 |

## 校验

`SHA-256`（签名后）：`50A84E607B253B811D9C1129B6051445C6D17A4F9444A7B07913BAA72997F86B`

大小：19,395,368 字节

签名：`Valid`（PCL Lite，DigiCert 时间戳）

## 已知问题

- 1.7.1 那条「Lunar 物料下载异常可以先退回 1.7.0」的公告仍然有效，这一版**没有改下载链路**
- 令牌预热只覆盖「打开页面」这一下；如果挂着聊天超过一小时，还是要在游戏里或重进页面才会续上
- 用 Minecraft 令牌导入的 Lunar 身份账号没有 refreshToken，预热带不动它，到期只能重新导入
