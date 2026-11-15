using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test;

[TestClass]
public class LunarJvmArgumentsTest
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-lunar-jvm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void JavaAgent_ReachesJvmUnchangedAndDisablesBakedClasses()
    {
        const string argument = @"-javaagent:E:\mods\lunar-agent.jar=E:\mods\agent-mods.json";
        var arguments = Build(argument);

        Assert.AreEqual(1, arguments.Count(value => value == argument));
        CollectionAssert.Contains(arguments, "-Dichor.prebakeClasses=false");
        Assert.IsTrue(Array.IndexOf(arguments, "-Dichor.prebakeClasses=false") <
                      Array.IndexOf(arguments, argument));
        Assert.IsTrue(Array.IndexOf(arguments, argument) < Array.IndexOf(arguments, "-cp"));
    }

    [TestMethod]
    public void QuotedAgentPaths_WithSpacesAndUnicodeRemainOneArgument()
    {
        var arguments = Build("-javaagent:\"E:\\Lunar Mods\\测试 agent.jar\"=\"E:\\Lunar Mods\\agent config.json\" -Dtest=enabled");

        CollectionAssert.Contains(arguments,
            @"-javaagent:E:\Lunar Mods\测试 agent.jar=E:\Lunar Mods\agent config.json");
        CollectionAssert.Contains(arguments, "-Dtest=enabled");
        CollectionAssert.Contains(arguments, "-Dichor.prebakeClasses=false");
    }

    [TestMethod]
    public void MultipleAgents_PreserveOrderAndAddOnlyOneCacheDefault()
    {
        var arguments = Build("-javaagent:first.jar=one -javaagent:second.jar=two");

        Assert.AreEqual(1, arguments.Count(value => value == "-Dichor.prebakeClasses=false"));
        Assert.IsTrue(Array.IndexOf(arguments, "-javaagent:first.jar=one") <
                      Array.IndexOf(arguments, "-javaagent:second.jar=two"));
    }

    [TestMethod]
    public void ExplicitCacheChoice_ComesAfterCompatibilityDefault()
    {
        var arguments = Build("-Dichor.prebakeClasses=true -javaagent:agent.jar");

        CollectionAssert.Contains(arguments, "-Dichor.prebakeClasses=false");
        CollectionAssert.Contains(arguments, "-Dichor.prebakeClasses=true");
        Assert.IsTrue(Array.IndexOf(arguments, "-Dichor.prebakeClasses=false") <
                      Array.IndexOf(arguments, "-Dichor.prebakeClasses=true"));
    }

    [TestMethod]
    public void WithoutAgent_DoesNotChangeCaching()
    {
        var arguments = Build("-Xmx6G -Dpath=-javaagent:not-an-agent.jar");

        Assert.IsFalse(arguments.Contains("-Dichor.prebakeClasses=false"));
        Assert.IsTrue(Array.IndexOf(arguments, "-Xmx6G") > Array.IndexOf(arguments, "-Xmx2048m"));
    }

    [TestMethod]
    public void CustomArguments_OverrideMetadataAndStayBeforeMainClass()
    {
        var arguments = Build("-Dtest=user -javaagent:agent.jar",
            ["-Dtest=metadata", "-Dichor.prebakeClasses=true"]);

        CollectionAssert.Contains(arguments, "-Dichor.prebakeClasses=false");
        CollectionAssert.Contains(arguments, "-Dtest=user");
        Assert.IsTrue(Array.IndexOf(arguments, "-Dtest=metadata") < Array.IndexOf(arguments, "-Dtest=user"));
        Assert.IsTrue(Array.IndexOf(arguments, "-Dichor.prebakeClasses=true") <
                      Array.IndexOf(arguments, "-Dichor.prebakeClasses=false"));
        Assert.IsTrue(Array.IndexOf(arguments, "-Dtest=user") < Array.IndexOf(arguments, "example.Main"));
    }

    private string[] Build(string raw, string[]? metadataArguments = null)
    {
        var data = new LunarLaunchData
        {
            MainClass = "example.Main",
            Artifacts = [new LunarArtifact("genesis.jar", "", "", "CLASS_PATH", 0)],
            JvmExtraArguments = metadataArguments ?? []
        };
        var options = new LunarLaunchOptions
        {
            LunarRoot = _root, GameDirectory = _root, VersionName = "1.8.9",
            PlayerName = "Test", PlayerUuid = "test", AccessToken = "test",
            ExtraJvmArguments = LunarLaunchProfile.SplitArguments(raw)
        };
        var startInfo = LunarClientService.BuildStartInfo(data, options, "java.exe", _root, _root);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.AreEqual(_root, startInfo.WorkingDirectory);
        return startInfo.ArgumentList.ToArray();
    }
}
