# 镜像仓库指南

想给 PCL Lite 做一份更新源备份（镜像）时看这个。从复制仓库、搬 Release，到被列进更新清单，
一步步写清楚。

> 只是想改代码提 PR？看 [CONTRIBUTE.md](CONTRIBUTE.md)。
> 想搞清楚清单本身怎么工作？看 [UPDATE.md](UPDATE.md)。

## 目录

- [0. 三十秒版](#0-三十秒版)
- [1. 镜像在更新链路里是什么位置](#1-镜像在更新链路里是什么位置)
- [2. 合格的镜像要满足什么](#2-合格的镜像要满足什么)
- [3. 一步步做](#3-一步步做)
- [4. 让客户端真的用上你的镜像](#4-让客户端真的用上你的镜像)
- [5. 身份校验与分档规则](#5-身份校验与分档规则)
- [6. 常见错误](#6-常见错误)
- [7. 撤下镜像](#7-撤下镜像)

---

## 0. 三十秒版

1. 复制仓库（fork 或 import 都行），**根目录 `update.json` 保持原样**（内容是 `{"update": "self"}`）
2. 把官方 Release 的 tag 和资产**原样搬过来**，`prerelease` 标记也要一致
3. 把 `owner/repo` 提给维护者，由维护者在锚点仓库的 `updatev2.json` 里加一条
4. 客户端在官方源拉不到版本时才会用到你

---

## 1. 镜像在更新链路里是什么位置

```
客户端
  └─> 锚点仓库 PCL-Lite-service 的 updatev2.json
        ├─ sources[0] official  (权重 100)  ← 官方仓库，先问它
        ├─ sources[1] mirror    (权重 70)   ← 官方挂了才问
        └─ sources[2] backup    (权重 40)   ← 前两个都挂了才问
```

规则：

- **按权重从高到低依次尝试，第一个能拿到版本的赢**，不是投票、不是取最高版本号
- 官方源（`tier: official`）**不做身份校验**，其余档位都要校验
- 所有源都拿不到，才退回「直接读仓库的 GitHub Releases」兜底

所以镜像的作用只有一个：**官方仓库整体出事时（组织被标记、账号被暂停、Release 被删）让更新还能走通**。
它不是 CDN，平时不会分流下载。

---

## 2. 合格的镜像要满足什么

| 要求 | 说明 |
| --- | --- |
| 公开仓库 | 私有仓库客户端拉不到 |
| 根目录有 `update.json` | 内容写 `{"update": "self"}` 即可，这是「这个仓库属于本项目」的标记 |
| 有 GitHub Releases | 客户端只从 Release 的资产里取包，不读仓库文件 |
| tag 命名与官方一致 | `v1.6.4` 是正式版，`v1.6.4-pre` / `-snapshot` / `-nightly` 是预发布 |
| `prerelease` 标记与 tag 一致 | 正式版**不要**勾 prerelease；预发布**必须**勾 |
| 资产名带架构 | `PCL-Lite_<版本>_x64.exe` 或 `_arm64.exe`，`.zip` 也认 |
| 保留 LICENSE 与署名 | 协议要求，不能抹掉原有标识 |

**不要做**的三件事：

- **不要改版本号**。镜像发一个版本号更高的包，等于把更新权拿走
- **不要改 `PCL.Core/App/ProjectLinks.cs` 里的 `GitHubOwner` / `GitHubRepo`**。
  改了 `update.json` 里的 `self` 会展开到你的仓库，别人装上的镜像版会去查你的 Release
- **不要自己重新编译**。镜像应该原样搬运官方资产；自己编的包即使能用，
  也等于让用户装了一个来源不明的二进制

---

## 3. 一步步做

### 3.1 复制仓库

两种方式都行：

**A. Fork**（保留和上游的关系，方便同步）

网页上点 Fork，或者在本地：

```bash
git clone --mirror https://github.com/askbbtsk/pcl-lite.git pcl-lite-mirror.git
cd pcl-lite-mirror.git
git remote set-url --push origin https://github.com/<你的账号>/pcl-lite.git
git push --mirror
```

**B. Import**（GitHub 的 Import repository，得到一个干净的独立仓库）

> 不管哪种方式，`update.json` 都不用改。它写的是 `self`，在你仓库里展开出来就是你的仓库，
> 内容本身跟仓库无关，所以不会跟上游冲突。

### 3.2 确认 `update.json`

```bash
curl -s https://raw.githubusercontent.com/<你的账号>/pcl-lite/main/update.json
```

应该看到：

```json
{
  "update": "self"
}
```

这就是身份校验要的东西。**不要**把它改成官方仓库地址——校验只看「能不能解析成合法指针」，
写 `self` 就够了，而且这样所有镜像仓库的内容都一样。

### 3.3 搬 Release

tag 和资产都要搬，tag 名一个字符都不能改（客户端按 tag 里的版本号比较大小）。

```bash
# 拉全部 tag
git fetch --tags https://github.com/askbbtsk/pcl-lite.git
git push --tags origin

# 资产要手动传：GitHub 的 Release 资产不能通过 git 同步
```

推荐用 `gh` 逐个重建：

```bash
# 以 1.6.4 为例
gh release create v1.6.4 -R <你的账号>/pcl-lite \
  --target main \
  --title "PCL Lite 1.6.4 正式版" \
  --notes-file release/notes-1.6.4.md \
  --latest \
  "PCL-Lite_1.6.4_x64.exe"
```

预发布版本加 `--prerelease`，并且**不要**加 `--latest`：

```bash
gh release create v1.6.4-pre -R <你的账号>/pcl-lite \
  --target main --title "PCL Lite 1.6.4 预览版" \
  --notes-file release/notes-1.6.4-pre.md \
  --prerelease "PCL-Lite_1.6.4-pre_x64.exe"
```

> `--prerelease` 不是装饰：稳定版通道会**跳过**被标成 prerelease 的 Release。
> 一个正式版被误勾成 prerelease，稳定版用户就永远收不到它。

### 3.4 自查

```bash
# 1. 身份标记能拉到
curl -s https://raw.githubusercontent.com/<你的账号>/pcl-lite/main/update.json

# 2. Release 列表里能看到你的 tag，且 prerelease 标记正确
curl -s https://api.github.com/repos/<你的账号>/pcl-lite/releases \
  | grep -E '"tag_name"|"prerelease"'

# 3. 资产在、大小对得上
curl -sI https://github.com/<你的账号>/pcl-lite/releases/download/v1.6.4/PCL-Lite_1.6.4_x64.exe
```

三条都通，镜像本身就算合格了。**但此时客户端还不会用它**——还得被列进清单。

---

## 4. 让客户端真的用上你的镜像

清单在锚点仓库 `bedkillerspacex-boop/PCL-Lite-service` 里，改它需要那个仓库的写权限。
所以流程是：**把你的仓库地址提给维护者**（Issue 或 PR），由维护者加一行。

要加的内容长这样：

```json
{
  "schema": 2,
  "sources": [
    { "repo": "askbbtsk/pcl-lite", "tier": "official" },
    { "repo": "<你的账号>/pcl-lite", "tier": "mirror" }
  ]
}
```

提交 PR 时请一并说明：

- 仓库地址、维护者是谁
- 打算搬哪些版本（只搬正式版？还是连预发布一起？）
- 多久同步一次（官方发版后几小时内）

维护者会先按第 3.4 节自查一遍，再合进去。合进去之后 1～2 分钟生效
（raw / jsDelivr 有缓存），可以用下面的方法验证。

### 怎么确认自己真的被用上了

打开客户端的日志，检查更新时会出现：

```
[Update] 多源清单已加载，共 2 个源：askbbtsk/pcl-lite(official, 100)、xxx/pcl-lite(mirror, 70)
```

只有官方源失败时，才会出现 `[Update] 已确认 xxx/pcl-lite 是本项目的镜像（仓库内有 update.json）`。

---

## 5. 身份校验与分档规则

| `tier` | 默认权重 | 要不要校验 `update.json` | 适合谁 |
| --- | --- | --- | --- |
| `official` | 100 | 不校验 | 官方仓库 |
| `mirror` | 70 | 校验 | 完整搬运官方 Release 的镜像 |
| `backup` | 40 | 校验 | 不常更新、只求「还有一口气」的备用源 |
| `community` | 25 | 校验 | 第三方构建，最后才轮到 |
| 不写 / 其它值 | 50 | 校验 | 不推荐，语义不清 |

- `weight` 可以显式写，不写就按 `tier` 取默认值
- **权重只决定「先问谁」，决定不了「信不信」**——信任由 `tier` 和 `update.json` 决定
- 官方源免校验是有意的：校验要发一次网络请求，走 raw / jsDelivr 这些国内常挂的域名，
  如果官方源也要验，「raw 被墙」会连带把官方源判成不可信，用户就彻底收不到更新了

---

## 6. 常见错误

| 现象 | 原因 | 怎么修 |
| --- | --- | --- |
| 客户端日志说「无法确认 … 是本项目的仓库，跳过该源」 | 仓库根目录没有 `update.json`，或者内容不是合法指针 | 放一份 `{"update": "self"}` |
| 加了清单但一直没被用到 | 官方源正常，镜像只在官方挂了才轮得到 | 正常行为，不用管 |
| 稳定版用户收不到你的版本 | Release 被勾了 prerelease | 取消 prerelease 标记 |
| 预览版用户收不到 | tag 没写后缀，或者没勾 prerelease | tag 用 `v1.6.4-pre` 这种形式并勾 prerelease |
| 「这个 Release 的附件里没有 .zip 或 .exe」 | 资产没传，或者传成了 `.7z` / `.msi` | 只认 `.exe` 和 `.zip` |
| 下载到的架构不对 | 资产名里没有 `x64` / `arm64` | 按 `PCL-Lite_<版本>_x64.exe` 命名 |
| 哈希对不上 | 资产被替换过，或者传了个重新编译的包 | 原样搬运官方资产 |
| 镜像仓库内容跟上游天天冲突 | 改过 `update.json` | `git checkout -- update.json` 还原成 `self` |

---

## 7. 撤下镜像

不想继续做了：

1. 通知维护者把 `updatev2.json` 里那条删掉（这一步必须做，否则客户端还会去问一个空仓库，
   虽然会跳过，但会拖慢每次检查更新）
2. 归档或删除你的仓库

只删仓库不改清单的后果：客户端每次检查更新都要多等一次超时，用户会感觉「检查更新变慢了」。
