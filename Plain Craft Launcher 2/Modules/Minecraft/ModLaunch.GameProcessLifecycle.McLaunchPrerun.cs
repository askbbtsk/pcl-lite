using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Windows;
using PCL.Core.App;
using PCL.Core.App.Localization;
using PCL.Core.Minecraft;
using PCL.Core.Minecraft.Backups;
using PCL.Core.Minecraft.Worlds;
using PCL.Core.Minecraft.PersonalOptions;
using PCL.Core.Minecraft.Packs;
using PCL.Core.Minecraft.Launch.Utils;
using PCL.Core.Utils.OS;
using PCL.Core.Utils;
using PCL.Core.Utils.Secret;
using PCL.Network;
using PCL.Core.Minecraft.IdentityModel;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Authentication;
using PCL.Core.Minecraft.Profile.Models;
using System.Globalization;
using PCL.Core.Logging;
using PCL.Core.Plugins;
using PCL.Plugin.SDK;

namespace PCL;

public static partial class ModLaunch
{



    private static void McLaunchPrerun()
    {
        // 要求 Java 使用高性能显卡
        var javaExePath = mcLaunchJavaSelected.Installation.JavawExePath ??
                          mcLaunchJavaSelected.Installation.JavaExePath;
        try
        {
            ModMain.SetGPUPreference(javaExePath, Config.Launch.SetGpuPreference);
        }
        catch (Exception ex)
        {
            if (ProcessInterop.IsAdmin() || !Config.Launch.SetGpuPreference)
            {
                ModBase.Log(ex, "直接调整显卡设置失败");
            }
            else
            {
                ModBase.Log(ex, "直接调整显卡设置失败，将以管理员权限重启 PCL 再次尝试");
                try
                {
                    if (ProcessInterop.StartAsAdmin($"--gpu \"{javaExePath}\"").ExitCode ==
                        (int)ModBase.ProcessReturnValues.TaskDone)
                        McLaunchLog("以管理员权限重启 PCL 并调整显卡设置成功");
                    else
                        throw new Exception("调整过程中出现异常");
                }
                catch (Exception exx)
                {
                    ModBase.Log(
                        exx,
                        Lang.Text("Minecraft.Launch.Error.GpuSet"),
                        ModBase.LogLevel.Hint,
                        userSummary: Lang.Text("Minecraft.Launch.Error.GpuSet"));
                }
            }
        }

        // 更新 launcher_profiles.json
        do
        {
            try
            {
                // 确保可用
                if (mcLoginLoader.output.Type != "Microsoft")
                    break;
                ModFolder.McFolderLauncherProfilesJsonCreate(ModFolder.mcFolderSelected);
                // 构建需要替换的 Json 对象
                var replaceJsonString = @"
            {
              ""authenticationDatabase"": {
                ""00000111112222233333444445555566"": {
                  ""username"": """ + mcLoginLoader.output.Name.Replace("\"", "-") + @""",
                  ""profiles"": {
                    ""66666555554444433333222221111100"": {
                        ""displayName"": """ + mcLoginLoader.output.Name + @"""
                    }
                  }
                }
              },
              ""clientToken"": """ + mcLoginLoader.output.ClientToken + @""",
              ""selectedUser"": {
                ""account"": ""00000111112222233333444445555566"", 
                ""profile"": ""66666555554444433333222221111100""
              }
            }";
                var replaceJson = (JsonObject)ModBase.GetJson(replaceJsonString);
                // 更新文件
                var profiles =
                    (JsonObject)ModBase.GetJson(
                        ModBase.ReadFile(ModFolder.mcFolderSelected + "launcher_profiles.json"));
                profiles.Merge(replaceJson);
                ModBase.WriteFile(ModFolder.mcFolderSelected + "launcher_profiles.json", profiles.ToString(),
                    encoding: Encoding.GetEncoding("GB18030"));
                McLaunchLog("已更新 launcher_profiles.json");
            }
            catch (Exception ex)
            {
                ModBase.Log(ex, "更新 launcher_profiles.json 失败，将在删除文件后重试");
                try
                {
                    File.Delete(ModFolder.mcFolderSelected + "launcher_profiles.json");
                    ModFolder.McFolderLauncherProfilesJsonCreate(ModFolder.mcFolderSelected);
                    // 构建需要替换的 Json 对象
                    var replaceJsonString = @"
                    {
                      ""authenticationDatabase"": {
                        ""00000111112222233333444445555566"": {
                          ""username"": """ + mcLoginLoader.output.Name.Replace("\"", "-") + @""",
                          ""profiles"": {
                            ""66666555554444433333222221111100"": {
                                ""displayName"": """ + mcLoginLoader.output.Name + @"""
                            }
                          }
                        }
                      },
                      ""clientToken"": """ + mcLoginLoader.output.ClientToken + @""",
                      ""selectedUser"": {
                        ""account"": ""00000111112222233333444445555566"", 
                        ""profile"": ""66666555554444433333222221111100""
                      }
                    }";
                    var replaceJson = (JsonObject)ModBase.GetJson(replaceJsonString);
                    // 更新文件
                    var profiles =
                        (JsonObject)ModBase.GetJson(
                            ModBase.ReadFile(ModFolder.mcFolderSelected + "launcher_profiles.json"));
                    profiles.Merge(replaceJson);
                    ModBase.WriteFile(ModFolder.mcFolderSelected + "launcher_profiles.json", profiles.ToString(),
                        encoding: Encoding.GetEncoding("GB18030"));
                    McLaunchLog("已在删除后更新 launcher_profiles.json");
                }
                catch (Exception exx)
                {
                    ModBase.Log(
                        exx,
                        "更新 launcher_profiles.json 失败",
                        ModBase.LogLevel.Feedback,
                        userSummary: Lang.Text("Minecraft.Launch.Error.UpdateProfilesFailed"));
                }
            }
        } while (false);

        // 更新 options.txt
        var setupFileAddress = Path.Combine(ModInstanceList.McMcInstanceSelected.PathIndie, "options.txt");

        // 辅助切换游戏语言
        if (Config.Tool.AutoChangeLanguage)
        {
            if (!File.Exists(setupFileAddress))
            {
                // Yosbr Mod 兼容（#2385）：https://www.curseforge.com/minecraft/mc-mods/yosbr
                var yosbrFileAddress = Path.Combine(ModInstanceList.McMcInstanceSelected.PathIndie, "config", "yosbr", "options.txt");
                if (File.Exists(yosbrFileAddress))
                {
                    McLaunchLog("将修改 Yosbr Mod 中的 options.txt");
                    setupFileAddress = yosbrFileAddress;
                    ModBase.WriteIni(setupFileAddress, "lang", "none"); // 忽略默认语言
                }
            }

            try
            {
                // 语言
                // 1.0-     ：没有语言选项
                // 1.1 ~ 5  ：zh_CN 时正常，zh_cn 时崩溃（最后两位字母必须大写，否则将会 NPE 崩溃）
                // 1.6 ~ 10 ：zh_CN 时正常，zh_cn 时自动切换为英文
                // 1.11 ~ 12：zh_cn 时正常，zh_CN 时虽然显示了中文但语言设置会错误地显示选择英文
                // 1.13+    ：zh_cn 时正常，zh_CN 时自动切换为英文
                var currentLang = ModBase.ReadIni(setupFileAddress, "lang", "none");
                var isLanguageUnconfigured = string.Equals(currentLang, "none", StringComparison.OrdinalIgnoreCase);
                var hasExistingSaves = Directory.Exists(Path.Combine(ModInstanceList.McMcInstanceSelected.PathIndie, "saves"));
                var shouldUseDefault = isLanguageUnconfigured || !hasExistingSaves;
                var requiredLang = _ResolveMinecraftLanguage(currentLang, shouldUseDefault,
                    ModInstanceList.McMcInstanceSelected.releaseTime);

                if (currentLang == requiredLang)
                {
                    McLaunchLog($"需要的语言为 {requiredLang}，当前语言为 {currentLang}，无需修改");
                }
                else
                {
                    ModBase.WriteIni(setupFileAddress, "lang", "-"); // 触发缓存更改，避免删除后重新下载残留缓存
                    ModBase.WriteIni(setupFileAddress, "lang", requiredLang);
                    McLaunchLog($"已将语言从 {currentLang} 修改为 {requiredLang}");
                }

                // 如果是初次设置，一并按启动器语言需要修改 forceUnicodeFont，确保 CJK 字符正常显示
                if ((isLanguageUnconfigured || !hasExistingSaves) && _ShouldEnableForceUnicodeFont())
                {
                    ModBase.WriteIni(setupFileAddress, "forceUnicodeFont", "true");
                    McLaunchLog("已开启 forceUnicodeFont，确保当前启动器语言字体正常显示");
                }
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    "更新 options.txt 失败",
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Launch.Error.UpdateOptionsFailed"));
            }
        }

        // 窗口
        switch (Config.Launch.GameWindowMode)
        {
            case GameWindowSizeMode.Fullscreen: // 全屏
            {
                ModBase.WriteIni(setupFileAddress, "fullscreen", "true");
                break;
            }
            case GameWindowSizeMode.Default: // 默认
                // 其他
            {
                break;
            }

            default:
            {
                ModBase.WriteIni(setupFileAddress, "fullscreen", "false");
                break;
            }
        }
    }


    private static string _ResolveMinecraftLanguage(string? currentLanguage, bool shouldUseLauncherLanguage,
        DateTime? mcReleaseTime)
    {
        if (_IsMinecraftVersionUnder1Dot1(mcReleaseTime)) return "none";

        var useLegacyRegionCase = _ShouldUseLegacyMinecraftLanguageCode(mcReleaseTime);
        var languageCode = shouldUseLauncherLanguage
            ? LocalizationService.CurrentLanguage.Code
            : currentLanguage;
        return _NormalizeMinecraftLanguageCode(languageCode, useLegacyRegionCase);
    }


    private static string _NormalizeMinecraftLanguageCode(string? languageCode, bool useLegacyRegionCase)
    {
        var normalizedCode = string.IsNullOrWhiteSpace(languageCode)
            ? "none"
            : languageCode.Replace('-', '_').Trim();
        if (string.Equals(normalizedCode, "none", StringComparison.OrdinalIgnoreCase)) return "none";

        var segments = normalizedCode.Split('_', 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2) return normalizedCode.ToLowerInvariant();

        var language = segments[0].ToLowerInvariant();
        var region = useLegacyRegionCase ? segments[1].ToUpperInvariant() : segments[1].ToLowerInvariant();
        return $"{language}_{region}";
    }


    private static bool _IsMinecraftVersionUnder1Dot1(DateTime? releaseTime)
    {
        return releaseTime.HasValue &&
               releaseTime.Value > new DateTime(2000, 1, 1) &&
               releaseTime.Value <= new DateTime(2011, 11, 18);
    }


    private static bool _ShouldUseLegacyMinecraftLanguageCode(DateTime? releaseTime)
    {
        return releaseTime.HasValue &&
               releaseTime.Value >= new DateTime(2012, 1, 12) &&
               releaseTime.Value <= new DateTime(2016, 6, 8);
    }


    private static bool _ShouldEnableForceUnicodeFont()
    {
        return LocalizationService.CurrentLanguage.FontProfile is LocalizationFontProfile.SimplifiedChinese
            or LocalizationFontProfile.TraditionalChinese
            or LocalizationFontProfile.Japanese
            or LocalizationFontProfile.Korean;
    }


    private static void McLaunchCustom(ModLoader.LoaderTask<int, int> loader)
    {
        // 获取自定义命令
        var customCommandGlobal = Config.Launch.PreLaunchCommand;
        if (!string.IsNullOrEmpty(customCommandGlobal))
            customCommandGlobal = ArgumentReplace(customCommandGlobal, true);
        var customCommandVersion = Config.Instance.PreLaunchCommand[ModInstanceList.McMcInstanceSelected?.PathInstance];
        if (!string.IsNullOrEmpty(customCommandVersion))
            customCommandVersion = ArgumentReplace(customCommandVersion, true);

        // 输出 bat
        try
        {
            var cmdString =
                $"{(mcLaunchJavaSelected.Installation.MajorVersion > 8 ? "chcp 65001>nul" + "\r\n" : "")}" +
                "@echo off" + "\r\n" + $"title 启动 - {ModInstanceList.McMcInstanceSelected.Name}" +
                "\r\n" + "echo 游戏正在启动，请稍候。" + "\r\n" +
                $"cd /D \"{ModBase.ShortenPath(ModInstanceList.McMcInstanceSelected.PathIndie)}\"" + "\r\n" +
                customCommandGlobal + "\r\n" + customCommandVersion + "\r\n" +
                $"\"{mcLaunchJavaSelected.Installation.JavaExePath}\" {mcLaunchArgument}" + "\r\n" +
                "echo 游戏已退出。" + "\r\n" + "pause";
            ModBase.WriteFile(currentLaunchOptions.SaveBatch ?? ModBase.exePath + @"PCL\LatestLaunch.bat",
                McLogFilter.FilterAccessToken(cmdString, 'F'),
                encoding: mcLaunchJavaSelected.Installation.MajorVersion > 8 ? Encoding.UTF8 : Encoding.Default);
            if (currentLaunchOptions.SaveBatch is not null)
            {
                McLaunchLog("导出启动脚本完成，强制结束启动过程");
                abortHint = Lang.Text("Minecraft.Launch.ExportScript.Success");
                ModBase.OpenExplorer(currentLaunchOptions.SaveBatch);
                loader.parent.Abort();
                return; // 导出脚本完成
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(ex, "输出启动脚本失败");
            if (currentLaunchOptions.SaveBatch is not null)
                throw; // 直接触发启动失败
        }

        // 执行自定义命令
        if (!string.IsNullOrEmpty(customCommandGlobal))
        {
            McLaunchLog("正在执行全局自定义命令：" + customCommandGlobal);
            var customProcess = new Process();
            try
            {
                customProcess.StartInfo.FileName = "cmd.exe";
                customProcess.StartInfo.Arguments = "/c \"" + customCommandGlobal + "\"";
                customProcess.StartInfo.WorkingDirectory = ModBase.ShortenPath(ModFolder.mcFolderSelected);
                customProcess.StartInfo.UseShellExecute = false;
                customProcess.StartInfo.CreateNoWindow = true;
                customProcess.Start();
                if (Config.Launch.PreLaunchCommandWait)
                    while (!customProcess.HasExited && !loader.IsAborted)
                        Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    Lang.Text("Minecraft.Launch.Error.CustomCommand"),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Launch.Error.CustomCommand"));
            }
            finally
            {
                if (!customProcess.HasExited && loader.IsAborted)
                {
                    McLaunchLog("由于取消启动，已强制结束自定义命令 CMD 进程"); // #1183
                    customProcess.Kill();
                }
            }
        }

        if (!string.IsNullOrEmpty(customCommandVersion))
        {
            McLaunchLog("正在执行实例自定义命令：" + customCommandVersion);
            var customProcess = new Process();
            try
            {
                customProcess.StartInfo.FileName = "cmd.exe";
                customProcess.StartInfo.Arguments = "/c \"" + customCommandVersion + "\"";
                customProcess.StartInfo.WorkingDirectory = ModBase.ShortenPath(ModFolder.mcFolderSelected);
                customProcess.StartInfo.UseShellExecute = false;
                customProcess.StartInfo.CreateNoWindow = true;
                customProcess.Start();
                if (Config.Instance.PreLaunchCommandWait[ModInstanceList.McMcInstanceSelected?.PathInstance])
                    while (!customProcess.HasExited && !loader.IsAborted)
                        Thread.Sleep(10);
            }
            catch (Exception ex)
            {
                ModBase.Log(
                    ex,
                    Lang.Text("Minecraft.Launch.Error.CustomCommand"),
                    ModBase.LogLevel.Hint,
                    userSummary: Lang.Text("Minecraft.Launch.Error.CustomCommand"));
            }
            finally
            {
                if (!customProcess.HasExited && loader.IsAborted)
                {
                    McLaunchLog("由于取消启动，已强制结束自定义命令 CMD 进程"); // #1183
                    customProcess.Kill();
                }
            }
        }
    }


    private static void McLaunchRun(ModLoader.LoaderTask<int, Process> loader)
    {
        var noJavaw = Config.Launch.NoJavaw &&
                      mcLaunchJavaSelected.Installation.JavawExePath is not null;

        // 启动信息
        var gameProcess = new Process();
        var startInfo = new ProcessStartInfo(noJavaw
            ? mcLaunchJavaSelected.Installation.JavaExePath
            : mcLaunchJavaSelected.Installation.JavawExePath);

        // 设置环境变量
        var paths = new List<string>(startInfo.EnvironmentVariables["Path"].Split(";"));
        paths.Add(ModBase.ShortenPath(mcLaunchJavaSelected.Installation.JavaFolder));
        startInfo.EnvironmentVariables["Path"] = paths.Distinct().ToList().Join(";");
        startInfo.EnvironmentVariables["appdata"] = ModBase.ShortenPath(ModFolder.mcFolderSelected);
        if (currentLaunchOptions?.Environment is not null)
            foreach (var pair in currentLaunchOptions.Environment)
                if (!string.IsNullOrWhiteSpace(pair.Key)) startInfo.EnvironmentVariables[pair.Key] = pair.Value ?? "";

        // 设置其他参数
        startInfo.WorkingDirectory = ModBase.ShortenPath(ModInstanceList.McMcInstanceSelected.PathIndie);
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = noJavaw;
        startInfo.Arguments = mcLaunchArgument;
        gameProcess.StartInfo = startInfo;

        var startedInstance = currentLaunchOptions?.instance ?? ModInstanceList.McMcInstanceSelected;
        var startedId = startedInstance?.PathInstance;
        var startedName = startedInstance?.Name;
        var worldSessionHistory = new RecentWorldHistory();
        RecentWorldEntry? recentWorldSession = null;
        InstancePersonalOptionsMemory.PersonalOptionsRunSession? personalOptionsSession = null;
        string? worldPath = null;
        var worldName = currentLaunchOptions?.WorldName;
        if (startedInstance is not null && !string.IsNullOrWhiteSpace(worldName))
        {
            worldPath = !string.IsNullOrWhiteSpace(currentLaunchOptions?.WorldPath)
                ? Path.GetFullPath(currentLaunchOptions.WorldPath)
                : Path.Combine(startedInstance.PathIndie, "saves", Path.GetFileName(worldName));
            if (Directory.Exists(worldPath) && File.Exists(Path.Combine(worldPath, "level.dat")))
            {
                try
                {
                    recentWorldSession = worldSessionHistory.StartSession(startedInstance.PathInstance,
                        startedInstance.PathIndie, worldPath, Path.GetFileName(worldPath), startedInstance.Info.VanillaName);
                }
                catch (Exception ex) { ModBase.Log(ex, "记录最近世界会话失败", ModBase.LogLevel.Hint); }
            }
        }

        if (startedInstance is not null) personalOptionsSession = PreparePersonalOptionsMemory(startedInstance);

        if (startedId is not null && startedName is not null)
        {
            gameProcess.EnableRaisingEvents = true;
            gameProcess.Exited += (_, _) =>
            {
                try { PluginRuntimeManager.Notify(new PluginGameExitedEvent(startedId, startedName, gameProcess.ExitCode)); }
                catch (Exception ex) { LogWrapper.Warn(ex, "Plugin", "发布游戏退出事件失败"); }
                if (startedInstance is not null)
                    FinishPersonalOptionsMemory(personalOptionsSession, startedInstance, gameProcess, loader.IsAborted);
                var normalExit = gameProcess.ExitCode == 0;

                if (recentWorldSession is not null)
                {
                    try { worldSessionHistory.FinishSession(recentWorldSession.SessionId, normalExit); }
                    catch (Exception ex) { ModBase.Log(ex, "保存最近世界退出状态失败", ModBase.LogLevel.Hint); }
                }
            };
        }

        // 开始进程
        try
        {
            gameProcess.Start();
            try { personalOptionsSession?.MarkGameStarted(gameProcess.Id, gameProcess.StartTime.ToUniversalTime().Ticks); }
            catch (Exception ex) { ModBase.Log(ex, "无法持久化个人设置会话的游戏身份", ModBase.LogLevel.Developer); }
        }
        catch
        {
            personalOptionsSession?.Dispose();
            if (recentWorldSession is not null)
            {
                try { worldSessionHistory.FinishSession(recentWorldSession.SessionId, false); }
                catch (Exception ex) { ModBase.Log(ex, "清理未能启动的最近世界会话失败", ModBase.LogLevel.Hint); }
            }
            throw;
        }
        if (startedId is not null && startedName is not null)
            PluginRuntimeManager.Notify(new PluginGameStartedEvent(startedId, startedName, gameProcess.Id));
        McLaunchLog("已启动游戏进程：" + startInfo.FileName);
        if (loader.IsAborted)
        {
            McLaunchLog("由于取消启动，已强制结束游戏进程"); // #1631
            gameProcess.Kill();
            return;
        }

        loader.output = gameProcess;
        mcLaunchProcess = gameProcess;
        // 立刻把 Discord 状态切成「正在游玩」，不用等下一次轮询
        PCL.Core.Link.Discord.DiscordRichPresenceService.NotifyGameStateChanged();
        // 进程优先级处理
        try
        {
            gameProcess.PriorityBoostEnabled = true;
            switch (Config.Launch.ProcessPriority)
            {
                case GameProcessPriority.RealTime: // 实时
                {
                    gameProcess.PriorityClass = ProcessPriorityClass.RealTime;
                    break;
                }
                case GameProcessPriority.High: // 极高
                {
                    gameProcess.PriorityClass = ProcessPriorityClass.High;
                    break;
                }
                case GameProcessPriority.AboveNormal: // 高
                {
                    gameProcess.PriorityClass = ProcessPriorityClass.AboveNormal;
                    break;
                }
                case GameProcessPriority.BelowNormal: // 低
                {
                    gameProcess.PriorityClass = ProcessPriorityClass.BelowNormal;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ModBase.Log(
                ex,
                Lang.Text("Minecraft.Launch.Error.PrioritySet"),
                ModBase.LogLevel.Feedback,
                userSummary: Lang.Text("Minecraft.Launch.Error.PrioritySet"));
        }
    }


    private static void McLaunchWait(ModLoader.LoaderTask<Process, int> loader)
    {
        // 输出信息
        McLaunchLog("");
        McLaunchLog("~ 基础参数 ~");
        McLaunchLog("PCL 版本：" + ModBase.versionBaseName + " (" + ModBase.versionCode + ")");
        McLaunchLog(
            $"游戏版本：{ModInstanceList.McMcInstanceSelected.Info.VanillaName}（{ModInstanceList.McMcInstanceSelected.Info.vanilla}，Drop {ModInstanceList.McMcInstanceSelected.Info.Drop}{(ModInstanceList.McMcInstanceSelected.Info.Reliable ? "" : "，无法完全确定")}）");
        McLaunchLog("资源版本：" + ModAssets.McAssetsGetIndexName(ModInstanceList.McMcInstanceSelected));
        McLaunchLog("实例继承：" + (string.IsNullOrEmpty(ModInstanceList.McMcInstanceSelected.InheritInstanceName)
            ? "无"
            : ModInstanceList.McMcInstanceSelected.InheritInstanceName));
        var launchRamGb = PageInstanceSetup.GetRam(ModInstanceList.McMcInstanceSelected,
            !mcLaunchJavaSelected.Installation.Is64Bit);
        McLaunchLog("分配的内存：" +
                    launchRamGb.ToString("N1", CultureInfo.InvariantCulture) + " GiB（" +
                    Math.Round(launchRamGb * 1024d).ToString("N0", CultureInfo.InvariantCulture) + " MiB）");
        McLaunchLog("MC 文件夹：" + ModFolder.mcFolderSelected);
        McLaunchLog("实例文件夹：" + ModInstanceList.McMcInstanceSelected.PathInstance);
        McLaunchLog("版本隔离：" + ((ModInstanceList.McMcInstanceSelected.PathIndie ?? "") ==
                               (ModInstanceList.McMcInstanceSelected.PathInstance ?? "")));
        McLaunchLog("HMCL 格式：" + ModInstanceList.McMcInstanceSelected.IsHmclFormatJson);
        McLaunchLog("Java 信息：" + mcLaunchJavaSelected.Installation);
        // McLaunchLog("环境变量：" & If(McLaunchJavaSelected IsNot Nothing, If(McLaunchJavaSelected.HasEnvironment, "已设置", "未设置"), "未设置"))
        McLaunchLog("Natives 文件夹：" + GetNativesFolder());
        McLaunchLog("");
        McLaunchLog("~ 档案参数 ~");
        McLaunchLog("玩家用户名：" + mcLoginLoader.output.Name);
        McLaunchLog("AccessToken：" + mcLoginLoader.output.AccessToken);
        McLaunchLog("ClientToken：" + mcLoginLoader.output.ClientToken);
        McLaunchLog("UUID：" + mcLoginLoader.output.Uuid);
        McLaunchLog("验证方式：" + mcLoginLoader.output.Type);
        McLaunchLog("");

        // 获取窗口标题
        var windowTitle = Config.Instance.Title[ModInstanceList.McMcInstanceSelected?.PathInstance];
        if (string.IsNullOrEmpty(windowTitle) &&
            !Config.Instance.UseGlobalTitle[ModInstanceList.McMcInstanceSelected?.PathInstance])
            windowTitle = Config.Launch.Title;
        windowTitle = ArgumentReplace(windowTitle, false);

        // JStack 路径
        var jStackPath = Path.Combine(mcLaunchJavaSelected.Installation.JavaFolder, "jstack.exe");

        // 初始化等待
        var watcher = new ModWatcher.Watcher(loader, ModInstanceList.McMcInstanceSelected, windowTitle,
            File.Exists(jStackPath) ? jStackPath : "", currentLaunchOptions.IsTest);
        mcLaunchWatcher = watcher;

        // 显示实时日志
        if (currentLaunchOptions.IsTest)
        {
            if (ModMain.frmLogLeft is null)
                ModBase.RunInUiWait(() => ModMain.frmLogLeft = new PageLogLeft());
            if (ModMain.frmLogRight is null)
                ModBase.RunInUiWait(() =>
                {
                    ModAnimation.AniControlEnabled += 1;
                    ModMain.frmLogRight = new PageLogRight();
                    ModAnimation.AniControlEnabled -= 1;
                });
            ModMain.frmLogLeft.Add(watcher);
            McLaunchLog("已显示游戏实时日志");
        }

        // 等待
        while (watcher.State == ModWatcher.Watcher.MinecraftState.Loading)
            Thread.Sleep(100);
        if (watcher.State == ModWatcher.Watcher.MinecraftState.Crashed) throw new Exception("$$");
    }


    private static PersonalOptionCategories _GetPersonalOptionCategories()
    {
        var categories = PersonalOptionCategories.None;
        if (Config.Launch.PersonalOptionsKeys) categories |= PersonalOptionCategories.KeyBindings;
        if (Config.Launch.PersonalOptionsMouse) categories |= PersonalOptionCategories.Mouse;
        if (Config.Launch.PersonalOptionsView) categories |= PersonalOptionCategories.View;
        if (Config.Launch.PersonalOptionsAccessibility) categories |= PersonalOptionCategories.Accessibility;
        if (Config.Launch.PersonalOptionsAudio) categories |= PersonalOptionCategories.Audio;
        return categories;
    }


    private static void McLaunchEnd()
    {
        McLaunchLog("开始启动结束处理");

        // 暂停或开始音乐播放
        if (Config.Preference.Music.StopInGame)
            ModBase.RunInUi(() =>
            {
                if (ModMusic.MusicPause()) ModBase.Log("[Music] 已根据设置，在启动后暂停音乐播放");
            });
        else if (Config.Preference.Music.StartInGame)
            ModBase.RunInUi(() =>
            {
                if (ModMusic.MusicResume()) ModBase.Log("[Music] 已根据设置，在启动后开始音乐播放");
            });
        // 暂停视频背景播放
        ModVideoBack.IsGaming = true;
        ModVideoBack.VideoPause();
        // Persist success before a configured "exit immediately" can close the launcher.
        PCL.Core.Minecraft.Launch.GameLaunchStatistics.RecordSuccessfulProcess(mcLaunchProcess);
        // 启动器可见性
        McLaunchLog(
            "启动器可见性：" + Config.Launch.LauncherVisibility);
        switch (Config.Launch.LauncherVisibility)
        {
            case LauncherVisibility.ExitImmediately:
            {
                // 直接关闭
                McLaunchLog("已根据设置，在启动后关闭启动器");
                ModBase.RunInUi(() => ModMain.frmMain.EndProgram(false));
                break;
            }
            case LauncherVisibility.HideAndExit:
            case LauncherVisibility.HideAndReopen:
            {
                // 隐藏
                McLaunchLog("已根据设置，在启动后隐藏启动器");
                ModBase.RunInUi(() => ModMain.frmMain.Hidden = true);
                break;
            }
            case LauncherVisibility.MinimizeAndReopen:
            {
                // 最小化
                McLaunchLog("已根据设置，在启动后最小化启动器");
                ModBase.RunInUi(() => ModMain.frmMain.WindowState = WindowState.Minimized);
                break;
            }
            case LauncherVisibility.DoNothing:
            {
                break;
            }
            // 啥都不干
        }

        // 启动计数
        States.Instance.LaunchCount[ModInstanceList.McMcInstanceSelected.PathInstance] =
            States.Instance.LaunchCount[ModInstanceList.McMcInstanceSelected.PathInstance] + 1;
    }

}
