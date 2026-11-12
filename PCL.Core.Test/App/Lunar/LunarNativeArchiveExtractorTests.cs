using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public sealed class LunarNativeArchiveExtractorTests
{
    [TestMethod]
    public async Task IdenticalNativeIsSkippedEvenWhenAnotherProcessHasItOpen()
    {
        var root = CreateRoot();
        try
        {
            string native = Path.Combine(root, "natives", "msvcp140.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            await File.WriteAllTextAsync(native, "same native bytes");
            string archive = CreateArchive(root, "msvcp140.dll", "same native bytes");

            using (new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.Read))
                await LunarNativeArchiveExtractor.ExtractChangedFilesAsync(archive, Path.GetDirectoryName(native)!,
                    CancellationToken.None);

            Assert.AreEqual("same native bytes", await File.ReadAllTextAsync(native));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(native)!, "*.pcl-native-*.tmp").Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ChangedNativeShowsFriendlyOccupiedFileAndPreservesOriginalUntilRetry()
    {
        var root = CreateRoot();
        try
        {
            string native = Path.Combine(root, "natives", "WebOSR-Binding.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(native)!);
            await File.WriteAllTextAsync(native, "old native");
            string archive = CreateArchive(root, "WebOSR-Binding.dll", "new native");

            using (new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var exception = await Assert.ThrowsExactlyAsync<LunarNativeFilesUnavailableException>(() =>
                    LunarNativeArchiveExtractor.ExtractChangedFilesAsync(archive, Path.GetDirectoryName(native)!,
                        CancellationToken.None));
                CollectionAssert.Contains(exception.Files.ToArray(), "WebOSR-Binding.dll");
                StringAssert.Contains(exception.Message, "请关闭正在运行的相关游戏后重试");
                Assert.AreEqual("old native", await File.ReadAllTextAsync(native));
                Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(native)!, "*.pcl-native-*.tmp").Length);
            }

            await LunarNativeArchiveExtractor.ExtractChangedFilesAsync(archive, Path.GetDirectoryName(native)!,
                CancellationToken.None);
            Assert.AreEqual("new native", await File.ReadAllTextAsync(native));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "PCL-LunarNative-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateArchive(string root, string name, string contents)
    {
        var path = Path.Combine(root, "native.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var stream = archive.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
        return path;
    }
}
