using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;


/// <summary>
///     基岩版 GDK（便携）版本支持。
///     参考 BedrockBoot / BedrockLauncher.Core：GDK 版下发的是加密的 .msixvc（XVC 容器），
///     先用内置内容密钥（CIK）解密并解包到独立目录，之后直接运行目录内的 Minecraft.Windows.exe。
///     与 UWP 版不同，GDK 版不向系统注册包，因此天然支持多版本共存、甚至可以同时运行。
/// </summary>
public static class ModBedrockGdk
{
    public const string ExecutableName = "Minecraft.Windows.exe";

    // 内容密钥（CIK）不写入源码，构建时通过 PCL_BEDROCK_RELEASE_CIK / PCL_BEDROCK_PREVIEW_CIK 注入，
    // 存放于 Secrets，详见 PCL.Core/App/Secrets.cs

    public static bool IsGdkPackage(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".msixvc", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".xvc", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>目录内是否已有可运行的 GDK 版（存在主程序，且 DLL 不是解坏的）。</summary>
    public static bool IsInstalled(string directory)
    {
        if (FindExecutable(directory) is null) return false;

        // 主程序位于 XVC 的明文区，密钥错了它也是好的；但同目录的 DLL 在加密区，
        // 密钥不匹配时会解成随机数据。所以只检查主程序不够，还要抽查根目录的 DLL。
        try
        {
            var sawAny = false;
            Span<byte> head = stackalloc byte[2];
            foreach (var dll in Directory.EnumerateFiles(directory, "*.dll"))
            {
                sawAny = true;
                using var stream = File.OpenRead(dll);
                head.Clear();
                if (stream.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z') return true;
            }

            // 一个 DLL 都没有的目录无从判断，交给主程序存在性兜底
            return !sawAny;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>在解包目录中查找游戏主程序。</summary>
    public static string? FindExecutable(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return null;
        try
        {
            var root = Path.Combine(directory, ExecutableName);
            if (File.Exists(root)) return root;

            foreach (var file in Directory.GetFiles(directory, ExecutableName, SearchOption.AllDirectories))
                return file;
        }
        catch { }

        return null;
    }

    /// <summary>
    ///     解包 .msixvc 到目标目录。
    /// </summary>
    /// <param name="packagePath">.msixvc 文件路径</param>
    /// <param name="targetDirectory">解包目标目录</param>
    /// <param name="onProgress">进度回调：文件名、当前序号、总文件数</param>
    /// <param name="token">取消标记</param>
    /// <param name="preview">是否为预览版通道；传 null 时按文件名猜（含 preview/beta）</param>
    public static void Extract(string packagePath, string targetDirectory,
        Action<string, int, int>? onProgress = null, CancellationToken token = default, bool? preview = null)
    {
        var isPreview = preview ?? (packagePath.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
                                    packagePath.Contains("beta", StringComparison.OrdinalIgnoreCase));
        var keyBytes = ModBedrockCik.Resolve(isPreview);
        if (keyBytes is not { Length: >= ModBedrockCik.MinLength })
            throw new InvalidOperationException(Lang.Text("Bedrock.Install.CikMissing"));

        var key = new XvcContentKey(keyBytes);

        using var container = new XvcContainer(packagePath);
        container.Parse();

        // 包里的 XvcInfo 明写了「需要哪把密钥」。GUID 对不上就必然解出乱码，
        // 与其花二十秒解完再验 PE，不如在这里立刻拦住，并把该包的 GUID 报给用户。
        if (container.KeyIds.Count > 0 &&
            !container.KeyIds.Any(id => string.Equals(id, key.KeyIdHex, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                Lang.Text("Bedrock.Install.KeyMismatch", string.Join(", ", container.KeyIds), key.KeyIdHex));

        container.Extract(targetDirectory, key, onProgress, token);

        // 解包完立刻验一遍。XVC 里部分区域是加密的（KeyId != 0xFFFF），一旦密钥与该通道
        // 不匹配，这些区域解出来就是随机数据——文件大小看着正常，但 PE 头是乱的，
        // 用户要等到启动时才会看到「损坏的映像 0xc000012f」。
        // 在这里拦住，能省掉「下载 2 GB + 解包 20 秒 + 启动失败」的整轮白折腾。
        var broken = CountBrokenExecutables(targetDirectory);
        if (broken > 0)
            throw new InvalidOperationException(Lang.Text("Bedrock.Install.VerifyFailed", broken));
    }

    /// <summary>
    ///     统计解包目录里 PE 头无效的可执行文件数量。
    ///     <para>正常解包出来一定是 <c>MZ</c> 开头；不是就说明那段数据没被正确解密。</para>
    /// </summary>
    private static int CountBrokenExecutables(string directory)
    {
        var broken = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return 0;
        }

        Span<byte> head = stackalloc byte[2];
        foreach (var path in files)
        {
            var ext = Path.GetExtension(path);
            if (!ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                using var stream = File.OpenRead(path);
                head.Clear();
                if (stream.Read(head) < 2 || head[0] != (byte)'M' || head[1] != (byte)'Z') broken++;
            }
            catch (Exception)
            {
                broken++;
            }
        }

        return broken;
    }

    /// <summary>
    ///     直接启动 GDK 版：不经过系统包注册，多版本可同时运行。
    ///     <para>
    ///         PCL Lite 不做 DLL 注入，所以这里就是「在解包目录里跑主程序」，
    ///         参数原样透传（游戏本身很少用到）。找不到主程序时返回 <c>null</c>。
    ///     </para>
    /// </summary>
    public static Process? Launch(string gameDirectory, string? arguments = null, string? versionId = null)
    {
        var exePath = FindExecutable(gameDirectory);
        if (exePath is null) return null;

        var workingDirectory = Path.GetDirectoryName(exePath) ?? gameDirectory;
        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false
        };
        if (!string.IsNullOrWhiteSpace(arguments)) startInfo.Arguments = arguments;

        // PCL Lite 不做 DLL 注入：GDK 版就是「解包目录里直接跑主程序」，多版本天然共存。
        ModBase.Log($"[Bedrock] 启动 GDK 版：{exePath}", ModBase.LogLevel.Normal);
        var previousProcesses = PCL.Core.Link.Bedrock.BedrockLaunchStatistics.CaptureRunningProcesses();
        var process = Process.Start(startInfo);
        if (process is not null)
            PCL.Core.Link.Bedrock.BedrockLaunchStatistics.ObserveStartedGame(process, previousProcesses);
        return process;
    }

    /// <summary>结束正在运行的 GDK 版进程（解包前释放文件占用）。</summary>
    public static void StopProcesses()
    {
        foreach (var name in new[] { "Minecraft.Windows", "Minecraft.Win10.DX11" })
        {
            try
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch { }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch { }
        }
    }
}
