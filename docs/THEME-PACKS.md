# PCL Lite 外观主题 API v1

外观主题扩展现有 `ThemeService`、配色资源与 PCL 控件，不重新实现启动器的导航、业务页面或输入逻辑。主题安装在 `Paths.Data/Themes/<id>/`，个性化设置提供打开目录、文件/目录安装、选择、手动重载和恢复内置主题。

主题下拉框第一项固定为“内置主题”，选择即可恢复启动器原有外观，不再提供独立的“恢复内置主题”按钮。

**第一次编辑 XAML？** 先看 [入门步骤](theme-examples/README.md) 和 [带中文注释、可直接安装的 StarterTheme.xaml](theme-examples/StarterTheme.xaml)。启动器的“打开注释示例”会导出同一份示例并打开目录，不覆盖你已编辑的副本。使用记事本／VS Code 修改后保存，再手动重新加载预览。

## 安装与恢复

- 将主题目录或根目录有 `theme.json` 的 ZIP 安装到列表。将根元素为 `ResourceDictionary` 的 `.xaml` 拖入启动器，可直接安装并预览；自定义主页 XAML 继续走主页导入流程。
- 单文件导入只复制所引用的字典、图片和字体。作者及兼容版本未声明时照实显示，不从文件名猜作者。源文件不移动、不改写。
- 同 ID 替换需要确认。先验证所有模式，再暂存、核对摘要并替换；失败保留已安装版本。
- 切换/重载后在宿主确认条中选择保留。15 秒未确认或取消就恢复上一次外观；安装内容仍在列表。确认条位于主题资源作用域之外，主题不能覆盖它。
- 未确认的预览或启动加载中断后，下次使用内置外观进入。主题内容被外部修改时，不直接执行上次保存的版本，需重新选择并预览。
- 高对比度下暂停自定义资源，恢复内置可读性。玻璃支持由主题声明，但不能绕过用户已有的性能/桌面采样授权。

## 清单

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "author.theme-name",
  "name": "主题名称",
  "author": "作者",
  "version": "1.0.0",
  "description": "主题介绍",
  "launcherVersion": ">=1.7.7 <2.0.0",
  "entry": "Theme.xaml",
  "light": "Light.xaml",
  "dark": "Dark.xaml",
  "supportsGlass": true,
  "glass": "Glass.xaml"
}
```

`light`、`dark`、`glass` 和 `launcherVersion` 可省略；默认不支持玻璃。所有路径是主题内相对路径，不接受绝对路径、网络 URL、链接、`..` 或程序集资源 URI。

版本范围复用 `PluginVersionRange` 的语法：精确 SemVer、比较条件、`^`、`~` 和 `*`。若需包含 `1.7.7-hotfix-*` 等带后缀版本，下界可写 `>=1.7.7-0`，示例主题已采用这一范围。不匹配时提示当前版本与范围，用户可强制预览。确认仅对当前启动器完整版本与主题内容摘要有效，任一变化后重新提示。强制加载不跳过格式、依赖及模板契约验证。

## 公共入口与资源优先级

- `ThemeService.IsDarkMode`、`CurrentTheme`、`CurrentTone`、`ColorModeChanged`、`ColorThemeChanged` 保持现有语义。
- `ThemeService.Packages` 提供 `IThemePackageHost`：主题目录、活动主题、预览状态、安装列表、安装、预览、恢复及 `Changed` 事件。主窗口准备前为 null；API 不要求插件存在。
- 文件/解析模型在 `PCL.Core.UI.Theme.Packages`；主题呈现控件为 `PCL.Core.UI.Theme.ThemePresenter`。
- 资源覆盖顺序为内置默认、现有配色/深浅色、主题公共入口、当前深浅色变体、可用的玻璃变体。缺少自定义项时使用宿主原有样式。
- 资源字典与视觉替换不改写正常配置。可选主题选择状态单独存储于 `Paths.Data/theme-selection.json`。

## 模板与内容槽

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:theme="clr-namespace:PCL.Core.UI.Theme;assembly=PCL.Core">
  <ControlTemplate x:Key="Pcl.Theme.Template.Button"
                   TargetType="{x:Type theme:ThemePresenter}">
    <Border CornerRadius="12" BorderThickness="1" BorderBrush="{DynamicResource ColorBrush3}">
      <ContentPresenter Content="{TemplateBinding Content}" />
    </Border>
  </ControlTemplate>
</ResourceDictionary>
```

`ThemePresenter` 是内部呈现层，外围原 PCL 控件继续持有事件、鼠标/键盘操作及业务状态。保留 `Content` 槽是必要契约：它承载已有文本、图标或子操作，不能因换外观删掉这些功能。卡片只替换背景层，公开 `Children` 与折叠对象身份不变。

| 模板键后缀 | 呈现对象/必需内容 |
| --- | --- |
| `Button`、`IconButton`、`IconTextButton`、`ExtraButton`、`ExtraTextButton` | `ThemePresenter`；保留 Content 槽 |
| `Navigation`、`CheckBox`、`RadioBox`、`ListItem`、`Slider` | `ThemePresenter`；保留 Content 槽及原控制项操作 |
| `Card`、`Hint`、`Toast` | `ThemePresenter`；卡片原内容/折叠由宿主维护 |
| `TextBox` | `TextBox` 或 `local:MyTextBox` 模板；必须有 `ScrollViewer PART_ContentHost` |
| `ComboBox` | `ComboBox` 或 `local:MyComboBox` 模板；`Popup PART_Popup`、`local:MyTextBox PART_EditableTextBox`、`ContentPresenter PART_Content`、`Grid PanPopup` |
| `ComboBoxItem`、`ScrollBar`、`ScrollViewer`、`MenuItem`、`TextButton` | 对应原生基类/PCL 控件模板；ScrollBar 必须有 `PART_Track` |

本地 PCL 类型命名空间为 `xmlns:local="clr-namespace:PCL;assembly=Plain Craft Launcher 2"`。类型仅可作为模板目标；可编辑组合框内部可构造 `local:MyTextBox`，不开放任意宿主对象构造。

`ThemePresenter` 提供 `Title`、`Description`、`Role`、`IconData`、`Progress`、`IsEnabled`、`IsPressed`、`IsSelected`、`IsChecked`、`IsExpanded`、`IsInvalid`、`IsHighlighted`、`IsDanger`、`IsGlass`、`HasFocus`、`IsHovered` 等呈现属性，以及 Content、Padding、CornerRadius、字体和颜色属性。`HasFocus`/`IsHovered` 表示外围宿主的焦点/悬停，而非要求呈现层抢占焦点。外部模板仅单向读取这些状态，不改变所属控件的业务状态。

`Pcl.Theme.<角色>.Padding` 和 `.CornerRadius` 为可选呈现参数，默认保留原始控件布局。`Pcl.Theme.FontSize` 为可选继承字体大小。现有 `ColorBrush*`、`Tahoe*`、`LaunchFontFamily` 仍可覆盖；推荐用 DynamicResource 保证即时模式切换。

文本框的 `labHint`、`labWrong` 是可选提示/校验显示部件。缺省时仍可输入与校验，但主题应提供以呈现完整反馈。主题切换会重新找部件，避免引用旧模板对象。可编辑下拉框会解除旧编辑器的事件订阅再绑定新编辑器，防止反复切换导致重复通知。

图标可用 `Pcl.Theme.Icon.<图标ID>` 的 Geometry 资源替换 SVG 外观，例如 `Pcl.Theme.Icon.lucide/search`。保留原图标控件、布局与操作，仅替换绘制路径。

## 支持的 XAML 子集

- 视觉类型：Grid/Border/Panel/TextBlock/Path 等布局与图形，ContentPresenter、ItemsPresenter、滚动与下拉模板部件，Brush/Geometry/Transform、有限模糊与阴影，以及 Style/ControlTemplate/DataTemplate/Trigger。
- 资源扩展：StaticResource、DynamicResource、TemplateBinding、受限 Binding/RelativeSource、x:Type 与 x:Null。绑定只能读取公开呈现属性、Self、TemplatedParent 或模板内命名元素；不接受 Converter、XPath、外部 Source、双向写回和任意对象路径。
- 动画：Double/Color/Thickness/PointAnimation，单次、有限时长，每段最多 30 秒；Storyboard 仅驱动视觉变换/颜色/透明度。Loaded 可触发视觉动画，禁止事件处理程序。
- 图片用 `Image Source="Images/file.png"` 或 `ImageBrush ImageSource="Images/file.png"`；从快照解码并冻结，不持有主题图片文件句柄。字体使用 `FontFamily="Fonts/file.ttf#Family Name"`，复制到内容寻址的临时字体目录供 WPF 使用，不安装到系统字体库。
- 不支持任意 XAML：Code、Class、FactoryMethod、Static、ObjectDataProvider、外部程序集、网页/媒体/窗口及远程资源均拒绝。在 WPF 实例化前按正向类型/成员清单检查，不能把 XAML 当作插件脚本使用。
- 上限：每文件 8 MiB、清单 64 KiB、依赖总计 128 MiB、256 文件、单字典 16000 节点/64 层、整包 24000 节点、展开后 48000 节点、字典依赖 32 层；图片单边最多 8192 像素，总解码预算 32M 像素。重复合并字典同样计入展开预算，避免小文件形成指数展开。

完整允许项以 `ThemeXamlPolicy` 中公开维护的正向契约为准。失败提示包含文件、行列及原因。资源、控件模板和模式变体先验证后预览；这不能保证恶意或极端视觉布局在所有显卡上都不会造成性能问题。

## 示例与验证

[`examples/themes/SoftSlate`](../examples/themes/SoftSlate) 提供公共、浅色、深色和玻璃变体，可安装目录后直接修改并手动重载。先在副本上编辑，不需要重新编译启动器。

CI 执行文件/安装/兼容/解析测试、WPF 呈现及事件烟测和 x64/ARM64 构建。真实输入法、第三方模板、系统高对比度、玻璃与不同 DPI 的最终视觉结果需单独验收；测试通过不等于所有用户主题都适用。
