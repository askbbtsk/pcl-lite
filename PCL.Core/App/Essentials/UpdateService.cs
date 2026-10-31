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

    public override void Start()
    {
        var args = Basics.CommandLineArguments;
        
        if (args is not ["update", _, _, _, _])
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
                    Context.Error(
                        $"更新失败: {reason}\n你可以手动将 exe 文件替换为 PCL 目录中的新版本" +
                        $"或再次尝试更新，若再次尝试仍然失败，请尽快反馈这个问题");
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
            try
            {
                var oldProcess = Process.GetProcessById(oldProcessId);
                Context.Debug("正在等待旧版本进程退出");
                // 必须给超时。旧进程若在关闭流程里被某个服务卡住，这里会无限期挂着：
                // 用户看到的现象是「点了更新，启动器窗口没了，进程还在，再点启动也没反应」，
                // 而且没有任何日志能说明它在等谁。等不到就照常往下走，让替换和重启先完成。
                if (!oldProcess.WaitForExit(30000))
                {
                    Context.Warn("旧版本进程 30 秒内没有退出，不再等待，继续替换文件");
                }
                else
                {
                    Context.Trace("旧版本进程已退出");
                }
            }
            catch
            {
                /* ignored */
            }

            Context.Debug("正在替换文件");
            var target = args[2];
            Context.Trace($"目标: {target}");
            var source = args[3];
            Context.Trace($"来源: {source}");
            var ex = UpdateHelper.Replace(source, target);
            if (ex is null) Context.Trace("替换完成");
            else Context.Error("替换文件出错", ex);

            var restart = args[4].Convert<bool>();
            if (restart)
            {
                var restartArgs = (ex is null) ? $"finished \"{source}\"" : $"failed \"{ex.Message}\"";
                restartArgs = $"update_{restartArgs}";
                Context.Debug($"重启中，使用参数: {restartArgs}");
                Process.Start(target, restartArgs);
            }
        }
        catch (Exception ex)
        {
            Context.Error("更新过程出错", ex);
        }
        
        Context.RequestExit();
    }
}
