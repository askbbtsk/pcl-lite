# Lunar Client 启动链路（1.8.9）

本文记录「不启动 Lunar 启动器、直接拉起 Lunar 游戏」所需的全部事实。
结论来自对本机 Lunar Client 启动器（Electron）`resources/app.asar` 的静态分析，
以及对着线上接口的实测，不是猜测。

## 一句话结论

Lunar 的 `/launcher/launch` 接口**不需要任何鉴权**，直接返回完整物料
（classpath、原生库、主类、JRE、贴图与 UI 资源）。正版验证走的是
Minecraft 自己的 `accessToken`，通过命令行参数传给游戏。
所以「用自己的启动器拉起 Lunar」是可行的，代价是实现物料下载与命令行拼装。

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

## 正版验证：走 Minecraft 自己的通道

游戏进程通过命令行拿账号信息，字段与官方启动器一致：

```
--username <玩家名>
--uuid <无横线 UUID>
--xuid <Xbox User ID>
--accessToken <Minecraft accessToken>
--userProperties {}
```

也就是说：**用 PCL 已有的微软登录拿到的 accessToken 直接传进去即可**，
不需要先向 Lunar 换 token。这也是「走 lunar client 原本的通道」的落点——
Lunar 认的就是这个 MC token。

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
-cp {7 个 CLASS_PATH 的绝对路径，用 ; 连接}
com.moonsworth.lunar.genesis.Genesis
```

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

### 两个会直接崩启动的坑

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
`versions[].subversions[].assets.id` 取（1.8.9 → `1.8`）。启动器把它存进
store 的 `subversionAssets`，用的时候 `subversionAssets[subVersion]` 直接查。
传空串会让 Genesis 拿不到资源索引。

## 还没解决的点

1. **`--ipcPort`**：启动器会在该端口起一个 WebSocket 服务（实测默认 28190），
   游戏连上来做握手，启动器注册 9 个 protobuf 服务（社交、皮肤、设置同步等）。
   传 `0` 时游戏**不会卡住**，只是每 5 秒重试一次并打日志：

   ```
   [Bridge] Found IPC port: 0
   [WebSocketConnectReadThread-78] ERROR Lunar Client - [LC IPC] WS-Error: Cannot assign requested address: connect
   ```

   代价是好友/社交/实时设置同步不可用。游戏本体、单人、多人、模组都正常。

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

