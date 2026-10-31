<summary>1.6.4-hotfix-5：删掉 Lunar 页的「回到游戏」按钮 —— 游戏端收到 FocusGamePush 只做一次 AWT toFront()，被 Windows 前台锁定挡掉，点了窗口纹丝不动。</summary>

PCL Lite 1.6.4-hotfix-5。

> 热修复，稳定版通道会收到。

## 现象

Lunar 游戏跑起来之后，启动页会多出一颗「回到游戏」按钮。点它没反应 —— 游戏窗口不前置，
也没有任何提示（以前还会报「已把游戏窗口切到前台」，但窗口照样不动，报成功反而更迷惑）。

## 根因：外部进程没有前台权限

官方的「回到游戏」是这么做的（`dist-electron/electron/main.js`）：

```js
ipcMain.handle('misc:focusGame', () => gameipc.sendPush(FocusGamePush.create({})))
```

游戏端收到后调一次 AWT 的 `toFront()`。官方能work，是因为它和游戏在同一个 Electron
主进程体系里；PCL 是**外部进程**，Windows 的前台锁定（foreground lock）会把后台进程的
`toFront()` 直接吞掉，窗口不动。

三条路都试过了，都不行：

| 做法 | 结果 |
| --- | --- |
| PCL 自己 `EnumWindows` 找窗口 + `SetForegroundWindow` | 匹配不上就报「未找到游戏窗口」，纯误报 |
| 退回官方写法，只发 `FocusGamePush` | 就是现在这个坏按钮，发了等于没发 |
| 发推送 + 等存档落盘 | 游戏不理推送时界面卡在「正在保存存档」 |

**结论：这个功能在外部进程里做不出来。** 留一颗点了没反应的按钮，比没有这颗按钮更糟。

## 改了什么

| 位置 | 改动 |
| --- | --- |
| `PageLunarLaunch.xaml` | 移除 `BtnFocusGame` 按钮 |
| `PageLunarLaunch.xaml.cs` | 删掉 `BtnFocusGame_Click`，`RefreshButtons()` 里不再控制它的可见性 |
| `ModLunar` | 删掉 `FocusGameAsync()`（52 行） |
| `LunarIpcPush.FocusGame` | 定义**保留**，只作 wire 格式记录（`docs/LUNAR.md` 协议表还引用它），注释里写明没有任何调用点、别再加回去 |
| 四语言文案 | 同步删掉 `Lunar.Action.FocusGame`、`Lunar.Hint.Focused`、`Lunar.Hint.FocusFailed`，四份键数仍等长 |
| `docs/LUNAR.md` | 协议表 `location.v1.FocusGamePush` 由 ✅ 改 ❌，把「退回官方写法仍然无效」补进踩坑列表 |

启动、停止、账号、好友这些都不受影响，纯粹少一颗按钮。

## 校验值

```
SHA-256  D429C7BF75B31C771865ABE9E6CE475882B6ADA4C9A06EC42C9685230E3E4EAE
```

## 已知问题

同 1.6.4，未新增。

## 许可证

本项目以 MIT 许可证发布，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
