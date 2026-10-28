using System;
using System.Diagnostics;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Essentials;

namespace PCL.Core.Test;

/// <summary>
///     单例锁的回归测试。
///     <para>
///         锁本身依赖文件句柄的共享模式，这里用临时目录复现「拿到锁 / 拿不到锁 / 接管 / 释放」四种情况。
///         PID 存活性判断只测当前进程和必然不存在的 PID，避免依赖机器状态。
///     </para>
/// </summary>
[TestClass]
public class SingleInstanceLockTest
{
    private string _directory = string.Empty;
    private string _path = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), "pcl-single-instance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, SingleInstanceLock.FileName);
    }

    [TestCleanup]
    public void TearDown()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论
        }
    }

    [TestMethod]
    public void TryAcquire_WritesOwnerPid()
    {
        using var stream = SingleInstanceLock.TryAcquire(_path, 4321);

        Assert.IsNotNull(stream);
        Assert.AreEqual(4321, SingleInstanceLock.ReadOwner(_path));
    }

    [TestMethod]
    public void TryAcquire_FailsWhileHeld()
    {
        using var first = SingleInstanceLock.TryAcquire(_path, 4321);
        Assert.IsNotNull(first);

        var second = SingleInstanceLock.TryAcquire(_path, 8765);

        Assert.IsNull(second, "锁被持有时第二次获取必须失败，否则会同时跑起两个实例");
    }

    [TestMethod]
    public void TryAcquire_SucceedsAfterRelease()
    {
        var first = SingleInstanceLock.TryAcquire(_path, 4321);
        Assert.IsNotNull(first);
        SingleInstanceLock.Release(first, _path, 4321);

        Assert.IsFalse(File.Exists(_path), "释放后锁文件应当被删除");

        using var second = SingleInstanceLock.TryAcquire(_path, 8765);
        Assert.IsNotNull(second);
        Assert.AreEqual(8765, SingleInstanceLock.ReadOwner(_path));
    }

    [TestMethod]
    public void Release_DoesNotDeleteSuccessorLock()
    {
        var first = SingleInstanceLock.TryAcquire(_path, 4321);
        Assert.IsNotNull(first);
        first.Dispose();

        // 模拟「接管者已经写入自己的 PID」
        File.WriteAllText(_path, "8765");
        SingleInstanceLock.Release(null, _path, 4321);

        Assert.IsTrue(File.Exists(_path), "锁已经被别的进程接管，不能把它的锁删掉");
        Assert.AreEqual(8765, SingleInstanceLock.ReadOwner(_path));
    }

    [TestMethod]
    public void ReadOwner_ReturnsNullOnGarbage()
    {
        File.WriteAllText(_path, "not-a-pid");
        Assert.IsNull(SingleInstanceLock.ReadOwner(_path));

        File.WriteAllText(_path, "");
        Assert.IsNull(SingleInstanceLock.ReadOwner(_path));

        File.WriteAllText(_path, "0");
        Assert.IsNull(SingleInstanceLock.ReadOwner(_path));
    }

    [TestMethod]
    public void ReadOwner_ReturnsNullWhenMissing()
    {
        Assert.IsNull(SingleInstanceLock.ReadOwner(_path));
    }

    [TestMethod]
    public void IsAlive_RecognizesCurrentProcess()
    {
        using var current = Process.GetCurrentProcess();
        Assert.IsTrue(SingleInstanceLock.IsAlive(current.Id));
        Assert.IsFalse(SingleInstanceLock.IsAlive(0));
        Assert.IsFalse(SingleInstanceLock.IsAlive(-1));
    }

    [TestMethod]
    public void TryDelete_RemovesFile()
    {
        File.WriteAllText(_path, "4321");

        Assert.IsTrue(SingleInstanceLock.TryDelete(_path));
        Assert.IsFalse(File.Exists(_path));
        Assert.IsTrue(SingleInstanceLock.TryDelete(_path), "文件本来就不存在时应当视为成功");
    }
}
