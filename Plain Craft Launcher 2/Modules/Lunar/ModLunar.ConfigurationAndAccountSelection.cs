using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.App.Lunar;
using PCL.Core.App.Lunar.Social;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using PCL.Network;

namespace PCL;

public static partial class ModLunar
{

    /// <summary>Lunar 自带的默认数据根目录（<c>~/.lunarclient</c>）。</summary>
    public static string DefaultLunarRoot => LunarClientService.DefaultLunarRoot;


    /// <summary>
    ///     Lunar 数据根目录。设置里指定过就用指定的，否则用默认的 <c>~/.lunarclient</c>。
    ///     <para>
    ///         物料、JRE、账户库全挂在这下面，所以换目录等于换一套环境，需要重新下物料。
    ///     </para>
    /// </summary>
    public static string LunarRoot
    {
        get
        {
            var custom = Config.Preference.Lunar.Root;
            return string.IsNullOrWhiteSpace(custom) ? LunarClientService.DefaultLunarRoot : custom;
        }
    }


    /// <summary>默认启动目录：当前选中的 MC 根目录。</summary>
    public static string DefaultGameDirectory => ModFolder.mcFolderSelected;


    public static bool IsInstalled => LunarClientService.IsInstalled(LunarRoot);


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


    /// <summary>游戏进程是否还活着。已退出或从未启动都返回 <c>false</c>。</summary>
    public static bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null) return false;
            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                // 进程对象还没 Start 过，当成没在跑
                return false;
            }
        }
    }


    /// <summary>可选的 Lunar 子版本，未拉到版本列表时为空。</summary>
    public static IReadOnlyList<LunarVersion> Versions => LunarVersionCatalog.Versions;


    /// <summary>
    ///     版本下拉框里那一项的悬停说明：大版本名，外加「官方默认」标记。
    ///     <para>
    ///         两个页面（Lunar 板块、设置页）都用它，免得一处写「官方默认」、另一处忘了写。
    ///     </para>
    /// </summary>
    public static string DescribeVersion(LunarVersion version)
    {
        var name = string.IsNullOrWhiteSpace(version.ParentName) ? version.Id : version.ParentName;
        return version.IsDefault ? name + " " + Lang.Text("Lunar.Module.Default") : name;
    }


    /// <summary>Lunar 账户库里的账号。</summary>
    public static IReadOnlyList<LunarAccount> Accounts => _accounts;


    /// <summary>Lunar 当前激活的账号。</summary>
    public static string ActiveAccountLocalId => _activeAccountLocalId;


    /// <summary>
    ///     当前选中的子版本。存在配置里，没选过时用 <see cref="DefaultSubVersion" />；
    ///     如果配置里那个版本已经不在服务端列表里（Lunar 会下线老版本），也退回默认值。
    ///     <para>
    ///         兜底顺序：配置 → <see cref="DefaultSubVersion" />（1.8.9，Lunar 最稳的版本）
    ///         → 服务端标了 <c>default</c> 的那个 → 列表里版本号最大的。
    ///     </para>
    ///     <para>
    ///         特意把 1.8.9 排在官方默认之前：官方默认是「最新 drop」（现在是 26.3），
    ///         换 drop 时 loader 清单会跟着变，用户点开 Lunar 板块就被丢进一个没下过物料的新版本。
    ///         官方那个只在 1.8.9 也被下线时才用得上。
    ///     </para>
    /// </summary>
    public static string SelectedSubVersion
    {
        get
        {
            var configured = Config.Preference.Lunar.SubVersion;
            if (LunarVersionCatalog.IsEmpty)
                return string.IsNullOrWhiteSpace(configured) ? DefaultSubVersion : configured;

            if (!string.IsNullOrWhiteSpace(configured) && LunarVersionCatalog.Find(configured) is not null)
                return configured;
            if (LunarVersionCatalog.Find(DefaultSubVersion) is not null)
                return DefaultSubVersion;
            if (LunarVersionCatalog.DefaultVersion is { } official)
                return official.Id;

            // 列表里既没有配置值也没有默认值（服务端下线了 1.8.9 这类老版本）。
            // 这里必须返回一个**列表里真的有**的 id：界面上「下拉框选中项」是按它反查的，
            // 返回列表外的值会让下拉框退化成 SelectedIndex = 0，于是显示 A、实际启动 B。
            var versions = LunarVersionCatalog.Versions;
            return versions.Count > 0 ? versions[0].Id : DefaultSubVersion;
        }
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == SelectedSubVersion) return;
            Config.Preference.Lunar.SubVersion = value;
            JavaPath = "";
            Refresh();
        }
    }


    /// <summary>
    ///     当前版本可选的 loader。版本列表还没拉到时为空，界面上就不显示这一行。
    /// </summary>
    public static IReadOnlyList<LunarModule> Modules =>
        LunarVersionCatalog.Find(SelectedSubVersion)?.Modules ?? [];


    /// <summary>
    ///     这次启动实际用哪个 loader。
    ///     <para>
    ///         顺序：配置里记的（用户点过的）→ 该版本的官方默认 → 兜底 <c>lunar</c>。
    ///         配置里那个如果当前版本不提供（比如在 1.17.1 选了 fabric，又切到 1.8.9），
    ///         就退回官方默认，但**不改配置** —— 切回 1.17.1 时选择还在。
    ///         </para>
    ///     <para>
    ///         必须按服务端下发的 modules 走：1.17.1 的官方默认是 <c>fabric</c>，
    ///         一直写死 <c>lunar</c>（OptiFine）会在游戏初始化前静默退出。
    ///     </para>
    /// </summary>
    public static LunarModule? SelectedModule
    {
        get
        {
            var modules = Modules;
            if (modules.Count == 0) return null;

            var configured = Config.Preference.Lunar.ModuleId;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var match = modules.FirstOrDefault(x =>
                    string.Equals(x.Id, configured, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }

            return LunarVersionCatalog.Find(SelectedSubVersion)?.DefaultModule ?? modules[0];
        }
    }


    /// <summary>传给 <c>/launcher/launch</c> 的 <c>module</c> 值。</summary>
    public static string SelectedModuleId => SelectedModule?.Id ?? FallbackModule;


    /// <summary>
    ///     用户点了某个 loader。只记配置，不发请求 —— 物料清单在启动时才拉，
    ///     提前拉会白下一份用不上的附件。
    /// </summary>
    public static void SelectModule(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        if (string.Equals(Config.Preference.Lunar.ModuleId, id, StringComparison.Ordinal)) return;

        Config.Preference.Lunar.ModuleId = id;
        Log($"已选择 loader：{SelectedModule?.DisplayName ?? id}（{id}）");
        Refresh();
    }


    /// <summary>
    ///     当前选中的 Lunar 账号。空值表示「没选 Lunar 的」——要么选了 PCL 登录账号，
    ///     要么什么都没选（这时跟随 Lunar 自己的激活账号）。
    /// </summary>
    public static LunarAccount? SelectedAccount
    {
        get
        {
            var configured = Config.Preference.Lunar.AccountLocalId;
            if (IsIdentitySelection(configured)) return null;
            return new LunarAccountSnapshot(_activeAccountLocalId, _accounts).Resolve(StripLunarPrefix(configured));
        }
    }


    /// <summary>
    ///     当前选中的「Lunar 身份账号」—— 用户在 Lunar 页面用 Lunar 的 client id 登录出来的那个。
    ///     没选时为 <c>null</c>。
    ///     <para>
    ///         只读本地身份库，**不会触发任何登录/授权流程**，列进下拉框不会导致重新登录。
    ///     </para>
    /// </summary>
    public static LunarIdentityAccount? SelectedIdentityAccount
    {
        get
        {
            var configured = Config.Preference.Lunar.AccountLocalId;
            if (!IsIdentitySelection(configured)) return null;
            return LunarIdentityStore.Find(configured[IdentitySelectionPrefix.Length..]);
        }
    }


    /// <summary>
    ///     Lunar 页面登录出来的账号列表。
    ///     <para>
    ///         <b>和 PCL 主页面的账号列表没有任何关系</b>：这里只读
    ///         <see cref="LunarIdentityStore" />，不会把 PCL 的档案混进来。
    ///     </para>
    /// </summary>
    public static IReadOnlyList<LunarIdentityAccount> LunarIdentityAccounts => LunarIdentityStore.Read();


    /// <summary>PCL 里登录过的微软账号。读不出来就返回空列表，不抛异常。</summary>
    public static IReadOnlyList<McProfile> PclProfiles
    {
        get
        {
            try
            {
                return ProfileService.Profiles
                    .Where(x => x.ProfileType == ProfileType.Microsoft && !string.IsNullOrWhiteSpace(x.UserName))
                    .ToList();
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "[Lunar] 读取 PCL 账号列表失败", ModBase.LogLevel.Debug);
                return [];
            }
        }
    }


    /// <summary>下拉框里那一项的唯一标识：<c>pcl:&lt;ProfileId&gt;</c> 或 <c>lunar:&lt;localId&gt;</c>。</summary>
    public static string SelectionTag(LunarAccount account) => "lunar:" + account.LocalId;


    public static string SelectionTag(LunarIdentityAccount account) => IdentitySelectionPrefix + account.Uuid;


    private static bool IsIdentitySelection(string? value) =>
        value is not null && value.StartsWith(IdentitySelectionPrefix, StringComparison.OrdinalIgnoreCase);


    private static string StripLunarPrefix(string? value) =>
        value is not null && value.StartsWith("lunar:", StringComparison.OrdinalIgnoreCase) ? value[6..] : value ?? "";


    /// <summary>
    ///     刷新界面用的状态：是否装了 Lunar、版本列表、账号列表。
    ///     只读本地，不发网络请求，可以在 UI 线程直接调。
    ///     <para>
    ///         「没装」不再是禁用状态：物料全部由 <see cref="LunarInstaller" /> 从服务端
    ///         清单补齐，<c>~/.lunarclient</c> 是空的也能直接启动。
    ///     </para>
    /// </summary>
    public static void Refresh()
    {
        LunarVersionCatalog.TryLoadCache(LunarRoot);
        RefreshAccounts();

        if (!IsInstalled)
        {
            StatusText = Lang.Text("Lunar.Status.NoAssets");
            DetailText = Lang.Text("Lunar.Status.NoAssetsDesc");
            RaiseStateChanged();
            return;
        }

        StatusText = SelectedModule is { } module
            ? $"Lunar Client {SelectedSubVersion}（{module.DisplayName}）"
            : $"Lunar Client {SelectedSubVersion}";
        DetailText = Lang.Text("Lunar.Status.MaterialDir", LunarClientService.MultiverDirectory(LunarRoot));
        RaiseStateChanged();
    }


    /// <summary>重新读一遍 Lunar 的账户库。</summary>
    public static void RefreshAccounts()
    {
        var snapshot = LunarAccountStore.Read(LunarClientService.SettingsRoot);
        _accounts = snapshot.Accounts;
        _activeAccountLocalId = snapshot.ActiveLocalId;
    }


    /// <summary>
    ///     拉一次版本列表（网络）。失败时保留缓存，不抛异常。
    ///     <para>
    ///         没装 Lunar 也能拉 —— 版本列表本来就是启动前要用的东西。
    ///     </para>
    /// </summary>
    public static async Task RefreshVersionsAsync()
    {
        var installationId = ReadInstallationId();
        await LunarVersionCatalog.FetchAsync(LunarRoot, installationId).ConfigureAwait(false);
        RaiseStateChanged();
    }


    /// <summary>
    ///     状态变化通知。统一走 UI 线程 —— 启动流程跑在后台线程，
    ///     直接在这里触发事件会让订阅者跨线程改控件。
    /// </summary>
    private static void RaiseStateChanged() =>
        ModBase.RunInUi(() => StateChanged?.Invoke());


    private static void Log(string message)
    {
        ModBase.Log("[Lunar] " + message);
        LogReceived?.Invoke(message);
    }


    /// <summary>
    ///     接一行子进程输出：进环形缓冲（供退出时落盘）的同时推到界面日志框。
    /// </summary>
    private static void CaptureGameOutput(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Interlocked.Exchange(ref _lastOutputTicks, Environment.TickCount64);

        lock (_recentOutputLock)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > RecentOutputLimit) _recentOutput.Dequeue();
        }

        LogReceived?.Invoke(line);
    }


    private static List<string> SnapshotGameOutput()
    {
        lock (_recentOutputLock)
        {
            return _recentOutput.ToList();
        }
    }


    /// <summary>
    ///     把 PCL 的全局内存设置换算成 MB。算不出来就返回 0，让上层退回内置默认。
    ///     <para>
    ///         <c>is32BitJava</c> 明确传 <see langword="false" />：不传的话它会去读
    ///         当前选中实例的 Java 设置，而 Lunar 的 JRE 是它自己按版本下发的，
    ///         跟实例没关系。
    ///     </para>
    /// </summary>
    public static int ResolvePclGlobalMemoryMb()
    {
        try
        {
            var gigabytes = PageSetupLaunch.GetRam(null, true, false);
            return gigabytes <= 0 ? 0 : (int)Math.Round(gigabytes * 1024d);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 读取 PCL 全局内存设置失败，改用内置默认", ModBase.LogLevel.Developer);
            return 0;
        }
    }


    /// <summary>
    ///     把这次生效的设置写进日志。用户反馈「改了没反应」时，这几行就是唯一线索。
    /// </summary>
    private static void LogSettings(LunarEffectiveSettings settings)
    {
        Log($"内存：{settings.MemoryMb} MB（{LunarLaunchProfile.DescribeSource(settings.MemorySource)}）");

        if (settings.JvmArguments.Count == 0)
            Log("JVM 参数：没有额外的自定义参数");
        else
            Log($"JVM 参数：{string.Join(' ', settings.JvmArguments)}" +
                $"（{LunarLaunchProfile.DescribeSource(settings.JvmSource)}）");
    }


    /// <summary>
    ///     把 <see cref="LunarInstaller" /> 的阶段名翻成界面上的说法，并做节流。
    /// </summary>
    private static void ReportInstallProgress(LunarInstallProgress progress)
    {
        var percent = progress.Total > 0
            ? progress.Done * 100 / progress.Total
            : -1;

        // 同阶段内进度百分比没变就不刷新，换阶段一定要刷新
        if (progress.Stage == _lastProgressStage && percent == _lastProgressPercent && percent >= 0) return;

        _lastProgressStage = progress.Stage;
        _lastProgressPercent = percent;

        var name = progress.Stage switch
        {
            LunarInstaller.StageLicenses => Lang.Text("Lunar.Stage.Licenses"),
            LunarInstaller.StageTextures => Lang.Text("Lunar.Stage.Textures"),
            LunarInstaller.StageAssets => Lang.Text("Lunar.Stage.Assets"),
            LunarInstaller.StageArtifacts => Lang.Text("Lunar.Stage.Artifacts"),
            LunarInstaller.StageJre => Lang.Text("Lunar.Stage.Jre"),
            LunarInstaller.StageUi => Lang.Text("Lunar.Stage.Ui"),
            _ => progress.Stage
        };

        StatusText = progress.Total > 0
            ? Lang.Text("Lunar.Status.Preparing", name, progress.Done, progress.Total)
            : Lang.Text("Lunar.Status.PreparingName", name);
        InstallPercent = progress.Total > 0 ? percent : -1;
        RaiseStateChanged();
    }


    /// <summary>
    ///     账户库不存在时按这次登录建一份。
    ///     <para>
    ///         游戏进程不读命令行里的 <c>--accessToken</c>，它按
    ///         <c>accounts.json</c> 的 <c>activeAccountLocalId</c> 决定「我是谁」。
    ///         没装过 Lunar 启动器的机器上这个文件根本不存在，不补的话游戏进去没有身份。
    ///     </para>
    ///     <para>
    ///         账户库里已经**有任何账号**就什么都不做 —— 那是 Lunar 启动器或上一次启动的成果。
    ///         只有「文件不存在」或「文件里一个账号都没有」这两种情况才补。
    ///     </para>
    /// </summary>
    private static void EnsureAccountLibrary(string playerName, string playerUuid, string accessToken)
    {
        // 不能只看文件在不在：Lunar 启动器跑过但没登录时，文件里是个空的 accounts 对象
        if (_accounts.Count > 0) return;

        var uuid = NormalizeUuid(playerUuid).Replace("-", string.Empty);
        if (string.IsNullOrWhiteSpace(uuid) || uuid == new string('0', 32))
        {
            Log("账户库不存在，但这次没有拿到有效的游戏 UUID，跳过自动创建");
            return;
        }

        var localId = Guid.NewGuid().ToString("N");
        if (!LunarAccountStore.CreateIfMissing(LunarClientService.SettingsRoot, localId, playerName, playerName, uuid,
                accessToken, null))
        {
            Log("账户库不存在，自动创建失败 —— 游戏内可能没有身份，联机会失败");
            return;
        }

        _launchedLocalId = localId;
        RefreshAccounts();
        Log($"Lunar 账户库不存在，已按本次登录创建：{playerName}（{localId}）");
    }


    /// <summary>
    ///     启动 Lunar Client。
    ///     <para>
    ///         整个流程丢到后台线程：登录加载器的 <c>WaitForExit</c> 是
    ///         <c>while (...) Thread.Sleep(10)</c> 的同步阻塞，在 UI 线程上调用
    ///         会直接把界面冻住（1.4.0-nightly 就是这么翻的车）。
    ///     </para>
    /// </summary>
    /// <para>
    ///     内存、JVM 参数、Java 路径都由设置页决定（见 <see cref="LunarLaunchProfile" />），
    ///     这里不再接收参数，免得两个地方各算一套。
    /// </para>
    /// </summary>
    public static void Launch()
    {
        if (IsBusy || IsRunning) return;

        // 游戏目录必须在 UI 线程取：PathIndie 首次访问会触发实例加载
        var gameDirectory = ResolveGameDirectory();

        // IsBusy 在 UI 线程上先占位，防止连点两次起两个进程
        IsBusy = true;
        _ = Task.Run(() => LaunchCoreAsync(gameDirectory));
    }


    /// <summary>
    ///     结束游戏进程。
    ///     <para>
    ///         <b>和官方启动器完全一致</b>：直接结束进程。官方那边是给 spawn 出来的子进程挂
    ///         <c>abortController.abort()</c> → <c>child.kill()</c>，既不推
    ///         <c>ExitGamePush</c> 也不等存档。
    ///     </para>
    ///     <para>
    ///         以前这里是「先发 ExitGamePush 请游戏自己存盘、等它退完再兜底 Kill」。
    ///         那条路会在游戏没理推送时卡在「正在保存存档」，比官方多出来的只有卡死，
    ///         已经整段删掉。
    ///     </para>
    /// </summary>
    public static void Stop()
    {
        var process = _process;
        if (process is null) return;

        lock (StopLock)
        {
            if (_stopping) return;
            _stopping = true;
        }

        // 先立牌子再动手：Exited 事件可能在 Kill 返回前就排上队了
        _stopRequested = true;
        _ = Task.Run(() => StopCore(process));
    }


    private static void StopCore(Process process)
    {
        try
        {
            // 和官方启动器完全一致：点停止就是直接结束进程，不发 ExitGamePush、
            // 也不等它自己存盘。官方那边是 spawn 出来的子进程挂
            // abortController.abort() → child.kill()，没有任何等待逻辑。
            if (!process.HasExited)
            {
                process.Kill();
                Log("已结束游戏进程");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _stopRequested = false;
            Log("结束进程失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            _stopRequested = false;
            Log("结束进程时出错：" + ex);
        }
        finally
        {
            lock (StopLock) _stopping = false;
        }
    }



    /// <summary>
    ///     这个进程名下有没有已经出来的游戏窗口。
    ///     <para>
    ///         只给启动看门狗用（「窗口出来了就收工」），不参与任何前台切换 ——
    ///         找窗口抬前台那套已经按官方写法删掉了。
    ///     </para>
    /// </summary>
    private static bool HasGameWindow(int processId)
    {
        var found = false;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                var owner = 0;
                GetWindowThreadProcessId(hwnd, ref owner);
                if (owner != processId) return true;

                var buffer = new StringBuilder(64);
                GetClassName(hwnd, buffer, buffer.Capacity);
                var className = buffer.ToString();
                if (className is not ("LWJGL" or "SunAwtFrame") &&
                    !className.StartsWith("GLFW", StringComparison.Ordinal))
                    return true;

                if (!GetWindowRect(hwnd, out var rect)) return true;
                if (rect.Right - rect.Left < 100 || rect.Bottom - rect.Top < 100) return true;

                found = true;
                return false;
            }, nint.Zero);
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "[Lunar] 枚举游戏窗口失败", ModBase.LogLevel.Debug);
        }

        return found;
    }




    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);


    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }


    [DllImport("user32")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);


    [DllImport("user32", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder buffer, int maxCount);


    [DllImport("user32")]
    private static extern int GetWindowThreadProcessId(nint hwnd, ref int processId);


    [DllImport("user32")]
    private static extern bool IsWindowVisible(nint hwnd);


    [DllImport("user32")]
    private static extern bool GetWindowRect(nint hwnd, out Rect rect);


    /// <summary>
    ///     让已经在跑的游戏直接连某个服务器（<c>PlayServerPush</c>）。
    ///     <para>
    ///         游戏端要求当前**已经进了世界**，否则它只打一条日志就忽略 —— 这个判断
    ///         在游戏那边，PCL 看不到，所以返回 <c>true</c> 只代表「推过去了」。
    ///     </para>
    ///     <para>
    ///         PCL 没有服务器列表页，现在没有调用方；留着给「直接进服」这类入口。
    ///     </para>
    /// </summary>
    /// <param name="serverIp">形如 <c>mc.example.com</c> 或 <c>mc.example.com:25565</c>。</param>
    public static async Task<bool> JoinServerAsync(string serverIp)
    {
        if (string.IsNullOrWhiteSpace(serverIp)) return false;

        var server = _ipcServer;
        if (server is null) return false;

        try
        {
            var sent = await server.SendPushAsync(LunarIpcPush.PlayServer(serverIp)).ConfigureAwait(false);
            if (sent > 0) Log($"已请游戏直连服务器 {serverIp}");
            return sent > 0;
        }
        catch (Exception ex)
        {
            Log($"请游戏直连服务器失败：{ex.Message}");
            return false;
        }
    }

}
