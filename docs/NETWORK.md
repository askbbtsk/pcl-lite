# 连接管理

启动器发起连接时，模块先提交自己的名称、目标地址和用途。连接管理器完成授权后才打开传输，并把响应或连接交还模块。模块名保存在请求元数据中，不作为 HTTP 请求头发送。

## 接入方式

- 长期使用的 HTTP 客户端：`NetworkConnectionManager.CreateHttpClient(name, purpose, timeout)`。
- 通用请求：`Requester` 和 `HttpSenderExtension` 自动携带调用文件的模块名；`RequestParam.Source`、`FetchParam.Source` 可显式指定来源。
- 下载任务：`DownloadFile.Source` 在创建任务时记录来源，顺序下载、Range 探测、分段及重试沿用该来源。
- WebSocket：`NetworkConnectionManager.ConnectWebSocketAsync`。
- TCP：`NetworkConnectionManager.ConnectSocketAsync`。
- UDP 房间：创建会话前授权；不能按每个游戏数据包弹窗。页面应使用异步 `StartAsync`，避免等待授权时阻塞界面。
- 插件 SDK 网络及下载接口：使用 `Plugin: <插件 ID>` 作为独立授权名称。

不要直接新建未受管理的 `HttpClient`，也不要通过目标域名借用其他模块的授权。DNS 使用独立的受管理传输，避免其自身解析再次进入 DoH。

## 设置与兼容

「设置 → 杂项 → 连接管理」保留允许所有连接、隐私模式、永久授权和清空授权。已有五类用途的持久化 ID 不变。新增账号登录、Lunar、基岩版、插件管理、服务器连接及联机分类。

内置模块按功能分类共享授权；未分类模块和插件 SDK 请求按各自的模块名或插件 ID 单独授权。每个请求仍保留具体调用模块的名称。

未分类模块在首次请求时登记，随后可在列表中允许或撤销。弹窗中的选择只在本次启动有效。允许所有连接沿用已有设置，不会在升级时强制改为逐项询问。

本机回环 IPC 不要求外网授权。管理范围是启动器自身及插件 SDK 发起的连接，不代理 Minecraft、浏览器或其他外部进程的网络。

Lunar 只更换传输入口；认证地址、请求头、握手消息和令牌处理沿用 `docs/LUNAR.md` 中从官方启动器提取的协议。
