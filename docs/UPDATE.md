# 更新清单 `update.json`

## 它解决什么问题

启动器的代码里必须写死**某个**地址，才能知道去问「有没有新版本」。
如果这个地址指向的是发布仓库本身，那么仓库一改名、一换组织、一转到别的账号，
所有已经装在别人电脑上的旧版本就**永久失联**了——它们只会对着一个 404 报错，
而且没有任何远程手段能把它们掰回来，因为唯一的远程入口就是那个已经失效的地址。

所以这里把「地址」和「数据」拆开：

```
客户端（写死，永不改变）
    └──> Pcl-Lite/Pcl-Lite-service 的 update.json   ← 唯一锚点
             └──> 当前发布仓库 / tag / 直链 / sha256  ← 随时可以改
```

客户端只认 `update.json` 的地址。以后要换发布仓库，改 json 里的一行就行，
所有还在跑老版本的机器下次检查更新时自然会走到新地址。

## 地址

| 用途 | 地址 |
| --- | --- |
| 主通道 | `https://raw.githubusercontent.com/Pcl-Lite/Pcl-Lite-service/main/update.json` |
| 镜像通道 | `https://cdn.jsdelivr.net/gh/Pcl-Lite/Pcl-Lite-service@main/update.json` |

代码里的定义在 `PCL.Core/App/ProjectLinks.cs` 的 `UpdateManifest` / `UpdateManifestMirror`。

> **这个地址一旦发出去就不能再改。** 改它就等于把所有旧版本一起掐断。
> 需要换地方时，在新地址上放一份内容相同（或更新）的 json，然后让旧地址重定向过去，
> 而不是直接改客户端里的常量。

## 格式

```json
{
  "format": "pcl-lite-update",
  "version": 1,
  "updated_at": "2026-10-25T08:45:00Z",
  "repository": "https://github.com/Pcl-Lite/Pcl-lite",
  "channels": {
    "stable": {
      "version": "1.3.1",
      "version_code": 10,
      "release_url": "https://github.com/Pcl-Lite/Pcl-lite/releases/tag/v1.3.1",
      "published_at": "2026-10-25T08:32:00Z",
      "changelog": "简短说明，会写进更新日志文件",
      "assets": {
        "x64": {
          "url": "https://github.com/Pcl-Lite/Pcl-lite/releases/download/v1.3.1/PCL-Lite_1.3.1_x64.exe",
          "size": 18827048,
          "sha256": "e8a5e686f06d95be29eb2dffd463ed6b6b74a96597d98d7eef082d3b651cf4d4"
        }
      }
    },
    "beta": {
      "version": "1.3.1",
      "version_code": 10,
      "assets": {
        "x64": { "url": "…", "size": 0, "sha256": "…" }
      }
    }
  }
}
```

### 字段说明

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `format` | 否 | 固定 `pcl-lite-update`。写错会被拒，防止误把别的 json 当成清单 |
| `version` | 否 | 清单格式版本，当前 `1` |
| `updated_at` | 否 | 上次修改时间，纯给人看 |
| `repository` | 否 | 当前发布仓库，出错提示里会带上 |
| `channels.stable` | **是** | 正式版通道。缺了会报「无法检查正式版更新」 |
| `channels.beta` | 否 | 预发布通道。**缺了会回退到 `stable`**，不会报错 |
| `channels.*.version` | **是** | 版本号，必须是合法 SemVer（`1.3.1`、`1.4.0-beta`） |
| `channels.*.version_code` | 否 | 内部版本号，当前比较只用 SemVer，填着备查 |
| `channels.*.changelog` | 否 | 写进 `PCL/Cache/CEUpdateLog.md`，设置页的更新日志就是它 |
| `channels.*.assets.x64` | **是** | x64 资产 |
| `channels.*.assets.arm64` | 否 | ARM64 资产。缺了会回退到唯一存在的那个架构 |
| `assets.*.url` | **是** | 直链。**必须是能直接下到 exe 的地址**，不要填发布页 |
| `assets.*.sha256` | 建议 | 小写十六进制。留空则跳过完整性校验 |

### 为什么用直链而不是「仓库 + tag」

用直链的话，客户端**完全不需要碰 GitHub API**：
不消耗每小时 60 次的匿名限流额度，也不会因为「按发布时间挑第一个 Release」
这种推断在补发旧版本时挑错版本。版本号、通道、架构全部由清单显式声明。

代价是发版时要把直链抄进 json——这一步已经写进下面的检查清单。

## 发版检查清单

每次发版，**先发 Release，再改 json**（顺序反了会有一小段时间指向不存在的资产）：

1. 构建、签名、上传 Release 资产
2. `sha256sum` 算出签名后 exe 的哈希
3. 更新 `update.json`：
   - `channels.stable.version` / `version_code`
   - `channels.stable.assets.<arch>.url`（指向新 tag 的资产）
   - `channels.stable.assets.<arch>.sha256`
   - `channels.stable.release_url` / `published_at` / `changelog`
   - `updated_at`
   - 预发布版本同步更新 `channels.beta`
4. 提交并推送到 `Pcl-Lite/Pcl-Lite-service` 的 `main`
5. 等 1～2 分钟（raw / jsDelivr 有缓存），再开一个旧版本验证能看到更新

> 忘了第 3 步的后果：旧版本不会失联，但会一直以为「已经是最新版」，
> 因为清单是权威源。这是本设计唯一的运维风险，务必照清单走。

## 换发布仓库的步骤

假设以后要从 `Pcl-Lite/Pcl-lite` 搬到 `SomeoneElse/PCL-Lite-Next`：

1. 新仓库把代码、tag、Release 全部搬过去
2. 改 `update.json` 里 `repository` 与所有 `assets.*.url` 指向新仓库
3. 推送 `update.json`

**客户端代码一行都不用改，也不需要发版。** 所有旧版本下次检查更新时
就会顺着清单走到新仓库，完成最后一次「跨仓库更新」，之后就一直在新仓库上更新了。

如果新仓库的地址也变了（连 `Pcl-Lite` 这个组织都没了），那就在旧地址上
保留一个 302 重定向到新清单——这是唯一需要「留在原地」的东西。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `Modules/Updates/UpdatesManifestModel.cs` | 读清单、解析、给出最新版本与下载器 |
| `Modules/Updates/UpdatesGitHubModel.cs` | 兜底：直接读当前仓库的 GitHub Releases |
| `Modules/Updates/UpdatesWrapperModel.cs` | 按顺序试各个源，第一个成功的就是本次的版本源 |
| `Modules/Updates/UpdateManager.cs` | 源列表就在这里，清单源排第一 |

失败行为：清单拉不到（墙、仓库私有、json 写坏）→ 自动换 `UpdatesGitHubModel`，
行为与加清单之前完全一致。两个都失败才报「获取版本信息失败」。

## 已知限制

- 1.3.0 及更早的版本**没有**读清单的代码，它们仍然直接读 GitHub Releases。
  仓库搬家后这些版本会失联，这是无法通过远程手段修复的——它们连「去哪儿问」都不知道。
  能做的只有：在新仓库里保留旧 tag 与 Release，让它们的 GitHub API 请求继续能拿到结果。
- jsDelivr 镜像有缓存，刚推的 json 可能要等几分钟才生效。
