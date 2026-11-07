# 快速开始

## 环境

- Windows / WPF
- .NET 10 SDK
- `PCL.Plugin.SDK` 项目引用

复制仓库的 `PCL.Plugin.Sample` 作为起点，保持项目目标框架和 nullable 配置一致。

## 最小插件

```csharp
using PCL.Plugin.SDK;
using System.Windows.Controls;

namespace Example.Plugin;

public sealed class ExamplePlugin : IPclPlugin
{
    public Task StartAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        context.Logger.Info("Example started");
        context.Ui.RegisterPage("home", "Example", () => new TextBlock { Text = "Hello" });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
```

`StartAsync` 保存必要的订阅句柄；`StopAsync` 取消自建工作、释放资源。事件处理可能在非 UI 线程运行，更新 WPF 控件时通过 Dispatcher 切回 UI 线程。所有异步操作都应传递宿主提供的 `CancellationToken`。

## 清单

将 `plugin.json` 放在构建输出目录：

```json
{
  "schemaVersion": 1,
  "id": "example.hello",
  "name": "Example Hello",
  "version": "1.0.0",
  "author": "Example author",
  "description": "A small compiled PCL Lite plugin.",
  "apiVersion": 1,
  "entryAssembly": "Example.Plugin.dll",
  "entryType": "Example.Plugin.ExamplePlugin"
}
```

`id` 使用小写 ASCII 字母、数字、点和连字符；`entryAssembly` 必须是包内 DLL 的安全相对路径。使用 Mixin 时，另外阅读 [Mixin 指南](mixins.md)。

## 构建、签名、打包

请先建立干净的 Release 输出目录，只放插件运行所需的程序集、依赖、资源、`plugin.json` 和 Mixin 配置。`PCL.Plugin.Tools pack` 会读取输入目录中的所有普通文件（仅跳过已有 `signature.json`），不会自动排除 PDB、XML 文档、测试程序集、测试数据或大型资源；这些文件会计入签名和包大小。不要直接把整个 Debug/测试输出目录发布到市场。打包器会拒绝符号链接，并在压缩包超过 100 MiB 或解压载荷超过 500 MiB 时失败。

请使用仓库工具，不要手工拼签名 JSON：

```powershell
dotnet build .\Example.Plugin.csproj -c Release
dotnet run --project .\PCL.Plugin.Tools -- cert "CN=Example Hello" .\secrets\example-signing.pfx
dotnet run --project .\PCL.Plugin.Tools -- pack .\staging .\secrets\example-signing.pfx .\dist\example.pclplugin
```

工具会交互读取 PFX 密码，不要把密码写进命令行、源代码或仓库。对安装弹窗显示的签名人、签名时间和包信息进行检查；本地自签名只提供完整性校验，不证明现实身份。详见[包格式与安全](packaging-security.md)。

## 手动安装与调试

在启动器的插件页面选择本地 `.pclplugin` 安装。启用、禁用、更新、实验功能开关和卸载后重启启动器。测试时使用临时插件 ID、独立数据目录和测试签名证书，避免覆盖日常安装。
