using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using PCL.Core.App.Lunar;

namespace PCL;

/// <summary>
///     Lunar Client 的运行状态与启动流程。
///     <para>
///         这里只做三件事：拿正版 token、按服务端清单补齐物料、拼命令行拉起来。
///         协议细节见 <c>docs/LUNAR.md</c>；物料接口不需要鉴权，正版验证完全靠
///         Minecraft 自己的 accessToken，所以不需要跟 Lunar 换任何令牌。
///     </para>
/// </summary>
public static class ModLunar
{
    public const string DefaultSubVersion = "1.8.9";
    public const string DefaultModule = "lunar";

    private static Process? _process;
    private static bool _isBusy;

    public static event Action? StateChanged;

    /// <summary>Lunar 数据根目录。</summary>
    public static string LunarRoot => LunarClientService.DefaultLunarRoot;

    public static bool IsInstalled => LunarClientService.IsInstalled();

    public static bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            RaiseStateChanged();
        }
    }

    public static string StatusText { get; private set; } = "正在检测 Lunar Client…";

    public static string DetailText { get; private set; } = "";

    /// <summary>最近一次启动的完整命令行，出错时贴出来方便定位。</summary>
    public static string LastCommandLine { get; private set; } = "";

    /// <summary>最近一次启动用到的 Java，未启动过时为空。</summary>
    public static string JavaPath { get; private set; } = "";

    public static event Action<string>? LogReceived;

    public static void Refresh()
    {
        if (!IsInstalled)
        {
            StatusText = "未检测到 Lunar Client";
            DetailText = $"没有找到 {LunarRoot}。请先安装并至少启动过一次 Lunar Client，让物料落盘。";
            RaiseStateChanged();
            return;
        }

        StatusText = $"Lunar Client {DefaultSubVersion}";
        DetailText = $"物料目录：{LunarClientService.MultiverDirectory(LunarRoot)}";
        RaiseStateChanged();
    }

    private static void RaiseStateChanged() => StateChanged?.Invoke();

    private static void Log(string message)
    {
        ModBase.Log("[Lunar] " + message);
        LogReceived?.Invoke(message);
    }

    /// <summary>
    ///     启动 Lunar Client。
    /// </summary>
    /// <param name="gameDirectory">游戏实例目录（<c>--gameDir</c>）。</param>
    /// <param name="memoryMb">分配内存。</param>
    public static async Task LaunchAsync(string gameDirectory, int memoryMb = 2048)
    {
        if (IsBusy) return;

        try
        {
            IsBusy = true;
            StatusText = "正在获取正版凭证…";
            DetailText = "";
            RaiseStateChanged();

            // 走 PCL 自己的登录链路，拿到的就是微软正版 accessToken
            ModLaunch.mcLoginLoader.Start();
            ModLaunch.mcLoginLoader.WaitForExit();
            var login = ModLaunch.mcLoginLoader.output;
            if (string.IsNullOrWhiteSpace(login.AccessToken))
                throw new InvalidOperationException("没有拿到 Minecraft accessToken，请先在 PCL 里登录正版账号。");

            Log($"账号：{login.Name}（{login.Uuid}）");

            StatusText = "正在获取 Lunar 物料清单…";
            RaiseStateChanged();

            var installationId = ReadInstallationId();
            var data = await LunarClientService.FetchLaunchDataAsync(
                DefaultSubVersion, DefaultModule,
                installationId: installationId,
                profileId: NormalizeUuid(login.Uuid),
                profileName: login.Name).ConfigureAwait(false);

            Log($"物料清单：{data.Artifacts.Count} 个附件，主类 {data.MainClass}");

            var cacheDirectory = LunarClientService.MultiverDirectory(LunarRoot);
            var nativesDirectory = LunarClientService.NativesDirectory(LunarRoot);

            StatusText = "正在校验物料…";
            RaiseStateChanged();

            var progress = new Progress<(int Done, int Total)>(p =>
            {
                StatusText = $"正在校验物料 {p.Done}/{p.Total}";
                RaiseStateChanged();
            });
            await LunarClientService.EnsureArtifactsAsync(data, cacheDirectory, progress).ConfigureAwait(false);
            await LunarClientService.ExtractNativesAsync(data, cacheDirectory, nativesDirectory)
                .ConfigureAwait(false);

            var javaPath = LunarClientService.FindBundledJava(LunarRoot, data.JreFolderChecksum)
                           ?? throw new InvalidOperationException(
                               $"没有找到 Lunar 自带的 Java（期望目录 jre/{data.JreFolderChecksum}）。" +
                               "请先用 Lunar 启动器跑一次，让它把 JRE 下好。");

            Log("Java：" + javaPath);
            JavaPath = javaPath;
            RaiseStateChanged();

            // 资源索引是原版那套，缺了会没贴图没声音。只补索引文件，不拉几万个资源对象
            if (data.AssetIndexInfo is { } assetIndex)
            {
                Log($"资源索引：{assetIndex.Id}");
                await LunarClientService
                    .EnsureAssetIndexAsync(assetIndex, LunarClientService.AssetsDirectory(LunarRoot))
                    .ConfigureAwait(false);
            }
            else
            {
                Log("没有拿到资源索引，游戏可能缺贴图与音效");
            }

            var options = new LunarLaunchOptions
            {
                LunarRoot = LunarRoot,
                GameDirectory = gameDirectory,
                VersionName = DefaultSubVersion,
                PlayerName = login.Name,
                PlayerUuid = NormalizeUuid(login.Uuid),
                AccessToken = login.AccessToken,
                // 1.8.9 不用 xuid，但 Genesis 会原样转发给游戏，给个数字比空串安全
                XboxUid = "0",
                AllocatedMemoryMb = memoryMb,
                InstallationId = installationId
            };

            var startInfo = LunarClientService.BuildStartInfo(
                data, options, javaPath, cacheDirectory, nativesDirectory);

            // 换成 java.exe 并接管输出，否则启动失败时什么都看不到
            startInfo.FileName = Path.Combine(Path.GetDirectoryName(javaPath)!, "java.exe");
            if (!File.Exists(startInfo.FileName)) startInfo.FileName = javaPath;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;

            LastCommandLine = startInfo.FileName + " " + string.Join(' ', startInfo.ArgumentList);

            StatusText = "正在启动…";
            RaiseStateChanged();

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogReceived?.Invoke(e.Data);
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) LogReceived?.Invoke(e.Data);
            };
            _process.Exited += (_, _) =>
            {
                _process = null;
                IsBusy = false;
                StatusText = "游戏已退出";
                RaiseStateChanged();
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            StatusText = "游戏运行中";
            DetailText = "由 PCL 直接拉起，未经过 Lunar 启动器";
            RaiseStateChanged();
        }
        catch (Exception ex)
        {
            IsBusy = false;
            StatusText = "启动失败";
            DetailText = ex.Message;
            RaiseStateChanged();
            ModBase.Log(ex, "[Lunar] 启动失败", ModBase.LogLevel.Msgbox, userSummary: ex.Message);
        }
    }

    /// <summary>
    ///     Lunar 的 <c>installation_id</c> 存在 launcher-cache 下，有就复用，
    ///     这样服务端看到的是同一个「安装」。
    /// </summary>
    private static string ReadInstallationId()
    {
        try
        {
            var path = Path.Combine(LunarRoot, "launcher-cache", "installation-id");
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (Guid.TryParse(text, out var id)) return id.ToString();
            }
        }
        catch (IOException)
        {
            // 读不到就现生成一个，不影响启动
        }

        return Guid.NewGuid().ToString();
    }

    /// <summary>Lunar 要的是不带横线的 UUID，接口校验要的是带横线的，这里统一成带横线。</summary>
    private static string NormalizeUuid(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return Guid.Empty.ToString();
        var hex = uuid.Replace("-", string.Empty);
        if (hex.Length != 32) return Guid.Empty.ToString();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
