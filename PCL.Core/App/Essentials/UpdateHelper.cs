using System;
using System.Globalization;
using System.IO;
using System.Threading;

namespace PCL.Core.App.Essentials;

public static class UpdateHelper
{
    /// <summary>
    /// 替换失败时的重试次数。
    /// <para>
    /// 旧进程刚退出时，它的映像可能还被系统（预读、内存管理）或杀毒软件短暂持有，
    /// 这段时间里 <c>DeleteFile</c> 返回的是 <c>ERROR_ACCESS_DENIED</c>，
    /// .NET 映射成 <see cref="UnauthorizedAccessException"/>——注意它<b>不是</b>
    /// <see cref="IOException"/>，所以按「文件被占用」去判会漏掉。
    /// 实测几百毫秒后就放开了，但单次尝试必然偶发失败，必须重试。
    /// </para>
    /// </summary>
    private const int RetryCount = 25;

    /// <summary>每次重试之间的间隔。</summary>
    private const int RetryDelayMs = 200;

    /// <summary>
    /// 更新启动器（替换文件）
    /// </summary>
    /// <param name="source">用于替换的来源文件路径</param>
    /// <param name="target">目标文件路径</param>
    /// <returns>成功返回 <c>null</c>，失败返回最后一次的异常。</returns>
    public static Exception? Replace(string source, string target)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        var backup = $"{target}.bak.{DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";

        // 1) 备份目标文件。备份都做不了就别动目标文件了，否则失败后连回滚的余地都没有。
        //    读也可能被瞬时占用挡一下，同样要重试
        Exception? backupEx = null;
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                File.Copy(target, backup, true);
                if (!File.Exists(backup)) throw new FileNotFoundException("备份目标文件失败", backup);
                backupEx = null;
                break;
            }
            catch (Exception ex)
            {
                backupEx = ex;
                if (attempt < RetryCount - 1) Thread.Sleep(RetryDelayMs);
            }
        }

        if (backupEx is not null)
        {
            TryDelete(backup);
            return backupEx;
        }

        // 2) 替换：删目标 → 复制来源。整段重试，因为删除和复制都可能撞上瞬时占用
        Exception? lastEx = null;
        var replaced = false;
        for (var attempt = 0; attempt < RetryCount; attempt++)
        {
            try
            {
                if (File.Exists(target))
                {
                    // 只读属性会让 File.Delete 直接抛 UnauthorizedAccessException，
                    // 和「被占用」是同一个异常类型，先摘掉属性再试
                    var attributes = File.GetAttributes(target);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);

                    File.Delete(target);
                }

                File.Copy(source, target);

                if (!File.Exists(target)) throw new FileNotFoundException("复制到目标文件失败", target);

                // 复制过程中被截断（磁盘满、杀软中断写入）时 File.Copy 不一定报错，
                // 用大小兜一道，避免把一个残缺的 exe 留在目标位置
                if (new FileInfo(target).Length != new FileInfo(source).Length)
                    throw new IOException("复制后的文件大小与来源不一致");

                replaced = true;
                lastEx = null;
                break;
            }
            catch (Exception ex)
            {
                lastEx = ex;
                if (attempt < RetryCount - 1) Thread.Sleep(RetryDelayMs);
            }
        }

        // 3) 失败：把旧文件放回目标位置，至少保证用户还能启动
        if (!replaced)
        {
            try
            {
                if (File.Exists(target)) File.Delete(target);
                if (File.Exists(backup)) File.Move(backup, target);
            }
            catch (Exception restoreEx)
            {
                // 回滚也失败了：两份异常都带出去，日志里能看出是哪一步崩的
                return new AggregateException(lastEx!, restoreEx);
            }

            TryDelete(backup);
            return lastEx;
        }

        // 4) 成功：清理备份。这一步失败不能算更新失败——文件已经换好了，
        //    以前它写在 try 外面，删备份被拦会直接抛到上层，连重启都被跳过
        TryDelete(backup);
        return null;
    }

    /// <summary>删除文件，失败不抛。用于各种收尾清理。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
        catch
        {
            // 清理失败只留下一个 .bak 文件，不影响启动
        }
    }
}
