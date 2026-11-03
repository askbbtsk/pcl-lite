<summary>修掉游戏内主界面按钮整块消失：界面资源的下载目录以前会「猜」，猜中 ui/assets 这种数据目录时，整个 ui 压缩包被解进错误位置，--uiDir 跟着指过去，Genesis 拿到的界面根目录就是错的。现在下载只认 ui/&lt;sha1&gt;/。</summary>

PCL Lite 1.7.1 热修复 2。

## 症状

进游戏后主界面按钮整块不显示，HUD、模组、游戏本身都正常。

游戏日志（`logs/game/ichor-boot.log`）里有这么一行：

```
[Bridge] Found ui dir: E:\lunar\ui\assets
```

`assets` 是放 css、字体、背景图的数据目录，不是界面根目录。

## 原因

`LunarClientService.UiDirectory` 负责决定 `--uiDir` 传什么。它原来的退回逻辑是「从 `ui/` 的子目录里挑修改时间最新的那个」：

```csharp
var candidates = Directory.GetDirectories(root);
if (candidates.Length == 1) return candidates[0];
if (candidates.Length > 1)
    return candidates.OrderByDescending(Directory.GetLastWriteTimeUtc).First();
```

`ui/` 下面只有 `assets/` 和 `static/` 两个子目录，而 `assets/` 因为刚好被写过，几乎总是最新的 —— 于是被挑中。连带两个后果：

| 环节 | 后果 |
| --- | --- |
| `EnsureUiAsync` | 整个 ui 压缩包被解进 `ui/assets/`，还套出了 `assets/assets/`、`assets/static/` |
| `--uiDir` | 指到 `ui/assets`，Genesis 去一个数据目录里找入口页 |

## 修复

把「下载目标」和「实际使用目录」拆成两个方法，职责不再混在一起：

| 方法 | 用途 | 行为 |
| --- | --- | --- |
| `UiTargetDirectory` | 下载、解压唯一合法目标 | 固定返回 `ui/<sha1>/`（版本号缺失时是 `ui/`），不做任何猜测 |
| `UiDirectory` | 决定 `--uiDir` 传什么 | ① `ui/<sha1>/index.html` 存在就用它 ② `ui/index.html` 存在就用 `ui/` ③ 遍历子目录但**只收含 `index.html` 的** ④ 都没有才退回 `UiTargetDirectory` |

关键改动是 `EnsureUiAsync` 不再用 `UiDirectory` 的结果当下载目标 —— 那个方法为了「尽量能用」会去猜，而猜错的代价是把物料铺到错误的位置，之后每次启动都跟着错。

已经踩坏的目录不用手动清：版本目录补上 `index.html` 之后，判定第一步就会命中它，旧的 `ui/assets/` 不再参与。

## 校验

`SHA-256`（签名后）：`8AB596044646B2E893A4ACB6CE9C4E16B410A191418B2065DB203DB190033A40`

大小：19,395,368 字节

签名：`Valid`（PCL Lite，DigiCert 时间戳）

## 已知问题

- 如果你已经踩过这个坑，升级后第一次启动会重新下一遍界面资源包（几十 MB），属正常
- 1.7.1 那条「Lunar 物料下载异常可以先退回 1.7.0」的公告仍然有效
