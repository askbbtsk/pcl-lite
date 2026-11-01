<summary>1.7.0-snapshot：Lunar 页面的账号改成独立登录 —— 在本页点「登录」用 Lunar 的应用身份授权一次，产出的令牌游戏端才认，不再卡 Connecting...</summary>

PCL Lite 1.7.0-snapshot。

> 快照版，只有选了「快照」或更激进通道的用户会收到。稳定版通道请继续用 1.6.4-hotfix-5。

## 问题

Lunar 页面以前直接复用 PCL 主页面的登录。选一个 PCL 账号启动，游戏端会卡在
`Connecting...`，进不去。

根因不在网络，在**令牌身份对不上**。Lunar 游戏端拿到 accessToken 后会解 JWT，
按里面的 `aid`（应用标识）选刷新实现：

```js
oPe = e => {
  let { aid: t } = decodeJwt(e.accessToken);
  return t === LunarAppId ? SV          // 标准 OAuth，能刷
       : t === XboxLiveAppId ? iPe      // 走不通，见下
       : wV;                            // SISU，需要 deviceToken 和密钥对
}
```

PCL 主页面登录用的是 PCL 自己的应用注册，签出来的 refreshToken 的 `aid` 是 PCL 的。
三个分支全命中不了，落到 `wV`（SISU），那条路要 deviceToken、密钥对、还要过区域封禁
和年龄验证 —— PCL 造不出来，于是刷新失败，界面永远停在 `Connecting...`。

顺带说一句：`iPe` 那个分支其实是**死代码**。它比的是 16 位无横线的应用标识，
而真实令牌里 `aid` 是 36 位带横线的标准 UUID 形式，`decodeJwt` 不做归一化，
永远不相等。所以这条路不用再试了。

## 改了什么

**Lunar 页面的账号现在是独立的一套，和主页面互不相通。**

| | 主页面 | Lunar 页面 |
| --- | --- | --- |
| 应用身份 | PCL 自己的 | Lunar 的 |
| 账号存哪 | `ProfileService` 的档案列表 | `%LOCALAPPDATA%\PCLLite\Lunar\identities.json` |
| 怎么产生 | 主页面点登录 | Lunar 页面点「登录」 |
| 互相可见 | 看不到 Lunar 的 | 看不到 PCL 的 |

具体改动：

- 新增 `LunarIdentityStore`：多账号存储，只写 PCL 自己的目录，不碰 `~/.lunarclient`
- 账号下拉框第一组从「PCL 账号」换成「Lunar 身份账号」，不再读 `ProfileService`
- 账号行右侧新增「登录」按钮：走设备码流程，用 Lunar 的应用身份授权，
  产出的 refreshToken 的 `aid` 就是 Lunar 的，游戏端认得出来
- 没登录时**不再**偷偷拿 PCL 的登录顶上，而是明确提示去登录
- 续期改成两级：优先用账户库里那份 refreshToken 配 Lunar 的应用身份刷新（刷新后
  连新的 refreshToken 一起写回，微软每次都轮换）；账户库里没有才退回用 PCL 登录兜底

主页面登录、普通账号刷新、离线登录**一行没动**，还是原来的应用注册。

## 怎么用

1. 进 Lunar 页面，账号行右侧点「登录」
2. 按提示打开 `microsoft.com/link` 输入设备码，选账号
3. 登录完自动选中，直接启动

登录一次之后就一直有效，之后启动不再弹。

## 校验

`SHA-256`（签名后）：`C8EE6EBBB35D72092C98CED89126F43E71190FDFC654551CC259BF50FBC232B9`

大小：19,346,216 字节

## 已知代价

这个登录走的是 Lunar 自己的 Azure 应用注册，所以：

- 授权记录会记在 Lunar 名下，在 account.microsoft.com 的「应用权限」里看到的是
  Lunar Client，不是 PCL
- Lunar 单方面改那个应用注册（关公共客户端、删应用）会让这条路直接失效，
  PCL 无法自救

这是让游戏端认令牌的**唯一**办法，没有别的绕法。
