using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Link.Bedrock;

namespace PCL;

/// <summary>
///     把下载到的基岩版安装包装进系统（UWP）或解包成独立目录（GDK）。
///     <para>
///         UWP 走「解压 → 删签名 → 改清单 → Add-AppxPackage -Register」，
///         也就是 BedrockBoot / BedrockLauncher.Core 一直在用的开发模式安装法：
///         好处是可以装比商店里更旧或更新的版本、多版本并存，且商店不会把它顶掉。
///         代价是需要系统开启开发者模式（旁加载），没有的话这里会尝试开，失败就给明确提示。
///     </para>
/// </summary>
public static class ModBedrockInstaller
{
    /// <summary>安装 UWP 包，返回最终被注册的目录（里面有改写后的 AppxManifest.xml）。</summary>
    /// <param name="packagePath">下载到本地的 .appxbundle / .appx / .msix / .msixbundle</param>
    /// <param name="versionId">版本标识，用来命名解压目录</param>
    /// <param name="report">进度文本回调</param>
    public static string InstallUwp(string packagePath, string versionId, Action<string>? report = null)
    {
        var extractDir = ModBedrockUwp.GetVersionDirectory(ModBedrockDownload.ExtractedDirectory, versionId);

        report?.Invoke(Lang.Text("Bedrock.Install.Extracting"));
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        try
        {
            ZipFile.ExtractToDirectory(packagePath, extractDir);
        }
        catch (Exception ex)
        {
            // 加密包（GDK 的 .msixvc）解不开，这里只处理 UWP，直接把原因抛给上层
            throw new InvalidOperationException(
                Lang.Text("Bedrock.Install.ExtractFailed", ex.Message), ex);
        }

        // 包束（.appxbundle）解出来只有内部包，要再解一层才是真正的程序目录
        var manifestPath = Path.Combine(extractDir, "AppxManifest.xml");
        if (!File.Exists(manifestPath))
        {
            var innerPackage = Directory.GetFiles(extractDir, "*.appx")
                .Concat(Directory.GetFiles(extractDir, "*.msix"))
                .OrderByDescending(f => Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (innerPackage is null) throw new InvalidOperationException(Lang.Text("Bedrock.Install.ManifestMissing"));

            var innerDir = Path.Combine(extractDir, "inner");
            if (Directory.Exists(innerDir)) Directory.Delete(innerDir, true);
            Directory.CreateDirectory(innerDir);
            ZipFile.ExtractToDirectory(innerPackage, innerDir);

            manifestPath = Path.Combine(innerDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) throw new InvalidOperationException(Lang.Text("Bedrock.Install.ManifestMissing"));

            extractDir = innerDir;
            ModBase.Log($"[Bedrock] 已从包束中取出内部安装包：{Path.GetFileName(innerPackage)}");
        }

        // 删掉签名与块映射：签名对不上改写后的清单，块映射也会让注册报哈希不匹配
        foreach (var name in new[] { "AppxSignature.p7x", "AppxBlockMap.xml" })
        {
            var path = Path.Combine(extractDir, name);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            ModBase.Log($"[Bedrock] 已删除 {name}");
        }

        report?.Invoke(Lang.Text("Bedrock.Install.RewritingManifest"));
        ModBedrockUwp.EditManifest(extractDir, $"Minecraft {versionId}");

        // 部分增量包缺启动图，缺了注册会报找不到资源
        var splash = Path.Combine(extractDir, "MCSplashScreen.png");
        if (!File.Exists(splash)) WriteMinimalPng(splash);

        if (!IsDeveloperModeEnabled())
        {
            ModBase.Log("[Bedrock] 开发者模式未启用，尝试自动开启（需要管理员权限）");
            if (!EnableDeveloperMode())
                throw new InvalidOperationException(Lang.Text("Bedrock.Install.DeveloperModeRequired"));
        }

        report?.Invoke(Lang.Text("Bedrock.Install.Registering"));
        var registerResult = ModBase.ShellAndGetOutput("powershell.exe",
            ModBedrockUwp.GetAppxInstallArguments(manifestPath, register: true),
            timeout: 300000);

        if (registerResult.Contains("Exception") || registerResult.Contains("错误"))
        {
            // 回退：把改写后的目录重新打包成 .appx 再装一次
            ModBase.Log($"[Bedrock] -Register 失败，改为重新打包安装：{registerResult.Trim()}");
            var appxPath = Path.Combine(ModBedrockDownload.PackagesDirectory,
                $"Minecraft_Bedrock_{versionId}.appx");
            if (File.Exists(appxPath)) File.Delete(appxPath);
            ZipFile.CreateFromDirectory(extractDir, appxPath);

            var appxResult = ModBase.ShellAndGetOutput("powershell.exe",
                ModBedrockUwp.GetAppxInstallArguments(appxPath),
                timeout: 300000);

            if (appxResult.Contains("Exception") || appxResult.Contains("错误"))
                throw new InvalidOperationException(
                    Lang.Text("Bedrock.Install.Failed", Trim(appxResult)));
        }

        ModBase.Log($"[Bedrock] 已安装 {versionId}（{extractDir}）");
        return extractDir;
    }

    /// <summary>解包 GDK 包到独立目录，返回版本目录。没有配置内容密钥时会抛出明确异常。</summary>
    public static string InstallGdk(string packagePath, string versionId,
        Action<string, int, int>? progress = null, CancellationToken token = default)
    {
        var target = ModBedrockUwp.GetVersionDirectory(ModBedrockDownload.VersionsDirectory, versionId);

        // 解包前先结束正在运行的 GDK 版，否则文件被占用会解不出来
        ModBedrockGdk.StopProcesses();

        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);

        ModBedrockGdk.Extract(packagePath, target, progress, token);
        ModBase.Log($"[Bedrock] 已解包 GDK 版 {versionId} 到 {target}");
        return target;
    }

    /// <summary>当前是否已开启开发者模式 / 旁加载。</summary>
    public static bool IsDeveloperModeEnabled()
    {
        try
        {
            var result = ModBase.ShellAndGetOutput("powershell.exe",
                "-NoProfile -Command \"" +
                "$paths = @('HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock', " +
                "'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\Appx'); " +
                "foreach ($p in $paths) { " +
                "  try { $v = Get-ItemProperty -Path $p -Name 'AllowDevelopmentWithoutDevLicense' -ErrorAction Stop; " +
                "    if ($v.AllowDevelopmentWithoutDevLicense -eq 1) { Write-Output 'true'; exit } " +
                "  } catch { } " +
                "}; " +
                "try { $v = Get-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock' -Name 'AllowAllTrustedApps' -ErrorAction Stop; " +
                "  if ($v.AllowAllTrustedApps -eq 1) { Write-Output 'true'; exit } " +
                "} catch { }; " +
                "Write-Output 'false'\"",
                timeout: 10000);

            return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Bedrock] 开发者模式检测失败", ModBase.LogLevel.Normal);
            return false;
        }
    }

    /// <summary>尝试开启开发者模式（写 HKLM，需要管理员权限）。</summary>
    public static bool EnableDeveloperMode()
    {
        try
        {
            var result = ModBase.ShellAndGetOutput("powershell.exe",
                "-NoProfile -Command \"" +
                "try { " +
                "  New-Item -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock' -Force -ErrorAction SilentlyContinue | Out-Null; " +
                "  Set-ItemProperty -Path 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\AppModelUnlock' -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -ErrorAction Stop; " +
                "  Write-Output 'true' " +
                "} catch { Write-Output 'false' }\"",
                timeout: 10000);

            return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Bedrock] 开启开发者模式失败", ModBase.LogLevel.Normal);
            return false;
        }
    }

    /// <summary>写一个 1×1 的白色 PNG，用作缺失的启动图占位。</summary>
    private static void WriteMinimalPng(string path)
    {
        // 1×1 白色 PNG
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP4z8BQDwAFeAFK5RqavAAAAABJRU5ErkJggg==");
        File.WriteAllBytes(path, png);
    }

    private static string Trim(string text)
        => text.Length > 300 ? text[..300] : text;
}
