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

## 安装阶段全清单（从零装完 Lunar Client）

官方启动器把「准备物料」拆成若干 stage，**每个 stage 一个 <code>super(`&lt;名&gt;`)</code>**。
在 `app.asar` 里 `grep -o 'super(\`[a-z-]*\`'` 能全抓出来，一共 23 个：

```
artifacts assets jre natives ui textures libraries licenses jar-validation
modpack lcpack dawnpack processors optifine-patcher assetserver gameipc
overwolf promotions styngr checkout updater window
```

真正在「启动前准备物料」这一步跑的是其中 7 个，顺序固定：

| stage | 落点 | 索引 / 清单来源 | 备注 |
| --- | --- | --- | --- |
| `licenses` | `~/.lunarclient/licenses/` | 响应顶层 `licenses[]` | 每项 `{file, sha1, url, size}`，平铺 |
| `textures` | `~/.lunarclient/textures/` | `textures.indexUrl` | 索引式，见下 |
| `assets` | `~/.lunarclient/shared/assets/` | `versions[].subversions[].assets` | Mojang 原版索引 + objects |
| `artifacts` + `natives` | `~/.lunarclient/offline/multiver/`<br>`…/multiver/natives/` | `launchTypeData.artifacts[]` | natives 是 zip，解到 `natives/` |
| `jre` | `~/.lunarclient/jre/<folderChecksum>/` | `jre.download` | `url` 失败再用 `fallbackUrl` |
| `ui` | `~/.lunarclient/ui/<sourceSha1>/` | `ui.sourceUrl` + `ui.assets` | 先解 zip，再跑一次索引式 |
| `logs` | `~/.lunarclient/logs/game/config.xml` | 内置模板 | 见下 |

**`textures` / `ui.assets` 是「索引式」物料源**，不是 JSON：

```
GET <indexUrl>          → 纯文本，每行：<相对路径> <sha1> [字节数] [mtime]
GET <baseUrl><sha1>     → 单个文件
```

官方启动器的 `indexed-downloader`（`O3` 类）解析时只取前两列，
索引缓存在 `launcher-cache/texturesIndex.txt`、`launcher-cache/uiIndex-<sha1>.txt`。
路径安全性检查是必须的：绝对路径、盘符、含 `..` 的条目会被判成
`INDEX_UNSAFE_PATH` 直接抛异常。

**`logs/game/config.xml` 只有启动器会写**，是个 1276 字符的 log4j2 模板，
只有两个占位符：`absolute-path-here` → `latest.log`、`gz-absolute-path-here` →
`%d{yyyy-MM-dd}-%i.log.gz`。`-Dlog4j.configurationFile` 指着它，
文件不存在时 log4j2 退回默认配置，结果是 `latest.log` 里一条游戏日志都没有。

> **PCL 侧对应实现**：`PCL.Core/App/Lunar/LunarInstaller.cs`。
> 1.5.2 起这 7 个阶段全部由 PCL 自己跑，`~/.lunarclient` 是空的也能从零装完，
> 不再要求「先装一次 Lunar 启动器」。
>
> 原版相关的地址（`resources.download.minecraft.net` 的 objects、
> `launchermeta.mojang.com` 的资源索引）走 `PCL.Core/IO/Net/DownloadSource.cs`，
> 与「设置 → 下载 → 文件下载源」**完全同一份实现**：
> 优先镜像时 BMCLAPI 在前，优先官方时官方在前，自动模式看官方源探测耗时。
> Lunar 自己的 CDN（multiver 物料、textures、ui、licenses、jre）没有镜像，
> 只能直连。

## 首次启动某个版本：Genesis 会先下载整个原版客户端

**这是「进程活着但窗口永远不出来」的头号原因，跟 loader 选错、跟物料缺失都无关。**

`ClientGameBootstrap.apply()` 里有一句 `...download(...)`，走的是 Java 的
`java.net.http.HttpClient`（`-Djdk.httpclient.HttpClient.log=requests` 能看到完整 URL）。
它把目标版本的原版客户端补到 **`%APPDATA%\.minecraft`**：

| 内容 | 实测大小 |
| --- | --- |
| `versions/<ver>/<ver>.jar` | 1.17.1 是 19.5 MB，1.16.5 是 17.5 MB |
| `versions/<ver>/<ver>.json` | 30 KB |
| `libraries/**` | 上百个文件，几十 MB |
| `assets/indexes/*.json` + objects | 视版本而定 |

三个必须知道的事实：

1. **它写死用标准 `.minecraft`**，和命令行里的 `--gameDir` 无关。
   证据：1.17.1 的 jar 落在 `%APPDATA%\.minecraft\versions\1.17.1\`，
   而同一时刻 `options.txt` / `logs/latest.log` 的 mtime 还是旧的 —— 游戏数据确实用的
   是 `--gameDir`，只有原版客户端这一份去标准目录找。
2. **全程没有窗口、没有进度、ichor-boot.log 也停在 `Launching Minecraft <ver>`**。
   从外面看就是「双击了没反应」，实测被用户当成卡死在下载到一半时杀掉过。
3. **下载完就永久好了**。1.8.9 的 jar 早就在（07-14），所以它一直没出问题；
   1.17.1 是第一次启动，于是「只有 1.17.1 打不开」。

所以 PCL 现在的做法是：启动前先看 `versions/<ver>/<ver>.{jar,json}` 在不在，
不在就把状态文案改成「正在下载原版 X 的依赖…」并说明会下到哪；
启动后每 20 秒量一次 `libraries` + `versions` 的体积，涨了就报一行进度继续等，
**连续 100 秒不涨**才判定网络卡住并提示配代理。

### 代理必须手动注入 JVM

Genesis 那次下载用的是 `java.net.http.HttpClient`，**不读系统代理，也不读
`java.net.useSystemProxies` 之外的任何东西**。直连被墙时 TCP 能连上、HTTP 应答永远不来，
主线程就永久阻塞在 `HttpClientImpl.send()`：

```
"main" ... WAITING (parking)
  at jdk.internal.net.http.HttpClientImpl.send(...)
  at com.moonsworth.lunar...download(...)
  at com.moonsworth.lunar.genesis.ClientGameBootstrap.apply(...)
  at com.moonsworth.lunar.genesis.Genesis.run(...)
```

所以 `BuildStartInfo` 里必须把 PCL 当前生效的代理翻成
`-Dhttp.proxyHost/-Dhttps.proxyHost`（`LunarClientService.BuildProxyArguments`）。
没有显式代理时退回 `-Djava.net.useSystemProxies=true`。
本机回环一定要在 `-Dhttp.nonProxyHosts` 里放过 —— 游戏要连 `127.0.0.1:<ipcPort>`
做会话续期。

实测游戏只连三个域名：

```
api.lunarclientprod.com            接口
thirdpartycache.lunarclientprod.com 第三方缓存（tier-tagger 等）
analytics.lunarclientprod.com      遥测（国内常连不上，失败只是刷 WARN，不影响启动）
```

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

### loader 选择（1.5.5 起）

`subversions[].modules[]` 就是「这个版本能选哪些 loader」，键是
`id / name / description / default / sort / loaders[]`。实测全集四个：

| id | 名字 | 说明 |
| --- | --- | --- |
| `lunar` | Lunar + OptiFine | Lunar 自带 OptiFine 的那套（1.8.9 的默认） |
| `lunar-noOF` | Lunar | 不带任何额外 mod 的原版 |
| `fabric` | Lunar + Fabric | 1.17.1 的默认 |
| `forge` | Lunar + Forge | 老版本才有 |

**默认值每个版本不一样**，1.17.1 是 `fabric`，1.8.9 是 `lunar`。
1.5.4 及以前 PCL 写死 `module = "lunar"`，等于在 1.17.1 上走了一条官方默认之外的分支。

同一个版本换 module，只有附件清单变，其余完全一致：

```
module=lunar   61 个附件（含 OptiFine_v1_17_1.jar、optifine-0.1.0-SNAPSHOT-all.jar）
module=fabric  63 个附件（含 lunar-fabric-mixins-0.1.0-SNAPSHOT-all.jar 等）
两者 mainClass / jre.folderChecksum / jre.extraArguments 完全相同
```

所以切换 loader 不需要换 JRE，也不需要改命令行结构，只换 `module` 字段和附件清单。
解析顺序是 `配置里点过的 → 该版本官方默认 → lunar`；配置里那个如果当前版本不提供，
**退回默认但不改配置**（切回来选择还在）。

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

PCL Lite 的做法：账号下拉框分两组列出「PCL Lite 登录账号」和「Lunar 启动器账号」，
选 Lunar 那条时启动前把 `activeAccountLocalId` 同步过去（先备份 `accounts.json.pclbak`，
只改这一个字段）；选 PCL 那条时，如果账户库里有同一个 UUID 的账号，也一并切过去，
没有就只换命令行令牌（游戏内身份仍由 Lunar 决定，日志里会写明）。
Lunar 账户库为空时（从没装过、或装了但没在 Lunar 启动器里登录过），直接回落到
**PCL Lite 里已登录的微软账号**，不需要先跑一遍主页面登录加载器；账户库缺失时
会按这个账号新建一份 `accounts.json`（已有内容一律不动）。
只有 PCL 里也一个账号都没有，才兜底走一次主页面登录。

列 PCL 账号只是读本地档案（`ProfileService.Profiles`），**不会触发任何授权流程**，
不会重新登录。只有「启动时发现这个档案的令牌过期」才会静默刷一次
（`MicrosoftProvider.RefreshAsync`，用档案里已有的 `refreshToken`，同样不弹窗）。

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

### ②-1 请求里的 UUID 是账户库里的 `minecraftProfile.id`

游戏侧是这么拼的（`lunar.jar` → `com.moonsworth.lunar.client.util.ICCOCOHOCCOROROIRHCCRHICOOOHHC`
的 `IOOCCRIRHHROHIHRCHHRRIOICHIOIR` 方法）：

```java
RefreshAccountRequest.newBuilder()
  .setUuid(toProtoUuid(UUID.fromString(withDashes(account.getProfile().getId()))))
```

`Uuid` 是 `{ fixed64 high64 = 1; fixed64 low64 = 2; }`，取值就是
`UUID.getMostSignificantBits()` / `getLeastSignificantBits()`——和 Java 的
UUID 文本一一对应，没有大小端花样（`high64` 就是前 16 个十六进制字符）。

1.5.1 起匹配按这个顺序走，只要有一条命中就续期：

1. `minecraftProfile.id`
2. `localId`（万一哪天协议改传这个）
3. **本次启动用的那个账号** —— 启动前已经把 `activeAccountLocalId` 切成它了，
   游戏问「当前账号」时指的必然是它

第 3 条是 1.5.0 报「账户库里没有这个 UUID」的兜底：账户库被 Lunar 启动器改过、
或者文件正好写到一半，UUID 就可能对不上。命中兜底时日志里会写明。
另外账户库读出来是空的话会隔 200ms 重读一次 —— 游戏退出前会连续刷好几个 json，
撞上写一半的窗口并不稀奇。

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

## 内存与 JVM 参数（1.5.2 起可配）

Lunar 的 JRE 是它自己按版本下发的（1.8.9 要 17、1.21 要 21、26.x 要 25），
内存需求跟原版实例不是一回事，所以这套设置**独立于** `Config.Launch`。
但默认仍然跟随 PCL 的全局内存，免得用户以为改了全局却没作用。

分层顺序（`PCL.Core/App/Lunar/LunarLaunchProfile.cs` 里的 `Resolve`）：

```
版本覆盖 → Lunar 全局 → PCL 全局 → 内置默认（2048 MB）
```

配置项都在 `Config.Preference.Lunar` 下：

| 配置项 | 含义 |
| --- | --- |
| `MemoryMb` | Lunar 的堆上限（MB）。`0` = 跟随 PCL 全局 |
| `JvmArgs` | 全局附加 JVM 参数 |
| `JavaPath` | 覆盖 Lunar 自带的 JRE。文件不存在时自动忽略 |
| `VersionOverrides` | 按版本覆盖，JSON：`{"1.8.9":{"on":true,"mem":4096,"jvm":"..."}}` |

### 用户参数的插入位置很关键

`BuildStartInfo` 里的顺序是：

```
内置默认（-Xmx / -Djava.library.path / -Dichor.* …）
  → 用户参数（ExtraJvmArguments）
  → 官方按版本下发的 JVM 参数（data.JvmExtraArguments）
```

**1.5.2 把用户参数挪到了最后。** 原来它在官方参数之前，结果是用户写的
`-XX:+UseZGC`、`-Xmx8G` 会被官方值盖掉 —— JVM 对重复参数取**最后一次**。
挪到末尾之后用户设置才真正有最高优先级。

### 参数必须切分成独立元素

Lunar 是往 `ProcessStartInfo.ArgumentList` 里逐项塞的，
整串塞进去会被当成一个参数，JVM 直接报 `Unrecognized option`。
`LunarLaunchProfile.SplitArguments` 负责切：引号内的空格不切，
`\"` 视为字面引号，`""` 算一个空参数。

## IPC 服务支持矩阵

游戏进程连上 `--ipcPort` 之后，能向启动器发起的 RPC 分布在 9 个服务里。PCL 侧一共应答
32 个方法名（下表），其中 22 个是官方启动器服务端真正实现过的 —— 剩下那些属于
启动器 → 游戏方向，游戏不会调用，PCL 也备了默认答复以防万一。
这张表是从官方启动器的 `app.asar`（`dist-electron/electron/main.js`）里反编译出来的：
把所有 `class extends HY { constructor(e) { super(<ns>.XxxServiceServiceName, e) } }`
抓出来得到服务端实现，再从配套的 protobufjs 生成代码里取 RPC 名与字段号。

> `lunarclient.websocket.*` 那一批（Friend / Cosmetic / Store / Notification…）
> 是**启动器连自家后端**用的，不走本地 IPC，跟 `--ipcPort` 无关。

图例：✅ 真做了 · 🟡 明确降级（有响应、行为确定，只是没有对应界面）· ❌ 未实现

| 服务（`lunarclient.gameipc.` 前缀） | RPC | PCL | 说明 |
| --- | --- | --- | --- |
| `auth.v1.AuthService` | `RefreshAccount` | ✅ | 用 PCL 登录的微软账号刷令牌写回 `accounts.json` |
| `auth.v1.AuthService` | `AddAccount` | 🟡 | 把窗口切到启动页并提示去登录，回 `success = false` |
| `browser.v1.BrowserService` | `OpenUrl` | ✅ | 用系统默认浏览器打开（只放行 http / https） |
| `chat.v1.ChatService` | `ReceiveMessage` | 🟡 | 落到日志，PCL 没有启动器聊天面板 |
| `launch.v1.LaunchService` | `LaunchGame` | 🟡 | 解析版本 / 模块 / 服务器并提示，**不自动杀进程重启** |
| `location.v1.LocationService` | `UpdateFocus` | 🟡 | 记日志 |
| `location.v1.LocationService` | `UpdateLocation` | 🟡 | 记日志（嵌套的 `Location` 定义在别的包里，不解析） |
| `promotion.v1.PromotionService` | `CheckPendingPromotion` | 🟡 | 回「没有待播活动」 |
| `promotion.v1.PromotionService` | `PlayPromotion` / `Login` / `Start|EndPromotionView` / `Claim*` | 🟡 | 空响应 |
| `tebex.v1.TebexService` | `CheckTebexCapabilities` | 🟡 | 回 `embeddedCheckout = UNSPECIFIED`（不支持） |
| `tebex.v1.TebexService` | `OpenTebexJsCheckout` | 🟡 | 回 `STATUS_INTERNAL_ERROR`，游戏走自己的降级提示 |
| `tebex.v1.TebexService` | `TebexJsCheckoutOpened` / `Closed` | 🟡 | 空响应 |
| `paynow.v1.PayNowService` | `CheckPayNowCapabilities` | 🟡 | 同 Tebex |
| `paynow.v1.PayNowService` | `OpenPayNowJsCheckout` | 🟡 | 同 Tebex |
| `paynow.v1.PayNowService` | `PayNowCheckoutOpened` / `Closed` | 🟡 | 空响应 |
| `styngr.v1.StyngrRadioService` | `FetchState` | 🟡 | 回「未播放 + 已静音」，界面显示不可用而不是转圈 |
| `styngr.v1.StyngrRadioService` | `FetchPlaylists` | 🟡 | 空列表 + `premium = false` |
| `styngr.v1.StyngrRadioService` | `SetMuted` / `SetPlaying` / `SetVolume` / `NextTrack` / `ToggleLiked` / `StartPlaylist` / `OpenSelector` | 🟡 | 接受但无操作 |

实现在 `PCL.Core/App/Lunar/LunarIpcDispatcher.cs`（协议 → 动作）和
`Plain Craft Launcher 2/Modules/Lunar/LunarIpcHost.cs`（动作 → UI）。

### 字段号（从 `app.asar` 的 protobufjs 生成代码里提取）

只列 PCL 用到的那几个，都是反编译出来的，不是猜的：

```
OpenUrlRequest            { string url = 1; Initiator initiator = 2; OpenType openType = 3; }
LaunchGameRequest         { string version = 1; string module = 2; string autoJoinServerIp = 3;
                            LaunchSource launchSource = 4; PlayerModpack modpack = 5; }
UpdateFocusRequest        { bool focused = 1; }
ReceiveMessageRequest     { UuidAndUsername sender = 1; string content = 2; }
CheckTebexCapabilitiesResponse { EmbeddedCheckout embeddedCheckout = 1; }
OpenTebexJsCheckoutRequest  { string basketIdent = 1; string locale = 2; }
OpenTebexJsCheckoutResponse { Status status = 1; bool openMinecraftScreen = 2; }
FetchStateResponse        { StyngrTrackState track = 1; StyngrControlsState controls = 2;
                            StyngrProgressState progress = 3; }
StyngrControlsState       { bool playing = 1; bool muted = 2; float volume = 3; }
FetchPlaylistsResponse    { repeated Playlist playlists = 1; bool premium = 2; }
```

枚举：

```
OpenUrlRequest_OpenType   UNSPECIFIED=0  FORCE_EMBEDDED=1  FORCE_EXTERNAL=2
OpenUrlRequest_Initiator  UNSPECIFIED=0  COSMETICS=1  ALERT=2  AUTH=3  BLOG=4
                          SOCIAL_LINK=5  HOME_BUTTON=6  LINK_CONFIRM=7
                          SCREENSHOT_UPLOAD=8  HOME_CTA=9  STORE_PREVIEW=10
EmbeddedCheckout          UNSPECIFIED=0  OVERWOLF_OVERLAY=1  ELECTRON_WINDOW=2
Tebex/PayNow Response.Status  UNSPECIFIED=0  OK=1  INTERNAL_ERROR=2
LaunchGameRequest_LaunchSource  UNSPECIFIED=0  MAIN_MENU_SWITCHER=1
                                SERVER_KICK_PROMPT=2  SERVER_MODPACK_RECOMMENDATION=3
```

**注意 protobuf 的「等于默认值就不编码」**：空 `output` 解出来就是全默认实例。
所以能力探测类请求回空字节 = 回 `0` = 不支持，这是**有意义的响应**，不是偷懒。
反过来 `OpenTebexJsCheckoutResponse.status` 要的是 `2`，不写就是 `0`，
游戏会以为还在处理 —— 这类非默认值必须显式编码。

### 反方向：启动器 → 游戏（`push_notification`）

`GameboundIPCMessage { push_notification = 2 }` 里的 `google.protobuf.Any`：
`type_url = "type.googleapis.com/" + 消息全名`，`value` 是消息体。
游戏端在 `onOpen` 里把类型注册进一张 `Map<Class, Consumer>`，收到后按类型找处理器；
**找不到就只打一条日志然后丢掉** —— 所以推一条游戏不认识的消息绝对安全，只是没有效果。

判断「这个推送有没有用」不能只看 `app.asar` 里有没有消息定义，得看 `lunar.jar` 里
有没有注册处理器。实测（`javap` 反编译 `com.moonsworth.lunar.client.*` 的 IPC 客户端）：

| 推送 | 游戏端处理器 | PCL |
| --- | --- | --- |
| `launch.v1.ExitGamePush` | `bridge$shutdownMinecraftApplet()` —— 走 Minecraft 正常关闭，存档落盘 | ✅ 停止游戏时先发它 |
| `location.v1.FocusGamePush` | 把游戏窗口切到前台 | ✅ 接在「回到游戏」按钮上 |
| `launch.v1.PlayServerPush` | 连服务器；**当前没有 session 就只打日志忽略** | 🟡 API 已就绪，PCL 没有服务器列表页，暂未接入口 |
| `chat.v1.SendChatPush` | **没有注册处理器**，只有消息定义 | ❌ 发了等于没发，没实现 |
| `promotion.v1.PendingPromotionUpdatePush` | 更新推广状态 | ⬜ PCL 没有推广模块 |
| `store.v1.StartStorePreviewSessionPush` | 开商店预览会话 | ⬜ PCL 没有商店 |
| `tebex.v1.TebexJsCheckoutClosed` / `paynow.v1.PayNowJsCheckoutClosed` | 关掉结账界面 | ⬜ PCL 从不打开结账界面 |
| `styngr.v1.StyngrTrack*Update` | 推电台播放状态 | ⬜ PCL 没有电台 |

字段号（同样是反编译出来的）：

```
Any                       { string type_url = 1; bytes value = 2; }
ExitGamePush              { }                       // 空消息
FocusGamePush             { }                       // 空消息
PlayServerPush            { string autoJoinServerIp = 1; }
SendChatPush              { string message = 1; }    // 游戏端无处理器
StartStorePreviewSessionPush { string basketIdent = 1; int32 packageId = 2; string currency = 3; }
PendingPromotionUpdatePush { bool hasPendingPromotion = 1; repeated int32 pendingPromotions = 2; }
```

官方启动器「结束游戏」和「回到游戏」的真实路径（`dist-electron/electron/main.js`）：

- **结束游戏**：启动时给 spawn 出来的子进程挂
  `abortController.signal.addEventListener('abort', () => { stdout.destroy(); stderr.destroy(); child.kill(); })`
  —— 点退出就是**直接杀进程**，既不发 `ExitGamePush`，也不等存档落盘。
  整个 asar 里 `ExitGamePush` 只出现在 protobuf 定义块，一处 `sendPush` 调用都没有。
- **回到游戏**：`ipcMain.handle('misc:focusGame')` 里只有一句
  `gameipc.sendPush(FocusGamePush.create({}))`，没有任何 Win32 动作。

> **PCL 现在与官方完全一致**：`Stop()` 直接 `process.Kill()`；
> `FocusGameAsync()` 只发一条 `FocusGamePush`，发出去就算成功。
>
> 曾经试过两条「比官方更好」的路，都因为副作用撤掉了：
> ① 结束前先发 `ExitGamePush` 请游戏自己存盘、再按「吐日志 / 烧 CPU / 写盘」三个信号
> 判断它是在存盘还是卡死 —— 游戏没理推送时界面会卡在「正在保存存档」；
> ② 自己 `EnumWindows` 找窗口再 `SetForegroundWindow` 抬前台 —— 匹配不上时
> 只能报「未找到游戏窗口」，而官方的做法根本不需要知道窗口在哪。
>
> 结论：这两个功能**不要**再自己加逻辑。官方只靠 IPC 推送，PCL 也只靠 IPC 推送。

`HasGameWindow()` 还留着，但只给启动看门狗用（「窗口出来了就收工」），
不参与任何前台切换。判据是 `_process.Id` + 类名（`LWJGL` / `GLFW*` / `SunAwtFrame`）
+ 尺寸 ≥ 100×100。

`AuthService` 的 `NotifySwitchAccount` / `OpenMicrosoftPopup` 只有消息定义，
连官方启动器都没给它们绑定客户端方法，实测不会出现在 IPC 上，不用管。

## 后端通道（好友 / 私聊）

`lunarclient.websocket.*` 那一批服务**不走本地 IPC**，走的是 Lunar 自己的
WebSocket 后端。PCL 现在接上了其中两个：`FriendService` 和 `ConversationService`。

### 认证：拿 Minecraft 令牌换启动器 JWT

Lunar 后端不认 Minecraft 令牌，只认它自己签的 JWT。换法是一条三段式握手，
和 Mojang 的服务器加密握手一模一样，只是把「连服务器」换成了「连 Lunar」：

```
① 连 wss://authenticator.lunarclientprod.com/launcher-v3
   头：User-Agent: Lunar Client Launcher v<版本>、Accept: application/x-protobuf、
       X-Initiator: LAUNCHER_WEBSOCKET
   发 ServerboundWebSocketMessage { hello { identity { uuid, username }, initiator } }

② 收 ClientboundWebSocketMessage { encryptionRequest { publicKey, randomBytes } }

③ sharedSecret = random(16)
   serverId = sha1(sharedSecret ‖ publicKey) 的补码十六进制（Mojang 的 ServerHash）
   POST https://sessionserver.mojang.com/session/minecraft/join
        { accessToken, selectedProfile: <32 位无连字符 uuid>, serverId }   → 必须 204
   回 encryptionResponse { secretKey: RSA_PKCS1(sharedSecret),
                           publicKey: RSA_PKCS1(randomBytes) }

④ 收 authSuccess { encryptedJwt }，用 sharedSecret 做 AES-128-GCM 解密
   （nonce 12 字节 ‖ 密文 ‖ tag 16 字节）→ JWT
```

`AuthSuccessMessage` 有两个字段：`jwt`（1，明文，一般空）和 `encryptedJwt`（2）。
真正要用的永远是 2。

### 连资产服务器

```
wss://websocket.lunarclientprod.com/launcher-v3
头：Accept: application/x-protobuf（不发 User-Agent）
```

连上先发**一条裸的 `Handshake`**（没有信封），然后才是 RPC：

```
Handshake {
  MinecraftIdentity identity = 1 { UuidAndUsername player = 1; int32 type = 2;
                                   string authenticatorJwt = 3; ... }
  LauncherVersion launcherVersion = 2 { string semver = 1 }
  string installationId = 3
  string operatingSystem = 5        // "win32"
  string cpuArchitecture = 6        // "x64"
  Language language = 7 { string iso6393Code = 1; string minecraftCode = 2 }
  LauncherHandshake launcherHandshake = 9 { string environment = 1 ("production"); ... }
}
```

`identity.type` 是 `MinecraftIdentity_Type.TYPE_MICROSOFT = 2`。

> **`launcherVersion.semver` 填错会被直接踢**：关码 1008
> `Unsupported launcher version`。要填**官方启动器**的版本（当前 3.7.27，
> 从 `Lunar Client.exe` 的文件版本读），不是游戏日志里那个 `--launcherVersion`。

### 信封与 RPC

```
ServerboundWebSocketMessage { bytes request_id = 1; string service = 2;
                              string method = 3; bytes input = 4; }
ClientboundWebSocketMessage { WebSocketRpcResponse rpc_response = 1;
                              google.protobuf.Any push_notification = 2; }
WebSocketRpcResponse        { bytes request_id = 1; bytes output = 2; }
```

`request_id` 就是自增计数器的十进制字符串的 UTF-8 字节。`method` 是裸方法名
（`Login` / `GetConversations` / …），`service` 是全名：

```
lunarclient.websocket.friend.v1.FriendService
lunarclient.websocket.conversation.v1.ConversationService
```

### 好友与私聊用到的消息

```
friend.v1.LoginResponse   { int32 status = 1; repeated OfflineFriend offlineFriends = 2;
                            bool = 3; ...; repeated OnlineFriend onlineFriends = 11 }
friend.v1.OnlineFriend    { UuidAndUsername player = 1; int32 status = 2; Location location = 3;
                            string rankName = 13; string countryCode = 14 }
friend.v1.OfflineFriend   { UuidAndUsername player = 1; string rankName = 8 }   // 注意是 8，不是 13
common.v1.UuidAndUsername { Uuid uuid = 1; string username = 2 }
common.v1.Uuid            { fixed64 high64 = 1; fixed64 low64 = 2 }
```

`OnlineFriendStatus`：1 在线、2 离开、3 忙、4 隐身。
`Location` 是 11 分支的 oneof，只有 `publicServer`（名字在 2、地址在 3）、
`localServer`、`singlePlayer`、`minecraftRealms`、`inMenus`、`inLauncher`、`inGame`
这几个有展示价值。

```
conversation.v1.ConversationReference { Uuid friendUuid = 1; Uuid conversationReference = 2 }
conversation.v1.Conversation { ConversationReference conversationReference = 1;
                               repeated ConversationParticipant participants = 2;
                               int32 type = 3; ConversationMessage latestMessage = 14; ... }
conversation.v1.ConversationMessage { Uuid id = 1; Timestamp sentAt = 2;
                                      ConversationSender sender = 3;
                                      ConversationMessageContents contents = 4 }
conversation.v1.ConversationMessageContents { string plainText = 1; ... }
conversation.v1.CreateConversationRequest { int32 type = 1; repeated Uuid participantUuids = 2 }
conversation.v1.SendConversationMessageRequest { ConversationReference ref = 1;
                                                 ConversationMessageContents messageContents = 2 }
```

`ConversationType`：1 = 一对一（`CONVERSATION_TYPE_FRIEND`）、2 = 群聊。
`SendConversationMessageResponse.status`：0 未指定、**1 成功**、2 会话不存在、
3 内容不合适、4 超长。

### 踩过的坑

- **`LoadConversation` 只按会话 id 找**，只给 `friendUuid` 会返回 0 条。
  所以顺序必须是：`GetConversations` → 拿 id → `LoadConversation`；
  第一次跟某个好友说话要先 `CreateConversation`（已存在时服务端会把原来那个还回来）。
- **`Conversation.participants` 里包含自己**，要按自己的 UUID 排掉才是对方。
- `GetConversations` 返回的 `ConversationReference` 只有会话 id，`friendUuid` 是空的，
  别指望从那里拿好友。
- 消息发送成功后服务端会把这条消息**当推送再回一遍**（`ConversationMessagePush`），
  客户端按「发送者是不是自己」渲染即可，不用去重。
- 认证服务和资产服务是**两条独立连接**：前者换完 JWT 就可以关，后者常驻。

## 还没解决的点

1. **`LaunchGame` 不自动重启**：官方启动器是先回响应、再延迟 1 秒调自己的
   `launch` 模块重启。PCL 没有强杀运行中游戏的道理（会丢未保存的世界），
   所以只解析参数、切到启动页、弹一条提示。想改成真重启，入口是
   `LunarIpcHost.RequestLaunchGame`。

2. **`--ipcPort 0`**：端口被别的程序占着又不响应 WebSocket 时才会传 `0`。
   这时游戏**不会卡住**，只是每 5 秒重试一次并打日志：

   ```
   [Bridge] Found IPC port: 0
   [WebSocketConnectReadThread-78] ERROR Lunar Client - [LC IPC] WS-Error: Cannot assign requested address: connect
   ```

3. **`canaryToken`**：实测返回 `control:<hash>,<hash>` 形式，看起来是
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
  **1.5.2 起 objects 也会补齐**：1.8 索引去重后是 722 个对象，
  以前只补索引文件、靠「装过 Lunar 启动器」蒙混过去，全新机器上是没贴图没声音的。
- 全新临时目录从零装 1.8.9 的实测体积与条数见下（1.5.2 验证）。

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


