using System;
using System.IO;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App.Lunar;

namespace PCL.Core.Test.App.Lunar;

[TestClass]
public sealed class LunarGameAccountSelectionTests
{
    private string _root = null!;
    private string PathToAccounts => LunarAccountStore.FilePath(_root);
    private static readonly Guid First = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Second = Guid.Parse("22222222-2222-4222-8222-222222222222");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pcl-game-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(PathToAccounts)!);
        File.WriteAllText(PathToAccounts, """
            {"activeAccountLocalId":"local-one","custom":42,"accounts":{
              "local-one":{"accessToken":"first-access","refreshToken":"first-refresh","minecraftProfile":{"id":"11111111111141118111111111111111","name":"First"}},
              "local-two":{"accessToken":"second-access","refreshToken":"second-refresh","custom":true,"minecraftProfile":{"id":"22222222222242228222222222222222","name":"Second"}}}}
            """);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, true);

    [TestMethod]
    public void StillValidTokenMustAlsoSwitchTheDiskSelection()
    {
        string original = File.ReadAllText(PathToAccounts);
        var before = JsonNode.Parse(original)!;
        Assert.IsTrue(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", Second));
        var after = JsonNode.Parse(File.ReadAllText(PathToAccounts))!;
        Assert.AreEqual("local-two", after["activeAccountLocalId"]!.GetValue<string>());
        Assert.IsTrue(JsonNode.DeepEquals(before["accounts"], after["accounts"]), "Selection must not replace credentials or profiles.");
        Assert.AreEqual(42, after["custom"]!.GetValue<int>());
        Assert.AreEqual(original, File.ReadAllText(PathToAccounts + ".pclbak"));
    }

    [TestMethod]
    public void BackgroundTokenRefreshDoesNotSelectThatAccount()
    {
        Assert.IsTrue(LunarAccountStore.UpdateToken(_root, "local-two", "renewed-access", null));
        Assert.AreEqual("local-one", LunarAccountStore.Read(_root).ActiveLocalId);
        Assert.IsTrue(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", Second));
        Assert.AreEqual("local-two", LunarAccountStore.Read(_root).ActiveLocalId);
        Assert.IsTrue(LunarAccountStore.UpdateToken(_root, "local-one", "other-renewed-access", null));
        Assert.AreEqual("local-two", LunarAccountStore.Read(_root).ActiveLocalId);
    }

    [TestMethod]
    public void RepeatedGameSwitchesFollowTheRequestedUuidRatherThanTheFirstAccount()
    {
        foreach (var (localId, uuid) in new[] { ("local-two", Second), ("local-one", First), ("local-two", Second) })
        {
            Assert.IsTrue(LunarAccountStore.SelectRefreshedGameAccount(_root, localId, uuid));
            Assert.AreEqual(localId, LunarAccountStore.Read(_root).ActiveLocalId);
        }
    }

    [TestMethod]
    public void WrongUuidMissingEntryAndEmptyUuidLeaveTheFileUntouched()
    {
        string original = File.ReadAllText(PathToAccounts);
        Assert.IsFalse(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", First));
        Assert.IsFalse(LunarAccountStore.SelectRefreshedGameAccount(_root, "missing", Second));
        Assert.IsFalse(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", Guid.Empty));
        Assert.AreEqual(original, File.ReadAllText(PathToAccounts));
        Assert.IsFalse(File.Exists(PathToAccounts + ".pclbak"));
    }

    [TestMethod]
    public void UpsertUsesTheRequestedUuidWhenItsSuggestedLocalIdBelongsToAnotherAccount()
    {
        var before = JsonNode.Parse(File.ReadAllText(PathToAccounts))!["accounts"]!["local-one"];
        Assert.IsTrue(LunarAccountStore.Upsert(_root, "local-one", "Second", "Second", Second.ToString("D"),
            "second-renewed", null, null, out var active));
        Assert.AreEqual("local-two", active);
        var root = JsonNode.Parse(File.ReadAllText(PathToAccounts))!;
        Assert.IsTrue(JsonNode.DeepEquals(before, root["accounts"]!["local-one"]));
        Assert.AreEqual("second-renewed", root["accounts"]!["local-two"]!["accessToken"]!.GetValue<string>());
        Assert.AreEqual(active, root["activeAccountLocalId"]!.GetValue<string>());

        const string third = "33333333-3333-4333-8333-333333333333";
        Assert.IsTrue(LunarAccountStore.Upsert(_root, "local-one", "Third", "Third", third,
            "third-access", null, null, out active));
        Assert.AreNotEqual("local-one", active);
        Assert.AreNotEqual("local-two", active);
        root = JsonNode.Parse(File.ReadAllText(PathToAccounts))!;
        Assert.IsTrue(JsonNode.DeepEquals(before, root["accounts"]!["local-one"]));
        Assert.AreEqual("third-access", root["accounts"]![active]!["accessToken"]!.GetValue<string>());
        Assert.AreEqual(active, root["activeAccountLocalId"]!.GetValue<string>());
    }

    [TestMethod]
    public void RemovedOrInvalidTargetCannotBecomeTheActiveAccount()
    {
        var document = JsonNode.Parse(File.ReadAllText(PathToAccounts))!;
        document["accounts"]!["local-two"]!["accessToken"] = "";
        File.WriteAllText(PathToAccounts, document.ToJsonString());
        Assert.IsFalse(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", Second));
        ((JsonObject)document["accounts"]!).Remove("local-two");
        File.WriteAllText(PathToAccounts, document.ToJsonString());
        Assert.IsFalse(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", Second));
        Assert.AreEqual("local-one", LunarAccountStore.Read(_root).ActiveLocalId);
    }

    [TestMethod]
    public void FailedReplacementKeepsTheOldSelectionAndRemovesTheTemporaryFile()
    {
        string original = File.ReadAllText(PathToAccounts);
        using (var held = new FileStream(PathToAccounts, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.IsFalse(LunarAccountStore.SelectRefreshedGameAccount(_root, "local-two", Second));
        Assert.AreEqual(original, File.ReadAllText(PathToAccounts));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(PathToAccounts)!, "*.tmp"));
    }
}
