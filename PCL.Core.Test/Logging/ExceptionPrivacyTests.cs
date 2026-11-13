using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Logging;
using PCL.Core.Utils.OS;

namespace PCL.Core.Test.Logging;

[TestClass]
[DoNotParallelize]
public class ExceptionPrivacyTests
{
    private bool _previous;

    [TestInitialize]
    public void Initialize()
    {
        _previous = UserPathMasker.Enabled;
        UserPathMasker.Enabled = true;
    }

    [TestCleanup]
    public void Cleanup() => UserPathMasker.Enabled = _previous;

    [TestMethod]
    public void InitializationFailureMasksNestedExceptionAndPreservesStack()
    {
        Exception error;
        try
        {
            throw new InvalidOperationException(@"Initialization: C:\Users\Jane Doe\PCL",
                new IOException(@"File unavailable: C:\\Users\\张三\\PCL\\cache.db"));
        }
        catch (Exception ex) { error = ex; }

        var details = ExceptionDetails.GetDebugDetails(error);
        StringAssert.Contains(details, nameof(InvalidOperationException));
        StringAssert.Contains(details, nameof(IOException));
        StringAssert.Contains(details, nameof(InitializationFailureMasksNestedExceptionAndPreservesStack));
        StringAssert.Contains(details, @"C:\Users\*****\PCL");
        StringAssert.Contains(details, @"C:\\Users\\*****\\PCL\\cache.db");
        Assert.IsFalse(details.Contains("Jane Doe", StringComparison.Ordinal));
        Assert.IsFalse(details.Contains("张三", StringComparison.Ordinal));
        StringAssert.Contains(ExceptionDetails.GetUserReason(error), @"C:\Users\*****\PCL");
    }

    [TestMethod]
    public void SummaryWithoutDetailsIsMaskedBeforeLocalizationIsAvailable()
    {
        // No WPF Application or configuration initialization is needed.
        Assert.AreEqual(@"Cannot initialize C:\Users\*****\PCL",
            ExceptionDetails.Compose(@"Cannot initialize C:\Users\Jane Doe\PCL"));
    }

    [TestMethod]
    public void ErrorDetailsRespectDisabledPrivacySetting()
    {
        UserPathMasker.Enabled = false;
        var error = new IOException(@"C:\Users\Jane Doe\PCL");
        Assert.AreEqual(error.ToString(), ExceptionDetails.GetDebugDetails(error));
        Assert.AreEqual(error.Message, ExceptionDetails.GetUserReason(error));
    }

    [TestMethod]
    public async Task LogFilesAreMaskedAtWriteTime()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PCL-Privacy", Guid.NewGuid().ToString("N"));
        try
        {
            await using (var logger = new Logger(new LoggerConfiguration(folder)))
                logger.Error(@"Exception at C:\Users\Jane Doe\PCL\launcher.dll");
            var text = await File.ReadAllTextAsync(Directory.GetFiles(folder, "*.log")[0]);
            StringAssert.Contains(text, @"C:\Users\*****\PCL\launcher.dll");
            Assert.IsFalse(text.Contains("Jane Doe", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
