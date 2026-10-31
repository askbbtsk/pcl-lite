<summary>1.6.4-hotfix-2：修「更新偶发要重启两次」——同一秒里起了两个更新程序抢同一个 exe，一个在复制另一个在删，两边都报 Access denied；现在只允许起一个，替换也改成了带重试、失败必定回滚。</summary>

PCL Lite 1.6.4-hotfix-2。

> 热修复，稳定版通道会收到。

## 现象

点「重启更新」之后，有一定概率提示更新失败：

```
更新失败: Access to the path 'PCL-Litex64.exe' is denied.
```

再点一次就好了 —— 所以看起来像「偶发，重启两次才行」。

## 两个原因叠在一起

**一、同一秒里起了两个更新程序。**

日志里同一秒有两条：

```
22:38:33.570  更新程序启动 … true
22:38:33.838  更新程序启动 … false
```

隔了 268 毫秒。启动更新程序的地方有三处——点「重启更新」按钮、安装任务、
关闭窗口时的 `EndProgramForce`——三处之间没有任何互斥。两个更新器同时抢同一个
目标文件：一个正在写，另一个在删，`DeleteFile` 返回 `ERROR_ACCESS_DENIED`。

**二、替换文件是一次性尝试，没有重试。**

这里有个容易判错的地方：文件被短暂持有时，`DeleteFile` 返回的**不是**「文件被占用」，
而是 `ERROR_ACCESS_DENIED`，.NET 映射成 `UnauthorizedAccessException`。
按「被占用」去处理会漏掉这一类。

而且收尾那句 `File.Delete(备份)` 写在 `try` 外面 —— 备份删不掉（杀软正在扫那个
19 MB 的 .bak）会直接抛到上层，连后面的重启都被跳过。结果就是文件其实已经换好了，
但启动器没起来，用户只能手动再开一次。

## 改了什么

| 位置 | 改动 |
| --- | --- |
| `UpdateManager.UpdateRestart` | 用原子标记保证**只起一个**更新器；重复调用直接忽略并记日志；被安全软件拦截时把标记还回去，装完白名单还能再点 |
| `UpdateHelper.Replace` | 备份、删除、复制全部 **25 次 × 200ms** 重试；删之前先摘掉 `ReadOnly` 属性；复制后比对文件大小，防止把被截断的 exe 留在目标位置；失败时回滚，保证旧版本一定还能启动；收尾清理绝不抛异常 |
| `UpdateService` | `update_finished` 删来源文件改成重试 10 次，删不掉只记一条 Warn。新进程是在旧进程 `Process.Start` 之后才起来的，那时候更新器很可能还没退干净，以前这里会直接抛异常 |

## 关于这次更新本身

更新器跑的是**新版本自己的代码**（旧版本下载完新 exe，再把新 exe 当更新器启动），
所以从 `1.6.4-hotfix-1` 升上来的这一次，就已经走修好的逻辑了，不用等下一版。

## 校验值

```
SHA-256  2AAF3EDA5ED34574B32BEB2B559DEEBF5ADFB05EDF0B5084E9C17FC8D8BCC040
```

## 已知问题

同 1.6.4，未新增。

## 许可证

本项目以 MIT 许可证发布，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
