using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.PersonalOptions;

namespace PCL.Core.Test.Minecraft.PersonalOptions;

[TestClass]
public sealed class PersonalOptionsSyncServiceTest
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLPersonalOptions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void Capture_StoresOnlySelectedCategoriesAndCanonicalizesLegacyAlias()
    {
        const string options = "key_key.swapHands:key.keyboard.f\nmouseSensitivity:0.5\nfov:0.5\nsoundCategory_music:0.8\nunknownCustom:value\n";
        var profile = PersonalOptionsSyncService.Capture(options, null,
            PersonalOptionCategories.KeyBindings | PersonalOptionCategories.View);

        Assert.AreEqual("key.keyboard.f", profile.Values["key_key.swapOffhand"]);
        Assert.AreEqual("90", profile.Values["fov"]);
        Assert.IsFalse(profile.Values.ContainsKey("mouseSensitivity"));
        Assert.IsFalse(profile.Values.ContainsKey("soundCategory_music"));
        Assert.IsFalse(profile.Values.ContainsKey("unknownCustom"));
    }

    [TestMethod]
    public void Apply_PreservesUnknownOptionsBomAndCrLfAndDoesNotOverwriteUnselectedValues()
    {
        var text = "\uFEFFfov:70\r\nmouseSensitivity:0.25\r\ncustomModOption:keep\r\n";
        var profile = new PersonalOptionsProfile
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["fov"] = "90",
                ["mouseSensitivity"] = "0.75",
                ["key_key.attack"] = "key.mouse.left",
            },
        };
        var result = PersonalOptionsSyncService.Apply(text, profile,
            PersonalOptionCategories.View | PersonalOptionCategories.Mouse, targetUsesModernKeyNames: true);

        Assert.IsTrue(result.Text.StartsWith('\uFEFF'));
        Assert.IsTrue(result.Text.Contains("fov:90\r\n", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("mouseSensitivity:0.75\r\n", StringComparison.Ordinal));
        Assert.IsTrue(result.Text.Contains("customModOption:keep\r\n", StringComparison.Ordinal));
        Assert.IsFalse(result.Text.Contains("key_key.attack", StringComparison.Ordinal));
        Assert.AreEqual(2, result.AppliedKeys.Count);
    }

    [TestMethod]
    public void Apply_ConvertsModernKeyBindingsToLegacyScancodes()
    {
        var profile = new PersonalOptionsProfile
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal) { ["key_key.forward"] = "key.keyboard.w" },
        };
        var result = PersonalOptionsSyncService.Apply("", profile,
            PersonalOptionCategories.KeyBindings, targetUsesModernKeyNames: false);

        Assert.AreEqual("key_key.forward:17", result.Text);
    }

    [TestMethod]
    public void SessionAppliesBeforeLaunchAndCapturesChangesAfterExit()
    {
        var optionsPath = Path.Combine(_root, "instance", "options.txt");
        var profilePath = Path.Combine(_root, "profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(optionsPath)!);
        File.WriteAllText(profilePath, "{\"schemaVersion\":1,\"values\":{\"fov\":\"95\",\"key_key.forward\":\"key.keyboard.w\"}}");
        File.WriteAllText(optionsPath, "fov:70\nkey_key.forward:17\ncustom:keep\n");

        var session = PersonalOptionsSyncService.PrepareSession(optionsPath, profilePath,
            PersonalOptionCategories.View | PersonalOptionCategories.KeyBindings, targetUsesModernKeyNames: false);
        var appliedText = File.ReadAllText(optionsPath);
        Assert.IsTrue(appliedText.Contains("fov:95\n", StringComparison.Ordinal));
        Assert.IsTrue(appliedText.Contains("key_key.forward:17\n", StringComparison.Ordinal));
        Assert.IsTrue(appliedText.Contains("custom:keep\n", StringComparison.Ordinal));

        File.WriteAllText(optionsPath, "fov:82\nkey_key.forward:30\ncustom:changed\n");
        PersonalOptionsSyncService.FinishSession(session);
        var profile = PersonalOptionsSyncService.LoadProfile(profilePath);
        Assert.AreEqual("82", profile.Values["fov"]);
        Assert.AreEqual("key.keyboard.a", profile.Values["key_key.forward"]);
        Assert.IsFalse(profile.Values.ContainsKey("custom"));
    }

    [TestMethod]
    public void SaveLoad_RoundTripsOnlySupportedCanonicalValues()
    {
        var path = Path.Combine(_root, "nested", "profile.json");
        var profile = new PersonalOptionsProfile
        {
            Values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["key_key.swapHands"] = "key.keyboard.f",
                ["soundCategory_music"] = "0.65",
                ["unknown"] = "not synced",
            },
        };
        PersonalOptionsSyncService.SaveProfile(path, profile);
        var loaded = PersonalOptionsSyncService.LoadProfile(path);

        Assert.AreEqual("key.keyboard.f", loaded.Values["key_key.swapOffhand"]);
        Assert.AreEqual("0.65", loaded.Values["soundCategory_music"]);
        Assert.IsFalse(loaded.Values.ContainsKey("unknown"));
    }
}
