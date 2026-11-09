using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using PCL.Core.App;
using PCL.Core.App.Localization;

namespace PCL;


internal sealed class XvcContainer : IDisposable
{
    private const ulong HeaderInclSignatureSize = 12288;
    private const int PageSize = 4096;

    private readonly FileStream _stream;
    private readonly BinaryReader _reader;

    private ulong _hashTreePageCount;
    private ulong _hashTreePageOffset;
    private ulong _mutableDataOffset;
    private bool _dataIntegrity;
    private bool _resiliency;
    private ulong _hashTreeLevels;
    private ulong _xvdUserDataOffset;
    private bool _hasSegmentMetadata;

    private SegmentMetadataHeader _segmentMetadataHeader;
    private Segment[] _segments = [];
    private string[] _segmentPaths = [];

    private XvcInfo _xvcInfo;
    private XvcRegionHeader[] _xvcRegions = [];
    private XvcUpdateSegment[] _xvcUpdateSegments = [];

    private UserDataHeader _userDataHeader;
    private readonly System.Collections.Generic.Dictionary<string, byte[]> _userDataContents = [];

    private int _hashEntryLength;

    private MsiXvdHeader Header { get; set; }

    public bool IsEncrypted { get; private set; }

    /// <summary>
    ///     包内声明的内容密钥 GUID（小写十六进制）。
    ///     <para>
    ///         来自 <c>XvcInfo.EncryptionKeyIds</c> 的前 <c>KeyCount</c> 项。为空表示这个包没有需要
    ///         密钥的区域，任何密钥都能解。非空时，使用的密钥 GUID 必须命中其中之一。
    ///     </para>
    /// </summary>
    public IReadOnlyList<string> KeyIds { get; private set; } = [];

    public XvcContainer(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("找不到基岩版安装包", filePath);

        _stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _reader = new BinaryReader(_stream);
    }

    public void Parse()
    {
        _stream.Position = 0;
        ParseFileHeader();
        _resiliency = Header.Volumes.HasFlag(XvdVolumeAttributes.ResiliencyEnabled);
        _dataIntegrity = !Header.Volumes.HasFlag(XvdVolumeAttributes.DataIntegrityDisabled);
        _hashTreePageCount = CalculateNumberHashPages(out _hashTreeLevels, Header.NumberOfHashedPages, _resiliency);
        _mutableDataOffset = PageToOffset(Header.EmbeddedXvdPageCount) + HeaderInclSignatureSize;
        _hashTreePageOffset = Header.MutableDataLength + _mutableDataOffset;
        _xvdUserDataOffset = (_dataIntegrity ? PageToOffset(_hashTreePageCount) : 0) + _hashTreePageOffset;

        ParseUserData();
        if (_userDataContents.ContainsKey("SegmentMetadata.bin"))
            ParseSegment();
        ParseArea();
    }

    private void ParseFileHeader()
    {
        var size = Marshal.SizeOf<MsiXvdHeader>();
        Header = ReadStruct<MsiXvdHeader>(_reader.ReadBytes(size));
        IsEncrypted = !Header.Volumes.HasFlag(XvdVolumeAttributes.EncryptionDisabled);
        _hashEntryLength = IsEncrypted ? 20 : 24;
    }

    private void ParseUserData()
    {
        _stream.Position = (long)_xvdUserDataOffset;
        var data = new byte[Header.UserDataLength];
        ReadFully(_stream, data);
        using var reader = new BinaryReader(new MemoryStream(data));

        _userDataHeader = ReadStruct<UserDataHeader>(reader.ReadBytes(Marshal.SizeOf<UserDataHeader>()));
        if (_userDataHeader.Type != XvdUserDataType.PackageFiles) return;

        reader.BaseStream.Position = _userDataHeader.Length;
        var filesHeader = ReadStruct<UserDataPackageFilesHeader>(
            reader.ReadBytes(Marshal.SizeOf<UserDataPackageFilesHeader>()));

        var fileCount = (int)filesHeader.FileCount;
        var entrySize = Marshal.SizeOf<UserDataPackageFileEntry>();
        var entries = ReadStructs<UserDataPackageFileEntry>(
            reader.ReadBytes(entrySize * fileCount), fileCount);

        foreach (var entry in entries)
        {
            reader.BaseStream.Position = _userDataHeader.Length + entry.Offset;
            var content = reader.ReadBytes((int)entry.Size);
            _userDataContents[entry.FilePath] = content;
        }
    }

    private void ParseSegment()
    {
        using var reader = new BinaryReader(new MemoryStream(_userDataContents["SegmentMetadata.bin"]));
        _segmentMetadataHeader = ReadStruct<SegmentMetadataHeader>(
            reader.ReadBytes(Marshal.SizeOf<SegmentMetadataHeader>()));
        _hasSegmentMetadata = true;

        var count = (int)_segmentMetadataHeader.SegmentCount;
        _segments = ReadStructs<Segment>(reader.ReadBytes(Marshal.SizeOf<Segment>() * count), count);
        _segmentPaths = new string[count];

        var pathsOffset = _segmentMetadataHeader.HeaderLength + _segmentMetadataHeader.SegmentCount * 16u;
        for (var i = 0; i < _segments.Length; i++)
        {
            reader.BaseStream.Position = pathsOffset + _segments[i].PathOffset;
            var bytes = reader.ReadBytes(_segments[i].PathLength * 2);
            _segmentPaths[i] = new string(MemoryMarshal.Cast<byte, char>(bytes));
        }
    }

    private void ParseArea()
    {
        var offset = PageToOffset(Header.UserDataPageCount) + _xvdUserDataOffset;
        _stream.Position = (long)offset;

        var data = new byte[Header.XvcDataLength];
        ReadFully(_stream, data);
        using var reader = new BinaryReader(new MemoryStream(data));

        var infoSize = Marshal.SizeOf<XvcInfo>();
        var infoBytes = reader.ReadBytes(infoSize);
        _xvcInfo = ReadStruct<XvcInfo>(infoBytes);
        if (_xvcInfo.Version < 1) return;

        // EncryptionKeyIds 是「结构体数组，每个结构体里又嵌了 byte[16]」，这种双层 ByValArray
        // 交给 PtrToStructure 不保证能填好，所以按布局偏移直接切字节更稳妥。
        KeyIds = ReadKeyIds(infoBytes);

        var regionSize = Marshal.SizeOf<XvcRegionHeader>();
        _xvcRegions = ReadStructs<XvcRegionHeader>(
            reader.ReadBytes(regionSize * (int)_xvcInfo.RegionCount), (int)_xvcInfo.RegionCount);

        var updateSize = Marshal.SizeOf<XvcUpdateSegment>();
        _xvcUpdateSegments = ReadStructs<XvcUpdateSegment>(
            reader.ReadBytes(updateSize * (int)_xvcInfo.UpdateSegmentCount), (int)_xvcInfo.UpdateSegmentCount);
    }

    /// <summary>
    ///     从 <see cref="XvcInfo" /> 的原始字节里取出密钥 GUID 列表。
    ///     <para>
    ///         只取前 <c>KeyCount</c> 项，并跳过全零槽位（包里没用到的位置都是零）。
    ///         偏移量用 <see cref="Marshal.OffsetOf{T}" /> 取，不硬编码，结构改动后不会悄悄错位。
    ///     </para>
    /// </summary>
    private static IReadOnlyList<string> ReadKeyIds(byte[] infoBytes)
    {
        var ids = new List<string>();
        try
        {
            var idsOffset = (int)Marshal.OffsetOf<XvcInfo>(nameof(XvcInfo.EncryptionKeyIds));
            var countOffset = (int)Marshal.OffsetOf<XvcInfo>(nameof(XvcInfo.KeyCount));
            var slotSize = Marshal.SizeOf<XvcEncryptionKeyId>();

            var keyCount = BitConverter.ToUInt16(infoBytes, countOffset);
            var limit = Math.Min((int)keyCount, 192);
            for (var i = 0; i < limit; i++)
            {
                var start = idsOffset + i * slotSize;
                if (start + 16 > infoBytes.Length) break;

                var span = infoBytes.AsSpan(start, 16);
                var allZero = true;
                foreach (var b in span)
                {
                    if (b == 0) continue;
                    allZero = false;
                    break;
                }

                if (allZero) continue;
                ids.Add(Convert.ToHexString(span).ToLowerInvariant());
            }
        }
        catch (Exception)
        {
            // 解析失败就当作「包没声明密钥」，让后续按明文流程走，不要因此拦住安装
        }

        return ids;
    }

    public void Extract(string outputDirectory, XvcContentKey key,
        Action<string, int, int>? onProgress, CancellationToken token)
    {
        if (!_hasSegmentMetadata || _segments.Length == 0)
            throw new InvalidOperationException("安装包内没有文件段信息，可能不是有效的 GDK 包");
        if (_xvcUpdateSegments.Length == 0)
            throw new InvalidOperationException("安装包内没有可解包的区域信息");

        Directory.CreateDirectory(outputDirectory);

        using var decoder = new XvcDecoder(key);
        var firstSegmentOffset = PageToOffset(_xvcUpdateSegments[0].PageNum);

        foreach (var region in _xvcRegions)
        {
            if (token.IsCancellationRequested) break;

            // 文件段可能被拆分到多个区域（例如主程序、DLL、资源各占一个区域），必须逐个解包；
            // 元数据区域（XVC-HD / EXVD / MDU / MD / FS-MD）的 FirstSegmentIndex 同样是 0，
            // 但起始偏移与 0 号段的偏移不同，据此排除。
            if (region.FirstSegmentIndex >= _segments.Length || region.Length == 0) continue;
            if (region.FirstSegmentIndex == 0 && firstSegmentOffset != region.Offset) continue;

            ExtractRegion(outputDirectory, decoder, (uint)region.Id, region.Offset, region.Length,
                region.FirstSegmentIndex, IsEncrypted && region.KeyId != ushort.MaxValue, onProgress, token);
        }
    }

    private void ExtractRegion(string outputDirectory, XvcDecoder decoder, uint regionId,
        ulong regionStartOffset, ulong regionLength, uint startSegmentIndex, bool shouldDecrypt,
        Action<string, int, int>? onProgress, CancellationToken token)
    {
        Span<byte> tweak = stackalloc byte[16];
        if (shouldDecrypt)
        {
            MemoryMarshal.Cast<byte, uint>(tweak)[1] = regionId;
            Header.VdUid.AsSpan(0, 8).CopyTo(tweak[8..]);
        }

        var buffer = new byte[1024 * 1024];
        var hashBuffer = new byte[1024 * 1024];
        var decryptBuffer = new byte[4096];
        var plainBuffer = new byte[4096];

        var needDataRead = true;
        var needHashRead = _dataIntegrity;
        var bufferOffset = 0;
        long regionPosition = (long)regionStartOffset;

        var hashFileOffset = (long)CalculateHashEntryBlockOffset(
            GetPageOffset(regionStartOffset - _xvdUserDataOffset), out var hashEntryId);
        var hashEntryOffset = (int)(hashEntryId * 24);
        var segmentIndex = startSegmentIndex;
        var pageCounter = 0;
        var totalPages = (long)GetPageOffset(regionLength);

        while (_segments.Length > segmentIndex && totalPages > pageCounter && !token.IsCancellationRequested)
        {
            var fileSize = _segments[segmentIndex].FileSize;
            var relativePath = _segmentPaths[segmentIndex];
            var path = Path.Join(outputDirectory, relativePath);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);

            using (var file = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var remaining = fileSize;
                do
                {
                    var writeLength = (int)Math.Min(remaining, PageSize);

                    if (needHashRead)
                    {
                        _stream.Position = hashFileOffset;
                        ReadFully(_stream, hashBuffer);
                        needHashRead = false;
                    }

                    if (needDataRead)
                    {
                        _stream.Position = regionPosition;
                        ReadFully(_stream, buffer);
                        needDataRead = false;
                    }

                    var page = buffer.AsSpan(bufferOffset, PageSize);
                    if (_dataIntegrity)
                    {
                        var entry = hashBuffer.AsSpan(hashEntryOffset, 24);
                        if (shouldDecrypt)
                            MemoryMarshal.Cast<byte, uint>(tweak)[0] =
                                MemoryMarshal.Cast<byte, uint>(entry[_hashEntryLength..])[0];

                        hashEntryOffset += 24;
                        hashEntryId++;
                        if (hashEntryId == 170)
                        {
                            hashEntryId = 0;
                            hashEntryOffset += 16;
                        }

                        if (hashEntryOffset == hashBuffer.Length)
                        {
                            hashFileOffset += hashEntryOffset;
                            hashEntryOffset = 0;
                            hashEntryId = 0;
                            needHashRead = true;
                        }
                    }

                    if (shouldDecrypt)
                    {
                        page.CopyTo(decryptBuffer);
                        decoder.Decrypt(decryptBuffer, plainBuffer, tweak);
                        file.Write(plainBuffer, 0, writeLength);
                    }
                    else
                    {
                        file.Write(page[..writeLength]);
                    }

                    remaining -= (uint)writeLength;
                    bufferOffset += PageSize;
                    if (bufferOffset == buffer.Length)
                    {
                        regionPosition += bufferOffset;
                        bufferOffset = 0;
                        needDataRead = true;
                    }

                    pageCounter++;
                } while (remaining != 0);
            }

            onProgress?.Invoke(relativePath, (int)segmentIndex + 1, _segments.Length);
            segmentIndex++;
        }
    }

    private ulong CalculateHashEntryBlockOffset(ulong blockNo, out ulong hashEntryId)
    {
        var index = ComputeHashBlockIndex(Header.Kind, _hashTreeLevels, Header.NumberOfHashedPages,
            blockNo, 0, out hashEntryId, _resiliency);
        return _hashTreePageOffset + PageToOffset(index);
    }

    private static ulong CalculateNumberHashPages(out ulong hashTreeLevels, ulong hashedPagesCount, bool resilient)
    {
        var count = (hashedPagesCount + 170 - 1) / 170;
        hashTreeLevels = 1;
        if (count > 1)
        {
            ulong previous = 2;
            while (previous > 1)
            {
                ulong next = 0;
                switch (hashTreeLevels)
                {
                    case 0: next = (hashedPagesCount + 170 - 1) / 170; break;
                    case 1: next = (hashedPagesCount + 28900 - 1) / 28900; break;
                    case 2: next = (hashedPagesCount + 4913000 - 1) / 4913000; break;
                    case 3: next = (hashedPagesCount + 835210000 - 1) / 835210000; break;
                }

                previous = next;
                hashTreeLevels++;
                count += next;
            }
        }

        if (resilient) count *= 2;
        return count;
    }

    private static ulong ComputeHashBlockIndex(XvdKind imageType, ulong hashTreeDepth, ulong totalHashedPages,
        ulong dataBlockIndex, uint currentHashLevel, out ulong entryIndexInHashBlock, bool isResilient = false)
    {
        entryIndexInHashBlock = 0;
        if (imageType > XvdKind.Dynamic || currentHashLevel > 3)
            return 65535;

        entryIndexInHashBlock = currentHashLevel == 0
            ? dataBlockIndex % 170
            : dataBlockIndex / LevelMultiplier(currentHashLevel) % 170;

        if (currentHashLevel == 3) return 0;

        var result = dataBlockIndex / LevelMultiplier(currentHashLevel + 1);
        hashTreeDepth -= currentHashLevel + 1;

        if (currentHashLevel == 0 && hashTreeDepth != 0)
        {
            result += (totalHashedPages + LevelMultiplier(2) - 1) / LevelMultiplier(2);
            hashTreeDepth--;
        }

        if (currentHashLevel is 0 or 1 && hashTreeDepth != 0)
        {
            result += (totalHashedPages + LevelMultiplier(3) - 1) / LevelMultiplier(3);
            hashTreeDepth--;
        }

        if (hashTreeDepth != 0)
            result += (totalHashedPages + LevelMultiplier(4) - 1) / LevelMultiplier(4);

        if (isResilient) result *= 2;
        return result;

        static ulong LevelMultiplier(ulong level) => (ulong)Math.Pow(170.0, level);
    }

    private static ulong PageToOffset(ulong pages) => pages * PageSize;
    private static ulong GetPageOffset(ulong bytes) => bytes / PageSize;

    private static void ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0) break;
            total += read;
        }
    }

    private static T ReadStruct<T>(ReadOnlySpan<byte> bytes) where T : struct
    {
        var array = bytes.ToArray();
        var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static T[] ReadStructs<T>(ReadOnlySpan<byte> bytes, int count) where T : struct
    {
        var array = bytes.ToArray();
        var handle = GCHandle.Alloc(array, GCHandleType.Pinned);
        try
        {
            var address = handle.AddrOfPinnedObject();
            var size = Marshal.SizeOf<T>();
            var result = new T[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStructure<T>(address + size * i);
            return result;
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }
}
