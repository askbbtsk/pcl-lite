# 贡献指南：从 fork 到 PR 合入

写给第一次给这个仓库提交代码的人。照着做能跑起来、能提 PR、不会踩我们踩过的坑。

> 只想编一个自己用的 exe，不打算提 PR？看 [BUILD.md](BUILD.md)。
> 想自己发一版能自动更新的？看完 [BUILD.md](BUILD.md) 再看 [UPDATE.md](UPDATE.md)。

## 目录

- [0. 三十秒版](#0-三十秒版)
- [1. 仓库与分支](#1-仓库与分支)
- [2. 环境准备](#2-环境准备)
- [3. 第一次构建与运行](#3-第一次构建与运行)
- [4. `update.json` 到底是个什么文件](#4-updatejson-到底是个什么文件)
- [5. 版本号怎么改（以及为什么你别改）](#5-版本号怎么改以及为什么你别改)
- [6. 代码地图](#6-代码地图)
- [7. 改界面的规矩](#7-改界面的规矩)
- [8. 加文案的规矩](#8-加文案的规矩)
- [9. 测试怎么跑](#9-测试怎么跑)
- [10. 绝对不要提交的东西](#10-绝对不要提交的东西)
- [11. PR 提交前清单](#11-pr-提交前清单)
- [12. 常见坑](#12-常见坑)

---

## 0. 三十秒版

```bash
git clone https://github.com/<你的账号>/pcl-lite.git
cd pcl-lite
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Debug
```

- 分支：从 `main` 切，PR 提到 `main`
- **不要**改 `update.json`、`metadata.json`、`ProjectLinks.cs` 里的仓库常量（理由见第 4、5 节）
- 想让自己的构建查自己的 Release，用 `update.local.json`（第 4.3 节），它已被 git 忽略
- 加文案要 zh-CN / zh-TW / en-US / en-GB **四份一起加**
- 提 PR 前跑一遍 `dotnet test PCL.Core.Test`，已知的环境性失败在第 9 节

---

## 1. 仓库与分支

| 远端 | 地址 | 用途 |
| --- | --- | --- |
| 上游 | `PCL-Community/PCL-CE` | 我们的代码来源，只读，不要往这里提 |
| 发布仓 | `askbbtsk/pcl-lite` | **你 fork 的就是它**，PR 提到这里 |

- 默认分支叫 `main`。本地开发请开新分支：`git switch -c fix/xxx`
- 一个 PR 只做一件事。顺手重构 + 改功能 + 调格式混在一起的 PR 很难审，大概率被打回
- 提交信息用中文说清楚"改了什么、为什么"，例如：
  `修复深色主题下设置页输入框文字看不见`
  不要写 `update`、`fix bug`、`1` 这种

---

## 2. 环境准备

| 需要 | 版本 | 备注 |
| --- | --- | --- |
| Windows | 10 1809+ / 11 | WPF 项目，只能在 Windows 上编 |
| .NET SDK | 10.0 | `dotnet --version` 确认 |
| Git | 任意 | |
| JDK | 21（可选） | 只有改 Lunar / 启动相关功能、或跑完整测试才需要 |

不需要 Visual Studio。装了也可以直接打开 `Plain Craft Launcher 2.slnx`，但本文的命令都按 `dotnet` CLI 写。

---

## 3. 第一次构建与运行

```bash
# 调试构建
dotnet build "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" -c Debug

# 产物在这里，双击就能跑
# Plain Craft Launcher 2/bin/Debug/net10.0-windows/Plain Craft Launcher 2.exe
```

**数据目录是分开的**，调试版和正式版互不污染：

| 构建 | 配置 / 缓存目录 |
| --- | --- |
| Debug | `%APPDATA%\PCLLite_Debug` |
| Release | `%APPDATA%\PCLLite` |

**单例锁是全机共用的**（`%LOCALAPPDATA%\PCLLite\instance.lock`）。调试版和正式版**不能同时开**，
第二个会直接被顶掉或者报"已有一个实例在运行"。改代码前先把正式版关掉。

想编发布版（单文件、约 19 MB）：

```powershell
dotnet publish "Plain Craft Launcher 2/Plain Craft Launcher 2.csproj" `
  -c Release -p:Platform=x64 --no-self-contained -o ./publish-x64
```

**没有微软登录密钥的构建，正版登录会提示"未配置 Client ID"**，其他功能正常。
密钥怎么注入见 [BUILD.md](BUILD.md) 第三节——那套东西是发版用的，**PR 里不要碰**。

---

## 4. `update.json` 到底是个什么文件

这是这个仓库最容易被误解的一个文件，先说结论：**它不是一个给你本地改的开关。**

### 4.1 它长什么样

仓库根目录就一行：

```json
{
  "update": "askbbtsk/pcl-lite/releases/latest"
}
```

### 4.2 它是给谁读的

更新检查的链路是这样的：

```
客户端（写死锚点，永不改变）
  └─> bedkillerspacex-boop/PCL-Lite-service 仓库里的 update.json / updatev2.json
        └─> 里面写着「当前该去哪个仓库拿版本」
              └─> 那个仓库的 GitHub Releases
```

关键点：**客户端读的是锚点仓库那份，不是你自己仓库根目录这份。**

所以——**你在 fork 里把 `update.json` 改成自己的仓库，对你构建出来的 exe 一点影响都没有。**
它依然会去问官方锚点，依然会告诉你"有新版本 1.6.3-pre"。

那本仓库根目录这份留着干嘛？两个作用：

1. 它是"**这个仓库属于本项目**"的公开标记。任何第三方仓库想被列进更新清单当镜像，
   根目录必须有一份合法的 `update.json`，客户端会去拉它做身份校验（`tier` 不是 `official` 时才验）。
2. 换发布仓库时，这份和锚点仓库那份要一起改（只有维护者需要）。

### 4.3 想让自己的构建查自己的 Release：用 `update.local.json`

以前没这个口子，贡献者只能去改 `update.json` 或者 `ProjectLinks.cs` 里的常量，
改完又怕提交上去，于是得 `git update-index --skip-worktree update.json` 把它"藏"起来。
**这就是它傻的地方，现在不用这么干了。**

把 `update.local.json` 放在 exe 同目录，内容跟 `updatev2.json` 同格式：

```json
{
  "schema": 2,
  "sources": [
    { "repo": "你的账号/pcl-lite", "tier": "official" }
  ]
}
```

- `tier` 写 `official` = 免身份校验，直接去那个仓库的 Releases 找版本。
  写成别的值（比如 `mirror`）就得在你那个仓库根目录也放一份 `update.json`，否则这个源会被判不可信
- 想验证多源优先级，可以在 `sources` 里写多条，`weight` 大的先问，前面的全挂了才轮到后面的
- 文件位置二选一：
  - 直接丢在 `Plain Craft Launcher 2/bin/Debug/net10.0-windows/` 里（默认位置）
  - 或者设环境变量 `PCL_UPDATE_MANIFEST=E:\某个地方\my-update.json` 指到任意路径（相对路径按 exe 目录解析）

生效后日志里会出现：

```
[Update] 使用本地清单覆盖：...\update.local.json
```

**这个文件名已经写进 `.gitignore`**，放哪都不会被提交；正式发版也不会有这个文件，所以对线上零影响。

> 自己发 Release 时注意：附件名里要带架构，比如 `PCL-Lite_1.0.0_x64.exe`。
> 客户端挑资产时优先选文件名含 `x64` / `arm64` 的 `.exe` 或 `.zip`，
> 都不含就退到第一个 `.zip`，再退到第一个附件——名字随便起就可能挑错。

### 4.4 你什么时候真的需要动 `update.json`

基本不需要。唯一的场景是**换发布仓库**，那是维护者的活：

1. 改锚点仓库 `bedkillerspacex-boop/PCL-Lite-service` 里的 `update.json` / `updatev2.json`
2. 改本仓库根目录的 `update.json`，两处内容保持一致

只改一处会怎样：老客户端认锚点，新客户端认锚点，两边都对不上时就只能靠 GitHub Releases 兜底源，
那时候换仓库就等于把老版本用户丢了。细节见 [UPDATE.md](UPDATE.md)。

---

## 5. 版本号怎么改（以及为什么你别改）

版本号的唯一真源是 `Plain Craft Launcher 2/metadata.json`：

```json
{
  "version": { "base": "1.6.3", "suffix": "pre", "code": 51 }
}
```

规则：

- **`base` 必须是三段**（`1.6.3`）。写成两段（`1.6`）会让 `SemVer.Parse` 抛异常，**整个更新检查直接炸**
- `suffix` 为空 = 正式版；`snapshot` / `pre` / `nightly` 会拼成 `1.6.3-pre` 这样
- `code` 每次发版 +1，**不许回退**。客户端用它判断"同版本号的不同构建"
- 改了 `metadata.json` 还要同步改两个 csproj 的 `AssemblyVersion` / `FileVersion` / `InformationalVersion`，
  否则文件属性写的是一套、界面显示的是另一套

**提 PR 时不要动版本号。** 维护者合并后统一发版，你改了会造成后面每次合并都冲突。
真需要发版看 [BUILD.md](BUILD.md)。

---

## 6. 代码地图

```
PCL-CE/
├── PCL.Core/                    核心库（无 UI 依赖）
│   ├── App/                     配置模型、路径、元数据、ProjectLinks（仓库地址常量）
│   ├── App/Updates/             更新清单解析（纯函数，带单元测试）
│   ├── App/Lunar/               Lunar 客户端协议（Social / IPC）
│   ├── IO/Net/                  网络层（DNS、代理、用途白名单）
│   ├── Minecraft/               启动、下载、档案、认证
│   └── UI/Theme/                ★ 配色引擎（见 THEME.md）
├── Plain Craft Launcher 2/      WPF 前端
│   ├── Modules/Updates/         更新检查、下载、替换
│   ├── Pages/                   各个页面（PageSetup / PageLunar / PageDownload ...）
│   ├── Controls/                自定义控件（MyListItem / MyTextBox / MyButton ...）
│   └── metadata.json            ★ 版本号真源
├── PCL.Core.SourceGenerators/   源生成器：配置项、密钥注入
├── PCL.Core.Test/               单元测试
├── docs/                        文档
└── tools/                       验证脚本（verify-channels.py 等）
```

几个容易找错的地方：

- **主题算法在 `PCL.Core/UI/Theme/`**，不在前端目录。前端只负责把资源应用上去
- **密钥不在源码里**，编译期由源生成器从环境变量注入。仓库里搜不到任何 client_id，这是设计如此
- **上游代码和本项目代码混在 `Plain Craft Launcher 2/` 与 `PCL.Core/` 里**。
  改之前先 `git log` 看这行是谁写的，避免把上游的修复覆盖掉

---

## 7. 改界面的规矩

### 7.1 XAML 事件签名（编译期查不出来）

`Click="方法名"` 在 XAML 里只是字符串，**加载页面时才反射匹配**。签名写错不会编译报错，
运行时直接 `XamlParseException` 白屏。第二参数必须对上：

| 控件 | `Click` 的第二参数 |
| --- | --- |
| `MyButton` / `MyExtraButton` / `MyExtraTextButton` / `MyListItem` | `MouseButtonEventArgs` |
| `MyIconTextButton` | `ModBase.RouteEventArgs` |
| `MyIconButton` / `MyTextButton` | `EventArgs` |
| 原生 `Button` | `RoutedEventArgs` |

其他事件：

- `MyCheckBox.Change` → `(object sender, bool user)`
- `MyTextBox.ValidatedTextChanged` → `RoutedEventHandler`
- `MyComboBox.SelectionChanged` → `SelectionChangedEventArgs`

改完 XAML 跑一遍检查脚本：

```bash
python tools/audit-xaml-events.py
```

### 7.2 深色主题

- 原生 `TextBox` / `RichTextBox` **必须显式写** `Foreground="{DynamicResource ColorBrush1}"`，
  否则深色模式下是黑字配深色底，等于看不见。能用 `local:MyTextBox` 就别用原生的，它自带主题背景和圆角
- 禁用态输入框在深色主题下会变成一条黑条，用 `Visibility.Collapsed` 代替 `IsEnabled="false"`
- 文案会变长的按钮别写死 `Width`，用 `MinWidth` + 所在列 `Width="Auto"`

### 7.3 控件选择

- 带 `DynamicResource` 的文本不要塞进 `MyScrollViewer`，它的模板把 `CanHorizontallyScroll` 关了
- 图标放 `PCL.Core/UI/Assets/IconPacks/<包>/<名>.svg`。`lucide/` 是第三方（ISC），
  自制图标放 `brand/`，别混进第三方目录

---

## 8. 加文案的规矩

语言文件在 `PCL.Core/App/Localization/Languages/*.xaml`，四份：

```
zh-CN.xaml  zh-TW.xaml  en-US.xaml  en-GB.xaml
```

- **四份必须同时加**，缺了会回退到 zh-CN（不是报错，是悄悄显示中文，很难发现）
- 键名要短、要有层级，比如 `Lunar.Social.Placeholder`
- 简体中文里带 `{` `}` 的要转义
- code-behind 里的提示文案也要走 `Lang.Text("...")`，不要硬编码中文字符串
  （诊断用的 `Log()` 行除外，那个保持中文，方便看日志）

改完自查键差集（没有现成脚本，用这段一把梭，以 zh-CN 为基准）：

```bash
python - <<'PY'
import re, pathlib
d = pathlib.Path("PCL.Core/App/Localization/Languages")
keys = {p.stem: set(re.findall(r'x:Key="([^"]+)"', p.read_text(encoding="utf-8-sig")))
        for p in d.glob("*.xaml")}
base = keys["zh-CN"]
print("zh-CN 共", len(base), "键")
for name, ks in keys.items():
    if name == "zh-CN":
        continue
    print(f"{name}: {len(ks)} 键  缺 {len(base - ks)}  多 {len(ks - base)}")
    for k in sorted(base - ks)[:10]:
        print("   缺:", k)
PY
```

四份键数应当完全一致，缺的会回退到 zh-CN。

---

## 9. 测试怎么跑

```bash
dotnet test PCL.Core.Test
```

**已知的环境性失败**：全量 `dotnet test` 有几十条红，都不是代码问题，是本机环境导致的：

- `UriFormatException: Invalid port` —— 某些测试依赖注入进来的代理环境变量，格式不合规就炸
- LiteDB 并发文件锁 —— 多个测试同时开数据库文件
- `EventBus` 未初始化 —— 需要 UI 生命周期的测试在纯 CLI 下跑不起来

判断标准：**你改的那块对应的测试过了就行**，其余红的和你的改动无关就不用管。
实在不放心，先在不改代码的干净分支上跑一遍，把红的记下来做基线。

`LocalizationTest.FontProfileShouldFollowCultureGlyphStandard` 是稳定红的，忽略它。

---

## 10. 绝对不要提交的东西

| 东西 | 为什么 |
| --- | --- |
| 任何 `client_id` / API key / 密钥 | 密钥是编译期注入的，源码里本来就没有。提交上去等于泄露 |
| `update.local.json` | 已经 gitignore 了，这是你本地的调试开关 |
| `publish-x64/`、`.release/`、`bin/`、`obj/` | 构建产物，体积大且没意义 |
| 签名后的 exe / pfx 证书 | 私钥绝不能进仓库 |
| `%APPDATA%\PCLLite*` 里的配置和缓存 | 那是运行时数据，不是源码 |

提交前 `git status` 扫一眼，看到 `?? publish-x64/` 之类的东西不要 `git add .`。

---

## 11. PR 提交前清单

- [ ] 从 `main` 切的分支，PR 提到 `main`
- [ ] 一个 PR 只做一件事，提交信息说清了"改了什么、为什么"
- [ ] 本地能跑起来，改的功能自己点过一遍
- [ ] 没改 `update.json` / `metadata.json` / `ProjectLinks.cs` 的仓库常量
- [ ] 加了文案的话，四个语言文件都加了
- [ ] 改了 XAML 的话，`python tools/audit-xaml-events.py` 通过
- [ ] 深色模式下看过一眼（改界面的话）
- [ ] `git status` 里没有构建产物和本地调试文件
- [ ] 对应的单元测试过了（其余红的按第 9 节判断）

PR 描述里写清楚：

1. 解决什么问题（最好带复现步骤或截图）
2. 怎么改的（关键文件、思路）
3. 怎么验证的（跑了什么、看到了什么）

---

## 12. 常见坑

**改完代码运行还是旧行为**
Release 版和 Debug 版是两个数据目录，也可能你开着的还是正式版实例。先全关掉再跑。

**改了源生成器相关的东西（配置项、`PCL_*` 环境变量）没生效**
源生成器只在 `PCL_WRITE_SECRET` 存在时才写 `SecretDictionary`，而且**增量编译不会重跑生成器**：

```bash
dotnet build-server shutdown
dotnet build ... -t:Rebuild
```

**页面一进去就白屏 / 抛 `XamlParseException`**
99% 是 XAML 事件签名对不上，看第 7.1 节的表。

**更新检查一直转圈**
国内直连 `raw.githubusercontent.com` 基本不通，客户端会并发抢答多个通道。
本机有代理的话，可以设 `PCL_TEST_PROXY=http://127.0.0.1:7890` 再试。

**单例锁卡住**
`%LOCALAPPDATA%\PCLLite\instance.lock` 靠文件句柄互斥，强杀进程不会留死锁。
如果真卡了，确认没有残留进程，删掉这个文件即可。

**合并时冲突一片**
先把 `main` 上的改动 rebase 下来再改，不要拖很久才 PR。
