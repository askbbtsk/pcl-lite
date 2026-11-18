using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using PCL.Core.App.IoC;
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
                    var toDelete = args[1];
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
                    break;
                }
                default: Context.Debug("无更新任务"); break;
            }
            Context.DeclareStopped();
            return;
        }

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
                    return;
                }
            }

            Context.Debug("正在替换文件");
            var target = args[2];
            Context.Trace($"目标: {target}");
            var source = args[3];
            Context.Trace($"来源: {source}");
            var restart = args[4].Convert<bool>();
            var ex = UpdateHelper.Replace(source, target, restart
                ? () => _StartUpdated(target, "update_finished", source)
                : null);
            if (ex is null) Context.Trace("替换完成");
            else
            {
                Context.Error("替换或启动新版出错", ex);
                if (restart) _StartUpdated(target, "update_failed", ex.Message);
            }
        }
        catch (Exception ex)
        {
            Context.Error("更新过程出错", ex);
        }
        finally
        {
            Context.RequestExit();
        }
    }

    private static void _StartUpdated(string target, string command, string value)
    {
        var start = new ProcessStartInfo(target);
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("更新后启动失败");
    }
}
