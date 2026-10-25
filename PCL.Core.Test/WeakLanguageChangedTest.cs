using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Localization;

namespace PCL.Core.Test;

[TestClass]
public class WeakLanguageChangedTest
{
    private sealed class Dummy;

    // 正确写法：静态方法组，Target 一定为 null
    private static void StaticHandler(Dummy page) { }

    [TestMethod]
    public void AddShouldAcceptStaticMethodGroup()
    {
        var page = new Dummy();
        WeakLanguageChanged.Add(page, StaticHandler);
    }

    [TestMethod]
    public void AddShouldRejectInstanceCapturingLambda()
    {
        var page = new Dummy();
        var message = CaptureArgumentException(() => WeakLanguageChanged.Add(page, p => { }));
        StringAssert.Contains(message, "静态方法组");
    }

    [TestMethod]
    public void AddShouldRejectStaticLambdaWithMethodGroupConversion()
    {
        // 这个用例是回归保护：static lambda 内部做方法组转换（例如
        // static page => ModBase.RunInUi(page.Foo())）时，编译器生成的委托依然带有 Target，
        // Add 必须拒绝，否则主窗体启动时会抛异常导致白屏。
        var page = new Dummy();
        var message = CaptureArgumentException(() =>
            WeakLanguageChanged.Add(page, static p => Run(p)));
        StringAssert.Contains(message, "静态方法组");
    }

    private static string CaptureArgumentException(Action action)
    {
        try
        {
            action();
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        Assert.Fail("预期抛出 ArgumentException，但实际没有抛出");
        return string.Empty;
    }

    private static void Run(Dummy page) { }
}
