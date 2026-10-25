# 更新清单 `update.json`

## 它解决什么问题

启动器必须写死**某个**地址才能知道去问「有没有新版本」。
如果这个地址指向发布仓库本身，那么仓库一改名、一换组织、一转到别的账号，
所有已经装在别人电脑上的旧版本就**永久失联**了——它们只会对着一个 404 报错，
而且没有任何远程手段能把它掰回来，因为唯一的远程入口就是那个已经失效的地址。

所以这里把「地址」和「数据」拆开：

```
客户端（写死，永不改变）
    └──> Pcl-Lite/Pcl-Lite-service 的 update.json   ← 唯一锚点，只有一行指针
             └──> 当前发布仓库 / releases/latest    ← 随时可以改
                      └──> Release 自己提供版本号、资产、直链、SHA-256
```

客户端只认 `update.json` 的地址。以后要换发布仓库，改那一行就行，
所有还在跑老版本的机器下次检查更新时自然会走到新地址。

## 地址

| 用途 | 地址 |
| --- | --- |
| 主通道 | `https://raw.githubusercontent.com/Pcl-Lite/Pcl-Lite-service/main/update.json` |
| 镜像通道 | `https://cdn.jsdelivr.net/gh/Pcl-Lite/Pcl-Lite-service@main/update.json` |

代码里的定义在 `PCL.Core/App/ProjectLinks.cs` 的 `UpdateManifest` / `UpdateManifestMirror`。

> **这个地址一旦发出去就不能再改。** 改它就等于把所有旧版本一起掐断。
> 真要换地方，得在新地址上放一份内容相同的 json，并让旧地址重定向过去，
> 而不是直接改客户端里的常量。

## 内容

整个文件只有一行指针：

```json
{
  "update": "Pcl-Lite/Pcl-lite/releases/latest"
}
```

就这些。**版本号、tag、附件直链、SHA-256 都不写在这里**——
它们全部由那个仓库的 Release 自己提供，客户端顺着指针去读。

### 兼容的写法

解析器很宽容，下面几种都认，手改的时候多写一对括号不会把更新搞挂：

```jsonc
"Pcl-Lite/Pcl-lite/releases/latest"                     // 裸字符串
{ "update": "Pcl-Lite/Pcl-lite/releases/latest" }       // 单字段对象
{ "repository": "Pcl-Lite/Pcl-lite" }                   // 换个别名
Pcl-Lite/Pcl-lite/releases/latest                       // 干脆不是 JSON 的纯文本
"https://github.com/Pcl-Lite/Pcl-lite/releases/latest"  // 完整 URL
"Pcl-Lite/Pcl-lite"                                     // 不带 releases/latest
```

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

假设以后要从 `Pcl-Lite/Pcl-lite` 搬到 `SomeoneElse/PCL-Lite-Next`：

1. 新仓库把代码、tag、Release 全部搬过去，确认 `releases/latest` 能打开
2. 把 `update.json` 改成：
   ```json
   { "update": "SomeoneElse/PCL-Lite-Next/releases/latest" }
   ```
3. 推送到 `Pcl-Lite/Pcl-Lite-service` 的 `main`
4. 等 1～2 分钟（raw / jsDelivr 有缓存），开一个旧版本验证能看到更新

**客户端代码一行都不用改，也不需要发版。** 所有旧版本下次检查更新时
就会顺着指针走到新仓库，完成最后一次「跨仓库更新」，之后一直在新仓库上更新。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `Modules/Updates/UpdatesManifestModel.cs` | 读清单、解析指针、把仓库交给内层源 |
| `Modules/Updates/UpdatesGitHubModel.cs` | 真正取版本：读 Release 列表、挑资产、读 `digest`、下载 |
| `Modules/Updates/UpdatesWrapperModel.cs` | 按顺序试各个源，第一个成功的就是本次的版本源 |
| `Modules/Updates/UpdateManager.cs` | 源列表就在这里，清单源排第一 |

失败行为：清单拉不到（墙、仓库私有、json 写坏）→ 自动换 `UpdatesGitHubModel`，
行为与加清单之前完全一致。两个都失败才报「获取版本信息失败」。
清单能拉到但 GitHub API 被限流 → 内层源自己降级到 Atom 订阅源，仍然能更新。

## 已知限制

- 1.3.0 及更早的版本**没有**读清单的代码，它们仍然直接读旧仓库的 GitHub Releases。
  旧仓库转私有后，这些版本的自动更新会失败，只能手动下载一次新版。
  这是无法远程修复的——它们连「该去问谁」都不知道。
- jsDelivr 镜像有缓存，刚推的 json 可能要等几分钟才生效。
- 清单只在**仓库搬家**时才有意义，它不能修正「Release 发错了」这类问题；
  发错了就重发 Release，不要试图用清单去绕。
