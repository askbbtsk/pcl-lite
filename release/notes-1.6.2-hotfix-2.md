<summary>在任务列表里点 × 取消基岩版下载，不再弹「安装失败：A task was canceled.」——取消就只是取消；顺手把失败原因里那层没营养的英文包装拆掉了。</summary>

PCL Lite 1.6.2 热修复 2。

## ① 取消下载不再算「安装失败」

1.6.2-hotfix-1 把基岩版下载接到了 PCL 的下载器上，下载任务会出现在右下角的任务列表里，
卡片右上角有个 ×。点它，下载确实停了 —— 但紧接着弹出：

```
安装失败：A task was canceled.
System.Threading.Tasks.TaskCanceledException: A task was canceled.
```

原因是那段代码把所有异常都当成失败：任务被中止 → `TaskCanceledException` → 落进
`catch (Exception ex)` → 弹错误框、写「安装失败」状态。**取消是用户主动做的正常操作，
不该被当成错误。**

现在取消走单独一条分支：

- 不弹错误框
- 不把版本状态写成「失败」
- 行内进度条归零、按钮恢复成「下载并安装」，可以直接重来
- 底部状态栏显示「已取消下载 <版本号>」

顺带处理了一个小问题：取消之后 PCL 的下载线程还会再推几次进度，把已经清空的行内
进度条又写回去。现在进度回调会先确认这一行还在忙，才更新。

## ② 失败原因不再是一句英文

`FileDownloader` 最后统一抛 `IOException("下载失败：<本地路径>")`，真正的原因（403、
超时、连接重置……）藏在 `InnerException` 里。以前直接显示外层那句话，等于什么都没说。

现在会把 `AggregateException` / `IOException` / `TimeoutException` 一层层剥开，显示最里面
那句能看懂的原因。真的走到取消分支时也不会再把英文的 `A task was canceled.` 甩给用户。

## 如果你没点 × 却也看到了这个错

那说明有别的东西在替你中止任务。hotfix-2 会在日志里留下这一行：

```
[Bedrock] 下载被中止：<版本号>
```

配合上一行的 `[Taskbar] 关闭任务管理卡片：…` 就能看出是不是被点了 ×。
把这两行发我。

## 升级

直接覆盖，配置不变。

## 下载

| 架构 | 文件 |
| --- | --- |
| x64 | `PCL-Lite_1.6.2-hotfix-2_x64.exe` |

单文件、免解压。**需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)**。

SHA-256：`AB67BB6816484658589BF3EE109D5A5F4A136ECE47BF02530EDFBE239BDF08F9`

## 已知问题

- **hotfix 只对 1.6.2 及以后的客户端可见。** 更早的版本里没有 `hotfix` 这套比较器，
  会把 `1.6.2-hotfix-2` 当成预发布丢掉。跨版本的修复仍然要发正式版
- 发布文件已做代码签名（SHA-256 + DigiCert 时间戳）；证书为自签名，暂无商业信誉，
  个别安全软件首次运行时仍可能提示发布者信息
- 基岩版 GDK 安装包（`.msixvc`）解包依赖内置的内容密钥（CIK），正式版与预览版各一把，
  均已编入本构建；密钥若被官方轮换会失效，届时用环境变量覆盖即可，不必重新下载启动器
- Lunar 的本地 IPC 只实现了 `AuthService/RefreshAccount`（会话续期），好友列表、游戏内商店、
  聊天面板这些走 IPC 的功能仍然是空的。回空响应是安全的，游戏不会崩，只是对应界面没数据
- **PCL 没登录对应账号时续期会失败**，游戏内还是显示 Invalid session。想彻底解决，
  在主页面登录同一个微软账号即可
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 补齐原版依赖是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等 Genesis 自己
  发现并重下，那种情况依旧没有进度
- PCL2 原版（`setup.ini`）来源的导入只搬设置，**账号需要在账号页重新登录**
- 主页语言信封与 `lang:` 标签是 PCL Lite 扩展，原版 PCL 加载含这些内容的文件时
  会按普通 XAML 渲染（所有语言分支堆在一起）——属于预期的单向兼容
- UWP 安装需要开发者模式；系统策略禁用旁加载时只能手动到「设置 → 系统 → 开发者选项」打开

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
