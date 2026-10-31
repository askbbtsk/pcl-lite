using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;

namespace PCL.Core.Test;

/// <summary>
/// 更新替换的回归测试。
/// <para>
/// 背景：线上出现过「第一次点更新提示替换失败、再点一次就好了」。
/// 日志里是 <c>File.Delete</c> 抛 <see cref="UnauthorizedAccessException"/> ——
/// 旧进程刚退出时映像还被短暂持有，删除会返回 ERROR_ACCESS_DENIED。
/// 以前这里是一次性尝试、零重试，撞上就整个更新失败。
/// </para>
/// </summary>
[TestClass]
public class UpdateHelperTest
{
    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pcl-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }
        catch
        {
            // 测试目录清理失败不影响结论
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void ReplaceShouldSwapFileContent()
    {
        var target = Write("app.exe", "old");
        var source = Write("new.exe", "brand new content");

        var ex = UpdateHelper.Replace(source, target);

        Assert.IsNull(ex, ex?.ToString());
        Assert.AreEqual("brand new content", File.ReadAllText(target));
        // 来源文件要留着：它会被 update_finished 阶段删掉
        Assert.IsTrue(File.Exists(source));
    }

    /// <summary>
    /// 目标文件被别的进程短暂占用时，必须靠重试跨过去，而不是直接判失败。
    /// 这是线上那个 bug 的直接复现。
    /// </summary>
    [TestMethod]
    public void ReplaceShouldRetryWhileTargetIsLocked()
    {
        var target = Write("app.exe", "old");
        var source = Write("new.exe", "new content");

        // 只挡删除、不挡读取：这正是「旧进程刚退出、映像还被短暂持有」的真实形态。
        // 用 FileShare.Read 而不是 None，否则连备份那一步的读取都会失败，测不到删除分支
        var holder = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(600);
            holder.Dispose();
        });

        Exception? ex;
        try
        {
            ex = UpdateHelper.Replace(source, target);
        }
        finally
        {
            release.Wait(TimeSpan.FromSeconds(5));
            holder.Dispose();
        }

        Assert.IsNull(ex, ex?.ToString());
        Assert.AreEqual("new content", File.ReadAllText(target));
    }

    /// <summary>
    /// 只读属性会让 File.Delete 抛 UnauthorizedAccessException，要先摘属性。
    /// </summary>
    [TestMethod]
    public void ReplaceShouldClearReadOnlyAttribute()
    {
        var target = Write("app.exe", "old");
        var source = Write("new.exe", "new content");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        try
        {
            var ex = UpdateHelper.Replace(source, target);

            Assert.IsNull(ex, ex?.ToString());
            Assert.AreEqual("new content", File.ReadAllText(target));
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// 来源不存在时必须失败，但目标文件要原样保留 —— 不能把用户唯一能启动的 exe 弄没了。
    /// </summary>
    [TestMethod]
    public void ReplaceShouldKeepTargetWhenSourceMissing()
    {
        var target = Write("app.exe", "old");
        var source = Path.Combine(_dir, "missing.exe");

        var ex = UpdateHelper.Replace(source, target);

        Assert.IsNotNull(ex);
        Assert.AreEqual("old", File.ReadAllText(target));
    }

    /// <summary>
    /// 替换成功后不该留下 .bak；失败回滚后同样不该留。
    /// </summary>
    [TestMethod]
    public void ReplaceShouldNotLeaveBackupFiles()
    {
        var target = Write("app.exe", "old");
        var source = Write("new.exe", "new content");

        UpdateHelper.Replace(source, target);

        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.bak.*").Length);
    }
}
