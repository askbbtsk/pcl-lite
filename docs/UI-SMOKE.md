# 发布前 UI 原生控件检查

PR CI、main/dev CI 和 Release 发布均运行 `UI smoke (native controls)`。检查失败后，发布构建、草稿创建、签名和正式发布均不能继续。无需先合并代码才发现界面引入了原生按钮。

在仓库根目录、Windows + .NET 10 SDK 环境运行：

```powershell
dotnet run --project tools/UiSmoke/UiSmoke.csproj -- --self-test
dotnet run --project tools/UiSmoke/UiSmoke.csproj --no-build
```

扫描启动器与 Core 的手写 XAML / C#，排除 bin、obj、publish。XAML 使用 XML 命名空间识别实例化元素；C# 使用 Roslyn 语义分析识别 WPF 类型，包括别名、完整类型名和目标类型推断的 new，分别检查 DEBUG、CI、RELEASE、BETA。常量字符串形式的 XamlReader.Parse/Load 也检查。发现违规输出 GitHub `error` 注释，包含文件、行号和控件类型，并返回非零退出码。

禁止直接使用 Button、RepeatButton、ToggleButton、CheckBox、RadioButton、ComboBox、TextBox、RichTextBox、PasswordBox、Slider、ListBox、ListView、TreeView、DataGrid、TabControl、ProgressBar、DatePicker、Calendar。即使附加 Style，也不自动放行；优先使用 PCL 自有控件。Grid、Border、TextBlock、图片、滚动容器以及 Style/ControlTemplate 的 TargetType 声明不属于这些禁止实例。控件继承 WPF 基类是正常实现方式，不等于直接创建原生控件。

现有安全密码输入、只读日志及 PCL 控件模板内部的原语登记在 `tools/UiSmoke/exceptions.json`。每项绑定文件、类型、完整元素 SHA-256、数量与原因。增加相同元素、移动文件、修改元素、删除后遗留例外都会失败；没有目录、文件通配或“模板全部放行”的豁免。例外变更须人工核对具体用途，不能为了让 CI 变绿批量更新。

该门禁是源码静态检查，不启动真实游戏或页面。动态拼接 XAML、反射创建、外部插件运行时 UI 以及完整主题/焦点交互仍需运行时验收；检查通过不代表所有界面已经视觉验收。
