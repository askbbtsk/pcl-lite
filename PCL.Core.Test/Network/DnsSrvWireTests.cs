using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.IO.Net.Dns;

namespace PCL.Core.Test.Network;

[TestClass]
public class DnsSrvWireTests
{
    [TestMethod]
    public void SerializationRespectsOuterOffsetAndIncludesTerminator()
    {
        var source = new DnsSrvResource { Target = "mc.example.net.", Priority = 12, Weight = 34, Port = 25565 };
        var bytes = new byte[100];
        Array.Fill(bytes, (byte)0xCC);
        var end = 9;
        source.WriteBytes(bytes, ref end);
        Assert.AreEqual((byte)0xCC, bytes[8]);
        Assert.AreEqual((byte)0, bytes[end - 1]);
        Assert.AreEqual((byte)0xCC, bytes[end]);
        var read = 9;
        var result = new DnsSrvResource();
        result.ReadBytes(bytes, ref read, end - 9);
        Assert.AreEqual(end, read);
        Assert.AreEqual("mc.example.net", result.Target);
        Assert.AreEqual(25565, result.Port);
        Assert.AreEqual(12, result.Priority);
        Assert.AreEqual(34, result.Weight);
    }

    [TestMethod]
    public void InvalidOrUndersizedWriteDoesNotPartiallyModifyOffset()
    {
        var offset = 3;
        var record = new DnsSrvResource { Target = new string('x', 64) + ".net" };
        Assert.ThrowsExactly<ArgumentException>(() => record.WriteBytes(new byte[100], ref offset));
        Assert.AreEqual(3, offset);
        record.Target = "example.net";
        Assert.ThrowsExactly<ArgumentException>(() => record.WriteBytes(new byte[4], ref offset));
        Assert.AreEqual(3, offset);
    }
}
