using System;
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
    public static Exception? Replace(string source, string target, Action? launch = null)
    {
        source = Path.GetFullPath(source);
        target = Path.GetFullPath(target);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            return new IOException("更新来源与目标相同");
        var id = Guid.NewGuid().ToString("N");
        var backup = $"{target}.bak.{id}";
        var replacement = $"{target}.new.{id}";

        var replaced = false;
        var keepBackup = false;
        FileAttributes? originalAttributes = null;
        try
        {
            if (!File.Exists(source)) throw new FileNotFoundException("未找到更新文件", source);
            _Retry(() =>
            {
                File.Copy(target, backup, true);
                originalAttributes = File.GetAttributes(backup);
                _ClearReadOnly(backup);
            });
            _Retry(() => File.Copy(source, replacement, true));
            if (new FileInfo(replacement).Length != new FileInfo(source).Length)
                throw new IOException("复制后的文件大小与来源不一致");

            _Retry(() =>
            {
                _ClearReadOnly(target);
                File.Move(replacement, target, true);
                replaced = true;
            });
            launch?.Invoke();
            return null;
        }
        catch (Exception error)
        {
            try
            {
                if (replaced)
                    _Retry(() =>
                    {
                        try { _ClearReadOnly(target); }
                        catch (FileNotFoundException) { }
                        File.Move(backup, target, true);
                    });
                if (originalAttributes is { } attributes && File.Exists(target))
                    _Retry(() => File.SetAttributes(target, attributes));
            }
            catch (Exception restoreEx)
            {
                keepBackup = replaced;
                return new AggregateException(error, restoreEx);
            }
            return error;
        }
        finally
        {
            TryDelete(replacement);
            if (!keepBackup) TryDelete(backup);
        }
    }

    private static void _Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < RetryCount && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    private static void _ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    /// <summary>删除文件，失败不抛。用于各种收尾清理。</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            _ClearReadOnly(path);
            File.Delete(path);
        }
        catch
        {
            // 清理失败只留下一个 .bak 文件，不影响启动
        }
    }
}
