using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using PCL.Core.App.IoC;
using PCL.Core.App.Localization;
using PCL.Core.Utils.Exts;

namespace PCL.Core.App.Essentials;

[LifecycleService(LifecycleState.BeforeLoading)]
public sealed class UpdateService : GeneralService
{
    private static LifecycleContext? _context;
    private static LifecycleContext Context => _context!;

    private UpdateService() : base("update", "更新", false) { _context = ServiceContext; }

    internal static bool IsUpdateHelper => Basics.CommandLineArguments is ["update", _, _, _, _];

    public override void Start()
    {
        var args = Basics.CommandLineArguments;
        
        if (!IsUpdateHelper)
        {
            switch (args)
            {
                case ["update_finished", _]:
                {
                    if (!_TryGetStagedUpdatePath(args[1], Basics.ExecutablePath, out var toDelete))
                        break;
                    // 这个文件是上一个进程（更新器）自己。它是先 Process.Start 新版本、
                    // 再 RequestExit 的，所以新版本走到这里时它很可能还没退干净——
                    // 此时删不掉是正常现象，重试几次，删不掉就算了，绝不能因此让启动失败
                    for (var attempt = 0; attempt < 10 && File.Exists(toDelete); attempt++)
                    {
                        try
                        {
                            File.Delete(toDelete);
                            Context.Debug("更新来源文件已删除");
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == 9) Context.Warn($"更新来源文件删不掉，已跳过: {ex.Message}");
                            else Thread.Sleep(200);
                        }
                    }
                    break;
                }
                case ["update_failed", _]:
                {
                    var reason = args[1];
                    Context.Error($"更新失败: {reason}\n请重新检查更新后重试。");
                    _NotifyFailure("Update.Error.InstallFailedMessage");
                    break;
                }
                default: Context.Debug("无更新任务"); break;
            }
            Context.DeclareStopped();
            return;
        }

        var target = args[2];
        var source = args[3];
        var cleanupOnExit = true;
        try
        {
            Context.Info("开始更新");
            Lifecycle.PendingLogDirectory = Path.Combine(Basics.ExecutableDirectory, "Log");
            Lifecycle.PendingLogFileName = "LastPending_Update.log";

            var oldProcessId = args[1].Convert<int>();
            Context.Debug($"旧版本进程 ID: {oldProcessId}");
            Process? oldProcess;
            try
            {
                oldProcess = Process.GetProcessById(oldProcessId);
            }
            catch (ArgumentException)
            {
                oldProcess = null;
            }
            using (oldProcess)
            {
                if (oldProcess is not null && !oldProcess.WaitForExit(30000))
                {
                    Context.Warn("旧版本进程未退出，已取消替换，请重试更新。");
                    _NotifyFailure("Update.Error.OldProcessTimeout");
                    return;
                }
            }

            Context.Debug("正在替换文件");
            Context.Trace($"目标: {target}");
            Context.Trace($"来源: {source}");
            var restart = args[4].Convert<bool>();
            var ex = UpdateHelper.Replace(source, target, restart
                ? () => _StartUpdated(target, "update_finished", source)
                : null);
            if (ex is null)
            {
                cleanupOnExit = !restart;
                Context.Trace("替换完成");
            }
            else
            {
                Context.Error("替换或启动新版出错", ex);
                _NotifyFailure("Update.Error.InstallFailedMessage");
                // 回滚后运行的是旧版；失败提示由当前更新器显示。
                if (restart)
                {
                    try { _StartUpdated(target); }
                    catch (Exception restartEx) { Context.Error("回滚后启动旧版出错", restartEx); }
                }
            }
        }
        catch (Exception ex)
        {
            Context.Error("更新过程出错", ex);
            _NotifyFailure("Update.Error.InstallFailedMessage");
        }
        finally
        {
            if (cleanupOnExit) _CleanStagedUpdateAfterExit(source, target);
            Context.RequestExit();
        }
    }

    private static void _StartUpdated(string target, params string[] args)
    {
        var start = new ProcessStartInfo(target);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("更新后启动失败");
    }

    private static void _NotifyFailure(string messageKey)
    {
        var message = "PCL Lite could not install the update. Reopen PCL Lite and check for updates to try again.";
        var title = "PCL Lite";
        try
        {
            // BeforeLoading 尚未合并语言资源，使用系统语言显示启动前的提示。
            var language = LocalizationService.ResolveLanguage(LocalizationService.Auto);
            var text = LocalizationService._LoadLanguageDictionary(language.Code);
            message = text[messageKey] as string ?? message;
            title = text["Update.Error.UpdateBlocked"] as string ?? title;
        }
        catch (Exception ex)
        {
            Context.Warn("无法读取更新失败提示的语言资源", ex);
        }
        try { MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch (Exception ex) { Context.Warn("无法显示更新失败提示", ex); }
    }

    private static void _CleanStagedUpdateAfterExit(string source, string target)
    {
        try
        {
            if (!_TryGetStagedUpdatePath(source, target, out var staged) ||
                !string.Equals(staged, Basics.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return;

            // Windows 仍锁着当前 EXE。让后台进程在本更新器退出后删除它。
            var start = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment["PCL_STAGED_UPDATE_PATH"] = staged;
            start.Environment["PCL_STAGED_UPDATE_PID"] = Basics.CurrentProcessId.ToString();
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("""
                $process = Get-Process -Id ([int]$env:PCL_STAGED_UPDATE_PID) -ErrorAction SilentlyContinue
                if ($process -and -not $process.WaitForExit(30000)) { exit 1 }
                foreach ($suffix in @('', '.sigstore.json', '.download')) {
                    $path = $env:PCL_STAGED_UPDATE_PATH + $suffix
                    for ($attempt = 0; $attempt -lt 10; $attempt++) {
                        try { [System.IO.File]::Delete($path); break }
                        catch { Start-Sleep -Milliseconds 200 }
                    }
                }
                """);
            using var cleanup = Process.Start(start);
        }
        catch (Exception ex)
        {
            Context.Warn("无法安排清理更新暂存文件", ex);
        }
    }

    private static bool _TryGetStagedUpdatePath(string source, string target, out string staged)
    {
        staged = "";
        try
        {
            var targetPath = Path.GetFullPath(target);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (targetDirectory is null) return false;
            var candidate = Path.GetFullPath(source);
            var name = Path.GetFileName(candidate);
            if (string.Equals(candidate, targetPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(candidate), Path.Combine(targetDirectory, "PCL"),
                    StringComparison.OrdinalIgnoreCase) ||
                name.Length != 43 || !name.StartsWith("update-", StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(name.AsSpan(7, 32), "N", out _))
                return false;

            staged = PCL.Core.IO.ContainedFilePath.Resolve(Path.Combine(targetDirectory, "PCL"), name);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
