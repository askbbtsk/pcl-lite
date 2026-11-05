<summary>修一个只在降级到 .NET 6 时才会暴露的坑：服务器地址解析误走了不带取消令牌的 ConnectAsync，超时后底层连接无法中止，会留下孤儿连接任务。顺带修掉那条从未被编译过的分支里的编译错误，并把 CancelAsync 的三处调用做成跨框架兼容。</summary>

PCL Lite 1.7.1 热修复 3。

## ① `ServerAddressResolver` 走错分支

`PCL.Core/Minecraft/ServerAddressResolver.cs` 里连接探测原本长这样：

```csharp
#if NET8_0_OR_GREATER
await sock.ConnectAsync(new IPEndPoint(ip, port), linked.Token).ConfigureAwait(false);
#else
await sock.ConnectAsync(new IPEndPoint(ip, port)).WaitAsync(ConnectTimeout, linked.Token).ConfigureAwait(false);
#endif
```

`Socket.ConnectAsync(EndPoint, CancellationToken)` 是 **.NET 5.0** 就有的 API，条件却写成了 `NET8_0_OR_GREATER`。项目跑在 net10 上时走的是第一条，看起来一切正常；一旦降级到 `net6.0-windows`，条件不成立就会落进 `#else`：

- 不带 token 的 `ConnectAsync` **不响应取消**。外层 `WaitAsync` 超时或 Happy Eyeballs 选中其它地址后，底层 TCP 握手还在继续；
- 紧接着 `using var sock` 释放 socket，后台连接任务会抛 `ObjectDisposedException` / `SocketException`，成为无人观察的孤儿异常。

现在宏改成 `NET5_0_OR_GREATER`，直接走原生可取消重载。

## ② 那条分支其实根本编译不过

`#else` 里写的是 `WaitAsync(ConnectTimeout, ...)`，而字段名是 `_ConnectTimeout`（少一个下划线），这是 `CS0103`。也就是说这条分支**从来没被编译过** —— 降 net6 会先撞编译错误，而不是先看到孤儿异常。已补回下划线。

## ③ `CancelAsync` 跨框架兼容

`CancellationTokenSource.CancelAsync` 是 .NET 8 的 API，全项目三处调用：

| 文件 | 场景 |
| --- | --- |
| `Minecraft/ServerAddressResolver.cs` | Happy Eyeballs 选中后取消其余连接 |
| `IO/Net/SocketForward/TcpForwardWorker.cs` | 双向转发结束 |
| `App/Lunar/Social/LunarSocialClient.cs` | `DisposeAsync` |

都改成 `#if NET8_0_OR_GREATER` 用异步版、老框架直接调同步 `Cancel()`。

**没有用返回 `Task` 的扩展垫片**：`Cancel()` 本身是同步执行所有回调的，包一层 `Task.CompletedTask` 看似等价，但只要垫片里用了 `Task.Run` 之类，就变成「延迟取消」，`using` 释放 CTS 后回调会碰到已释放对象 —— 语义就变了。老框架上同步 `Cancel()` 才是原语义。

## 校验

- 常规编译（net10，走 `NET8_0_OR_GREATER` 分支）：0 错误
- 把宏临时改成未定义名强制走 `#else` 分支，再编译：0 错误（验证完已还原）
- 产物密钥校验：client id UTF-16 **1 处**、UTF-8 **0 处**
- `ProductVersion` = `1.7.1-hotfix-3+00c37e8b`

`SHA-256`（签名后）：`F5FC1F67768D3D49B07D6DD8C180508B5AD0AC3AAFDFD23981EDCCCC5C6A9124`

大小：19,395,368 字节

签名：`Valid`（PCL Lite，DigiCert 时间戳）

## 说明

- 这一版**不影响 net10 构建的行为**：net10 下本来就是走可取消重载，改动只是让降级目标框架后不至于编不过、也不至于留下孤儿连接。
- 1.7.1 那条「Lunar 物料下载异常可以先退回 1.7.0」的公告仍然有效，这一版没有改下载链路。
