using System.IO.Compression;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace FEBuilderGBA.Core.Tests;

[Collection("ContentRepoGitGuard")]
public class PatchDatabaseImportCoreTests
{
    [Fact]
    public void JournalCodecUsesGeneratedMetadataForTrimmedAndroidBuilds()
    {
        var metadata = PatchDatabaseImportCore.JournalTypeInfo;
        Assert.IsAssignableFrom<System.Text.Json.Serialization.JsonSerializerContext>(metadata.OriginatingResolver);
        var journal = new PatchDatabaseImportCore.Journal
        {
            Owner = "FEBuilderGBA.PatchDatabaseImport", Schema = 1,
            Id = Guid.NewGuid().ToString("N"), Version = "FE8U",
        };
        string json = JsonSerializer.Serialize(journal, metadata);
        Assert.Equal(journal.Id, JsonSerializer.Deserialize(json, metadata)!.Id);
        Assert.Throws<JsonException>(() =>
        {
            _ = JsonSerializer.Deserialize(json.TrimEnd('}') + ",\"Unexpected\":true}", metadata);
        });
    }

    [Fact]
    public async Task PreparationDoesNotReplaceExistingDatabase()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("New");
        using (var prepared = await fixture.Prepare(zip))
        {
            Assert.True(prepared.ReplacesExisting);
            Assert.Equal("old", File.ReadAllText(fixture.OldFile));
            Assert.Equal("FE8U", prepared.Version);
        }
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(fixture.OperationDirectories());
    }

    [Fact]
    public async Task CommitReplacesOnlyTheSelectedVersion()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        string other = Path.Combine(fixture.Root, "config", "patch2", "FE7U", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "other version");
        using var zip = Fixture.Zip("New");
        using (var prepared = await fixture.Prepare(zip))
        {
            var result = prepared.Commit();
            Assert.True(result.Success, result.Message);
        }
        Assert.False(File.Exists(fixture.OldFile));
        Assert.Contains("NAME=New", File.ReadAllText(Path.Combine(fixture.Target, "PATCH_test.txt")));
        Assert.Equal("other version", File.ReadAllText(other));
        Assert.Empty(fixture.OperationDirectories());
    }

    [Fact]
    public async Task DeferredCleanupKeepsTheBackupUntilExplicitCompletion()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("New");
        using var prepared = await fixture.Prepare(zip);
        prepared.ValidateForCommit();
        var result = prepared.Commit(deferCleanup: true);
        Assert.True(result.Success, result.Message);
        Assert.True(result.RecoveryRequired);
        string operation = Assert.Single(fixture.OperationDirectories());
        Assert.Equal("old", File.ReadAllText(Path.Combine(operation, "old", "old.txt")));
        result = prepared.CompleteCleanup();
        Assert.True(result.Success, result.Message);
        Assert.False(result.RecoveryRequired);
        Assert.Empty(fixture.OperationDirectories());
    }

    [Fact]
    public async Task DeferredFailedCommitLeavesFullRollbackForWorkerCompletion()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("New");
        using var prepared = await fixture.Prepare(zip, checkpoint: point =>
        {
            if (point == PatchDatabaseImportCore.Checkpoint.AfterOldMove)
                throw new IOException("Injected failure after moving the old database.");
        });
        prepared.ValidateForCommit();
        var result = prepared.Commit(deferCleanup: true);
        Assert.False(result.Success);
        Assert.True(result.RecoveryRequired);
        Assert.False(Directory.Exists(fixture.Target));
        string operation = Assert.Single(fixture.OperationDirectories());
        Assert.Equal("old", File.ReadAllText(Path.Combine(operation, "old", "old.txt")));
        result = await Task.Run(prepared.CompleteCleanup);
        Assert.False(result.Success);
        Assert.False(result.RecoveryRequired, result.Message);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(fixture.OperationDirectories());
    }

    [Fact]
    public void DatabaseInventoryExcludesRootAndGrantsOnlyTheValidatedMarkerAllowance()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Target);
        Assert.Equal(0, PatchDatabaseImportCore.InspectDatabaseForTest(fixture.Target, "FE8U",
            new() { MaxUserNodes = 0, MaxDatabaseNodes = 0 }));
        Directory.CreateDirectory(Path.Combine(fixture.Target, "empty"));
        File.WriteAllText(Path.Combine(fixture.Target, "state.json"), "user data");
        var limits = new PatchDatabaseImportCore.InventoryLimits { MaxUserNodes = 2, MaxDatabaseNodes = 3 };
        Assert.Equal(2, PatchDatabaseImportCore.InspectDatabaseForTest(fixture.Target, "FE8U", limits));
        WriteMarker(fixture.Target, Guid.NewGuid().ToString("N"));
        Assert.Equal(3, PatchDatabaseImportCore.InspectDatabaseForTest(fixture.Target, "FE8U", limits));
        File.WriteAllText(Path.Combine(fixture.Target, "source.zip"), "also user data");
        Assert.Throws<IOException>(() => PatchDatabaseImportCore.InspectDatabaseForTest(fixture.Target, "FE8U", limits));
    }

    [Fact]
    public void AnUnrecognizedRootMarkerNeverBuysAnotherUserNode()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        File.WriteAllText(Path.Combine(fixture.Target, PatchDatabaseZipReaderCore.OwnershipFileName), "not an owned marker");
        Assert.Throws<IOException>(() => PatchDatabaseImportCore.InspectDatabaseForTest(fixture.Target, "FE8U", new()));
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
    }

    [Fact]
    public void OperationInventoryCountsBothDatabaseRootsAndAllFixedBookkeepingSlots()
    {
        using var fixture = new Fixture();
        string operation = CreateOperation(fixture.Root);
        foreach (string name in new[] { "new", "old" })
        {
            string database = Path.Combine(operation, name);
            Directory.CreateDirectory(Path.Combine(database, "empty"));
            File.WriteAllText(Path.Combine(database, "state.json"), "user data");
            WriteMarker(database, name == "new" ? Path.GetFileName(operation) : Guid.NewGuid().ToString("N"));
        }
        File.WriteAllText(Path.Combine(operation, "state.next"), "");
        File.WriteAllText(Path.Combine(operation, "source.zip"), "");
        var limits = new PatchDatabaseImportCore.InventoryLimits
        {
            MaxUserNodes = 2, MaxDatabaseNodes = 3, MaxOperationNodes = 11,
        };
        Assert.Equal(11, PatchDatabaseImportCore.InspectOperationForTest(fixture.Root, operation, limits));
        Assert.Throws<IOException>(() => PatchDatabaseImportCore.InspectOperationForTest(fixture.Root, operation,
            limits with { MaxOperationNodes = 10 }));
        File.WriteAllText(Path.Combine(operation, "extra.txt"), "unknown");
        Assert.Throws<IOException>(() => PatchDatabaseImportCore.InspectOperationForTest(fixture.Root, operation, new()));
    }

    [Fact]
    public void ProductionInventoryArithmeticAndLowerOnlyOverridesAreExplicit()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Target);
        var limits = new PatchDatabaseImportCore.InventoryLimits();
        Assert.Equal(100_000, limits.MaxUserNodes);
        Assert.Equal(100_001, limits.MaxDatabaseNodes);
        Assert.Equal(200_010, limits.MaxOperationNodes);
        Assert.Equal(200_007, 2 * (limits.MaxDatabaseNodes + 1) + 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchDatabaseImportCore.InspectDatabaseForTest(
            fixture.Target, "FE8U", limits with { MaxUserNodes = 100_001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchDatabaseImportCore.InspectDatabaseForTest(
            fixture.Target, "FE8U", limits with { MaxDatabaseNodes = 100_002 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PatchDatabaseImportCore.InspectDatabaseForTest(
            fixture.Target, "FE8U", limits with { MaxOperationNodes = 200_011 }));
    }

    [Fact]
    public async Task OversizedExistingInventoryIsRefusedBeforeReadingTheSource()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        File.WriteAllText(Path.Combine(fixture.Target, "second.txt"), "keep");
        using var zip = Fixture.Zip("Unused");
        await Assert.ThrowsAsync<IOException>(() => fixture.Prepare(zip,
            inventory: new() { MaxUserNodes = 1 }));
        Assert.Equal(0, zip.Position);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
    }

    [Fact]
    public void OversizedRecoveryTreeIsRetainedWithoutPruningOrReplacingData()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        string operation = CreateOperation(fixture.Root);
        string stage = Path.Combine(operation, "new");
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(stage, "one"), "one");
        File.WriteAllText(Path.Combine(stage, "two"), "two");
        var result = PatchDatabaseImportCore.RecoverPendingForTest(fixture.Root, _ => false,
            new() { MaxUserNodes = 1 });
        Assert.True(result.RecoveryRequired);
        Assert.True(File.Exists(Path.Combine(stage, "one")));
        Assert.True(File.Exists(Path.Combine(stage, "two")));
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
    }

    [Fact]
    public async Task CommittedCleanupPreservesAnOversizedBackup()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("New");
        using var prepared = await fixture.Prepare(zip, inventory: new() { MaxUserNodes = 1 });
        Assert.True(prepared.Commit(deferCleanup: true).Success);
        string operation = Assert.Single(fixture.OperationDirectories());
        string unexpected = Path.Combine(operation, "old", "extra");
        File.WriteAllText(unexpected, "retain");
        var result = prepared.CompleteCleanup();
        Assert.True(result.Success);
        Assert.True(result.RecoveryRequired);
        Assert.Equal("old", File.ReadAllText(Path.Combine(operation, "old", "old.txt")));
        Assert.Equal("retain", File.ReadAllText(unexpected));
    }

    [Fact]
    public async Task ExplicitEmptyDirectoriesAreMaterializedAfterSuccessfulPreflight()
    {
        using var fixture = new Fixture();
        using var zip = Fixture.Zip("Directories", directories: new[] { "empty/", "nested/leaf/" });
        using var prepared = await fixture.Prepare(zip);
        Assert.True(prepared.Commit().Success);
        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "empty")));
        Assert.True(Directory.Exists(Path.Combine(fixture.Target, "nested", "leaf")));
    }

    [Fact]
    public async Task RejectedNodeBudgetNeverReachesUserDataMaterialization()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        int materializations = 0;
        using var zip = Fixture.Zip("Over budget", directories: new[] { "empty/" });
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(zip,
            checkpoint: point =>
            {
                if (point == PatchDatabaseImportCore.Checkpoint.BeforeMaterialization) materializations++;
            }, limits: new() { MaxMaterializedNodes = 1 }));
        Assert.Equal(0, materializations);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(fixture.OperationDirectories());
        using var accepted = Fixture.Zip("At budget", directories: new[] { "empty/" });
        using var prepared = await fixture.Prepare(accepted, checkpoint: point =>
        {
            if (point == PatchDatabaseImportCore.Checkpoint.BeforeMaterialization) materializations++;
        }, limits: new() { MaxMaterializedNodes = 2 });
        Assert.Equal(1, materializations);
    }

    static string CreateOperation(string root)
    {
        string id = Guid.NewGuid().ToString("N");
        string operation = Path.Combine(root, ".patch2-import", id);
        Directory.CreateDirectory(operation);
        var journal = new PatchDatabaseImportCore.Journal
        {
            Owner = "FEBuilderGBA.PatchDatabaseImport", Schema = 1, Id = id, Version = "FE8U",
        };
        File.WriteAllText(Path.Combine(operation, "state.json"), JsonSerializer.Serialize(journal));
        return operation;
    }

    static void WriteMarker(string directory, string id)
        => File.WriteAllText(Path.Combine(directory, PatchDatabaseZipReaderCore.OwnershipFileName),
            "FEBuilderGBA.PatchDatabaseImport\n" + id + "\nFE8U\n", new UTF8Encoding(false));

    [Fact]
    public async Task UnsafeMetadataNeverReachesTheLiveDatabase()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("Rejected", "PATCHED_IF:$FGREP4 ../../outside=01");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(zip));
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(fixture.OperationDirectories());
    }

    [Fact]
    public async Task CancellationPreservesTheOldDatabase()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("Cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Prepare(zip, cancellation.Token));
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
    }

    [Fact]
    public void NoPendingRecoveryDoesNotCreateAWorkspace()
    {
        using var fixture = new Fixture();
        var result = PatchDatabaseImportCore.RecoverPending(fixture.Root);
        Assert.True(result.Success, result.Message);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".patch2-import")));
    }

    [Fact]
    public void MissingApplicationDirectoryIsAHarmlessRecoveryNoOp()
    {
        using var fixture = new Fixture();
        string missing = Path.Combine(fixture.Root, "missing");
        Assert.True(PatchDatabaseImportCore.RecoverPending(missing).Success);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ASecondPrepareCannotRaceTheFirst()
    {
        using var fixture = new Fixture();
        using var one = Fixture.Zip("One");
        using var prepared = await fixture.Prepare(one);
        using var two = Fixture.Zip("Two");
        await Assert.ThrowsAsync<PatchDatabaseOperationLeaseCore.BusyException>(() => fixture.Prepare(two));
    }

    [Fact]
    public async Task SourceStreamIsNotDisposedOrDeletedByPreparation()
    {
        using var fixture = new Fixture();
        using var zip = Fixture.Zip("Owned source");
        using (await fixture.Prepare(zip)) { }
        Assert.True(zip.CanRead);
    }

    [Fact]
    public async Task TargetMutationBeforeCommitIsRefused()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("New");
        using var prepared = await fixture.Prepare(zip);
        File.WriteAllText(fixture.OldFile, "externally changed");
        var result = prepared.Commit();
        Assert.False(result.Success);
        Assert.Equal("externally changed", File.ReadAllText(fixture.OldFile));
    }

    [Theory]
    [InlineData("AfterOldMove")]
    [InlineData("AfterNewMove")]
    [InlineData("BeforeCommitRecord")]
    public async Task PromotionFailureRestoresExistingDatabase(string failAt)
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("New");
        using var prepared = await fixture.Prepare(zip, checkpoint: point =>
        {
            if (point.ToString() == failAt) throw new IOException("Injected promotion failure.");
        });
        Assert.False(prepared.Commit().Success);
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.True(PatchDatabaseImportCore.RecoverPending(fixture.Root).Success);
    }

    [Fact]
    public async Task CorruptPayloadDoesNotReplaceExistingDatabase()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("Corrupt");
        byte[] bytes = zip.ToArray();
        int offset = 30 + BitConverter.ToUInt16(bytes, 26) + BitConverter.ToUInt16(bytes, 28);
        bytes[offset] ^= 1;
        using var corrupt = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Prepare(corrupt));
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
    }

    [Fact]
    public async Task NonSeekableProviderStreamUsesOnlyBoundedOwnedSpooling()
    {
        using var fixture = new Fixture();
        using var zip = Fixture.Zip("SAF");
        using var provider = new NonSeekableSource(zip);
        using var prepared = await fixture.Prepare(provider, limits: new() { MaxInputBytes = zip.Length });
        Assert.True(prepared.Commit().Success);
        Assert.Equal(zip.Length, provider.ReadBytes);
        Assert.True(provider.CanRead);
    }

    [Fact]
    public async Task InputOneBytePastTheLimitIsRejectedWithoutReplacingAnything()
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        using var zip = Fixture.Zip("Too large");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Prepare(zip, limits: new() { MaxInputBytes = zip.Length - 1 }));
        Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(fixture.OperationDirectories());
    }

    [Fact]
    public async Task GitOwnershipIsRefusedBeforeReadingTheSelectedStream()
    {
        using var fixture = new Fixture();
        using var zip = Fixture.Zip("Unread");
        using var source = new NonSeekableSource(zip);
        await Assert.ThrowsAsync<IOException>(() => PatchDatabaseImportCore.PrepareForTestAsync(
            source, fixture.Root, "FE8U", default, _ => true));
        Assert.Equal(0, source.ReadBytes);
    }

    [Fact]
    public async Task UnsupportedVersionIsRefusedWithoutCreatingAWorkspace()
    {
        using var fixture = new Fixture();
        using var zip = Fixture.Zip("Unsupported");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PatchDatabaseImportCore.PrepareAsync(zip, fixture.Root, "NAZO"));
        Assert.Equal(0, zip.Position);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".patch2-import")));
    }

    [Fact]
    public void ExistingReadOnlyLeaseWithoutPendingWorkNeedsNoWritableStartupState()
    {
        using var fixture = new Fixture();
        using (PatchDatabaseOperationLeaseCore.Acquire(fixture.Root)) { }
        string lease = Path.Combine(fixture.Root, ".patch2-import", "lease.lock");
        DateTime before = File.GetLastWriteTimeUtc(lease);
        File.SetAttributes(lease, File.GetAttributes(lease) | FileAttributes.ReadOnly);
        try
        {
            Assert.True(PatchDatabaseImportCore.RecoverPending(fixture.Root).Success);
            Assert.Equal(before, File.GetLastWriteTimeUtc(lease));
        }
        finally { File.SetAttributes(lease, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Owner\":\"not ours\"}")]
    [InlineData("malformed")]
    public void UnknownRecoveryRecordsAreRetainedWithoutDeletingTheirContents(string record)
    {
        using var fixture = new Fixture();
        string operation = Path.Combine(fixture.Root, ".patch2-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(operation);
        File.WriteAllText(Path.Combine(operation, "state.json"), record);
        File.WriteAllText(Path.Combine(operation, "sentinel"), "not ours");
        var result = PatchDatabaseImportCore.RecoverPending(fixture.Root);
        Assert.False(result.Success);
        Assert.True(result.RecoveryRequired);
        Assert.Equal("not ours", File.ReadAllText(Path.Combine(operation, "sentinel")));
    }

    [Theory]
    [InlineData("Prepared", false)]
    [InlineData("AfterOldMove", false)]
    [InlineData("AfterNewMove", false)]
    [InlineData("BeforeCommitRecord", false)]
    [InlineData("Committed", true)]
    [InlineData("BeforeCleanup", true)]
    public void RecoveryAfterAbruptOwnedProcessExitIsIdempotent(string checkpoint, bool committed)
    {
        using var fixture = new Fixture();
        fixture.SeedOld();
        RunCrashProbe(fixture.Root, checkpoint);
        Assert.NotEmpty(fixture.OperationDirectories());
        var result = PatchDatabaseImportCore.RecoverPendingForTest(fixture.Root, _ => false);
        Assert.True(result.Success, result.Message);
        if (committed)
            Assert.Contains("NAME=After interruption",
                File.ReadAllText(Path.Combine(fixture.Target, "PATCH_test.txt")));
        else
            Assert.Equal("old", File.ReadAllText(fixture.OldFile));
        Assert.Empty(fixture.OperationDirectories());
        Assert.True(PatchDatabaseImportCore.RecoverPendingForTest(fixture.Root, _ => false).Success);
    }

    [Fact]
    public async Task ChildCrashProbe()
    {
        string? root = Environment.GetEnvironmentVariable("FEBUILDER_TEST_IMPORT_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        string? checkpoint = Environment.GetEnvironmentVariable("FEBUILDER_TEST_IMPORT_CHECKPOINT");
        using var zip = Fixture.Zip("After interruption");
        using var prepared = await PatchDatabaseImportCore.PrepareForTestAsync(zip, root, "FE8U", default, _ => false,
            point =>
            {
                if (point.ToString() == checkpoint) Environment.Exit(86);
            });
        prepared.Commit();
        Assert.Fail("The requested interruption checkpoint was not reached.");
    }

    [Fact]
    public void DuplicateJournalPropertiesAreRejectedWithoutCleanup()
    {
        using var fixture = new Fixture();
        string id = Guid.NewGuid().ToString("N");
        string operation = Path.Combine(fixture.Root, ".patch2-import", id);
        Directory.CreateDirectory(operation);
        var journal = new PatchDatabaseImportCore.Journal
        {
            Owner = "FEBuilderGBA.PatchDatabaseImport", Schema = 1, Id = id, Version = "FE8U",
        };
        string json = JsonSerializer.Serialize(journal);
        File.WriteAllText(Path.Combine(operation, "state.json"), json.Replace("\"Schema\":1", "\"Schema\":1,\"Schema\":1"));
        var result = PatchDatabaseImportCore.RecoverPendingForTest(fixture.Root, _ => false);
        Assert.True(result.RecoveryRequired);
        Assert.True(Directory.Exists(operation));
    }

    static void RunCrashProbe(string root, string checkpoint)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--Tests:FEBuilderGBA.Core.Tests.PatchDatabaseImportCoreTests.ChildCrashProbe");
        start.Environment["FEBUILDER_TEST_IMPORT_ROOT"] = root;
        start.Environment["FEBUILDER_TEST_IMPORT_CHECKPOINT"] = checkpoint;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(true);
            throw new TimeoutException("The disposable transaction process did not exit.");
        }
        Assert.NotEqual(0, process.ExitCode);
        _ = stdout.Result;
        _ = stderr.Result;
    }

    sealed class NonSeekableSource : Stream
    {
        readonly Stream source;
        public long ReadBytes { get; private set; }
        public NonSeekableSource(Stream source) => this.source = source;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("SAF has no local length.");
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            ReadBytes += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            ReadBytes += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "TestResults",
            "import-" + Guid.NewGuid().ToString("N"));
        public string Target => Path.Combine(Root, "config", "patch2", "FE8U");
        public string OldFile => Path.Combine(Target, "old.txt");

        public Fixture() => Directory.CreateDirectory(Root);

        public void SeedOld()
        {
            Directory.CreateDirectory(Target);
            File.WriteAllText(OldFile, "old");
        }

        public Task<PatchDatabaseImportCore.PreparedImport> Prepare(Stream zip,
            CancellationToken cancellation = default, Action<PatchDatabaseImportCore.Checkpoint>? checkpoint = null,
            PatchDatabaseZipReaderCore.Limits? limits = null, PatchDatabaseImportCore.InventoryLimits? inventory = null)
            => PatchDatabaseImportCore.PrepareForTestAsync(zip, Root, "FE8U", cancellation,
                _ => false, checkpoint, limits, inventory);

        public string[] OperationDirectories()
        {
            string workspace = Path.Combine(Root, ".patch2-import");
            return Directory.Exists(workspace) ? Directory.GetDirectories(workspace) : Array.Empty<string>();
        }

        public static MemoryStream Zip(string name, string extra = "", params string[] directories)
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                foreach (string directory in directories)
                    archive.CreateEntry("FE8U/" + directory, CompressionLevel.NoCompression);
                var entry = archive.CreateEntry("FE8U/PATCH_test.txt", CompressionLevel.NoCompression);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write("NAME=" + name + "\nTYPE=BIN\n" + extra);
            }
            stream.Position = 0;
            return stream;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
