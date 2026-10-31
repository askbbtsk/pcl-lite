<summary>1.6.4-hotfix-3：修游戏内点「添加账号」后 Lunar 报错、启动器界面被撕成两半 —— AddAccount 的 IPC 应答写错了（回了失败还顺手把主界面从 Lunar 页切走），页面切换的左右两栏又是两组独立动画，停掉其中一组就只剩半张脸。</summary>

PCL Lite 1.6.4-hotfix-3。

> 热修复，稳定版通道会收到。

## 现象

在 Lunar 游戏里点「添加账号」，游戏右上角弹：

```
Unknown error occurred while adding account
```

同时启动器窗口变成这样：左栏还是 Lunar 的「状态 / 账号 / 启动游戏」，
右栏却已经是启动页的欢迎卡片，标题栏还亮着 Lunar —— 两半对不上，
点别的标签才会自己恢复。

日志里能直接看到游戏在 3 秒内连发了 5 条：

```
23:05:11.159  游戏请求添加账号：PCL 不能代替游戏登录，已把窗口切到账号页
23:05:12.908  游戏请求添加账号：……
23:05:13.408  游戏请求添加账号：……
23:05:13.809  游戏请求添加账号：……
23:05:14.025  游戏请求添加账号：……
```

## 两个原因叠在一起

**一、`AddAccount` 的 IPC 应答是错的。**

官方启动器收到这条请求时，会弹自己的登录窗口，登录完回 `success = true`，
**从不碰主界面**。PCL 以前是：

- 回空响应 → `success = false`，游戏只能报「未知错误」；
- 顺手 `PageChange(PageType.Launch)`，把界面从 Lunar 页硬切到启动页。

游戏真正认的身份来源是 `~/.lunarclient/settings/game/accounts.json`，
所以正确做法不是「把用户带到账号页」，而是**把 PCL 已经登录的账号写进那个文件**，
再回 `success = true` —— 游戏重新读一遍就有账号了，和官方那条路的结果一致。

**二、页面切换的左右两栏是两组独立动画。**

`PageChangeAnim` 里左右各起一组动画，各自在 110ms 后换自己那一半：

```
AniStart(左栏交换 …, "FrmMain PageChangeLeft")
AniStart(右栏交换 …, "FrmMain PageChangeRight")
```

只要其中一组被 `AniStop`（Lunar 左栏切分页时就会停 `FrmMain PageChangeRight`），
或者 `pageLeft` / `pageRight` 这两个**字段**在中途被改写，就只剩一半换过去 ——
用户看到的就是「左栏 Lunar、右栏启动页」。

## 改了什么

| 位置 | 改动 |
| --- | --- |
| `LunarIpcDispatcher.DispatchAuth` | `AddAccount` 不再回空响应，改为按宿主的实际结果编 `AddAccountResponse`（成功写 `success = true`，失败仍是全默认值，和官方一致） |
| `LunarIpcHost.RequestAddAccount` | 不再切页面；改为把 PCL 当前登录的账号交给游戏。游戏连发多条时 3 秒内只提示一次，不再弹一串一样的提示 |
| `ModLunar.TryProvideAccountToStore` | 新增：取 PCL 当前登录（令牌过期就先刷一次），复用账户库里同 UUID 的 `localId` 写回 `accounts.json` 并切成激活账号 |
| `LunarIpcHost.RequestLaunchGame` | 同样不再切页面，只提示 |
| `FormMain.PageChangeAnim` | 左右两栏合并成一组动画一次换完，并用局部变量捕获目标页，中途被改写也影响不到已经排好的交换 |
| `FormMain.PageChange` | 标题栏按钮改成按 `Tag` 找。Lunar 是后加的标签（`Tag=13`），插在第 4 位，索引和枚举值早就不一一对应，用 `Children[(int)page]` 会点亮错误的标签甚至越界 |
| `PageLunarLeft.PageChangeRun` | 主页面已经不是 Lunar 时直接忽略过期的分页切换，避免把右栏拽回 Lunar 页 |

## 校验值

```
SHA-256  95B1B358A510ED5F9DE96A5F63A4C177D891FB9CB30AF4A41CAF0AC6B62E63F5
```

## 已知问题

同 1.6.4，未新增。

## 许可证

本项目以 MIT 许可证发布，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
