# 主题与配色系统

改界面颜色之前先读这篇。整套配色不是写死在 XAML 里的，而是运行时算出来、再塞进 WPF 资源字典的。

代码位置：`PCL.Core/UI/Theme/`

| 文件 | 职责 |
| --- | --- |
| `ToneProfile.cs` | 灰度档位（八级明度 + 若干透明度），决定「深色有多深」 |
| `ToneProfileConfig.cs` | 浅色/深色两套档位的默认值 |
| `PclThemeEngine.cs` | 原版 PCL2 配色引擎的移植 + 14 个预设表 |
| `ThemeService.cs` | 总调度：读配置、算颜色、写资源字典、发事件 |
| `LabColor.cs` | OKLab / OKLCH 工具（内置 4 主题走这条路） |
| `CatColorResource.cs` | 一条颜色资源（scRGB 分量 + 资源键后缀） |
| `SystemThemeHelper.cs` | 跟随系统深浅色 |

---

## 一、两条并存的配色路径

`ThemeService.ApplyColorResources()` 里只有一个分支判断：

```csharp
if (GetCustomThemeArgs() is { } custom)   // UiCustomTheme = true（默认）
    PclThemeEngine.Generate(custom, IsDarkMode, CurrentTone);   // 路径 A：HSL 引擎
else
    _CalculateColors(CurrentTone, GetCurrentThemeArgs());        // 路径 B：内置 4 主题
```

| | 路径 A（默认） | 路径 B |
| --- | --- | --- |
| 触发条件 | `UiCustomTheme = true` | `UiCustomTheme = false` |
| 色彩空间 | HSL + 14 段感知明度校正 | OKLCH |
| 可选色相 | 0–360 全色环 | 仅 4 个枚举值 |
| 预设 | `PclThemePresets` 14 个 | `ColorTheme` 4 个 |

**路径 B 的 4 个主题**（`ConfigEnums.cs`）：

| 枚举 | 值 | 色相 | 明度调整 | 彩度调整 |
| --- | --- | --- | --- | --- |
| `SkyBlue` | 0 | 235 | +0.36 | +0.20 |
| `CatBlue` | 1 | 255 | 0 | −0.20 |
| `DeathBlue` | 2 | 268 | −0.05 | −0.10 |
| `HmclBlue` | 3 | 275 | −0.03 | −0.35 |

> `HmclBlue` 只在愚人节生效，其余时间会被 `_AprilFoolLogic()` 打回 `CatBlue`。
> 也就是说正常情况下你只会看到 3 个内置主题——这是上游行为，不是 bug。

日常改配色走路径 A。下面全部按路径 A 讲。

---

## 二、八级色阶

界面所有彩色都来自 8 个色阶，编号 `1`–`8`，从深到浅（浅色模式）：

| 级别 | 浅色模式明度 | 深色模式明度 | 用途 |
| --- | --- | --- | --- |
| 1 | 35% | 96% | 标题栏、强调块 |
| 2 | 50% | 75% | 按钮主色 |
| 3 | 57.5% | 60% | 按钮悬停 |
| 4 | 65% | 65% | 边框、进度条 |
| 5 | 80% | 45% | 浅底强调 |
| 6 | 92% | 25% | 卡片背景 |
| 7 | 94% | 22.5% | 分隔线、半透明白 |
| 8 | 96% | 20% | 页面背景 |

生成时的三个特殊处理（`PclThemeEngine.Generate`）：

1. **一级色阶压饱和度**：`sat * 0.2`。不压的话深色底会发脏。
2. **明度调整量按级递减**：1 级吃 30%、2/3/4 级吃 100%、5 级吃 40%、6 级吃 10%、7/8 级不吃。
   这样调亮度时不会把背景一起带跑。
3. **不能按 `100 - l` 翻转深浅**：深色档位是独立的一组数（见上表），
   硬翻转会把玄素黑的标题栏翻成浅灰，或者让深色模式下 `ColorBrush1` 变深、文字直接看不见。

### 感知明度校正

同一个明度值，不同色相看起来亮暗差很多（黄色天生比蓝色亮）。原版用一张 14 个采样点的校正表
（每 30° 一个）把色相拉到视觉一致：

```
0.1, -0.06, -0.3, -0.19, -0.15, -0.24, -0.32, -0.09, 0.18, 0.05, -0.12, -0.02, 0.1, -0.06
```

`FromHsl2(h, s, l)` 先按这张表把 `l` 映射成视觉等效值，再走标准 HSL 转 RGB。
**这是原版观感一致的关键，别为了"简化"把它删了。**

### Bg0 的合成

`Bg0`（页面主背景）不是某一级色阶，而是三个颜色加权：

```
Bg0 = 色阶4 × 0.4 + 色阶5 × 0.4 + 灰阶4 × 0.2
```

混进 20% 纯灰是为了降低背景的彩度，避免大面积纯色看着累。

---

## 三、灰度档位 ToneProfile

彩色管"是什么色"，灰度管"有多深"。`ToneProfile` 是一个 record，字段含义：

| 字段 | 含义 |
| --- | --- |
| `L1`–`L8` | 八级灰阶明度（同时被彩色色阶当基准明度用） |
| `LWhite` / `LForeground` / `LBackground` | 纯白、前景文字、背景 |
| `C1`–`C8` | 路径 B 用到的彩度 |
| `ASemiWhite` / `AHalfWhite` / `ASemiTransparent` / `AHalfTransparent` / `ATransparent` | 各种透明度 |
| `ABackground` / `AToolTip` | 背景、提示框的透明度 |

默认值（`ToneProfileConfig`）：

```csharp
// 浅色
L1=0.35 L2=0.5 L3=0.575 L4=0.65 L5=0.8 L6=0.92 L7=0.94 L8=0.96
LWhite=1 LForeground=0 LBackground=0.995

// 深色（只覆盖差异项，其余沿用浅色的默认值）
L1=0.96 L2=0.75 L3=0.6 L4=0.65 L5=0.45 L6=0.25 L7=0.225 L8=0.2
LBackground=0.3 LForeground=1 LWhite=0.275
```

### 不改代码调灰度

`ToneProfiles` 是普通配置项，键名 `UiToneProfiles`，存在本地配置里（见 [CONFIG.md](CONFIG.md)）。
想整体加深、调白底亮度，改这个就够了，不用重编译：

```yaml
UiToneProfiles:
  Light:
    L1: 0.35
    L6: 0.9      # 卡片背景压暗一点
  Dark:
    L1: 0.94
    LBackground: 0.25
```

改完调 `ThemeService.InvalidateGrayCache()` 才会重算——启动时自动调，运行中热改需要重启。

---

## 四、标题栏渐变

三种形态，由 `PclThemeArgs` 的三个可选参数决定：

| 形态 | 参数 | 色标 | 谁在用 |
| --- | --- | --- | --- |
| 对称渐变（默认） | `TopbarDelta = d` | `h-d` / `h` / `h+d`，明度 48/54/48 | 大多数预设 |
| 非对称三元组 | `TopbarHueTriple = [a,b,c]` | `h+a` / `h+b` / `h+c` | 小草绿 `[5,-5,5]`、滑稽彩 `[-30,10,50]` |
| 固定明度三元组 | `TopbarLightTriple = [x,y,z]` | 明度直接取绝对值，**不叠加**明度调整 | 玄素黑 `[25,15,25]`（中间更暗） |
| 四点宽渐变 | `TopbarWide = true` | `h±21 / h±7`，明度 53/47/47/53 | 欧皇彩 |

优先级：`TopbarWide` > `TopbarLightTriple` > `TopbarHueTriple` > `TopbarDelta`。
`d = 0` 时退化成纯色（三个色标同色）。

**标题栏明度不随深浅模式翻转**——这是原版行为，深色模式下标题栏依然是中亮度彩色，才有辨识度。

### 背景渐变

`CreateBackgroundBrush`：三点 `h-15 / h / h+15`，饱和度压到 80%，明度浅色 91 / 深色 9。
只在 `UiBackgroundColorful = true` 时启用。

---

## 五、14 个预设

`PclThemePresets` 里两个列表，`Main` 是设置页直接显示的，`Hidden` 需要解锁（本项目已全部放开）。

| 编号 | 名称 | H | S | 明度调整 | 标题栏 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 龙猫蓝 | 210 | 85 | 0 | 对称 0 | 默认 |
| 1 | 甜柠青 | 180 | 50 | 0 | 对称 0 | |
| 2 | 小草绿 | 130 | 40 | 0 | 三元 `[5,-5,5]` | |
| 3 | 菠萝黄 | 75 | 50 | 0 | 对称 0 | |
| 4 | 橡木棕 | 35 | 30 | 0 | 对称 0 | |
| 5 | 玄素黑 | 225 | 10 | −20 | 固定明度 `[25,15,25]` | 中间更暗 |
| 6 | 铁杆粉 | 345 | 70 | +15 | 对称 0 | |
| 7 | 神秘紫 | 285 | 50 | 0 | 对称 0 | |
| 8 | 秋仪金 | 50 | 65 | +10 | 对称 0 | |
| 9 | 活跃橙 | 25 | 75 | +5 | 对称 0 | |
| 10 | 跳票红 | 5 | 70 | −5 | 对称 0 | |
| 11 | 极客蓝 | 230 | 60 | −5 | 对称 0 | |
| 12 | 欧皇彩 | 0 | 45 | 0 | 四点宽渐变 | 每次进随机换色 |
| 13 | 滑稽彩 | 290 | 50 | 0 | 三元 `[-30,10,50]` | |
| 14 | 自定义 | — | — | — | — | 由滑块决定 |

> 原版还有个编号 42 的「极客蓝」，参数 `(225, 60, -15)`，和编号 11 重名，本项目没有单独列出。

### 加一个新预设

1. 在 `PclThemePresets.Main` 或 `.Hidden` 里加一行：

   ```csharp
   new(15, "我的主题", new PclThemeArgs(Hue: 300, Saturation: 60, LightAdjust: 5, TopbarDelta: 10)),
   ```

   `Index` 随便取没被占用的数（别用 14，那是「自定义」）。
2. 如果要非对称标题栏，加 `TopbarHueTriple: [a,b,c]`；要固定明度加 `TopbarLightTriple`；
   要四点宽渐变加 `TopbarWide: true`。
3. 设置页的主题列表是遍历 `PclThemePresets.Main` / `.Hidden` 生成的，不用改 XAML。
4. 加文案：`PCL.Core/App/Localization/Languages/` 下四个语言文件都要加一条，
   键名形如 `Setup.Launcher.Theme.{Index}`（照着现有条目抄）。

---

## 六、自定义主题的配置映射

设置页的滑块存的是**偏移过的整数**，不是真实值。换算在 `ThemeService.GetCustomThemeArgs()`：

| 配置键 | 存储范围 | 实际含义 |
| --- | --- | --- |
| `UiLauncherHue` | 0–360 | 直接是色相 |
| `UiLauncherSat` | 0–100 | 直接是饱和度 |
| `UiLauncherLight` | 0–40 | **实际明度调整 = 存储值 − 20**（20 = 不调整） |
| `UiLauncherDelta` | 0–180 | **实际色相偏移 = 存储值 − 90**（90 = 纯色渐变） |
| `UiLauncherDeltaTriple` | 字符串 | 形如 `-30,10,50`，留空表示用对称渐变 |
| `UiLauncherTheme` | 0–14 | 当前选中的预设编号，14 = 自定义 |

「−20 / −90」这两个偏移是原版 PCL2 的历史包袱（滑块最小值对应存储 0），
**改配置时一定要减掉**，否则颜色会偏。

另外：`TopbarLightTriple` / `TopbarWide` 这类结构性参数只有选中预设时才生效。
手动拖滑块会把 `UiLauncherTheme` 打回 14，所以不会出现「自定义参数 + 玄素黑标题栏」的错配。

---

## 七、资源键命名

算出来的颜色最后写进 `Application.Resources`，键名由 `CatColorResource.Apply()` 拼：

```
ColorObject{Suffix}   →  System.Windows.Media.Color
ColorBrush{Suffix}    →  SolidColorBrush
```

`Suffix` 取值：

| 类别 | 键 |
| --- | --- |
| 彩色 | `1`–`8`、`Bg0`、`Bg1`、`SemiTransparent` |
| 灰度 | `Gray1`–`Gray8`、`HalfWhite`、`SemiWhite`、`White`、`Transparent`、`TransparentBackground`、`Background`、`ToolTip`、`RedBack`、`Memory` |

XAML 里这样用：

```xml
<Border Background="{DynamicResource ColorBrush6}"
        BorderBrush="{DynamicResource ColorBrush4}"
        TextElement.Foreground="{DynamicResource ColorBrush1}" />
```

**必须用 `DynamicResource`**，用 `StaticResource` 的话换主题不会跟着变。
`RedBack` 是错误/危险提示的浅红底，`Memory` 是内存占用条的底色，都是单独算的。

---

## 八、常见坑

1. **改了颜色但界面没变** —— 用了 `StaticResource`；或者改的是 `_CalculateColors`（路径 B），
   而默认走的是路径 A。
2. **深色模式下文字看不见** —— 别把 `ColorBrush1` 当"最深的颜色"用。深色模式下 1 级是**最亮**的
   （96%），文字色应该用 `Gray1` / `Foreground` 系列，不是彩色 1 级。
3. **背景发脏** —— 一级色阶的 `sat * 0.2` 被去掉了，或者往 Bg0 里多加了彩色。
4. **标题栏在深色模式下变成浅灰** —— 用了 `100 - l` 翻转而不是独立档位表。
5. **`InvalidOperationException`（资源字典只读）** —— `Apply()` 只能在
   `LifecycleState.Loaded` 之后调用，构造期调会炸。
6. **灰度改了没生效** —— `ToneProfiles` 有缓存（`LightGrayCache` / `DarkGrayCache`），
   热改要调 `InvalidateGrayCache()`。
