using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public sealed class LunarCrashDiagnosticsTests
{
    [TestMethod]
    public void RecognizesNativeMemoryCrashAfterResourcePackWarnings()
    {
        Assert.IsTrue(LunarCrashDiagnostics.IsNativeMemoryFailure([
            "Missing metadata in pack file/skidpack.zip",
            "# There is insufficient memory for the Java Runtime Environment to continue.",
            "# Native memory allocation (malloc) failed to allocate 9128728 bytes. Error detail: Chunk::new",
            "# E:\\game\\hs_err_pid15428.log"]));
        Assert.IsTrue(LunarCrashDiagnostics.IsNativeMemoryFailure([
            "# Native memory allocation (mmap) failed to map 1048576 bytes."]));
    }

    [TestMethod]
    public void DoesNotConfuseJarLocksOrInvalidPackJsonWithNativeMemoryFailure()
    {
        Assert.IsFalse(LunarCrashDiagnostics.IsNativeMemoryFailure([]));
        Assert.IsFalse(LunarCrashDiagnostics.IsNativeMemoryFailure([
            "com.google.gson.stream.MalformedJsonException: path $.pack.description",
            "The process cannot access the file because it is being used by another process.",
            "java.lang.OutOfMemoryError: Java heap space",
            "Allocated only 15/20 buffers"]));
    }
}
