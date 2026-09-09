using System.IO.Compression;
using System.Text;
using System.Reflection;
using Avalonia.Platform.Storage;
using FEBuilderGBA.Avalonia.Services;
using FEBuilderGBA.Avalonia.Dialogs;
using FEBuilderGBA.Avalonia.ViewModels;

namespace FEBuilderGBA.Avalonia.Tests;

[Collection("SharedState")]
public class PatchDatabaseImportServiceTests
{
    [Fact]
    public void ZipPickerUsesStreamCompatibleZipTypesAndSingleSelection()
    {
        var options = FileDialogHelper.CreatePatchDatabaseZipOpenOptions();
        Assert.False(options.AllowMultiple);
        Assert.Contains(options.FileTypeFilter!, item =>
            item.Patterns!.Contains("*.zip") && item.MimeTypes!.Contains("application/zip"));
    }

    [Fact]
    public async Task ProviderWithoutLocalPathImportsWithoutChangingTheRom()
    {
        using var fixture = new Fixture();
        using var selected = new PickedFile(Zip("FE8U"));
        byte[] original = (byte[])fixture.Rom.Data.Clone();
        bool modified = fixture.Rom.Modified;
        int undoCount = CoreState.Undo.UndoBuffer.Count;
        var identity = PatchDatabaseImportService.CaptureLoadedRom();
        var result = await fixture.Import(selected, identity!, _ => Task.FromResult(true));
        Assert.True(result.Imported, result.Message);
        Assert.Equal(original, fixture.Rom.Data);
        Assert.Equal(modified, fixture.Rom.Modified);
        Assert.Equal(undoCount, CoreState.Undo.UndoBuffer.Count);
        Assert.True(selected.ReadStream!.Disposed);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "config", "patch2", "FE8U", "PATCH_test.txt")));
    }

    [Fact]
    public async Task DecliningConsentDoesNotReplaceTheDatabase()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var selected = new PickedFile(Zip("FE8U"));
        var result = await fixture.Import(selected, PatchDatabaseImportService.CaptureLoadedRom()!,
            prepared =>
            {
                Assert.True(prepared.ReplacesExisting);
                Assert.Contains("FE8U", PatchDatabaseImportService.ConfirmationMessage(prepared));
                Assert.Contains("No patches will be applied", PatchDatabaseImportService.ConfirmationMessage(prepared));
                return Task.FromResult(false);
            });
        Assert.True(result.Cancelled);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(Directory.GetDirectories(Path.Combine(fixture.Root, ".patch2-import")));
    }

    [Fact]
    public async Task ReloadingTheSameRomInstanceDuringConsentAbortsPromotion()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var selected = new PickedFile(Zip("FE8U"));
        var result = await fixture.Import(selected, PatchDatabaseImportService.CaptureLoadedRom()!,
            _ =>
            {
                fixture.Rom.LoadLow("different.gba", new byte[0x1000000], "BE8E01");
                return Task.FromResult(true);
            });
        Assert.False(result.Imported);
        Assert.Contains("loaded ROM changed", result.Message);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
    }

    [Fact]
    public async Task InPlaceRomChangeDuringConsentAlsoAbortsPromotion()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var selected = new PickedFile(Zip("FE8U"));
        var result = await fixture.Import(selected, PatchDatabaseImportService.CaptureLoadedRom()!,
            _ =>
            {
                fixture.Rom.Data[0x100] = 42;
                return Task.FromResult(true);
            });
        Assert.False(result.Imported);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Equal(42, fixture.Rom.Data[0x100]);
    }

    [Fact]
    public async Task CanonicalLoadedVersionIsNotHardcodedToFe8u()
    {
        using var fixture = new Fixture("AE7E01");
        Assert.Equal("FE7U", fixture.Rom.RomInfo.VersionToFilename);
        using var selected = new PickedFile(Zip("FE7U"));
        var result = await fixture.Import(selected, PatchDatabaseImportService.CaptureLoadedRom()!,
            _ => Task.FromResult(true));
        Assert.True(result.Imported, result.Message);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "config", "patch2", "FE7U", "PATCH_test.txt")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "config", "patch2", "FE8U")));
    }

    [Fact]
    public async Task RomChangedBeforeOpeningSelectedStreamDoesNotReadIt()
    {
        using var fixture = new Fixture();
        var identity = PatchDatabaseImportService.CaptureLoadedRom();
        using var selected = new PickedFile(Zip("FE8U"));
        CoreState.ROM = null!;
        var result = await fixture.Import(selected, identity!, _ => Task.FromResult(true));
        Assert.False(result.Imported);
        Assert.Null(selected.ReadStream);
    }

    [Fact]
    public async Task ProviderCloseFailureReleasesThePreparedTransaction()
    {
        using var fixture = new Fixture();
        using var selected = new PickedFile(Zip("FE8U"), throwOnClose: true);
        var result = await fixture.Import(selected, PatchDatabaseImportService.CaptureLoadedRom()!,
            _ => Task.FromResult(true));
        Assert.False(result.Imported);
        using var retry = new PickedFile(Zip("FE8U"));
        var retried = await fixture.Import(retry, PatchDatabaseImportService.CaptureLoadedRom()!,
            _ => Task.FromResult(true));
        Assert.True(retried.Imported, retried.Message);
    }

    [Fact]
    public void CanonicalBaseDirectoryIsPreferredByActualPatchDiscovery()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Root, "config", "patch2", "FE8U");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "PATCH_owned.txt"), "NAME=Owned library\nTYPE=EA");
        Assert.Equal(path, PatchManagerViewModel.ResolvePatchDirectory("FE8U"));
        var vm = new PatchManagerViewModel();
        vm.LoadPatchList();
        Assert.Single(vm.FilteredPatches);
        Assert.Equal("Owned library", vm.FilteredPatches[0].Name);
        Assert.False(vm.FilteredPatches[0].IsInstallTypeSupported);
    }

    [Fact]
    public void ImportIsUnavailableWithoutALoadedRom()
    {
        using var fixture = new Fixture();
        CoreState.ROM = null!;
        Assert.Null(PatchDatabaseImportService.CaptureLoadedRom());
        Assert.False(new PatchManagerViewModel().CanImportPatchDatabase);
        Assert.Contains("Load a ROM", PatchDatabaseImportService.AvailabilityMessage);
    }

    static byte[] Zip(string version)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(version + "/PATCH_test.txt").Open(), new UTF8Encoding(false));
            writer.Write("NAME=Owned offline library\nTYPE=EA\nPATCHED_IF:0x100=AA");
        }
        return stream.ToArray();
    }

    sealed class PickedFile : IDisposable
    {
        readonly PickedFileProxy proxy;
        public IStorageFile File { get; }
        public NonSeekableStream? ReadStream => proxy.ReadStream;
        public PickedFile(byte[] bytes, bool throwOnClose = false)
        {
            File = DispatchProxy.Create<IStorageFile, PickedFileProxy>();
            proxy = (PickedFileProxy)(object)File;
            proxy.Bytes = bytes;
            proxy.ThrowOnClose = throwOnClose;
        }
        public void Dispose() => File.Dispose();
    }

    public class PickedFileProxy : DispatchProxy
    {
        internal byte[] Bytes { get; set; } = Array.Empty<byte>();
        internal bool ThrowOnClose { get; set; }
        internal NonSeekableStream? ReadStream { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method?.Name == nameof(IStorageFile.OpenReadAsync))
            {
                ReadStream = new NonSeekableStream(Bytes, ThrowOnClose);
                return Task.FromResult<Stream>(ReadStream);
            }
            if (method?.Name == nameof(IDisposable.Dispose)) return null;
            throw new InvalidOperationException("The importer must not use this provider operation: " + method?.Name);
        }
    }

    internal sealed class NonSeekableStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        readonly bool throwOnClose;
        public NonSeekableStream(byte[] bytes, bool throwOnClose = false) : base(bytes, false)
            => this.throwOnClose = throwOnClose;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
            if (throwOnClose) throw new IOException("Injected provider close failure.");
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly ROM savedRom = CoreState.ROM;
        readonly Undo savedUndo = CoreState.Undo;
        readonly string savedBase = CoreState.BaseDirectory;
        PatchDatabaseImportCore.PreparedImport? lastPrepared;
        public ROM Rom { get; } = new ROM();
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "TestResults", "zip-service-" + Guid.NewGuid().ToString("N"));
        public string OldFile => Path.Combine(Root, "config", "patch2", "FE8U", "old.txt");

        public Fixture(string header = "BE8E01")
        {
            Directory.CreateDirectory(Root);
            Rom.LoadLow("owned-synthetic.gba", new byte[0x1000000], header);
            CoreState.ROM = Rom;
            CoreState.Undo = new Undo();
            CoreState.BaseDirectory = Root;
        }

        public void SeedOld()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OldFile)!);
            File.WriteAllText(OldFile, "old");
        }

        public Task<PatchDatabaseImportService.Outcome> Import(PickedFile selected,
            PatchDatabaseImportService.RomIdentity identity, Func<PatchDatabaseImportCore.PreparedImport, Task<bool>> confirm)
            => PatchDatabaseImportService.ImportForTestAsync(selected.File, identity, Root, confirm, default,
                async (stream, root, version, token) =>
                    lastPrepared = await PatchDatabaseImportCore.PrepareForTestAsync(stream, root, version, token, _ => false));

        public void Dispose()
        {
            lastPrepared?.Dispose();
            CoreState.ROM = savedRom;
            CoreState.Undo = savedUndo;
            CoreState.BaseDirectory = savedBase;
            Directory.Delete(Root, true);
        }
    }
}
