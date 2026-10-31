<summary>1.6.4-hotfix-1：标题栏换成 LITE 图标，不再挂老的 PCL 字标；仓库里那套方块 logo 一并删掉；更新链路补了一个官方维护的镜像源，官方仓库再出整体故障时更新还有退路。</summary>

PCL Lite 1.6.4-hotfix-1。

> 热修复，稳定版通道会收到。已装 1.6.4 的用户直接更新即可。

## 一、标题栏换成 LITE 图标

标题栏左边以前是两段东西拼出来的：一个用 `Path` 描边画出来的 **PCL** 字标，
后面跟一个纯文字的 **Lite** 徽标。两个都是白色的，挤在一起，跟项目现在的图标没关系。

现在换成一个图标：蓝底圆角的 **LITE** 图标，和 exe 图标、安装包图标同源
（`Images/icon.png`），28×28，垂直居中。

顺带把那个 `TextBlock` 删掉了——图标本身已经写着 LITE，再跟一段 "Lite" 文字
就成了「LITE Lite」。

**PLC 彩蛋保留**：启动时有 1/1000 的概率标题栏变成 **PLC**（P、L 对调）。以前它靠改
那个 `Path` 的 `Data` 实现，现在命中时会自动切回老的 PCL 字标并隐藏图标，行为不变。

## 二、删掉那套方块 logo

`docs/assets/` 里原来有 `logo-1024/512/256.png` 三个尺寸，图案是一个蓝色圆角方块里
放白色 3D 立方体。这套图和项目现在的 LITE 图标不是一路的，删了。

`docs/DISCORD.md` 里两处「想给 Discord 应用传图标就用这张」的指引改成指向
`docs/assets/icon-*.png`——同样是蓝色圆角方块，但里面是 LITE 字标。

> 只是仓库文件清理，不影响任何功能。已经发出去的 1.6.4 资产里也没有这三个文件。

## 三、更新链路补了一个官方维护的镜像源

这一条不需要换 exe 也能生效（改的是锚点仓库的清单），但正好一起说。

以前清单里只有一个源：官方仓库。那个仓库整体出事时（组织被标记、账号被暂停、
Release 被删）更新就彻底断了——2026-10 断过一次。

现在多了一个官方维护的镜像源，独立仓库、独立账号，不是 fork 也不是 import，
靠 GitHub Actions 每两小时自动同步源码、tag 和 Release：

```json
{
  "schema": 2,
  "sources": [
    { "repo": "askbbtsk/pcl-lite", "tier": "official" },
    { "repo": "ciallobska/static-archive-6d1a", "tier": "mirror", "weight": 90 }
  ]
}
```

权重 90，紧挨着官方源（100）。**官方源正常时它一次请求都不会发**，零额外延迟；
只有官方源拿不到版本才轮到它。

镜像源要过身份校验（仓库根目录得有一份能解析的 `update.json`），所以不是随便
写个仓库地址进来就能冒充——这一条对 `tier: mirror` 强制生效。

## 四、新增 `docs/MIRROR.md`

想给更新链路做备份源的人看这个：怎么复制仓库、怎么搬 Release、怎么被列进清单、
身份校验与分档规则、常见错误。

里面有一条重点：**不要 Fork，也不要用 Import repository**。两者都会在平台上留下关联，
官方仓库被处理时会连坐。正确做法是新建空仓库 + `git clone --bare` + `git push --mirror`。

## 校验值

```
SHA-256  9B359E39BFF1A0A96683F06A6427D2983F37F08719C33BECD986FB5C97F2AF34
```

## 已知问题

同 1.6.4，未新增。

## 许可证

本项目以 MIT 许可证发布，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
