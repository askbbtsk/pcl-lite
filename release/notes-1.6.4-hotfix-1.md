<summary>1.6.4-hotfix-1：删掉液态玻璃材质。设置页那组开关与三个滑块、着色器、挂载器、四语言文案一并移除，界面上不再有这个选项。</summary>

PCL Lite 1.6.4-hotfix-1。

> 热修复，稳定版通道会收到。已装 1.6.4 的用户直接更新即可。

## 删掉液态玻璃

`1.6.3` 起加过一个可选的「液态玻璃」材质：给按钮、药丸形导航、圆形图标按钮
叠一层像素着色器，做边缘折射、色散和内侧高光。

现在整块移除。

**去掉的东西**：

| 位置 | 内容 |
| --- | --- |
| `PCL.Core/UI/Effects/LiquidGlassEffect.cs` | 着色器本体，209 行 |
| `Plain Craft Launcher 2/Modules/UI/Theme/GlassHelper.cs` | 挂载器：找控件、建 Adorner 覆盖层、拖滑块时刷新参数，305 行 |
| `PCL.Core/App/Config.cs` | `UiLiquidGlass` / `UiLiquidGlassRefraction` / `UiLiquidGlassHighlight` / `UiLiquidGlassDispersion` 四个配置项 |
| `PageSetupUI.xaml` | 设置页那一个复选框 + 三个滑块（高光亮度 / 边缘折射 / 色散） |
| `PageSetupUI.xaml.cs` | `LiquidGlass_Change`、`GlassParam_Change` 两个事件处理器，以及初始化与滑块提示文案 |
| `MyButton` / `MyIconButton` / `MyRadioButton` | 构造函数里挂的 `Loaded += GlassHelper.Apply(this)` |
| 四语言文件 | `Setup.Ui.Basic.LiquidGlass*` 共 8 个键 × 4 份 |

**为什么删**：这个材质只挂在圆角半径 ≥ 8 的控件上，方角按钮的边缘太长，玻璃会在描边上
糊成一条脏亮边，所以本来就得跳过一大半控件；实际效果也没达到预期，而代价是每次开关都要
遍历整个视觉树重建覆盖层。留着不如去掉。

## 老配置不用管

以前开过这个选项的用户，配置里会残留 `UiLiquidGlass*` 几个键。对应的配置项已经不存在了，
读配置时会被直接忽略，不需要手动清理，也不会影响启动。

## 校验值

```
SHA-256  9F4E1E5622981250FDC4D1F5A5185F96B206BC127929D38E1EA9EAE49C217505
```

## 已知问题

同 1.6.4，未新增。

## 许可证

本项目以 MIT 许可证发布，基岩版下载、安装与启动部分移植自
[PCL-Symbio-Edition](https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition)（Apache-2.0），
完整署名见 `docs/CREDITS.md`。
