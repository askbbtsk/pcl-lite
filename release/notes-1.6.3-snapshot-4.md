<summary>「回到游戏」和「结束游戏」改成和官方启动器完全一致的做法——只靠 IPC 推送，不再自己找窗口、不再等存档；聊天界面修好了输入框看不见字、滚动条压住气泡、消息没有日期三件事。</summary>

PCL Lite 1.6.3 快照版 4（snapshot-4）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「最新」也会收到，「稳定版」「预览体验」不会。
>
> 已经是 `1.6.3-snapshot-3` 的话，这次是它的增量：两个行为回退 + 三处界面修复。

## ① 「回到游戏」和「结束游戏」：删掉自造的写法，跟官方对齐

这一版把上两版自己加的逻辑全删了。原因是**它们比官方多出来的只有故障**。

官方启动器的做法（`dist-electron/electron/main.js`）：

```js
// 回到游戏：就一句，没有任何 Win32 动作
ipcMain.handle('misc:focusGame', () => {
  const ipc = modules.getModule('gameipc')
  ipc?.sendPush(FocusGamePush.create({}))
})

// 结束游戏：spawn 出来的子进程挂一个 abort 回调
signal.addEventListener('abort', () => {
  child.stdout.destroy(); child.stderr.destroy(); child.kill()
})
```

整个 `app.asar` 里 `ExitGamePush` 只出现在 protobuf 定义块，**一处 `sendPush` 调用都没有**。

### 「回到游戏」不再报「未找到游戏窗口」

以前 PCL 发完 `FocusGamePush` 还会自己 `EnumWindows` 找窗口、再 `SetForegroundWindow`
抬到前台。窗口匹配不上时就只能弹「未找到游戏窗口」，而这个报错**没有意义** ——
官方根本不需要知道窗口在哪，游戏自己收到推送就会处理。

现在 `FocusGameAsync()` 只做一件事：发 `FocusGamePush`，发出去就算成功。
只有「游戏还没连上 IPC」这一种情况会提示，文案也改成了
「游戏还没连上 PCL，前台切换请求送不出去」。

删掉的代码：`EnumWindows` 找窗口、`BringToForeground`、`AttachThreadInput` 绕前台锁定、
`SwitchToThisWindow`、`IsIconic`/`ShowWindow` 还原最小化，以及那套三档匹配。
`HasGameWindow()` 还留着，但只给启动看门狗用（窗口出来了就收工），不参与前台切换。

### 「结束游戏」不会再卡在「正在保存存档」

上一版是「先发 `ExitGamePush` 请游戏自己存盘，再按『吐日志 / 烧 CPU / 写盘』三个信号
判断它是在存盘还是卡死」。问题是：游戏万一没理这条推送，进程会一动不动地挂在那儿，
界面就一直停在「正在保存存档」，最多等 90 秒才强杀 —— 用户看到的就是
「点了停止，半天没反应，然后游戏被砍」。

现在照官方来：点停止 = 直接 `process.Kill()`，没有等待、没有中间状态。
「强制停止」点下去就是停，不会再出现第二次确认。

> 代价说清楚：正在存盘时被杀，理论上可能留下半截 region 文件。
> 官方一直是这个行为，PCL 现在和它一致。

## ② 聊天界面三处修复

### 输入框看不见打的字

截图里那个白底输入框其实是个原生 `TextBox`：WPF 默认给它白底，而 PCL 的深色主题把
前景色设成了近白色 —— **白字写白底，什么都看不见**。

换成 PCL 自己的 `MyTextBox`：主题背景、圆角、`HintText` 占位提示都是现成的，
还顺手加了「输入消息，回车发送…」的灰字提示。

### 右侧滚动条压住自己发的消息

PCL 的 `ScrollBar` 模板是浮在内容上的，而自己发的消息靠右对齐、正好顶到边，
于是滑块就盖在气泡上。给消息容器留了 10 像素右内边距，气泡不会再被压。

### 消息时间没有日期

以前所有消息只显示 `HH:mm`，昨天的和上个月的看起来一模一样。现在：

- 跨天的地方插一条居中的日期分隔：`2026/10/31`，两边带细横线
- 消息抬头的时间戳：当天只显示 `HH:mm`；今年内的显示 `10/31 14:05`；跨年的显示 `2025/12/31 14:05`

顺带给对方的气泡描了一圈细边 —— 之前是「透明底 + 透明面板」，等于没有气泡。

## ③ 启动器版本号改成动态读取

握手里上报的 `launcherVersion.semver` 以前硬编码 `3.7.27`。这个值是 Lunar 官方启动器的
版本，服务端拿它决定下发哪些功能，对不上就直接关连接（关码 `1008 Unsupported launcher version`）。
Lunar 更新一次，硬编码的版本就会被踢下线。

现在每次连接前去读本机 `%LOCALAPPDATA%\Programs\Lunar Client\Lunar Client.exe` 的
`FileVersion`（形如 `3.7.27-ow`，清洗成 `3.7.27`），读不到才回退到 `3.7.27`。

## 校验值

```
SHA-256  287FB1A799D60DEA80294DA7AFF4BC10FD027C361D27FC217871CB58A8474573
```

## 已知问题

- 好友/私聊依赖 Lunar 后端的私有协议，官方随时可能改字段；改了要重新逆向
- `launcherVersion` 读不到官方启动器时回退到 `3.7.27`，Lunar 大版本更新后这个回退值会过期
- 结束游戏是直接杀进程，不保证存档落盘（与官方行为一致）
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 补齐原版依赖是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等 Genesis 自己
  发现并重下，那种情况依旧没有进度

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
