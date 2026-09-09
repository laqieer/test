using System.Security.Cryptography;
using System.Text;

namespace FEBuilderGBA.TestFixtures;

public static class SyntheticFe8URom
{
    public const string Format = "fe8u-synthetic-huffman-v1";
    public const uint TreeBase = 0x1000;
    public const uint RootNode = TreeBase + 8;
    public const uint RootReference = 0x1010;
    private const uint TextTable = 0x1800;
    private const uint EmptyText = 0x2000;
    private const uint SingleAText = 0x2004;

    public static byte[] Create()
    {
        byte[] data = new byte[16 * 1024 * 1024];
        Encoding.ASCII.GetBytes("BE8E01").CopyTo(data, 0xAC);
        var rom = new ROM();
        if (!rom.LoadFromBytes("zipdb-proof.gba", data, out _)
            || rom.RomInfo.VersionToFilename != "FE8U")
            throw new InvalidOperationException("Synthetic FE8U identity was not recognized.");

        // Test-only generation is serialized by SharedState in tests and runs
        // in its own process for proof. Preserve the caller's ROM identity.
        var previous = CoreState.ROM;
        try
        {
            CoreState.ROM = rom;
            var undo = new Undo().NewUndoData("Generate synthetic patch-import fixture");
            using (ROM.BeginUndoScope(undo))
            {
                rom.write_u32(TreeBase, 0x80000000);
                rom.write_u32(TreeBase + 4, 0x80000041);
                rom.write_u16(RootNode, 0);
                rom.write_u16(RootNode + 2, 1);
                rom.write_p32(rom.RomInfo.mask_pointer, TreeBase);
                rom.write_p32(rom.RomInfo.mask_point_base_pointer, RootReference);
                rom.write_p32(RootReference, RootNode);
                rom.write_p32(rom.RomInfo.text_pointer, TextTable);
                rom.write_p32(TextTable, EmptyText);
                rom.write_p32(TextTable + 4, SingleAText);
                rom.write_u8(SingleAText, 1); // LSB-first A followed by terminator.
            }
        }
        finally
        {
            CoreState.ROM = previous;
        }
        return rom.Data;
    }

    public sealed record Receipt(string Format, int Length, string Sha256);

    public static Receipt WriteNew(string destination)
    {
        string path = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetFileName(path), "zipdb-proof.gba", StringComparison.Ordinal))
            throw new ArgumentException("Only the synthetic proof filename is permitted.");
        var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
        if (!directory.Exists)
            throw new DirectoryNotFoundException("The owned output directory must already exist.");
        if (!new[] { "android-patch-import-", "desktop-proof-", "fixture-test-" }
            .Any(prefix => directory.Name.StartsWith(prefix, StringComparison.Ordinal)))
            throw new ArgumentException("The destination must be an owned proof/test directory.");
        for (DirectoryInfo? ancestor = directory; ancestor != null; ancestor = ancestor.Parent)
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reparse-point output ancestry is not permitted.");
        byte[] data = Create();
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(data);
        return new Receipt(Format, data.Length,
            Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
    }
}
