using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PCL.Core.App.Essentials;

/// <summary>
/// 单例锁文件的读写。<br/>
/// 互斥靠文件句柄的共享模式实现：持有者以 <see cref="FileShare.Read"/> 打开文件，其他进程再想以写方式
/// 打开就会失败。因此进程被强杀后锁不会残留（句柄随进程一起释放），锁文件本身留在磁盘上也无害。
/// </summary>
internal static class SingleInstanceLock
{
    /// <summary>
    /// 锁文件名，放在 <see cref="Paths.SharedLocalData"/> 下。
    /// </summary>
    internal const string FileName = "instance.lock";

    /// <summary>
    /// 尝试取得锁。返回 <see langword="null"/> 表示锁已被其他进程持有（或路径不可写）。
    /// </summary>
    /// <param name="path">锁文件路径</param>
    /// <param name="ownerPid">写入锁文件的持有者 PID</param>
    internal static FileStream? TryAcquire(string path, int ownerPid)
    {
        FileStream? stream = null;
        try
        {
            stream = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.ASCII, 8, true);
            writer.Write(ownerPid);
            writer.Flush();
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 共享冲突、目录不存在、权限不足都归到这里：拿不到锁就是拿不到
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// 读取锁文件里记录的持有者 PID。文件不存在、内容不是正整数时返回 <see langword="null"/>。
    /// </summary>
    internal static int? ReadOwner(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var text = reader.ReadToEnd().Trim();
            return int.TryParse(text, out var pid) && pid > 0 ? pid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 判断 PID 对应的进程是否还在运行。<br/>
    /// PID 被系统回收给别的程序时这里会返回 <see langword="true"/>，无法区分，属已知局限。
    /// </summary>
    internal static bool IsAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 删除锁文件。文件仍被别的进程占用时会失败，此时返回 <see langword="false"/>。
    /// </summary>
    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 释放锁。<br/>
    /// 只有当文件里仍然记录着自己的 PID 时才删除：接管者可能已经写入新的 PID，
    /// 这时删掉会把别人的锁一起干掉，导致同时跑起两个实例。
    /// </summary>
    internal static void Release(FileStream? stream, string path, int ownerPid)
    {
        stream?.Dispose();
        if (ReadOwner(path) != ownerPid) return;
        TryDelete(path);
    }
}
