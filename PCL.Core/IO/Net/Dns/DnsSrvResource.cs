using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Ae.Dns.Protocol.Records;

namespace PCL.Core.IO.Net.Dns;

public class DnsSrvResource : IDnsResource
{
    public string Target { get; set; } = "";
    public int Weight { get; set; }
    public int Priority { get; set; }
    public int Port { get; set; }

    public void WriteBytes(Memory<byte> bytes, ref int offset)
    {
        if (Priority is < 0 or > ushort.MaxValue || Weight is < 0 or > ushort.MaxValue ||
            Port is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Port), "SRV fields must fit unsigned 16-bit values.");
        var segments = Target.TrimEnd('.').Length == 0
            ? Array.Empty<string>() : Target.TrimEnd('.').Split('.');
        var encoded = new List<byte[]>();
        var required = 7; // priority, weight, port, terminating root label
        foreach (var segment in segments)
        {
            var label = Encoding.UTF8.GetBytes(segment);
            if (label.Length is 0 or > 63)
                throw new ArgumentException("Invalid DNS label length.", nameof(Target));
            encoded.Add(label);
            required += label.Length + 1;
        }
        if (required - 6 > 255)
            throw new ArgumentException("DNS target exceeds 255 bytes.", nameof(Target));
        if (offset < 0 || bytes.Length - offset < required)
            throw new ArgumentException("Insufficient space for DNS SRV resource.", nameof(bytes));
        var buf = bytes.Span;
        BinaryPrimitives.WriteUInt16BigEndian(buf[offset..(offset + 2)], (ushort)Priority);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf[offset..(offset + 2)], (ushort)Weight);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf[offset..(offset + 2)], (ushort)Port);
        offset += 2;
        // target string
        foreach (var segBuf in encoded)
        {
            var segLength = (byte)segBuf.Length;
            buf[offset] = segLength;
            offset++;
            segBuf.CopyTo(buf[offset..]);
            offset += segBuf.Length;
        }
        buf[offset++] = 0;
    }

    public void ReadBytes(ReadOnlyMemory<byte> bytes, ref int offset, int length)
    {
        var buf = bytes.Slice(offset, length).Span;
        offset += length;
        var priorityBuf = buf[..2];
        Priority = BinaryPrimitives.ReadUInt16BigEndian(priorityBuf);
        var weightBuf = buf[2..4];
        Weight = BinaryPrimitives.ReadUInt16BigEndian(weightBuf);
        var portBuf = buf[4..6];
        Port = BinaryPrimitives.ReadUInt16BigEndian(portBuf);
        // left target buf. 1 Byte for length, then the string. Until we got end of string.
        var segments = new List<string>();
        var i = 6;
        while (i < length)
        {
            var segLength = buf[i];
            if (segLength == 0) break;
            i++;
            if (i + segLength > length)
            {
                throw new ArgumentException("Invalid DNS SRV resource record: segment length exceeds buffer size");
            }

            segments.Add(Encoding.UTF8.GetString(buf.Slice(i, segLength)));
            i += segLength;
        }

        Target = string.Join('.', segments);
    }
}
