# 正版登录：从零注册 Azure 应用

> 面向 PCL Lite 开发者。跟着做完，你会有自己的 `client_id`，启动器就能走微软正版登录。
>
> 全程免费。应用注册本身不收费，也不需要买任何 Azure 服务。

---

## 0. 先搞清楚为什么必须注册

启动器自己没法「直接登录微软账号」。微软的 OAuth 2.0 要求每个客户端先有一个**应用身份**（Application / Client ID），
用户授权时看到的也是这个应用的名字。

所以流程是：

```
你注册一个 Azure 应用  →  拿到 client_id  →  填进启动器  →  用户点「正版登录」
```

本项目用的是**设备代码流**（Device Code Flow），这是关键前提，它决定了后面要配什么、不用配什么：

| 因为用设备代码流 | 所以 |
|---|---|
| 没有浏览器回调 | **不需要**配置重定向 URI |
| 是公共客户端 | **不需要**客户端密码（client secret） |
| 靠用户手动输码授权 | **必须**开启「允许公共客户端流」 |

对应代码在 `PCL.Core/Minecraft/Profile/Authentication/MicrosoftProvider.cs`：

```csharp
private const string DeviceEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
private const string TokenEndpoint  = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
// scope = "XboxLive.signin offline_access"
```

注意端点里的 `/consumers/` —— 这表示**只接受个人微软账号**。所以下面注册时账号类型必须选「仅限个人帐户」，
选错了会直接报 `AADSTS700016`。

---

## 1. 准备一个 Azure 账号

如果你已经有能登录 Azure 门户的账号，跳到第 2 步。

**没有的话：**

1. 打开 <https://azure.microsoft.com/free> 注册免费账户
2. 用你的个人微软账号登录（就是平时登录 Xbox / Outlook 那个）
3. 按提示完成手机号 + 信用卡验证

关于信用卡：这是身份验证，会预授权 1 美元然后释放，**不会真扣费**。
只要不手动升级成「即用即付」，就不会产生任何账单 —— 因为整个流程里我们一个 Azure 资源都不创建，
只用 Entra ID 的免费应用注册功能。

> 如果你有学校邮箱，可以看看 Azure for Students，连卡都不用绑。
>
> 国内直连 Azure 门户可能很慢。需要的话挂代理：
> `set HTTPS_PROXY=http://127.0.0.1:7890`（端口按你自己的来）

注册完，你的账号会自动拥有一个**默认目录**（Default Directory），应用就注册在这里面。

---

## 2. 注册应用

打开 <https://entra.microsoft.com>（Microsoft Entra 管理中心，就是原来的 Azure AD 后台）。

如果你有多个目录，先点右上角**设置图标** → 「目录 + 订阅」，切到你要用的那个目录。

然后：

1. 左侧菜单 → **Entra ID** → **应用注册**
2. 点顶部 **+ 新注册**
3. 填写：

| 字段 | 填什么 | 说明 |
|---|---|---|
| **名称** | 随便起，比如 `MyLauncher` | ⚠️ 不能含 `Minecraft`、`Mojang`、`Microsoft`、`Xbox`、`Live`、`Discord`、`Hypixel`，否则后面 Mojang 审核会被打回 |
| **支持的帐户类型** | **仅限个人帐户** | 必须选这个。因为代码用的是 `/consumers/` 端点 |
| **重定向 URI** | **留空** | 设备代码流不需要。平台下拉框也不用选 |

4. 点 **注册**

### 记下两个 ID

注册成功会跳到「概述」页，把这两个值抄下来：

| 名称 | 在哪 | 长什么样 |
|---|---|---|
| **应用程序(客户端) ID** | 概述页第一行 | `3f2504e0-4f89-11d3-9a0c-0305e82c3301` |
| **目录(租户) ID** | 概述页第二行 | `9e8b2c1a-...` |

- **Client ID**：启动器要用的，就是这个
- **Tenant ID**：填 Mojang 审核表时要，先记着

---

## 3. 开启「允许公共客户端流」

**这一步不做，登录一定失败**（报 `AADSTS7000218`）。

1. 在应用页面左侧 → **管理** → **身份验证**
2. 拉到页面最下面 → **高级设置** 区块
3. 找到 **允许公共客户端流** → 选 **是**
4. 点顶部 **保存**

> 这个选项默认是「否」。微软默认认为你的应用是「机密客户端」（跑在服务器上、能保管密码的那种），
> 而桌面启动器属于「公共客户端」，没法保管密码，所以必须显式声明。

---

## 4. 添加 API 权限（建议做）

1. 左侧 → **管理** → **API 权限**
2. 点 **+ 添加权限**
3. 选 **我的组织使用的 API** 标签
4. 搜索 `Xbox Live`，点进去
5. 勾选 **委托的权限** → `XboxLive.signin`
6. 点 **添加权限**

`offline_access` 不用加 —— 它是 OIDC 标准 scope，请求时带上就行。

> 有些情况下不加这一步也能跑通（Xbox Live 支持动态同意），但加了更稳。
> 如果添加时搜不到 `Xbox Live`，可以先跳过，直接去第 5 步试；真报 `AADSTS65005` 再回来加。

---

## 5. 把 Client ID 填进项目

本项目有三种注入方式，按场景选。

### 方式 A：环境变量（推荐，不用重新编译）

项目特意允许 **Release 版也读运行时环境变量**，所以改完直接重启启动器就生效。

```bat
:: 当前用户永久生效
setx PCL_MS_CLIENT_ID "你的-client-id"
```

或者 PowerShell：

```powershell
[Environment]::SetEnvironmentVariable("PCL_MS_CLIENT_ID", "你的-client-id", "User")
```

**设完必须关掉启动器重新开** —— `Secrets` 是静态属性，只在进程启动时读一次。

验证是否生效：启动后在 `PCL/Log/Log*.log` 里搜 `读取到环境变量 PCL_MS_CLIENT_ID`。

### 方式 B：编译期写死（适合自己打包分发）

构建时带上两个环境变量：

```bat
set PCL_WRITE_SECRET=1
set PCL_MS_CLIENT_ID=你的-client-id
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Release
```

`PCL.Core.SourceGenerators/EnvironmentInteropGenerator.cs` 会把所有 `PCL_*` 变量
（去掉 `PCL_` 前缀）生成进 `SecretDictionary`，编进 dll 里。

> ⚠️ 这样 client_id 会以明文躺在你的二进制里。client_id 本身不是秘密（它会在授权 URL 里公开出现），
> 但别把 `PCL_WRITE_SECRET` 那一套用在你不想公开的东西上 —— 同一机制也会打包其他密钥。

### 方式 C：代码里直接传

`MicrosoftProvider` 构造函数支持显式传入，优先级最高：

```csharp
var provider = new MicrosoftProvider("你的-client-id");
```

默认路径 `ProfileService.cs:272` 用的是 `new MicrosoftProvider()`，即走 `Secrets.MSOAuthClientId`。

### 三种方式的优先级

```
new MicrosoftProvider("xxx")     ← 最高
  ↓ 没传
编译期 SecretDictionary（PCL_WRITE_SECRET）
  ↓ 没有
运行时环境变量 PCL_MS_CLIENT_ID
  ↓ 没有
抛 InvalidOperationException: "Microsoft OAuth client id is not configured."
```

---

## 6. 向 Mojang 提交审核（对外发布必须）

Azure 那边配好只是「微软认你」，**Mojang 那边还没认你**。这是两条独立的线：

- 微软那关管的是「你能不能代表用户去拿 token」
- Mojang 那关管的是「你拿了 token 之后能不能调 Minecraft 的接口」

表单地址：**<https://aka.ms/mce-reviewappid>**

### 不审核会怎样

先把事实说清楚，避免误解：

**技术上不会拦你。** 微软的 OAuth 只校验 `client_id` 是否有效，`api.minecraftservices.com` 也不检查你的
AppID 有没有进过审核名单。所以不审核，登录一样能跑通 —— 很多人就这么用了好几年。

**但合规上站不住。** 《Minecraft 使用准则》里有一句兜底条款：

> If something isn't covered by these guidelines and we haven't otherwise said it's okay,
> that probably means we don't want you to do it. In any case if it isn't covered,
> **please don't do it without getting written permission from us.**

启动器恰好属于「没被准则明确涵盖」的东西。而 AppID 审核表单，就是拿这份**书面许可**的正规通道。

**成本几乎为零。** 审核免费、每周一批，通过后不会给你任何凭证，`client_id` 也不用改，
代码一行都不用动 —— 它只是把你的 AppID 登记进他们的记录里。所以没有理由不做。

**代价不对称。** 准则里还有一条：

> All permissions and consents are given by us at our discretion and **may be revoked at any time**.

不审核的话，你等于一直踩在「随时可能被处置」的状态里。自用无所谓，公开分发就是给自己留了个雷。

### 一句话结论

| 你的情况 | 要不要审 |
|---|---|
| 自己用，不发给别人 | 可以不审 |
| 发给几个朋友 | 建议审，成本很低 |
| 公开在 GitHub / 论坛发布 | **必须审** |
| 想上架任何应用商店 | **必须审**，多数渠道会要这个 |

### 要填的 9 项

| # | 字段 | 怎么填 |
|---|---|---|
| 1 | 确认已读 EULA 和 Usage Guidelines | 勾 **Yes** |
| 2 | 联系邮箱 | 必须和 Azure 门户里的联系信息一致，否则对不上号 |
| 3 | 请求类型 | 选 **New AppID for Approval** |
| 4 | 应用名称 | 和 Azure 里填的一致。**不能含 Minecraft / Mojang / Microsoft / Xbox / Live / Discord / Hypixel** |
| 5 | Application ID | 你的 Client ID（GUID 格式，一次只能提交一个） |
| 6 | Tenant ID | 你的目录 ID（新申请必填） |
| 7 | 关联网站或域名 | 有 GitHub 仓库 / 官网就填，没有的话填个能说明项目的链接 |
| 8 | 理由说明 | 简单写：个人开发的 Minecraft 启动器，用设备代码流做微软账号登录，验证用户游戏所有权后启动游戏。**理由不充分会被直接跳过不审** |
| 9 | 其他信息 | 可留空 |

### 注意事项

- **审核是每周一次**，不是实时。重复提交不会加快，反而可能被当成骚扰
- 表单明确写了：应用必须**验证用户拥有游戏**、并且**在进入游戏或服务器前要求认证**。
  绕过所有权检查的应用一律不批
- 提交后等邮件。通过了你不会收到什么凭证，就是「可以用了」
- 如果你只是自己本地玩、不对外分发，可以先用着，但要清楚这是灰色状态

### 发布前还有两件事，比审核更容易出事

审核只是「Mojang 知道有你这么个应用」。真正容易翻车的是下面两条，都写在《使用准则》里：

**① 免责声明（硬性要求）**

准则原文要求在产品、列表页、描述、网站等所有相关材料上**显著**放置：

> NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT

中文版一般写「非官方产品，与 Mojang 和 Microsoft 无关，未经其批准或认可」。
GitHub README 顶部、关于页、下载页都该有。这条最容易被忽略，但它是明确写出来的。

**② 不要分发游戏文件**

> Do **not** redistribute our games or any alterations of our games or game files

这条没有「申请批准就能做」的例外 —— 是绝对禁止。所以启动器里任何「下载 / 打包 / 附带」游戏本体的功能，
都是比 AppID 审核严重得多的问题。

> 参考 2026 年 10 月 20 日的情况：PCL 在 v2.99.0.0 移除了 Minecraft 下载功能，HMCL 在中国大陆限制下载页，
> PCL CE 也禁用了相关功能。起因是一起未成年人私服充值举报，波及到启动器本身
> （MineBBS 澄清与网易、Mojang、微软无直接关联）。本质是合规风险导致的渠道收缩。
>
> 也就是说：发布前真正该盘的是「有没有分发游戏文件」，而不是「AppID 审没审」。

**③ 名字别踩线**

准则规定 `Minecraft` 不能作为主名称，只能作为次要描述。另外审核表单自己也列了禁用词：
`Mojang`、`Minecraft`、`Microsoft`、`Live`、`Xbox`、`Discord`、`Hypixel`。

`PCL Lite` 这个名字本身没问题。

---

## 7. 验证

1. 确保 `PCL_MS_CLIENT_ID` 已设好，**重启**启动器
2. 点「正版登录」
3. 界面会显示一个 8 位左右的代码，比如 `K7Q2X9PLM`
4. 打开 <https://microsoft.com/link>，输入这个代码，用你的微软账号登录并同意授权
5. 回到启动器，登录应该自动完成

### 看日志确认每一步

`PCL/Log/Log*.log` 里依次应该出现：

```
读取到环境变量 PCL_MS_CLIENT_ID          ← 确认 client_id 进来了
[Login] 获取设备代码成功
[Login] 轮询授权状态: authorization_pending   ← 正常，说明在等你操作
[Login] 微软账号登录成功
[Login] Xbox Live 认证成功
[Login] XSTS 认证成功
[Login] 获取 Minecraft 档案成功
```

完整链路是六步，任何一步断了都能从日志定位：

```
设备代码 → 微软 OAuth token → Xbox Live token → XSTS token → Minecraft token → 玩家档案
```

---

## 8. 报错对照表

| 报错 | 原因 | 怎么修 |
|---|---|---|
| `Microsoft OAuth client id is not configured.` | 三种注入方式都没生效 | 检查 `PCL_MS_CLIENT_ID` 是否设对，**是否重启过进程** |
| `AADSTS7000218` | 没开「允许公共客户端流」 | 回到第 3 步，选「是」并保存 |
| `AADSTS700016` | client_id 不存在，或账号类型选错 | 确认 ID 抄对了；确认应用选的是「仅限个人帐户」 |
| `AADSTS65005` | 缺少 Xbox Live 权限 | 回到第 4 步添加 `XboxLive.signin` |
| `AADSTS50020` / `AADSTS50034` | 用了工作/学校账号登录 | 这个应用只收个人账号，换个 Outlook / Xbox 账号 |
| `invalid_grant` | 设备代码过期（一般 15 分钟） | 重新点一次登录 |
| `xsts_authentication_failed` + 2148916233 | 微软账号没建过 Xbox 档案 | 去 <https://xbox.com> 登录一次，让它自动创建 |
| `xsts_authentication_failed` + 2148916238 | 账号是未成年人，且没加入家庭组 | 需要家长账号把你加进 Microsoft Family |
| `xbox_authentication_failed` | Xbox Live 拒绝 | 同上，多半是账号本身的问题，不是代码问题 |
| 拿不到 Minecraft 档案 | 账号没买游戏 | 正常。没买就是没有档案，不是 bug |

---

## 9. 一页速查

```
1. azure.microsoft.com/free        注册账号（要卡验证，不扣费）
2. entra.microsoft.com             Entra ID → 应用注册 → 新注册
   名称        → 不含 Minecraft/Mojang/Xbox 等字样
   账户类型    → 仅限个人帐户          ★ 选错就废
   重定向 URI  → 留空
3. 身份验证 → 高级设置 → 允许公共客户端流 → 是 → 保存   ★ 不开必挂
4. API 权限 → Xbox Live → XboxLive.signin（建议）
5. 抄下 Client ID
6. setx PCL_MS_CLIENT_ID "xxx"  然后重启启动器
7. aka.ms/mce-reviewappid        提交 Mojang 审核（对外发布才必须）
```

---

## 附：设备代码流的完整请求

想自己抓包或写测试脚本的话，这是四步原始请求。

### ① 申请设备代码

```http
POST https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode
Content-Type: application/x-www-form-urlencoded

client_id=<你的 client id>&scope=XboxLive.signin%20offline_access
```

返回：

```json
{
  "device_code": "GMMhm...",
  "user_code": "K7Q2X9PLM",
  "verification_uri": "https://microsoft.com/link",
  "expires_in": 900,
  "interval": 5,
  "message": "To sign in, use a web browser to open the page..."
}
```

把 `user_code` 和 `verification_uri` 显示给用户。

### ② 轮询换 token

```http
POST https://login.microsoftonline.com/consumers/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

client_id=<id>&grant_type=urn:ietf:params:oauth:grant-type:device_code&device_code=<device_code>
```

- 用户还没操作 → `{"error":"authorization_pending"}`，按 `interval` 秒后重试
- 用户操作太慢 → `{"error":"expired_token"}`，重来
- 成功 → 拿到 `access_token` 和 `refresh_token`

### ③ Xbox Live

```http
POST https://user.auth.xboxlive.com/user/authenticate
Content-Type: application/json

{
  "Properties": { "RpsTicket": "d=<微软 access_token>", "SiteName": "user.auth.xboxlive.com" },
  "RelyingParty": "http://auth.xboxlive.com",
  "TokenType": "JWT"
}
```

注意 `RpsTicket` 前面的 `d=` 前缀，少了会失败。

### ④ XSTS

```http
POST https://xsts.auth.xboxlive.com/xsts/authorize
Content-Type: application/json

{
  "Properties": { "SandboxId": "RETAIL", "UserTokens": ["<XBL token>"] },
  "RelyingParty": "rp://api.minecraftservices.com/",
  "TokenType": "JWT"
}
```

`RelyingParty` 必须是 `rp://api.minecraftservices.com/`。返回里的 `DisplayClaims.xui[0].uhs` 就是后面要用的 user hash。

### ⑤ 换 Minecraft token

```http
POST https://api.minecraftservices.com/authentication/login_with_xbox
Content-Type: application/json

{ "identityToken": "XBL3.0 x=<uhs>;<XSTS token>" }
```

### ⑥ 拿档案

```http
GET https://api.minecraftservices.com/minecraft/profile
Authorization: Bearer <minecraft access_token>
```

返回 `id`（无连字符 UUID）、`name`、`skins` 等。

---

## 参考

- [注册应用 - Microsoft Learn](https://learn.microsoft.com/zh-cn/entra/identity-platform/quickstart-register-app)
- [公共/机密客户端与公共客户端流](https://learn.microsoft.com/zh-cn/entra/identity-platform/msal-client-applications)
- [设备授权流协议](https://learn.microsoft.com/zh-cn/entra/identity-platform/v2-oauth2-device-code)
- [AppID 审核表单](https://aka.ms/mce-reviewappid)
- [Minecraft 使用准则](http://aka.ms/mcusageguidelines)
