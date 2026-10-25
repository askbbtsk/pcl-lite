# 更新清单 `update.json`

## 它解决什么问题

启动器必须写死**某个**地址才能知道去问「有没有新版本」。
如果这个地址指向发布仓库本身，那么仓库一改名、一换组织、一转到别的账号，
所有已经装在别人电脑上的旧版本就**永久失联**了——它们只会对着一个 404 报错，
而且没有任何远程手段能把它掰回来，因为唯一的远程入口就是那个已经失效的地址。

所以这里把「地址」和「数据」拆开：

```
客户端（写死，永不改变）
    └──> bedkillerspacex-boop/PCL-Lite-service 的 update.json   ← 唯一锚点，只有一行指针
             └──> 当前发布仓库 / releases/latest                ← 随时可以改
                      └──> Release 自己提供版本号、资产、直链、SHA-256
```

客户端只认 `update.json` 的地址。以后要换发布仓库，改那一行就行，
所有还在跑老版本的机器下次检查更新时自然会走到新地址。

## 地址

| 用途 | 地址 |
| --- | --- |
| 主通道 | `https://raw.githubusercontent.com/bedkillerspacex-boop/PCL-Lite-service/main/update.json` |
| 镜像通道 | `https://cdn.jsdelivr.net/gh/bedkillerspacex-boop/PCL-Lite-service@main/update.json` |
| API 通道 | `https://api.github.com/repos/bedkillerspacex-boop/PCL-Lite-service/contents/update.json` |

> **2026-10 迁址记录**：锚点原在 `Pcl-Lite` 组织下（`Pcl-Lite/Pcl-Lite-service`）。
> 该组织被 GitHub 标记隐藏后，组织下所有仓库对匿名访问全部 404，
> 锚点和 Release 同时失联。锚点已迁到个人账号 `bedkillerspacex-boop`。
> 已经发出去的 1.3.1 里写死的是旧地址，无法远程修正，必须重发一版。

代码里的定义在 `PCL.Core/App/ProjectLinks.cs` 的
`UpdateManifest` / `UpdateManifestMirror` / `UpdateManifestApi`。

### 实际拉取方式：并发抢答

国内直连 `raw.githubusercontent.com` 基本不通，所以清单是**同时**向下面这些地址发请求，
**谁先回来用谁**，拿到就取消其余请求：

1. GitHub Contents API（`api.github.com`，和后面取 Release 同一个域名，常常最快）
2. `raw` 直连
3. `raw` + 每个 GitHub 反代（复用 `UpdatesGitHubModel.DownloadProxies`，
   目前是 `cors.isteed.cc` / `gh-proxy.com` / `gitproxy.click`）
4. jsDelivr CDN

每个通道单独 6 秒超时、不重试。串行试的写法在国内很难受：排在前面的通道一旦被墙，
要等超时才换下一个，用户看到的就是「检查更新转圈十几秒」。并发抢答的代价只是多发几个
小请求，换来的是「最快那条通道的延迟」——正常几百毫秒出结果。

全部失败才回退到「直接读当前仓库的 Releases」。反代列表只有一处定义，
失效时改 `DownloadProxies` 即可，下载和清单同时生效。

### 什么算「这条通道失败」

通道失败有两种，判定都在客户端：

1. **HTTP 状态码不是 2xx。** 反代挂了会回 404 / 502，**响应体往往是
   `404: Not Found` 这种纯文本**。早期实现不看状态码，这段文本被当成「纯文本指针」接受，
   日志里就变成「清单里的仓库地址『404: Not Found』看不懂」——把真正的原因
   （通道挂了）伪装成「清单写错了」，排查方向直接跑偏。
2. **取出的内容不像指针。** 指针必须长得像 `owner/repo`：不含空白、不含 `<>{}\`?#
   这类字符，前两段不含冒号。被墙时反代常回一页 HTML，也会在这一步被挡下。

判定的意义在于**错误信息要指向正确的方向**：通道失败就报 `HTTP 404 Not Found`，
清单写错才报「应该写成 owner/repo」。

被抢答赢家取消的请求**不算失败**，也不会写日志——那是预期行为。
（顺手修掉了 `HttpSenderExtension` 在 `enableLogging: false` 时仍然打印
`TaskCanceledException` 堆栈的问题。）

> **这个地址一旦发出去就不能再改。** 改它就等于把所有旧版本一起掐断。
> 真要换地方，得在新地址上放一份内容相同的 json，并让旧地址重定向过去，
> 而不是直接改客户端里的常量。

## 清单托管位置本身的风险

清单再稳，也架不住**托管它的地方整体消失**。这不是假设，已经发生过一次：

> `Pcl-Lite` 组织被 GitHub 标记 → 组织对公众隐藏 → 组织下**所有**仓库
> （`Pcl-lite`、`Pcl-Lite-service`）对匿名访问全部 404，
> 而 `orgs/Pcl-Lite/repos` 里仍然写着 `private: false`。
> 表现为：清单 6 条通道、Releases 兜底源，**全部 404**。

所以选托管位置时有两条硬要求：

1. **必须是一个不会因为单点问题就整体消失的地方。** 组织被标记、账号被暂停，
   整个组织下的仓库会一起掉，清单和 Release 同时失联，客户端没有任何退路。
   个人账号下的长期仓库、或自建静态托管，抗风险能力都比新建组织强。
2. **必须公开且无需登录。** 私有仓库对匿名用户就是 404——和「不存在」无法区分，
   客户端也只能报 404。

这也是为什么清单要放在**独立仓库**：至少发布仓库搬家时不用动它。

### 已经做的缓解：多锚点

`ProjectLinks.ExtraServiceAnchors` 可以再挂一份放在**另一个账号**下的清单，
主锚点挂了还有第二条路：

```csharp
public static readonly string[] ExtraServiceAnchors = ["AnotherAccount/PCL-Lite-service"];
```

留空（默认）表示只用主锚点，零额外请求。

> **加备用锚点前想清楚一件事**：抢答是「谁快用谁」，不看顺序。
> 如果备用锚点里的指针指向一个**已经死掉**的仓库，它可能抢在主锚点前面返回，
> 于是客户端拿到一个坏指针——比没有备用锚点更糟。
> 所以备用锚点必须和主锚点**内容一致**，要么就别加。

另外，备用锚点只对「包含这段代码的版本」有效。已经装在用户电脑上的旧版本
只认主锚点，救不了它们——那种情况只能靠把主锚点恢复。

## 内容

整个文件只有一行指针：

```json
{
  "update": "askbbtsk/pcl-lite/releases/latest"
}
```

就这些。**版本号、tag、附件直链、SHA-256 都不写在这里**——
它们全部由那个仓库的 Release 自己提供，客户端顺着指针去读。

### 兼容的写法

解析器很宽容，下面几种都认，手改的时候多写一对括号不会把更新搞挂：

```jsonc
"askbbtsk/pcl-lite/releases/latest"                     // 裸字符串
{ "update": "askbbtsk/pcl-lite/releases/latest" }       // 单字段对象
{ "repository": "askbbtsk/pcl-lite" }                   // 换个别名
askbbtsk/pcl-lite/releases/latest                       // 干脆不是 JSON 的纯文本
"https://github.com/askbbtsk/pcl-lite/releases/latest"  // 完整 URL
"askbbtsk/pcl-lite"                                     // 不带 releases/latest
```

宽容归宽容，**形状校验是硬的**：不含空白、不含 `<>{}\`?#`、前两段不含冒号，
否则整条通道判失败（见上文「什么算这条通道失败」）。

指针里 `owner/repo` 之后的部分（`releases/latest`）只是给人看的，
程序不依赖它：**取哪一版由 Release 自己的 `prerelease` 标记决定**——
正式版通道取最新的非预发布 Release，预发布通道取最新的 Release（含预发布）。

## 为什么把 hash 交给 Release

GitHub 现在会给每个 Release 资产带上 `digest` 字段（`sha256:…`），
客户端直接读它做完整性校验。

好处是**没有人需要手工维护哈希**。以前把 sha256 写进 json 的做法，
每次发版都要重新算一遍再改文件，漏改一次就会让所有更新卡在「哈希不匹配」上，
而且这个错误只在用户那边才暴露。现在哈希跟着资产走，签名完直接上传，天然一致。

如果某个资产没有 `digest`（老接口、Atom 降级通道），客户端会跳过校验，
而不是拿空串去比导致必然失败。

## 发版时要不要改 update.json

**不用。** 这是这套设计最重要的性质：

- 发新版 → 只要推 Release，客户端自动看到
- 发预发布 → 标上 prerelease，走预发布通道的用户自动看到
- 换仓库 → 改 `update.json` 那一行（唯一需要动手的场景）

## 换发布仓库的步骤

假设以后要从 `askbbtsk/pcl-lite` 搬到 `SomeoneElse/PCL-Lite-Next`：

1. 新仓库把代码、tag、Release 全部搬过去，确认 `releases/latest` 能打开
2. 把 `update.json` 改成：
   ```json
   { "update": "SomeoneElse/PCL-Lite-Next/releases/latest" }
   ```
3. 推送到 `bedkillerspacex-boop/PCL-Lite-service` 的 `main`
4. 等 1～2 分钟（raw / jsDelivr 有缓存），开一个旧版本验证能看到更新

**客户端代码一行都不用改，也不需要发版。** 所有旧版本下次检查更新时
就会顺着指针走到新仓库，完成最后一次「跨仓库更新」，之后一直在新仓库上更新。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `Modules/Updates/UpdatesManifestModel.cs` | 并发拉清单、把仓库交给内层源、把通道失败归并成一句人话 |
| `PCL.Core/App/Updates/UpdateManifestPointer.cs` | 指针的解析与**形状校验**（纯函数，被单元测试盯着） |
| `Modules/Updates/UpdatesGitHubModel.cs` | 真正取版本：读 Release 列表、挑资产、读 `digest`、下载 |
| `Modules/Updates/UpdatesWrapperModel.cs` | 按顺序试各个源，第一个成功的就是本次的版本源 |
| `Modules/Updates/UpdateManager.cs` | 源列表就在这里，清单源排第一 |
| `PCL.Core.Test/UpdateManifestPointerTest.cs` | 17 个用例，含「反代回 404 正文」「回 HTML」这些实战翻车场景 |

失败行为：清单拉不到（墙、仓库私有、json 写坏）→ 自动换 `UpdatesGitHubModel`，
行为与加清单之前完全一致。两个都失败才报「获取版本信息失败」。
清单能拉到但 GitHub API 被限流 → 内层源自己降级到 Atom 订阅源，仍然能更新。

### 从日志里判断是哪种坏法

| 日志 | 含义 | 该干什么 |
| --- | --- | --- |
| `更新清单已从「…」取得` | 正常，而且能看到是哪条通道最快 | 没事 |
| `无法获取更新清单（…）：HTTP 404 Not Found（Contents API、raw 直连、…）` | **所有**通道都 404，且原因一致 | 仓库/组织对匿名不可见（隐藏、私有、被标记），不是网络问题 |
| `无法获取更新清单（…）：6 秒内没有响应（raw 直连、…）` | 那几条通道被墙或超时 | 换反代，或等镜像 |
| `更新清单通道失败：xxx → 返回的是网页而不是清单` | 反代把错误页当正文回了 | 这条反代不可靠，考虑从 `DownloadProxies` 里去掉 |
| `更新清单里的仓库地址有问题：…应该写成 owner/repo` | 清单**真的**写错了 | 改 `update.json` |

按原因归并过的错误信息很关键：`HTTP 404（所有通道）` 和
`6 秒内没有响应（raw 直连、gh-proxy）` 指向完全不同的两件事。

## 已知限制

- 1.3.0 及更早的版本**没有**读清单的代码，它们仍然直接读旧仓库的 GitHub Releases。
  旧仓库转私有后，这些版本的自动更新会失败，只能手动下载一次新版。
  这是无法远程修复的——它们连「该去问谁」都不知道。
- jsDelivr 镜像有缓存，刚推的 json 可能要等几分钟才生效。
- 清单只在**仓库搬家**时才有意义，它不能修正「Release 发错了」这类问题；
  发错了就重发 Release，不要试图用清单去绕。
- 清单挡不住「托管平台层面整体消失」：组织被标记 / 账号被暂停时，
  清单和 Release 会同时 404，客户端两条路一起断。这种情况只能靠人把账号/组织申诉回来。
