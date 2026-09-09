using System.Reflection;
using System.Security.Cryptography;
using FEBuilderGBA.Avalonia.Services;
using FEBuilderGBA.TestFixtures;

namespace FEBuilderGBA.Avalonia.Tests;

[Collection("SharedState")]
public sealed class SyntheticPatchImportFixtureTests
{
    [Fact]
    public void GeneratedTree_HasPointerAwareIndirectionAndFiniteTerminatingLeaves()
    {
        var previousRom = CoreState.ROM;
        var previousUndo = CoreState.Undo;
        byte[] data = SyntheticFe8URom.Create();
        Assert.Same(previousRom, CoreState.ROM);
        Assert.Same(previousUndo, CoreState.Undo);
        var rom = new ROM();
        Assert.True(rom.LoadFromBytes("zipdb-proof.gba", data, out _));
        Assert.Equal("FE8U", rom.RomInfo.VersionToFilename);
        Assert.Equal(16 * 1024 * 1024, data.Length);
        Assert.False(rom.Modified);
        Assert.Equal(0u, rom.u16(0x200));
        Assert.Equal(0u, rom.u8(0x300));
        uint tree = rom.p32(rom.RomInfo.mask_pointer);
        uint reference = rom.p32(rom.RomInfo.mask_point_base_pointer);
        uint root = rom.p32p(rom.RomInfo.mask_point_base_pointer);
        Assert.Equal(SyntheticFe8URom.TreeBase, tree);
        Assert.Equal(SyntheticFe8URom.RootReference, reference);
        Assert.Equal(SyntheticFe8URom.RootNode, root);
        Assert.Equal(U.toPointer(tree), rom.u32(rom.RomInfo.mask_pointer));
        Assert.Equal(U.toPointer(reference), rom.u32(rom.RomInfo.mask_point_base_pointer));
        Assert.Equal(U.toPointer(root), rom.u32(reference));

        var visited = new HashSet<uint>();
        var leaves = new HashSet<uint>();
        var pending = new Stack<(uint address, int depth)>();
        pending.Push((root, 0));
        while (pending.TryPop(out var node))
        {
            Assert.InRange(node.address, tree, tree + 8);
            Assert.True((ulong)node.address + 4 <= (ulong)data.Length);
            Assert.True(visited.Add(node.address), "The synthetic tree must not contain cycles or duplicate nodes.");
            Assert.InRange(node.depth, 0, 1);
            uint value = rom.u32(node.address);
            if ((value & 0x80000000) != 0)
                leaves.Add(value & 0xFFFF);
            else
            {
                pending.Push((tree + rom.u16(node.address) * 4, node.depth + 1));
                pending.Push((tree + rom.u16(node.address + 2) * 4, node.depth + 1));
            }
        }
        Assert.Equal(3, visited.Count);
        Assert.Equal(new uint[] { 0, 'A' }, leaves.Order().ToArray());
        Assert.Equal(data, SyntheticFe8URom.Create());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedBytes_CompleteFullPostLoadAndTextInitializationWithoutMutation(bool nonSeekable)
    {
        using var state = new InitializationState();
        byte[] data = SyntheticFe8URom.Create();
        byte[] originalHash = SHA256.HashData(data);
        using Stream stream = nonSeekable ? new NonSeekableStream(data) : new MemoryStream(data, false);
        var rom = new ROM();
        var loaded = await rom.LoadFromStreamAsync(stream, "zipdb-proof.gba");
        Assert.True(loaded.ok);
        Assert.Equal("BE8E01", loaded.version);

        // This is the actual post-load path used after the native SAF stream.
        // Run this test in both Debug and Release; never suppress Debug.Assert.
        RomFileService.InitializeLoadedRom(rom);
        Assert.Same(rom, CoreState.ROM);
        Assert.NotNull(CoreState.FETextEncoder);
        Assert.NotNull(CoreState.SystemTextEncoder);
        Assert.NotNull(CoreState.TextEscape);
        Assert.NotNull(CoreState.UseTextIDCache);
        Assert.NotNull(CoreState.FlagCache);
        Assert.NotNull(CoreState.Undo);
        Assert.NotNull(CoreState.EventScript);
        Assert.NotNull(CoreState.ProcsScript);
        Assert.NotNull(CoreState.AIScript);
        Assert.Equal("", CoreState.FETextEncoder.Encode("A", out byte[] encoded));
        Assert.Equal(new byte[] { 1 }, encoded);
        var decoder = new FETextDecode(rom, CoreState.SystemTextEncoder);
        Assert.Equal("", decoder.Decode(0));
        Assert.Equal("A", decoder.Decode(1));
        Assert.Equal(originalHash, SHA256.HashData(rom.Data));
        Assert.Equal(originalHash, SHA256.HashData(data));
        Assert.False(rom.Modified);
    }

    [Fact]
    public void FixtureWriter_CreatesOnlyANewNamedFixtureAndPreservesExistingBytes()
    {
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "TestResults",
            "fixture-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "zipdb-proof.gba");
        try
        {
            var result = SyntheticFe8URom.WriteNew(destination);
            byte[] generated = File.ReadAllBytes(destination);
            Assert.Equal(SyntheticFe8URom.Format, result.Format);
            Assert.Equal(generated.Length, result.Length);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(generated)).ToLowerInvariant(), result.Sha256);
            Assert.Equal(SyntheticFe8URom.Create(), generated);
            Assert.Throws<IOException>(() => SyntheticFe8URom.WriteNew(destination));
            Assert.Equal(generated, File.ReadAllBytes(destination));
            Assert.Throws<ArgumentException>(() =>
                SyntheticFe8URom.WriteNew(Path.Combine(directory, "unrelated.gba")));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
            Directory.Delete(directory);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override bool CanSeek => false;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    private sealed class InitializationState : IDisposable
    {
        private readonly Dictionary<PropertyInfo, object?> core = typeof(CoreState)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p, p => p.GetValue(null));
        private readonly Dictionary<PropertyInfo, object?> detection = typeof(PatchDetectionService)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToDictionary(p => p, p => p.GetValue(PatchDetectionService.Instance));

        public InitializationState()
        {
            CoreState.ROM = null!;
            CoreState.CommentCache = null!;
            CoreState.LintCache = null!;
            CoreState.WorkSupportCache = null!;
            CoreState.ResourceCache = null!;
            CoreState.SystemTextEncoder = null!;
            CoreState.FETextEncoder = null!;
            CoreState.TextEscape = null!;
            CoreState.FlagCache = null!;
            CoreState.ExportFunction = null!;
            CoreState.EventScript = null!;
            CoreState.ProcsScript = null!;
            CoreState.AIScript = null!;
            CoreState.Config = new Config();
            CoreState.Services = new HeadlessAppServices();
            CoreState.BaseDirectory = AppContext.BaseDirectory;
            CoreState.TextEncoding = TextEncodingEnum.Auto;
        }

        public void Dispose()
        {
            foreach (var pair in core) pair.Key.SetValue(null, pair.Value);
            foreach (var pair in detection) pair.Key.SetValue(PatchDetectionService.Instance, pair.Value);
            PatchDetection.ClearAllCaches();
            MagicSplitUtil.ClearCache();
        }
    }
}
