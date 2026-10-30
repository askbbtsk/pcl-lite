<summary>好友列表里同一个人出现两次（一个「启动器」、一个「离线」）—— 服务端把在线好友同时塞进了两个列表，PCL 没去重。这一版按 UUID 合并。</summary>

PCL Lite 1.6.3 快照版 6（snapshot-6）。

> **这是快照版，不是正式版。** 把更新通道选成「快照尝鲜」就能收到；
> 「最新」也会收到，「稳定版」「预览体验」不会。
>
> 已经是 `1.6.3-snapshot-5` 的话，这次只有一个改动：好友列表去重。

## 现象

好友列表里同一个人出现两次，一个显示「启动器」，一个显示「离线」。

## 原因：服务端两个列表会重叠

`friend.v1.LoginResponse` 里有 `onlineFriends`(字段 11) 和 `offlineFriends`(字段 2)
两个列表，看起来应该是互斥的。实测把原始响应 dump 出来：

```
LoginResponse 190B

== onlineFriends (1) ==
  93a6aca9-33ba-40da-bd77-de8bf072ff5e  Bed_Spacekiller_

== offlineFriends (1) ==
  93a6aca9-33ba-40da-bd77-de8bf072ff5e  Bed_Spacekiller_

两边都有的 uuid: 1
```

**UUID 一模一样**，是同一个人。服务端就是把他同时放进两个列表里的。

`offlineFriends` 这个名字有误导性 —— 它装的其实是**全部好友**，不只是离线的；
`onlineFriends` 是在线那部分的状态补充。PCL 之前直接把两个列表拼起来，
于是每个人在线的都出现两次：在线条目带 `location`（「启动器」），
离线条目不带你看着就是「离线」。

## 改法

按 UUID 合并，`offlineFriends` 打底，`onlineFriends` 覆盖：

- 同一个 UUID 只留一条，状态与位置取在线条目的
- 在线条目偶尔不带 `rankName`（字段 13），那种情况保留离线条目里的（字段 8）
- 解析不出 UUID 的条目原样保留，不参与合并，免得好友凭空消失

界面上的排序本来就是「在线优先、再按名字」，所以合并后在线的那个人自然排在最前面。

## 验证

- 真机跑完整链路：好友列表从 2 条变成 1 条，状态 `Online`、位置「启动器」
- 单测新增 2 条：同 UUID 只留一条、在线条目缺 `rankName` 时补离线那份；
  另外把原来的「在线离线各一条」用例改成两个不同 UUID，避免它掩盖去重逻辑
- Lunar 相关单测 77 条全过

## 校验值

```
SHA-256  F809ACCF50B536311475E62C88E268AF60CB7D5C3984B72A41650244AA5A0664
```

## 已知问题

- 好友/私聊依赖 Lunar 后端的私有协议，官方随时可能改字段；改了要重新逆向
- 服务端下限会随官方启动器更新继续往上抬；PCL 被拒时会自动升版本重连，能自愈
- 结束游戏是直接杀进程，不保证存档落盘（与官方行为一致）
- Lunar 物料清单接口偶发 `SSL unexpected EOF`，重试几次通常能过
- 补齐原版依赖是按文件存在性判断的，不校验哈希；文件损坏（下了一半）仍要等 Genesis 自己
  发现并重下，那种情况依旧没有进度

## 许可证

本项目基于 PCL-CE（MIT）修改，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
