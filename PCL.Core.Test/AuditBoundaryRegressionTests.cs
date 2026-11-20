using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO;
using PCL.Core.Mcp.Analysis;
using PCL.Core.Minecraft.Yggdrasil;
using PCL.Core.Utils.Encryption;

namespace PCL.Core.Test;

[TestClass]
public sealed class AuditBoundaryRegressionTests
{
    [TestMethod]
    public void NullJsonSourceKeepsAnIndependentCopyOfTheTarget()
    {
        var target = JsonNode.Parse("""{"retained":true}""")!;
        var result = Files.MergeJson(target, null);
        Assert.AreEqual(target.ToJsonString(), result.ToJsonString());
        Assert.AreNotSame(target, result);
        Assert.ThrowsExactly<ArgumentNullException>(() => Files.MergeJson(null, null));
    }

    [TestMethod]
    [DataRow("http://http-example.test/api/http-login", "https://http-example.test/api/http-login")]
    [DataRow("http-api", "https://example.test/base/http-api")]
    [DataRow("../login", "https://example.test/login")]
    public void AuthenticationRedirectChangesOnlyTheScheme(string location, string expected)
    {
        Assert.AreEqual(expected,
            ApiLocation.ResolveRedirect(new Uri("https://example.test/base/"), location));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(31)]
    [DataRow(47)]
    public void TruncatedLegacyEncryptionFailsBeforeSlicing(int length)
    {
#pragma warning disable CS0618
        Assert.ThrowsExactly<ArgumentException>(() =>
            AesCbcProvider.Instance.Decrypt(new byte[length], new byte[32]));
#pragma warning restore CS0618
    }

    [TestMethod]
    [DataRow("META-INF/mods.toml", "mandatory=false")]
    [DataRow("META-INF/neoforge.mods.toml", "type=\"optional\"")]
    public void ForgeDependencyVersionsComeFromTheirOwnSections(string entryName, string optionalMarker)
    {
        var path = Path.Combine(Path.GetTempPath(), "pcl-metadata-" + Guid.NewGuid().ToString("N") + ".jar");
        try
        {
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open()))
                writer.Write($$"""
                    [[mods]]
                    modId="test"
                    displayName="Test"
                    version="1.0"
                    [[dependencies.test]]
                    modId="forge"
                    versionRange="[47,)"
                    [[dependencies.test]]
                    versionRange="[1.20.1,1.21)"
                    modId="minecraft"
                    [[dependencies.test]]
                    modId="optional-mod"
                    {{optionalMarker}}
                    versionRange="[9,)"
                    """);
            var metadata = ModJarReader.Read(path);
            Assert.IsNotNull(metadata);
            CollectionAssert.Contains(metadata.Depends.ToArray(), "minecraft [1.20.1,1.21)");
            CollectionAssert.Contains(metadata.Depends.ToArray(), "forge [47,)");
            Assert.HasCount(2, metadata.Depends);
            Assert.AreEqual("[1.20.1,1.21)", metadata.McVersion);
        }
        finally { File.Delete(path); }
    }
}
