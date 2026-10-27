# Lunar Client 启动链路（1.7.10 → 26.x）

本文记录「不启动 Lunar 启动器、直接拉起 Lunar 游戏」所需的全部事实。
结论来自对本机 Lunar Client 启动器（Electron）`resources/app.asar` 的静态分析、
对 `offline/multiver` 下 jar 的反编译字符串，以及对着线上接口的实测，不是猜测。

## 一句话结论

Lunar 的 `/launcher/launch` 接口**不需要任何鉴权**，直接返回完整物料
（classpath、原生库、主类、JRE、贴图与 UI 资源）。**所有子版本共用同一套启动方式**，
只有 JRE 版本与 `jre.extraArguments` 不同。

但账号有个反直觉的地方：**游戏进程不读命令行里的 `--accessToken`**，
它读的是 `~/.lunarclient/settings/game/accounts.json` 里的激活账号。
详见「账号」一节。

## 物料来源

```
POST https://api.lunarclientprod.com/launcher/launch
User-Agent: Lunar Client Launcher v<版本>
Content-Type: application/json
```

请求体（POST 时公共字段放在 body 里，不是 query）：

```jsonc
{
  "installation_id": "<任意 GUID>",   // 本地 launcher-cache/installation-id
  "os": "win32",
  "os_release": "10.0.26300",
  "arch": "x64",
  "launcher_version": "3.3.0",
  "version": "1.8.9",                 // subversion id
  "branch": "master",
  "args": [],                          // 启动 flag，见下
  "module": "lunar",                   // lunar | forge
  "canary_preference": "NEUTRAL",      // OPT_IN | OPT_OUT | NEUTRAL
  "profile": { "id": "<uuid>", "name": "<玩家名>" }
}
```

约束（服务端用 zod 校验，写错会返回 `ZodError` 明细）：

- `overwolf_muid` 不填就不要传 `null`，直接省略该字段
- `profile.id` 必须是合法 UUID
- `canary_preference` 三选一，不能为 null

响应：

```jsonc
{
  "success": true,
  "updateAssets": true,
  "launchTypeData": {
    "mainClass": "com.moonsworth.lunar.genesis.Genesis",
    "artifacts": [ /* 61 条 */ ]
  },
  "licenses": [...],
  "textures": { "indexUrl": "...", "indexSha1": "...", "jitIndexUrl": "..." },
  "ui": { "sourceUrl": "...", "sourceSha1": "...", "assets": {...} },
  "jre": {
    "download": { "url": "https://cdn.azul.com/zulu/bin/zulu17...zip", "fallbackUrl": "..." },
    "folderChecksum": "...",
    "executablePathInArchive": [...]
  },
  "canaryToken": "control:....,...."
}
```

`artifacts[]` 每条形如：

```jsonc
{
  "name": "common-0.1.0-SNAPSHOT-all-nomappings.jar",
  "sha1": "c9b5b5b5...",
  "url": "https://multiver-releases.lunarclientprod.com/<name>/<sha1>",
  "differentialUrl": "https://multiver-releases.lunarclientprod.com/diff/<name>/to-<sha1>-from-PREVIOUS_HASH",
  "type": "CLASS_PATH",       // CLASS_PATH | EXTERNAL_FILE | NATIVES
  "size": 59820,
  "mtime": 1792807667
}
```

三类 artifact 的用途：

| type | 用途 |
| --- | --- |
| `CLASS_PATH` | 进 `-cp`，是游戏本体与 mixin（7 个） |
| `EXTERNAL_FILE` | 落到工作目录，如 `OptiFine_v1_8.jar`、`user-message-patterns.json`（50 个） |
| `NATIVES` | zip，解到 natives 目录，如 `client-natives-win-x86-v1_8.zip`（4 个） |

1.8.9 的 JRE 是 **Zulu 17**（`zulu17.64.17-ca-jre17.0.18-win_x64.zip`），
不是 Java 8——Lunar 用 Genesis 引导层跑在 17 上。

## 版本元数据（公开，无需鉴权）

```
GET https://api.lunarclientprod.com/launcher/metadata/versions/lunar
    ?installation_id=<guid>&os=win32&os_release=...&arch=x64&launcher_version=...
```

返回 `versions[]`，其中 1.8 条目：

- `id`: `1.8`
- `subversions[0].id`: `1.8.9`
- `assets`: `{ "id": "1.8", "url": "https://launchermeta.mojang.com/v1/packages/f6ad102b.../1.8.json" }`
- `modules`: `lunar`（Lunar + OptiFine）、`forge`（Lunar + Forge）

**资源索引走 Mojang 官方**，不需要 Lunar 另给一套。

### 服务端当前提供的全部子版本（实测 2026-10）

`versions[]` 一共 9 个大版本、34 个子版本，全部可启动：

| 大版本 | 子版本 |
| --- | --- |
| 26 | 26.1、26.1.1、26.1.2、26.2、26.3 |
| 1.21 | 1.21、1.21.1、1.21.3、1.21.4、1.21.5、1.21.6、1.21.7、1.21.8、1.21.9、1.21.10、1.21.11 |
| 1.20 | 1.20、1.20.1、1.20.2、1.20.3、1.20.4、1.20.5、1.20.6 |
| 1.19 | 1.19、1.19.2、1.19.3、1.19.4 |
| 1.18 | 1.18.1、1.18.2 |
| 1.17 | 1.17.1 |
| 1.16 | 1.16.5 |
| 1.12 | 1.12.2 |
| 1.8 | 1.8.9 |
| 1.7 | 1.7.10 |

## 全版本支持：高版本换了什么，没换什么

对 1.7.10 / 1.8.9 / 1.12.2 / 1.16.5 / 1.17.1 / 1.18.2 / 1.19.4 / 1.20.1 / 1.20.6 /
1.21.1 / 1.21.4 / 1.21.11 / 26.3 逐个打 `/launcher/launch` 比对，结论是**启动方式完全没变**：

- `launchTypeData.mainClass` 全是 `com.moonsworth.lunar.genesis.Genesis`
- `launchTypeData` 只有 `artifacts` 与 `mainClass` 两个字段，没有别的分支
- 附件类型始终是 `CLASS_PATH` / `EXTERNAL_FILE` / `NATIVES` 三种
- `--ichorClassPath` / `--ichorExternalFiles` / `--launchId` / `--canaryToken`
  这些参数名一个都没改（在 `legacy-*.jar` 与 `modern-*.jar` 的参数解析类里核过）

真正会变的是三样东西，都必须跟着服务端走：

| 变化点 | 1.8.9～1.20.1 | 1.20.6～1.21.11 | 26.x |
| --- | --- | --- | --- |
| JRE | Java 17（`5a6eb763…`） | Java 21（`d093c370…`） | Java 25（`515e47c1…`） |
| 附件数 | 61（7 类路径 / 50 外部 / 4 原生） | 69 / 68 | 66 |
| 响应里的 `baseModpack` | 无 | 有（mrpack，官方启动器用来铺实例） | 有 |

### `jre.extraArguments` 必须一起传

`/launcher/launch` 的 `jre` 对象里有一份 `extraArguments`，是官方启动器
逐个版本下发的 JVM 参数。**1.21+ 少了会直接起不来**，26.x 更明显：

```
--add-modules jdk.naming.dns
--add-exports jdk.naming.dns/com.sun.jndi.dns=java.naming
-Dlog4j2.formatMsgNoLookups=true
--add-opens java.base/java.io=ALL-UNNAMED
-XX:+UseStringDeduplication
-XX:FlightRecorderOptions=repository=jfr
-Dichor.filteredGenesisSentries=.*lcqt.*|.*Some of your mods are incompatible with the game or each other.*
-Dlunar.webosr.url=file:index.html          ← 游戏内 Web UI 的入口，别漏
# 1.20.1+ 追加
-DTrickNvidiaDriversForPerformance=' net.minecraft.client.main.Main '
# 26.x 追加
-XX:+UseCompactObjectHeaders -XX:+UseZGC -XX:+AlwaysPreTouch
--enable-native-access=ALL-UNNAMED
--add-exports java.base/jdk.internal.misc=ALL-UNNAMED
-XX:StackShadowPages=32
```

### JRE 要按版本补齐

`jre.folderChecksum` 是 `~/.lunarclient/jre/<checksum>/` 的目录名，
`jre.download.url` / `fallbackUrl` 是 Zulu 的 zip，`executablePathInArchive`
是 zip 里 `javaw.exe` 的相对路径。Lunar 官方启动器**只下载你玩过的那个版本**的 JRE，
所以在 PCL 里换一个没玩过的版本，本机大概率缺这一份，必须自己下。

### 物料是共享目录，但文件名不区分版本

所有版本的附件都落在 `offline/multiver/`，文件名是固定的
（`common-0.1.0-SNAPSHOT-all-nomappings.jar`、`lunar.jar`…）。
换个版本启动时，同名文件会被**新版本的 SHA-1 覆盖**。
所以校验不能只看「文件在不在」——文件在但版本对不上，会在启动早期抛
`java.lang.NoSuchMethodError`（实测过：拿 1.8.9 的 `common` jar 去跑 1.21.4）。

## 账号：Lunar 只认自己的账户库

这是最容易误判的一处。命令行里确实有 `--username` / `--uuid` / `--accessToken`，
`ClientGameBootstrap` 也确实把它们转给了原版客户端，但**游戏进程真正的会话来自别处**：

`lunar.jar` 里有一个 AccountManager，它读
`~/.lunarclient/settings/game/accounts.json`，按 `activeAccountLocalId` 挑账号，
token 过期时自己刷新（日志里会出现
`The launcher refreshed the access token for the current account %s.`）。

所以：

- 在 PCL 里登录了 A 账号、Lunar 里激活的是 B 账号 → 进游戏是 **B**
- 想换账号，只能改 `activeAccountLocalId`（等同于在 Lunar 启动器里换号）
- 命令行里的凭据对 Lunar 来说是装饰品，不影响会话

实测日志里能看到这两行挨着出现，是最直接的证据：

```
[Client thread/INFO]: Setting user: TestPlayer      ← 原版客户端用了 --username
[Client thread/INFO]: [LC] Setting user: lyricify   ← Lunar 立刻用账户库里的账号覆盖
[WebSocketConnectReadThread/INFO]: [LC Assets] Connection established as lyricify (Online)
```

PCL Lite 的做法：读 `accounts.json` 列出账号让用户选，启动前把
`activeAccountLocalId` 同步过去（先备份 `accounts.json.pclbak`，只改这一个字段）。
Lunar 账户库为空时才回落到 PCL 主页面的登录凭据。

### ① 那个 `accessToken` 就是 Minecraft 令牌，只是 24 小时就过期

`accounts.json` 里的 `accessToken` 长得很像「Lunar 自己签的 JWT」，其实**不是**：

```json
{"xuid":"2535464508196445","auth":"XBOX","iss":"authentication","platform":"PC_LAUNCHER",
 "profiles":{"mc":"694cd52b-…"},"pfd":[{"type":"mc","id":"694cd52b-…","name":"lyricify"}],
 "aid":"4358653d-…","nbf":1793021199,"exp":1793107599}
```

`iss: authentication` 是 Mojang 的签发者，`aid` 是拿它时用的 Azure 应用 id
（Lunar 的启动器流程走 `api.minecraftservices.com/launcher/login`，所以带
`platform: PC_LAUNCHER`）。启动器自己也是拿它去
`POST https://sessionserver.mojang.com/session/minecraft/join` 建会话的 ——
见 `dist-electron/electron/main.js` 里 `executeWithAccessToken(t, e => Bke(e, u))`
那一段，`Bke` 就是 join 请求。

所以它是**真令牌**，能联机。唯一的限制是 `exp` 只有 24 小时，
`accessTokenExpiresAt` 就是它。

PCL Lite 的做法：优先用 PCL 主页面登录里同名账号的令牌（更新），
拿不到才退回账户库那份；如果那份已经过期，日志里会写明联机大概率失败。
账号名对不上时不能混用 —— 令牌和 UUID 是绑在一起的。

### ② 过期不自己刷，是找启动器要 —— 现在由 PCL 应答

`lunar.jar` 里有这几句日志和一个失败原因枚举：

```
Current account's access token has expired, refreshing...
Failed to refresh access token for account %s
Successfully refreshed access token for account %s
```

枚举项里有 **`LAUNCHER_NOT_OPEN`**（`launcher_not_open`），旁边还有
`ACCOUNTS_FILE_EMPTY`、`MISSING_REFRESH_TOKEN`、`NOT_PURCHASED_MINECRAFT`，
弹窗 key 是 `popups.refresh_auth`。对应的调用是 gRPC 风格的手写 protobuf：

```
com.lunarclient.gameipc.auth.v1.AuthService / RefreshAccount
  RefreshAccountRequest  { Uuid uuid }
  RefreshAccountResponse { bool success; AccountError error }
```

响应里**没有 token 字段**——刷新是服务端做的：它自己刷新、自己写
`accounts.json`，然后只回一个 `success`，游戏拿到结果后重读那个文件。

这条链路走的是本地 WebSocket，不是 HTTP/2：

```
ws://127.0.0.1:<--ipcPort>
请求头 lc-handshake: base64(Handshake{launch_id, process_id, installation_id})
信封   LauncherboundIPCMessage / GameboundIPCMessage，按 request_id 配对
```

Lunar 启动器固定监听 **28190**（`mVe=28190`），启动器没开时没人应答 →
游戏内账号面板显示 **Invalid session. Please re-authenticate.**

**1.5 起 PCL 自己当这个服务端**（`PCL.Core/App/Lunar/LunarIpcServer.cs`）：
启动前抢 28190，抢到了就由 PCL 应答 `RefreshAccount`；抢不到说明 Lunar 启动器
正开着，把端口原样交给游戏让它处理。端口被占但对方不响应 WebSocket 时，
传 `--ipcPort 0` 干脆不启用。

### ③ 为什么不能直接拿 `refreshToken` 刷

`accounts.json` 里的 `refreshToken` 是微软发的，但**绑在 Lunar 的 OAuth 应用上**：

```json
{"error":"invalid_grant","error_description":"AADSTS70000: ... The token was issued for a different client id"}
```

换 `login.live.com/oauth20_token.srf` 也是同一句。所以那个 `refreshToken`
对第三方没有用处。

PCL 的做法是绕开它：用 **PCL 自己登录**的微软账号（refreshToken 绑在 PCL 的
OAuth 应用上，能用）刷一个新的 Minecraft 令牌，写回 `accounts.json` 的
`accessToken` / `accessTokenExpiresAt`，`refreshToken` 一个字节都不动。

前提是 PCL 主页面登录的是同一个账号（按游戏内名字比对）。对不上就宁可失败 ——
把别人的令牌写进这条账号，联机会被服务器直接踢。

另一条路 `wss://authenticator.lunarclientprod.com/launcher-v3` 也能换到
Lunar 侧的 JWT（见下文「那套 WebSocket 认证」），但那是启动器自用的令牌，
和 `accounts.json` 里的不是一回事，续期用不上。

## 正版验证：启动不强制要求 PCL 登录，但联机需要真令牌

既然账号身份来自 Lunar 自己的账户库，**启动 Lunar 并不要求 PCL 先登录正版**。
命令行里的 `--accessToken` 传 32 个 `0` 也能把游戏拉起来。

```
--username <玩家名>
--uuid <无横线 UUID>
--xuid <Xbox User ID>
--accessToken <Minecraft accessToken>
--userProperties {}
```

但「能启动」和「能联机」是两回事：进别人的服务器时，服务器会拿客户端交上来的
令牌去 Mojang 校验，这时必须是**真正的 Minecraft accessToken**，而且必须属于
`--uuid` 那个账号。所以 PCL Lite 会优先用 PCL 登录里的同名账号令牌。

### 那套 WebSocket 认证是干什么的

启动器里另有一条 `wss://authenticator.lunarclientprod.com/launcher-v3`，
用 protobuf 做「RSA 密钥 → 让客户端用 MC token 调
`POST https://sessionserver.mojang.com/session/minecraft/join` → 换 JWT」的握手。
它的产物是 **launcher 自己的 JWT**，用于启动器侧的服务（资产服务器、
Styngr、社交），不是游戏启动的必需品。

协议细节（备查，`lunarclient.authenticator.v1`）：

```proto
HelloMessage             { UuidAndUsername identity = 1; string initiator = 2; }
EncryptionRequestMessage { bytes publicKey = 1; bytes randomBytes = 2; }
EncryptionResponseMessage{ bytes secretKey = 1; bytes publicKey = 2; }
AuthSuccessMessage       { string jwt = 1; }
ServerboundWebSocketMessage { hello = 1; encryptionResponse = 2; encryptionFail = 3; }
ClientboundWebSocketMessage { encryptionRequest = 1; authSuccess = 2; }
```

请求头：`Accept: application/x-protobuf`、`X-Initiator: <用途>`、
`User-Agent: Lunar Client Launcher v<版本>`；`initiator` 取值如
`LAUNCHER_WEBSOCKET` / `LAUNCHER_STYNGR_JWT` / `LAUNCHER_SOCIAL_LINK`。

`serverId` 的计算就是 Minecraft 标准的 server hash：
`sha1(sharedSecret + publicKey)` 取十六进制，最高位为 1 时补负号。

## 命令行拼装

**最容易写错的地方**：Lunar 不用原版那套「classpath 全塞 `-cp`」的玩法。
Genesis 是引导层，它自己建类加载器，真正的类路径通过
`--ichorClassPath` 传进去（逗号分隔的**文件名**，不是绝对路径），
额外的资源文件走 `--ichorExternalFiles`。`-cp` 只负责让 JVM 能找到
Genesis 主类本身。

1.8.9 走 legacy 路径（版本 json 里没有 `arguments.jvm`），JVM 参数由启动器自己拼：

```
-Xmx{内存}m
-Djava.library.path={natives 目录}
-Dlog4j.configurationFile={logs}/game/config.xml
-Dichor.logsFile={logs}/game/ichor-boot.log
-Dichor.usingIsolatedProfiles=true
-XX:+DisableAttachMechanism
-XX:-CreateCoredumpOnCrash
-XX:-CreateMinidumpOnCrash
{jre.extraArguments 原样展开}
-cp {CLASS_PATH 的绝对路径，用 ; 连接}
com.moonsworth.lunar.genesis.Genesis
```

`jre.extraArguments` 的位置在内存参数之后、`-cp` 之前。1.8.9 是 9 条，
26.3 是 15 条，照抄即可，不要自己删减——`-Dlunar.webosr.url` 关系到游戏内 UI。

然后是 Genesis 自己的参数：

```
--ichorClassPath      {7 个 CLASS_PATH 文件名，逗号分隔}
--ichorExternalFiles  {50 个 EXTERNAL_FILE 文件名，逗号分隔}
--version 1.8.9
--launcherVersion <启动器版本>
--launcherFeatureFlags {"enabled":[],"disabled":[]}
--installationId <guid>
--overwolfMuid "not specified"
--sentryTraceId "not specified"
--launchId <uuid>
--canaryToken <响应里的 canaryToken>
--username ... --uuid ... --xuid ... --accessToken ... --userProperties {}
--assetIndex 1.8
--gameDir <实例目录>
--assetsDir <shared>/assets
--texturesDir <textures 目录>
--uiDir <ui 目录>
--webosrDir <natives 目录>
--workingDirectory .
--classpathDir <fileCacheRoot>
--width <宽> --height <高> [--fullscreen true]
--server <自动进服地址>
--ipcPort <端口>
```

### 几个会直接崩启动的坑

**① `--launcherFeatureFlags` 必须是 `{"enabled":[],"disabled":[]}`。**
启动器里是 `JSON.stringify({enabled: R, disabled: ie})`。Genesis 在
`Main.parse` 的注入点里直接 `get("enabled").getAsJsonArray()`，传 `{}` 会抛：

```
java.lang.NullPointerException: Cannot invoke "JsonElement.getAsJsonArray()"
because the return value of "JsonObject.get(String)" is null
    at ...registerFromJson(Unknown Source)
    at net.minecraft.client.main.Main.redirect$zde000$lunar$main$parse(SourceFile:610)
```

**② `--assetIndex` 的值不在 `/launcher/launch` 响应里。**
要去 `/launcher/metadata/versions/lunar` 的
`versions[].subversions[].assets.id` 取（1.8.9 → `1.8`，1.21.4 → `19`）。启动器把它存进
store 的 `subversionAssets`，用的时候 `subversionAssets[subVersion]` 直接查。
传空串会让 Genesis 拿不到资源索引。

**③ 物料必须按 SHA-1 校验，不能只看文件在不在。**
`offline/multiver/` 是全部版本共用的目录，文件名固定，换版本会被覆盖。
文件在、但版本不对时，启动早期抛 `NoSuchMethodError`：

```
java.lang.NoSuchMethodError: 'java.util.List com.moonsworth.lunar.ichor.CHHCRRRCCHCHOHCIHIHCIOHRORHCRC.RIORIOCIHICOOHOHICOHHHIOIRHOIH()'
```

实测拿 1.8.9 的 `common-0.1.0-SNAPSHOT-all-nomappings.jar` 跑 1.21.4 就是这样，
按 SHA-1 重下 15 个文件后正常启动。设置里的「启动 Lunar 前校验物料文件」
关掉之后只判断存在性，**跨版本切换时不要关**。

**④ `--uiDir` 要指到 `ui/<sourceSha1>/` 这一层。**
只给 `ui/` 的话 Genesis 找不到 `index.html`，游戏内菜单整块不渲染
（HUD 与模组还在，很容易误判成「菜单功能没做」）。

**⑤ `--gameDir` 要和主页面正常启动用同一个目录。**
正常启动用的是实例的版本隔离目录（`PathIndie`），不是 `.minecraft` 根目录。
Lunar 写错地方会把存档、`options.txt`、`mods` 和普通实例混在一起。

## 还没解决的点

1. **`--ipcPort` 只实现了 `AuthService`**：启动器会在该端口起一个 WebSocket 服务
   （实测默认 28190），注册 9 个 protobuf 服务（社交、皮肤、设置同步、Tebex 等）。
   PCL 只实现了 `AuthService/RefreshAccount`，其余调用一律回空响应，
   代价是好友/社交/实时设置同步仍然不可用。游戏本体、单人、多人、模组都正常。

   传 `0` 时游戏**不会卡住**，只是每 5 秒重试一次并打日志：

   ```
   [Bridge] Found IPC port: 0
   [WebSocketConnectReadThread-78] ERROR Lunar Client - [LC IPC] WS-Error: Cannot assign requested address: connect
   ```

2. **`canaryToken`**：实测返回 `control:<hash>,<hash>` 形式，看起来是
   防篡改/灰度开关。原样透传即可，实测未触发校验。

## 实测记录

- `POST /launcher/launch`（无任何 Authorization 头）→ HTTP 200，
  `success: true`，返回 61 个 artifact、主类、JRE、textures、ui、canaryToken
- `GET /launcher/metadata/versions/lunar?branch=master&installation_id=<guid>`
  → HTTP 200，10 个版本，1.8.9 是 1.8 的默认子版本。
  **缺 `installation_id` 会 400**（zod 校验 `invalid_type`）。
- `installation_id` 必须是合法 UUID，第三段首字符 `1-8`、第四段首字符 `89ab`。
  `11111111-1111-1111-1111-111111111111` 会被拒。
- 用假 token（`"0"`）真实启动 1.8.9：

  ```
  IchorPipeline: Loading Mixin runtime for MIXIN parent classloader: GenesisClassLoader(v1_8)
  Genesis/Baker: Found 3825 classes to bake.
  LUNARCLIENT_STATUS_PREINIT
  Genesis/GameBootstrap: Starting game!
  Genesis/GameBootstrap: Commit Hash: 9ad7631e7190dca388ad176e879a77fa245d1596
  Genesis/Baker: Baked 19064 classes in 21056ms.
  net.minecraft.client.Minecraft 初始化完成，进程持续存活
  ```

  `Completely ignored arguments` 里出现 `--ichorClassPath` / `--ichorExternalFiles` /
  `--workingDirectory` / `--classpathDir` / `--xuid` 是**正常现象**：
  这几个是 Genesis 自己消费的，原版 `Main` 看到不认识就打印出来。

- 1.8.9 用 **Zulu 17**，`jre.folderChecksum` 直接就是
  `~/.lunarclient/jre/<checksum>/` 的目录名，里面一层
  `<发行版>-<版本>/bin/javaw.exe`。
- 资源索引本地已存在 `shared/assets/indexes/1.8.json`，objects 是 256 个哈希前缀目录。
  PCL 只补索引文件，不拉几万个资源对象。

### 多版本实测（2026-10-26）

用 `tools/lunar_launch_test.py <版本>` 按 PCL 的拼装逻辑直接拉进程，
假 token、70 秒观察期：

| 版本 | JRE | 附件 | 结果 |
| --- | --- | --- | --- |
| 1.8.9 | Java 17 | 61 | 存活，`Baked 19064 classes` |
| 1.21.4 | Java 21 | 68 | 存活，`GenesisClassLoader(v1_21_4)`，MixinExtras 0.5.5 加载完成 |
| 26.3 | Java 25 | 66 | 存活，`GenesisClassLoader(v26_3)` |

1.21.4 第一次跑时抛 `NoSuchMethodError`（`common` jar 还是 1.8.9 那份），
按 SHA-1 重下 15 个文件后正常——这条就是上面「坑 ③」的由来。

脚本本身也留着，改协议时可以直接回归：

```bash
python tools/lunar_launch_test.py 1.21.4
```


