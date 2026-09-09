using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FEBuilderGBA.Core.Tests;

public class PatchDatabaseZipReaderCoreTests
{
    static MemoryStream Zip(params (string Name, string Text)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Name, CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(file.Text);
            }
        stream.Position = 0;
        return stream;
    }

    [Theory]
    [InlineData("FE6")]
    [InlineData("FE7J")]
    [InlineData("FE7U")]
    [InlineData("FE8J")]
    [InlineData("FE8U")]
    public void CanonicalVersion_SelectsOnlyItsTree(string version)
    {
        using var zip = Zip(
            ($"repository-master/{version}/patch/PATCH_test.txt", "NAME=Test\nTYPE=BIN"),
            ("repository-master/README.md", "Documentation"));
        var archive = PatchDatabaseZipReaderCore.Inspect(zip, version);
        var file = Assert.Single(archive.Files);
        Assert.Equal("patch/PATCH_test.txt", file.RelativePath);
    }

    [Theory]
    [InlineData("../FE8U/PATCH_x.txt")]
    [InlineData("/FE8U/PATCH_x.txt")]
    [InlineData("\\\\unused.invalid\\FE8U\\PATCH_x.txt")]
    [InlineData("C:/FE8U/PATCH_x.txt")]
    [InlineData("FE8U/a/../../outside")]
    [InlineData("FE8U/a/./file")]
    [InlineData("FE8U/CON.txt")]
    [InlineData("FE8U/CON .txt")]
    [InlineData("FE8U/COM1 .bin")]
    [InlineData("FE8U/NUL .bin")]
    [InlineData("FE8U/file:stream")]
    [InlineData("FE8U/space /file")]
    [InlineData("FE8U/.git/config")]
    [InlineData("FE8U/.febuilder-patch-import.json")]
    public void UnsafeNames_RejectWithoutOperandIo(string name)
    {
        using var zip = Zip((name, "data"));
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U"));
    }

    [Theory]
    [InlineData("FE8U/A.txt", "FE8U/a.txt")]
    [InlineData("FE8U/caf\u00e9.txt", "FE8U/cafe\u0301.txt")]
    [InlineData("FE8U/a", "FE8U/a/child.txt")]
    [InlineData("FE8U/x.txt", "FE8U/x.txt")]
    [InlineData("FE8U/Foo/a.txt", "FE8U/foo/b.txt")]
    [InlineData("FE8U/caf\u00e9/a.txt", "FE8U/cafe\u0301/b.txt")]
    [InlineData("FE8U/a/child.txt", "FE8U/a")]
    public void CollidingOrConflictingNames_Reject(string first, string second)
    {
        using var zip = Zip((first, "one"), (second, "two"));
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U"));
    }

    [Fact]
    public void TwoVersionRoots_AreAmbiguous()
    {
        using var zip = Zip(("FE8U/PATCH_one.txt", "one"), ("wrapper/FE8U/PATCH_two.txt", "two"));
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U"));
    }

    [Fact]
    public void UnknownVersion_Rejects()
    {
        using var zip = Zip(("NAZO/PATCH_one.txt", "one"));
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "NAZO"));
    }

    [Fact]
    public void EntryCountLimit_IsExact()
    {
        using var zip = Zip(("FE8U/PATCH_one.txt", "one"), ("FE8U/PATCH_two.txt", "two"));
        Assert.Equal(2, PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new PatchDatabaseZipReaderCore.Limits { MaxEntries = 2 }).Files.Count);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new PatchDatabaseZipReaderCore.Limits { MaxEntries = 1 }));
    }

    [Fact]
    public void GeneratedCanonicalSizedShapeFitsTheAcceptedProfile()
    {
        var entries = new List<(string Name, string Text)> { ("FE8U/", "") };
        for (int i = 0; i < 615; i++) entries.Add(($"FE8U/d{i:D4}/", ""));
        entries.Add(("FE8U/PATCH_generated.txt", "TYPE=BIN"));
        for (int i = 0; i < 34_669; i++) entries.Add(($"FE8U/d{i % 615:D4}/f{i:D5}.bin", ""));
        using var zip = Zip(entries.ToArray());
        var archive = PatchDatabaseZipReaderCore.Inspect(zip, "FE8U");
        Assert.Equal(34_670, archive.Files.Count);
        Assert.Equal(615, archive.Directories.Count);
        Assert.Equal(35_285, archive.MaterializedNodes);
    }

    [Theory]
    [InlineData(50_000, true)]
    [InlineData(50_001, false)]
    public void ProductionSelectedFileBoundaryIsExact(int count, bool accepted)
    {
        using var zip = Zip(Enumerable.Range(0, count).Select(i => ($"FE8U/f{i:D5}.bin", "")).ToArray());
        if (accepted) Assert.Equal(count, PatchDatabaseZipReaderCore.Inspect(zip, "FE8U").Files.Count);
        else Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U"));
    }

    [Fact]
    public void MaterializedNodesCountDistinctParentsAndEmptyDirectoriesButNotTheRoot()
    {
        using var zip = Zip(("wrapper/", ""), ("wrapper/FE8U/", ""), ("wrapper/FE8U/a/", ""),
            ("wrapper/FE8U/a/x.txt", ""), ("wrapper/FE8U/a/b/", ""), ("wrapper/FE8U/a/b/y.txt", ""),
            ("wrapper/FE8U/ab/z.txt", ""), ("wrapper/FE8U/empty/", ""), ("wrapper/README.txt", ""));
        var archive = PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new() { MaxMaterializedNodes = 7 });
        Assert.Equal(7, archive.MaterializedNodes);
        Assert.Equal(3, archive.Directories.Count);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new() { MaxMaterializedNodes = 6 }));
    }

    [Fact]
    public void ImplicitParentsAndNormalizedSeparatorsAreCountedPhysically()
    {
        using var zip = Zip(("FE8U\\a\\b\\x.txt", ""), ("FE8U/a/b/y.txt", ""), ("FE8U/a0/z.txt", ""));
        Assert.Equal(6, PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new() { MaxMaterializedNodes = 6 }).MaterializedNodes);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new() { MaxMaterializedNodes = 5 }));
    }

    [Fact]
    public void AmendedTestLimitsCannotExceedProductionCeilings()
    {
        using var zip = Zip(("FE8U/a.txt", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new() { MaxFiles = 50_001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new() { MaxMaterializedNodes = 100_001 }));
    }

    [Fact]
    public void EmptyDirectoryCannotClaimANonemptyCrc()
    {
        using var zip = Zip(("FE8U/", ""), ("FE8U/PATCH_x.txt", "TYPE=BIN"));
        byte[] bytes = zip.ToArray();
        int central = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(bytes.Length - 6));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(central + 16), 1);
        using var malformed = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(malformed, "FE8U"));
    }

    [Fact]
    public void ExpandedByteLimit_IsExact()
    {
        using var zip = Zip(("FE8U/file.bin", "1234"));
        Assert.Equal(4, PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new PatchDatabaseZipReaderCore.Limits { MaxExpandedBytes = 4 }).ExpandedBytes);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new PatchDatabaseZipReaderCore.Limits { MaxExpandedBytes = 3 }));
    }

    [Fact]
    public void ProductionLimits_CannotBeRaisedByAnOverride()
    {
        using var zip = Zip(("FE8U/file.bin", "1"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchDatabaseZipReaderCore.Inspect(zip, "FE8U",
            new PatchDatabaseZipReaderCore.Limits { MaxEntries = 100_001 }));
    }

    [Fact]
    public void SymlinkEntry_Rejects()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("FE8U/link");
            entry.ExternalAttributes = unchecked((int)(0xa1ffU << 16));
            using var writer = new StreamWriter(entry.Open());
            writer.Write("unused");
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(stream, "FE8U"));
    }

    [Fact]
    public void NonSeekableWriter_ProducesSupportedDataDescriptors()
    {
        using var bytes = new MemoryStream();
        using (var wrapper = new ForwardOnlyStream(bytes))
        using (var zip = new ZipArchive(wrapper, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("FE8U/PATCH_stream.txt", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("NAME=Stream\nTYPE=BIN\n");
        }
        Assert.NotEqual(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.GetBuffer().AsSpan(6)) & 8);
        bytes.Position = 0;
        Assert.Single(PatchDatabaseZipReaderCore.Inspect(bytes, "FE8U").Files);
    }

    [Fact]
    public void DescriptorMismatch_Rejects()
    {
        using var bytes = new MemoryStream();
        using (var wrapper = new ForwardOnlyStream(bytes))
        using (var zip = new ZipArchive(wrapper, ZipArchiveMode.Create, true))
        {
            using var output = zip.CreateEntry("FE8U/file", CompressionLevel.NoCompression).Open();
            output.Write(new byte[] { 1, 2, 3 });
        }
        var data = bytes.ToArray();
        int descriptor = Find(data, 0x08074b50);
        Assert.True(descriptor > 0);
        data[descriptor + 4] ^= 1;
        using var changed = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(changed, "FE8U"));
    }

    [Fact]
    public void LocalNameMismatch_Rejects()
    {
        using var zip = Zip(("FE8U/file", "content"));
        var data = zip.ToArray();
        data[30] = (byte)'X';
        using var changed = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(changed, "FE8U"));
    }

    [Fact]
    public void Crc32_KnownVector()
    {
        Assert.Equal(0xcbf43926U,
            PatchDatabaseZipReaderCore.Crc32(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public async Task CopyEntry_VerifiesActualContent()
    {
        using var zip = Zip(("FE8U/file", "payload"));
        var archive = PatchDatabaseZipReaderCore.Inspect(zip, "FE8U");
        using var output = new MemoryStream();
        await PatchDatabaseZipReaderCore.CopyEntryAsync(zip, archive, Assert.Single(archive.Files), output);
        Assert.Equal("payload", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task CopyEntry_RejectsBadActualCrcEvenWhenHeadersAgree()
    {
        using var zip = Zip(("FE8U/file", "payload"));
        var data = zip.ToArray();
        int offset = 30 + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26)) +
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
        data[offset] ^= 1;
        using var changed = new MemoryStream(data);
        var archive = PatchDatabaseZipReaderCore.Inspect(changed, "FE8U");
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PatchDatabaseZipReaderCore.CopyEntryAsync(changed, archive, archive.Files[0], output));
    }

    [Fact]
    public async Task CopyEntry_CancellationDoesNotWrite()
    {
        using var zip = Zip(("FE8U/file", "payload"));
        var archive = PatchDatabaseZipReaderCore.Inspect(zip, "FE8U");
        using var output = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PatchDatabaseZipReaderCore.CopyEntryAsync(zip, archive, archive.Files[0], output, cancelled.Token));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task StreamingDescriptors_SupportApplicableWidthsAndSignatures(bool zip64, bool signed, bool deflate)
    {
        using var stream = DescriptorZip(zip64, signed, deflate);
        var archive = PatchDatabaseZipReaderCore.Inspect(stream, "FE8U");
        using var output = new MemoryStream();
        await PatchDatabaseZipReaderCore.CopyEntryAsync(stream, archive, archive.Files[0], output);
        Assert.Equal("descriptor payload", Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingDescriptor_InconsistentWidthRejects(bool zip64)
    {
        using var stream = DescriptorZip(zip64, true, false, wrongWidth: true);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(stream, "FE8U"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamingDescriptor_AlreadyKnownLocalValuesAreAccepted(bool zip64)
    {
        using var stream = DescriptorZip(zip64, true, true, knownLocalValues: true);
        Assert.Single(PatchDatabaseZipReaderCore.Inspect(stream, "FE8U").Files);
    }

    [Fact]
    public void Zip64EndRecord_AgreesWithSmallActualArchive()
    {
        using var original = DescriptorZip(true, false, false);
        byte[] data = original.ToArray();
        int eocd = data.Length - 22;
        uint centralSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(eocd + 12));
        uint centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(eocd + 16));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(data, 0, eocd);
            writer.Write(0x06064b50U);
            writer.Write(44UL);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(1UL);
            writer.Write(1UL);
            writer.Write((ulong)centralSize);
            writer.Write((ulong)centralOffset);
            writer.Write(0x07064b50U);
            writer.Write(0U);
            writer.Write((ulong)eocd);
            writer.Write(1U);
            writer.Write(0x06054b50U);
            writer.Write(0U);
            writer.Write(ushort.MaxValue);
            writer.Write(ushort.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write((ushort)0);
        }
        stream.Position = 0;
        Assert.Single(PatchDatabaseZipReaderCore.Inspect(stream, "FE8U").Files);
    }

    [Fact]
    public void UnsupportedMethod_RejectsBeforePayloadIsRead()
    {
        using var original = DescriptorZip(false, true, false);
        byte[] data = original.ToArray();
        int central = Find(data, 0x02014b50);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 99);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(central + 10), 99);
        using var stream = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(stream, "FE8U"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0x40)]
    [InlineData(0x2000)]
    public void EncryptionFlags_Reject(int forbiddenFlag)
    {
        using var original = DescriptorZip(false, true, false);
        byte[] data = original.ToArray();
        int central = Find(data, 0x02014b50);
        ushort flags = (ushort)(8 | forbiddenFlag);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(central + 8), flags);
        using var stream = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(stream, "FE8U"));
    }

    [Fact]
    public void LocalExtentIntoCentralDirectory_Rejects()
    {
        using var original = DescriptorZip(false, true, false);
        byte[] data = original.ToArray();
        int central = Find(data, 0x02014b50);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(central + 20), 100_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(central + 24), 100_000);
        using var stream = new MemoryStream(data);
        Assert.Throws<InvalidDataException>(() => PatchDatabaseZipReaderCore.Inspect(stream, "FE8U"));
    }

    static MemoryStream DescriptorZip(bool zip64, bool signed, bool deflate,
        bool wrongWidth = false, bool knownLocalValues = false)
    {
        byte[] name = Encoding.UTF8.GetBytes("FE8U/PATCH_stream.txt");
        byte[] payload = Encoding.UTF8.GetBytes("descriptor payload");
        byte[] compressed = payload;
        if (deflate)
        {
            using var output = new MemoryStream();
            using (var compressor = new DeflateStream(output, CompressionLevel.Optimal, true))
                compressor.Write(payload);
            compressed = output.ToArray();
        }
        uint crc = PatchDatabaseZipReaderCore.Crc32(payload);
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(0x04034b50U);
        writer.Write((ushort)(zip64 ? 45 : 20));
        writer.Write((ushort)8);
        writer.Write((ushort)(deflate ? 8 : 0));
        writer.Write(0U);
        writer.Write(knownLocalValues ? crc : 0U);
        writer.Write(zip64 ? uint.MaxValue : knownLocalValues ? (uint)compressed.Length : 0U);
        writer.Write(zip64 ? uint.MaxValue : knownLocalValues ? (uint)payload.Length : 0U);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)(zip64 ? 20 : 0));
        writer.Write(name);
        if (zip64)
        {
            writer.Write((ushort)1);
            writer.Write((ushort)16);
            writer.Write(knownLocalValues ? (ulong)payload.Length : 0UL);
            writer.Write(knownLocalValues ? (ulong)compressed.Length : 0UL);
        }
        writer.Write(compressed);
        if (signed) writer.Write(0x08074b50U);
        writer.Write(crc);
        if (zip64 != wrongWidth)
        {
            writer.Write((ulong)compressed.Length);
            writer.Write((ulong)payload.Length);
        }
        else
        {
            writer.Write((uint)compressed.Length);
            writer.Write((uint)payload.Length);
        }
        uint central = (uint)stream.Position;
        writer.Write(0x02014b50U);
        writer.Write((ushort)45);
        writer.Write((ushort)(zip64 ? 45 : 20));
        writer.Write((ushort)8);
        writer.Write((ushort)(deflate ? 8 : 0));
        writer.Write(0U);
        writer.Write(crc);
        writer.Write(zip64 ? uint.MaxValue : (uint)compressed.Length);
        writer.Write(zip64 ? uint.MaxValue : (uint)payload.Length);
        writer.Write((ushort)name.Length);
        writer.Write((ushort)(zip64 ? 20 : 0));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0U);
        writer.Write(0U);
        writer.Write(name);
        if (zip64)
        {
            writer.Write((ushort)1);
            writer.Write((ushort)16);
            writer.Write((ulong)payload.Length);
            writer.Write((ulong)compressed.Length);
        }
        uint centralLength = (uint)stream.Position - central;
        writer.Write(0x06054b50U);
        writer.Write(0U);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralLength);
        writer.Write(central);
        writer.Write((ushort)0);
        stream.Position = 0;
        return stream;
    }

    static int Find(byte[] bytes, uint signature)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) == signature)
                return i;
        return -1;
    }

    sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
