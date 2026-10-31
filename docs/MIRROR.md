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

1. **新建一个名字随机的空仓库**——不要点 Fork，也不要用 Import
2. 手动把仓库内容复制过去（`git clone --bare` 官方仓库 → `git push --mirror`），
   根目录 `update.json` 保持原样
3. 把官方 Release 的资产**原样搬过来**，`prerelease` 标记也要一致
4. 把 `owner/repo` 提给维护者，由维护者在锚点仓库的 `updatev2.json` 里加一条
5. 客户端在官方源拉不到版本时才会用到你

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
| **独立仓库** | **不能是 fork，也不能是 Import 来的**——fork 会跟官方仓库连坐，见 3.1 |
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

### 3.1 新建仓库：**不要 fork，也不要 import**

这一步是最容易出事的地方。

- **不要点 Fork**：fork 会进入同一个 fork network，官方仓库被 DMCA 处理时，fork 会被一并处理
- **不要用 Import repository**：它会在仓库上留下来源关联，同样容易被顺藤摸瓜
- **不要在仓库名、简介、README 标题里带项目名**：名字随机一点，比如 `ml-archive-7f3a`、
  `static-mirror-92c1`

正确做法是**新建一个空的公开仓库，然后手动把内容推上去**。这样它就是一个完全独立的仓库，
和官方仓库之间没有任何平台层面的关联。

```bash
# 1) 在网页上新建一个空仓库（不要勾 README / .gitignore / LICENSE），名字随机

# 2) 裸克隆一份官方仓库（官方仓库：https://github.com/askbbtsk/pcl-lite）
git clone --bare https://github.com/askbbtsk/pcl-lite.git mirror.git
cd mirror.git

# 3) 整个推到你自己的新仓库：分支和 tag 一次带过去
git push --mirror https://github.com/<你的账号>/<随机仓库名>.git
```

推完检查一下：

- 仓库页面上**没有** "forked from …" 的字样
- 分支、tag 数量和官方一致

> `update.json` 不用改。它写的是 `self`，在你仓库里展开出来就是你的仓库，
> 内容本身跟仓库无关，所以以后跟官方同步时也不会冲突。

以后官方发新版，用同样的方式手动同步（**不要**在网页上点 Sync fork——那需要 fork 关系）：

```bash
cd mirror.git
git fetch --prune https://github.com/askbbtsk/pcl-lite.git \
  '+refs/heads/*:refs/heads/*' '+refs/tags/*:refs/tags/*'
git push --mirror https://github.com/<你的账号>/<随机仓库名>.git
```

### 3.2 确认 `update.json`

```bash
curl -s https://raw.githubusercontent.com/<你的账号>/<随机仓库名>/main/update.json
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

**tag 已经搬完了**——3.1 的 `git push --mirror` 会把所有分支和 tag 一起推过去，
不用再单独 `git fetch --tags` / `git push --tags`。以后同步也是同一条命令。

剩下的是 **Release 对象和资产**，这部分 git 传不了，必须逐个重建：

1. 下载官方 Release 的资产
2. 在你自己仓库里用同一个 tag 建 Release，把资产传上去

先把要搬的资产下载到本地：

```bash
# 以 1.6.4 为例，下载到当前目录
gh release download v1.6.4 -R askbbtsk/pcl-lite -D ./assets-1.6.4
ls ./assets-1.6.4
# PCL-Lite_1.6.4_x64.exe   PCL-Lite_1.6.4_arm64.exe   ...（有几个下几个）
```

> `gh release download` 也可以顺手把发布说明取下来：
> `gh release view v1.6.4 -R askbbtsk/pcl-lite --json body -q .body > notes-1.6.4.md`
> 直接复用官方说明，别自己重写——重写容易漏掉哈希和变更项。

然后在你的仓库里建 Release。正式版：

```bash
gh release create v1.6.4 -R <你的账号>/<随机仓库名> \
  --target main \
  --title "PCL Lite 1.6.4 正式版" \
  --notes-file notes-1.6.4.md \
  --latest \
  ./assets-1.6.4/*
```

预发布版加 `--prerelease`，并且**不要**加 `--latest`：

```bash
gh release create v1.6.4-pre -R <你的账号>/<随机仓库名> \
  --target main \
  --title "PCL Lite 1.6.4 预览版" \
  --notes-file notes-1.6.4-pre.md \
  --prerelease \
  ./assets-1.6.4-pre/*
```

三条不能忘：

- **tag 名一个字符都不能改**——客户端按 tag 里的版本号比较大小，改了就认不出来
- **`--prerelease` 不是装饰**：稳定版通道会**跳过**被标成 prerelease 的 Release。
  一个正式版被误勾成 prerelease，稳定版用户就永远收不到它
- **资产原样搬**，不要改名、不要重新编译。文件名里必须保留 `x64` / `arm64`，
  客户端靠它选架构

> 搬多个版本时就是重复上面两步，一个 tag 一次。不用一次搬完历史全部版本，
> 至少把**当前最新正式版 + 最近一个预览版**搬过去，镜像就能顶上用了。

### 3.4 自查

```bash
# 1. 身份标记能拉到
curl -s https://raw.githubusercontent.com/<你的账号>/<随机仓库名>/main/update.json

# 2. Release 列表里能看到你的 tag，且 prerelease 标记正确
curl -s https://api.github.com/repos/<你的账号>/<随机仓库名>/releases \
  | grep -E '"tag_name"|"prerelease"'

# 3. 资产在、大小对得上
curl -sI https://github.com/<你的账号>/<随机仓库名>/releases/download/v1.6.4/PCL-Lite_1.6.4_x64.exe

# 4. 确认没有 fork 关联（应该什么都不输出）
curl -s https://api.github.com/repos/<你的账号>/<随机仓库名> | grep -E '"fork": *true|"parent": *\{'
```

四条都通，镜像本身就算合格了。**但此时客户端还不会用它**——还得被列进清单。

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
    { "repo": "<你的账号>/<随机仓库名>", "tier": "mirror" }
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
[Update] 多源清单已加载，共 2 个源：askbbtsk/pcl-lite(official, 100)、mirror-7f3a(mirror, 70)
```

只有官方源失败时，才会出现 `[Update] 已确认 mirror-7f3a 是本项目的镜像（仓库内有 update.json）`。

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
| 仓库页面上写着 "forked from …" | 用了 Fork 或 Import | 删掉重做：新建空仓库，按 3.1 手动推 |
| 官方被处理时你的仓库跟着消失 | 是 fork，在同一个 fork network 里 | 同上，必须是独立仓库 |
| 客户端日志说「无法确认 … 是本项目的仓库，跳过该源」 | 仓库根目录没有 `update.json`，或者内容不是合法指针 | 放一份 `{"update": "self"}` |
| 加了清单但一直没被用到 | 官方源正常，镜像只在官方挂了才轮得到 | 正常行为，不用管 |
| 稳定版用户收不到你的版本 | Release 被勾了 prerelease | 取消 prerelease 标记 |
| 预览版用户收不到 | tag 没写后缀，或者没勾 prerelease | tag 用 `v1.6.4-pre` 这种形式并勾 prerelease |
| 「这个 Release 的附件里没有 .zip 或 .exe」 | 资产没传，或者传成了 `.7z` / `.msi` | 只认 `.exe` 和 `.zip` |
| 下载到的架构不对 | 资产名里没有 `x64` / `arm64` | 按 `PCL-Lite_<版本>_x64.exe` 命名 |
| 哈希对不上 | 资产被替换过，或者传了个重新编译的包 | 原样搬运官方资产 |
| 同步官方新版本时天天冲突 | 改过 `update.json` | `git checkout -- update.json` 还原成 `self` |

---

## 7. 撤下镜像

不想继续做了：

1. 通知维护者把 `updatev2.json` 里那条删掉（这一步必须做，否则客户端还会去问一个空仓库，
   虽然会跳过，但会拖慢每次检查更新）
2. 归档或删除你的仓库

只删仓库不改清单的后果：客户端每次检查更新都要多等一次超时，用户会感觉「检查更新变慢了」。
