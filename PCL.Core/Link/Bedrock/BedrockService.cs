using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PCL.Core.Link.Bedrock;

/// <summary>
///     基岩版安装类型。
/// </summary>
public enum BedrockKind
{
    /// <summary>微软商店（UWP / MSIX）安装的正式版或预览版，向系统注册了包。</summary>
    Store,

    /// <summary>GDK 便携版：解包后的独立目录，直接运行目录内的 <c>Minecraft.Windows.exe</c>，不注册系统包。</summary>
    GdkPortable
}

/// <summary>
///     一个可启动的基岩版安装。
/// </summary>
public sealed record BedrockInstall
{
    /// <summary>唯一标识，用于列表去重与点击回调。</summary>
    public required string Id { get; init; }

    /// <summary>显示名称。</summary>
    public required string DisplayName { get; init; }

    public required BedrockKind Kind { get; init; }

    /// <summary>版本号；便携版取自主程序文件版本，取不到时为空。</summary>
    public string Version { get; init; } = "";

    /// <summary>GDK 便携版的主程序路径。</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>商店版的包家族名（<c>PackageFamilyName</c>）。</summary>
    public string? PackageFamilyName { get; init; }

    /// <summary>商店版的 Application Id，启动时要拼进 <c>shell:appsFolder</c>。</summary>
    public string? AppId { get; init; }
}

/// <summary>
///     基岩版（Bedrock Edition）检测与启动。
///     <para>
///         移植自 <see href="https://github.com/Hydrogen-Peroxide/PCL-Symbio-Edition">PCL-Symbio-Edition</see>
///         （Apache-2.0）的检测与启动部分，按 PCL Lite 的结构重写。
///         下载与安装不在这里：商店包由 <c>ModBedrockUwp</c> / <c>ModBedrockInstaller</c> 处理，
///         GDK 包由 <c>ModBedrockGdk</c> 解包，版本列表由 <see cref="BedrockVersionCatalog" /> 提供。
///     </para>
/// </summary>
public static class BedrockService
{
    /// <summary>正式版的包名。</summary>
    public const string ReleasePackageName = "Microsoft.MinecraftUWP";

    /// <summary>预览版的包名。</summary>
    public const string PreviewPackageName = "Microsoft.MinecraftWindowsBeta";

    /// <summary>GDK 便携版的主程序文件名。</summary>
    public const string ExecutableName = "Minecraft.Windows.exe";

    /// <summary>基岩版进程名；UWP 与 GDK 用的是同一个可执行文件名。</summary>
    private static readonly string[] ProcessNames = ["Minecraft.Windows", "Minecraft.Win10.DX11"];

    /// <summary>
    ///     检测所有可启动的基岩版。
    /// </summary>
    /// <param name="extraDirectories">额外的 GDK 便携版目录（会同时扫描其一级子目录）。</param>
    /// <param name="error">检测过程中的错误信息；全部成功时为 <c>null</c>。</param>
    public static IReadOnlyList<BedrockInstall> Detect(IEnumerable<string>? extraDirectories, out string? error)
    {
        error = null;
        var result = new List<BedrockInstall>();

        try
        {
            result.AddRange(DetectStorePackages());
        }
        catch (Exception ex)
        {
            // 商店版查不到不该影响便携版，分开兜底
            error = ex.Message;
        }

        result.AddRange(DetectPortable(extraDirectories));
        return result;
    }

    /// <summary>
    ///     检测启动器自己下载并解包出来的 GDK 版本（<c>Bedrock/versions/&lt;版本号&gt;/</c>）。
    ///     这些版本不向系统注册，靠目录区分，所以只能由启动器自己记着。
    /// </summary>
    public static IReadOnlyList<BedrockInstall> DetectDownloaded(string versionsDirectory)
    {
        var list = new List<BedrockInstall>();
        if (string.IsNullOrWhiteSpace(versionsDirectory) || !Directory.Exists(versionsDirectory)) return list;

        string[] children;
        try
        {
            children = Directory.GetDirectories(versionsDirectory);
        }
        catch (Exception)
        {
            return list;
        }

        foreach (var child in children)
        {
            var exe = FindExecutable(child);
            if (exe is null) continue;

            // 解包中断或密钥不匹配时，目录里主程序是好的、DLL 全是乱码。
            // 这种半成品不能当成可启动版本列出来，否则用户点进去只会看到「损坏的映像」。
            if (!LooksRunnable(child)) continue;

            list.Add(new BedrockInstall
            {
                Id = "downloaded:" + child,
                DisplayName = $"Minecraft {new DirectoryInfo(child).Name}（已下载）",
                Kind = BedrockKind.GdkPortable,
                Version = ReadFileVersion(exe),
                ExecutablePath = exe
            });
        }

        return list;
    }

    /// <summary>
    ///     粗判一个解包目录是否真的能跑：抽查根目录的 DLL 头部。
    ///     <para>GDK 的 DLL 全在主程序同级目录，正常解包一定是 <c>MZ</c> 开头。</para>
    /// </summary>
    private static bool LooksRunnable(string directory)
    {
        try
        {
            var sawAny = false;
            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                sawAny = true;
                using var stream = File.OpenRead(dll);
                Span<byte> head = stackalloc byte[2];
                if (stream.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z') return true;
            }

            return !sawAny;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    ///     启动指定的基岩版。
    ///     <para>
    ///         商店版走 <c>explorer.exe shell:appsFolder\&lt;包家族名&gt;!&lt;Application Id&gt;</c>；
    ///         便携版直接起主程序。两者都不需要管理员权限。
    ///     </para>
    /// </summary>
    public static bool Launch(BedrockInstall install, out string? error)
    {
        error = null;
        try
        {
            if (install.Kind == BedrockKind.GdkPortable)
            {
                if (install.ExecutablePath is not { } exe || !File.Exists(exe))
                {
                    error = "找不到游戏主程序，可能已被移动或删除";
                    return false;
                }

                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
                    UseShellExecute = true
                });
                return true;
            }

            if (string.IsNullOrWhiteSpace(install.PackageFamilyName))
            {
                error = "缺少包家族名，无法定位已安装的商店版";
                return false;
            }

            // Application Id 由包清单决定（基岩版是 Game 而不是 App）。
            // 拼错不会报错，只会让资源管理器打开「文档」文件夹，所以这里必须有兜底值。
            var appId = string.IsNullOrWhiteSpace(install.AppId) ? "Game" : install.AppId;
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"shell:appsFolder\\{install.PackageFamilyName}!{appId}")
            {
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>结束所有正在运行的基岩版进程，返回实际结束的数量。</summary>
    public static int StopAll()
    {
        var count = 0;
        foreach (var name in ProcessNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                    count++;
                }
                catch (Exception)
                {
                    // 权限不足或已经退出，跳过
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return count;
    }

    /// <summary>当前是否有基岩版在运行。</summary>
    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) return true;
            }
            catch (Exception)
            {
                // 忽略
            }
        }

        return false;
    }

    /// <summary>
    ///     商店版的存档目录。装了正式版就返回正式版的，否则返回预览版的；
    ///     都没装时返回正式版路径（目录可能不存在，由调用方判断）。
    /// </summary>
    public static string GetSavesDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packages = Path.Combine(localAppData, "Packages");
        string[] families = [$"{ReleasePackageName}_8wekyb3d8bbwe", $"{PreviewPackageName}_8wekyb3d8bbwe"];

        foreach (var family in families)
        {
            var path = Path.Combine(packages, family, "LocalState", "games", "com.mojang", "minecraftWorlds");
            if (Directory.Exists(path)) return path;
        }

        return Path.Combine(packages, families[0], "LocalState", "games", "com.mojang", "minecraftWorlds");
    }

    /// <summary>
    ///     查已注册的商店版包。走 PowerShell 是因为 <c>PackageManager</c> 需要额外引用 WinRT 投影，
    ///     而上游也是这么做的，行为已经验证过。
    /// </summary>
    private static List<BedrockInstall> DetectStorePackages()
    {
        const string script = """
                              [Console]::OutputEncoding=[Text.Encoding]::UTF8
                              $names=@('Microsoft.MinecraftUWP','Microsoft.MinecraftWindowsBeta')
                              $out=@()
                              foreach ($p in Get-AppxPackage) {
                                if ($names -contains $p.Name) {
                                  $appId='Game'
                                  try {
                                    $ids=@((Get-AppxPackageManifest $p).Package.Applications.Application.Id)
                                    if ($ids.Count -gt 0) { $appId=$ids[0] }
                                  } catch {}
                                  $out += [pscustomobject]@{
                                    name=$p.Name
                                    version=$p.Version.ToString()
                                    family=$p.PackageFamilyName
                                    appId=$appId
                                  }
                                }
                              }
                              ConvertTo-Json -InputObject $out -Compress
                              """;

        var json = RunPowerShell(script);
        var list = new List<BedrockInstall>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return list;
        }

        if (node is null) return list;

        // 只有一个包时 ConvertTo-Json 输出对象而不是数组，两种都要认
        IEnumerable<JsonNode?> entries = node is JsonArray array ? array : [node];
        foreach (var entry in entries)
        {
            if (entry is not JsonObject obj) continue;
            var name = obj["name"]?.GetValue<string>();
            var family = obj["family"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(family)) continue;

            var isPreview = name.Equals(PreviewPackageName, StringComparison.OrdinalIgnoreCase);
            list.Add(new BedrockInstall
            {
                Id = "store:" + family,
                DisplayName = isPreview ? "Minecraft（预览版）" : "Minecraft（商店版）",
                Kind = BedrockKind.Store,
                Version = obj["version"]?.GetValue<string>() ?? "",
                PackageFamilyName = family,
                AppId = obj["appId"]?.GetValue<string>()
            });
        }

        return list;
    }

    /// <summary>扫描给定的 GDK 便携版目录，以及它们的一级子目录（按版本分文件夹的布局）。</summary>
    private static List<BedrockInstall> DetectPortable(IEnumerable<string>? directories)
    {
        var list = new List<BedrockInstall>();
        if (directories is null) return list;

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            var exe = FindExecutable(directory);
            if (exe is not null)
            {
                list.Add(CreatePortable(exe));
                continue;
            }

            // 没有主程序就往下看一层：常见布局是 <根目录>\<版本号>\Minecraft.Windows.exe
            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                var childExe = FindExecutable(child);
                if (childExe is not null) list.Add(CreatePortable(childExe));
            }
        }

        return list;
    }

    private static BedrockInstall CreatePortable(string exePath)
    {
        var directory = Path.GetDirectoryName(exePath) ?? exePath;
        return new BedrockInstall
        {
            Id = "gdk:" + directory,
            DisplayName = new DirectoryInfo(directory).Name,
            Kind = BedrockKind.GdkPortable,
            Version = ReadFileVersion(exePath),
            ExecutablePath = exePath
        };
    }

    /// <summary>在目录内查找主程序，先看根目录再看子目录。</summary>
    private static string? FindExecutable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        try
        {
            var root = Path.Combine(directory, ExecutableName);
            if (File.Exists(root)) return root;

            foreach (var file in Directory.GetFiles(directory, ExecutableName, SearchOption.AllDirectories))
                return file;
        }
        catch (Exception)
        {
            // 权限不足等，当作没找到
        }

        return null;
    }

    private static string ReadFileVersion(string path)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static string RunPowerShell(string script)
    {
        // 脚本里只用单引号，不含双引号，所以直接包一层双引号即可
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15000);
        return output.Trim();
    }
}
