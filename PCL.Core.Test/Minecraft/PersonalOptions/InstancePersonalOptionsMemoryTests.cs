using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.PersonalOptions;

namespace PCL.Core.Test.Minecraft.PersonalOptions;

[TestClass]
public sealed class InstancePersonalOptionsMemoryTests
{
    private string _root = null!;
    private string _instance = null!;
    private string _game = null!;
    private string _shared = null!;
    private string Options => Path.Combine(_game, "options.txt");

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "PCLInstanceControls", Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "versions", "one");
        _game = Path.Combine(_root, "game");
        _shared = Path.Combine(_root, "profiles", "profile.json");
        Directory.CreateDirectory(_instance);
        Directory.CreateDirectory(_game);
        File.WriteAllText(Options, Controls("key.keyboard.q", "0.5"));
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static string Controls(string attack, string mouse) =>
        $"key_key.attack:{attack}\nkey_key.use:key.mouse.right\nmouseSensitivity:{mouse}\nfov:70\nmod_setting:keep\n";

    private PersonalOptionsMemoryRequest Request(bool remember = true,
        PersonalOptionCategories categories = PersonalOptionCategories.None) =>
        new(_instance, _game, _shared, remember, categories, TargetUsesModernKeyNames: true);

    private InstancePersonalOptionsMemory.PersonalOptionsRunSession Open(
        Func<PersonalOptionsChangePreview, PersonalOptionsApplyChoice>? choose = null,
        PersonalOptionsMemoryRequest? request = null) =>
        InstancePersonalOptionsMemory.Prepare(request ?? Request(), choose ?? (_ => PersonalOptionsApplyChoice.Skip), () => false)!;

    private void SeedMemory()
    {
        using var session = Open();
        session.Complete(true, () => false, true, false);
    }

    [TestMethod]
    public void FirstUse_DoesNotApplyOtherProfiles_AndCapturesOnlyAfterReliableExit()
    {
        var bytes = File.ReadAllBytes(Options);
        var memory = InstancePersonalOptionsMemory.GetMemoryPath(_instance);
        using (var failed = Open(_ => throw new InvalidOperationException("No first-use preview expected.")))
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Options));
            failed.Complete(false, () => false, true, false);
        }
        Assert.IsFalse(File.Exists(memory));
        SeedMemory();
        var document = JsonSerializer.Deserialize<InstancePersonalOptionsDocument>(File.ReadAllText(memory),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.AreEqual("key.keyboard.q", document.Profile.Values["key_key.attack"]);
        Assert.IsFalse(document.Profile.Values.ContainsKey("fov"));
        Assert.IsFalse(document.Profile.Values.ContainsKey("mod_setting"));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Options));
    }

    [TestMethod]
    public void ExistingDifferentValues_RequireConfirmation_SkipPreservesBytes()
    {
        SeedMemory();
        var changed = Controls("key.keyboard.e", "0.75");
        File.WriteAllText(Options, changed);
        var offered = false;
        using var session = Open(preview =>
        {
            offered = true;
            Assert.IsTrue(preview.Differences.Count >= 2);
            return PersonalOptionsApplyChoice.Skip;
        });
        Assert.IsTrue(offered);
        Assert.AreEqual(changed, File.ReadAllText(Options));
    }

    [TestMethod]
    public void SharedConflict_OffersBothSources_AndChoiceIsExplicit()
    {
        SeedMemory();
        File.WriteAllText(Options, Controls("key.keyboard.e", "0.75"));
        PersonalOptionsSyncService.SaveProfile(_shared, new PersonalOptionsProfile
        {
            Values = new Dictionary<string, string> { ["key_key.attack"] = "key.keyboard.r", ["fov"] = "90" },
        });
        using var session = Open(preview =>
        {
            Assert.IsTrue(preview.HasConflict);
            Assert.IsTrue(preview.HasInstanceValues && preview.HasSharedValues);
            return PersonalOptionsApplyChoice.PreferShared;
        }, Request(categories: PersonalOptionCategories.KeyBindings | PersonalOptionCategories.View));
        var result = File.ReadAllText(Options);
        StringAssert.Contains(result, "key_key.attack:key.keyboard.r");
        StringAssert.Contains(result, "fov:90");
        StringAssert.Contains(result, "mod_setting:keep");
    }

    [TestMethod]
    public void PreviewThenExternalChange_PreservesTheNewOptions()
    {
        SeedMemory();
        File.WriteAllText(Options, Controls("key.keyboard.e", "0.75"));
        var external = Controls("key.keyboard.t", "0.25");
        using var session = Open(_ =>
        {
            File.WriteAllText(Options, external);
            return PersonalOptionsApplyChoice.PreferInstance;
        });
        Assert.AreEqual(external, File.ReadAllText(Options));
    }

    [TestMethod]
    public void PreviewThenMemoryChange_PreservesSourceAndDoesNotApplyStaleValues()
    {
        SeedMemory();
        var memory = InstancePersonalOptionsMemory.GetMemoryPath(_instance);
        var current = Controls("key.keyboard.e", "0.75");
        File.WriteAllText(Options, current);
        using var session = Open(_ =>
        {
            File.WriteAllText(memory, "{\"schemaVersion\":999}");
            return PersonalOptionsApplyChoice.PreferInstance;
        });
        Assert.AreEqual(current, File.ReadAllText(Options));
        session.Complete(true, () => false, true, false);
        Assert.AreEqual("{\"schemaVersion\":999}", File.ReadAllText(memory));
    }

    [TestMethod]
    public void DisabledFeatures_DoNotCreateSidecars_AndDisabledSharedDoesNotReadBadProfile()
    {
        Assert.IsNull(InstancePersonalOptionsMemory.Prepare(Request(remember: false),
            _ => throw new InvalidOperationException(), () => false));
        Assert.IsFalse(Directory.Exists(Path.Combine(_game, "PCL")));
        Directory.CreateDirectory(Path.GetDirectoryName(_shared)!);
        File.WriteAllText(_shared, "invalid JSON");
        using var session = Open();
        session.Complete(true, () => false, true, false);
        Assert.AreEqual("invalid JSON", File.ReadAllText(_shared));
        Assert.IsTrue(File.Exists(InstancePersonalOptionsMemory.GetMemoryPath(_instance)));
    }

    [TestMethod]
    public void SameOptionsDirectory_IsCoordinatedAcrossInstances_AndOtherDirectoriesRemainAvailable()
    {
        using var first = Open();
        var secondInstance = Path.Combine(_root, "versions", "two");
        Directory.CreateDirectory(secondInstance);
        var second = Request() with { InstanceDirectory = secondInstance };
        Assert.IsNull(InstancePersonalOptionsMemory.Prepare(second, _ => PersonalOptionsApplyChoice.Skip, () => false));
        var otherGame = Path.Combine(_root, "other-game");
        Directory.CreateDirectory(otherGame);
        using var independent = InstancePersonalOptionsMemory.Prepare(second with { GameDirectory = otherGame },
            _ => PersonalOptionsApplyChoice.Skip, () => false);
        Assert.IsNotNull(independent);
        Assert.ThrowsExactly<IOException>(() => PersonalOptionsSyncService.PrepareSession(Options, _shared,
            PersonalOptionCategories.KeyBindings, true));
    }

    [TestMethod]
    public void UnknownSchemaOrConcurrentMemoryChange_IsNeverRewritten()
    {
        var memory = InstancePersonalOptionsMemory.GetMemoryPath(_instance);
        Directory.CreateDirectory(Path.GetDirectoryName(memory)!);
        File.WriteAllText(memory, "{\"schemaVersion\":123,\"custom\":\"keep\"}");
        using (var session = Open()) session.Complete(true, () => false, true, false);
        Assert.AreEqual("{\"schemaVersion\":123,\"custom\":\"keep\"}", File.ReadAllText(memory));
        File.Delete(memory);
        using var next = Open();
        File.WriteAllText(memory, "user edit");
        next.Complete(true, () => false, true, false);
        Assert.AreEqual("user edit", File.ReadAllText(memory));
    }

    [TestMethod]
    public void CopiedOrRenamedInstance_DoesNotAdoptTheOriginalOwnerMemory()
    {
        SeedMemory();
        var original = InstancePersonalOptionsMemory.GetMemoryPath(_instance);
        var target = Path.Combine(_root, "versions", "copy");
        var copiedOriginalPath = Path.Combine(target, "PCL", "Enhancements", "PersonalOptions", Path.GetFileName(original));
        Directory.CreateDirectory(Path.GetDirectoryName(copiedOriginalPath)!);
        File.Copy(original, copiedOriginalPath);
        File.WriteAllText(Options, Controls("key.keyboard.e", "0.75"));
        using var session = Open(_ => throw new InvalidOperationException("Copied memory must not be offered."),
            Request() with { InstanceDirectory = target });
        session.Complete(true, () => false, true, false);
        Assert.IsTrue(File.Exists(InstancePersonalOptionsMemory.GetMemoryPath(target)));
        Assert.AreNotEqual(original, InstancePersonalOptionsMemory.GetMemoryPath(target));
        Assert.AreEqual(File.ReadAllText(original), File.ReadAllText(copiedOriginalPath));
    }

    [TestMethod]
    public void IncompleteOrBusyExit_DoesNotReplaceGoodMemory_AndTurningOffStopsCapture()
    {
        SeedMemory();
        var memory = InstancePersonalOptionsMemory.GetMemoryPath(_instance);
        var saved = File.ReadAllBytes(memory);
        using (var session = Open())
        {
            File.WriteAllText(Options, "mouseSensitivity:0.99\n");
            session.Complete(true, () => false, true, false);
        }
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(memory));
        File.WriteAllText(Options, Controls("key.keyboard.e", "0.75"));
        using (var session = Open()) session.Complete(true, () => true, true, false);
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(memory));
        using (var session = Open()) session.Complete(true, () => false, false, false);
        CollectionAssert.AreEqual(saved, File.ReadAllBytes(memory));
    }

    [TestMethod]
    public void SharedProfileChangedDuringPlay_IsNotOverwrittenByAnOlderSession()
    {
        PersonalOptionsSyncService.SaveProfile(_shared, new PersonalOptionsProfile
        { Values = new Dictionary<string, string> { ["key_key.attack"] = "key.keyboard.q" } });
        using var session = Open(request: Request(categories: PersonalOptionCategories.KeyBindings));
        var external = "{\"schemaVersion\":1,\"values\":{\"key_key.attack\":\"key.keyboard.t\"}}";
        File.WriteAllText(_shared, external);
        session.Complete(true, () => false, true, true);
        Assert.AreEqual(external, File.ReadAllText(_shared));
    }

    [TestMethod]
    public void LauncherExitsWhileGameStillRuns_PersistedPidIdentityPreventsAnotherRestore()
    {
        using (var owner = Open())
        {
            using var process = Process.GetCurrentProcess();
            owner.MarkGameStarted(process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        Assert.IsNull(InstancePersonalOptionsMemory.Prepare(Request(),
            _ => throw new InvalidOperationException("A running owner must prevent this preview."), () => false));
    }

    [TestMethod]
    public void InvalidPersistedOwnerCanBeRepairedOnlyWhenNoPotentialJavaGameIsRunning()
    {
        var marker = Path.Combine(_game, "PCL", "Enhancements", "options-session.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        foreach (var content in new[] { "not-a-pid", new string('x', 129), "0:-1" })
        {
            File.WriteAllText(marker, content);
            using var session = InstancePersonalOptionsMemory.Prepare(Request(), _ => PersonalOptionsApplyChoice.Skip, () => false);
            var java = Process.GetProcessesByName("java").Concat(Process.GetProcessesByName("javaw")).ToArray();
            try
            {
                if (java.Length == 0)
                {
                    Assert.IsNotNull(session, "An invalid marker must not permanently disable synchronization.");
                }
                else
                {
                    Assert.IsNull(session, "An unidentifiable owner must not override a potential surviving game.");
                }
            }
            finally { foreach (var process in java) process.Dispose(); }
        }
    }

    [TestMethod]
    public void LineMerge_PreservesBomMixedDelimitersAndUnknownFields_AndConvertsKnownLegacyBinding()
    {
        var current = "\uFEFFkey_key.attack:30\r\nmouseSensitivity:0.5\nmod_custom:keep\rfov:70";
        var profile = new PersonalOptionsProfile
        { Values = new Dictionary<string, string> { ["key_key.attack"] = "key.keyboard.q" } };
        var merged = InstancePersonalOptionsMemory.MergePreservingLines(current, profile, modern: false, fovIsDegrees: true);
        Assert.AreEqual("\uFEFFkey_key.attack:16\r\nmouseSensitivity:0.5\nmod_custom:keep\rfov:70", merged);
    }
}
