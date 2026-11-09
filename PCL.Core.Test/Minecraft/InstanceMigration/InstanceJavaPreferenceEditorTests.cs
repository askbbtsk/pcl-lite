using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.Minecraft.InstanceMigration;

namespace PCL.Core.Test.Minecraft.InstanceMigration;

[TestClass]
public sealed class InstanceJavaPreferenceEditorTests
{
    [TestMethod]
    public void OnlyJavaValueChangesWhileCommentsSpacingAndUnknownUserFieldsRemainIdentical()
    {
        const string old = "# user comment\r\nUnknown:保持原样\r\nVersionArgumentJavaSelect:{\"kind\":\"exist\",\"path\":\"old\"}\nMemory:4096\r\n";
        const string preference = "{\"kind\":\"auto\"}";
        var original = new UTF8Encoding(true).GetPreamble().AsSpan().ToArray();
        var payload = Encoding.UTF8.GetBytes(old);
        var combined = new byte[original.Length + payload.Length]; original.CopyTo(combined, 0); payload.CopyTo(combined, original.Length);
        var updated = InstanceJavaPreferenceEditor.WithJavaPreference(combined, preference);
        CollectionAssert.AreEqual(new byte[] { 0xef, 0xbb, 0xbf }, updated.AsSpan(0, 3).ToArray());
        Assert.AreEqual(old.Replace("{\"kind\":\"exist\",\"path\":\"old\"}", preference, StringComparison.Ordinal), Encoding.UTF8.GetString(updated, 3, updated.Length - 3));
        Assert.AreEqual(old, Encoding.UTF8.GetString(combined, 3, combined.Length - 3));
    }

    [TestMethod]
    public void MissingJavaKeyAppendsWithoutReformattingUserSettingsAndDuplicateKeysAllChange()
    {
        const string preference = "{\"kind\":\"auto\"}";
        Assert.AreEqual("Memory:4096\nUnknown:user\nVersionArgumentJavaSelect:" + preference + "\n",
            Encoding.UTF8.GetString(InstanceJavaPreferenceEditor.WithJavaPreference(Encoding.UTF8.GetBytes("Memory:4096\nUnknown:user"), preference)));
        const string duplicates = "VersionArgumentJavaSelect:old\r\nUnknown:user\nVersionArgumentJavaSelect:other\r";
        Assert.AreEqual("VersionArgumentJavaSelect:" + preference + "\r\nUnknown:user\nVersionArgumentJavaSelect:" + preference + "\r",
            Encoding.UTF8.GetString(InstanceJavaPreferenceEditor.WithJavaPreference(Encoding.UTF8.GetBytes(duplicates), preference)));
    }
}
