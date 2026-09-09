using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace FEBuilderGBA
{
    public static partial class PatchDatabaseImportCore
    {
        const int MaxRecoveryOperations = 16;
        const int MaxRecordBytes = 4096;
        const int MaxDatabaseTreeEntries = 100_001;
        const string Owner = "FEBuilderGBA.PatchDatabaseImport";
        const string RecordName = "state.json";
        [JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
        [JsonSerializable(typeof(Journal))]
        internal partial class ImportJournalContext : JsonSerializerContext { }

        internal static JsonTypeInfo<Journal> JournalTypeInfo => ImportJournalContext.Default.Journal;

        internal sealed record InventoryLimits
        {
            public int MaxUserNodes { get; init; } = 100_000;
            public int MaxDatabaseNodes { get; init; } = MaxDatabaseTreeEntries;
            public int MaxOperationNodes { get; init; } = 200_010;

            internal void Validate()
            {
                if (MaxUserNodes < 0 || MaxUserNodes > 100_000 ||
                    MaxDatabaseNodes < 0 || MaxDatabaseNodes > MaxDatabaseTreeEntries ||
                    MaxOperationNodes < 0 || MaxOperationNodes > 200_010)
                    throw new ArgumentOutOfRangeException(nameof(InventoryLimits));
            }
        }

        internal enum Checkpoint
        {
            BeforeMaterialization, Prepared, AfterOldMove, AfterNewMove, BeforeCommitRecord, Committed, BeforeCleanup,
        }

        internal enum Phase { Preparing, Prepared, OldMoved, Promoted, Committed }

        internal sealed class Journal
        {
            public string Owner { get; set; } = "";
            public int Schema { get; set; }
            public string Id { get; set; } = "";
            public string Version { get; set; } = "";
            public Phase Phase { get; set; }
            public string? OriginalFingerprint { get; set; }
        }

        public sealed class Result
        {
            public bool Success { get; internal set; }
            public bool RecoveryRequired { get; internal set; }
            public string Message { get; internal set; } = "";
            public string RetainedPath { get; internal set; } = "";
        }

        public sealed class RecoveryException : IOException
        {
            public string RetainedPath { get; }
            internal RecoveryException(string retainedPath, Exception inner)
                : base("The owned import workspace could not be cleaned up: " + retainedPath, inner)
                => RetainedPath = retainedPath;
        }

        public sealed class PreparedImport : IDisposable
        {
            readonly PatchDatabaseOperationLeaseCore.Lease lease;
            readonly Journal journal;
            readonly Func<string, bool> isGitOwned;
            readonly Action<Checkpoint>? checkpoint;
            readonly InventoryLimits inventory;
            bool finished;
            bool disposed;
            bool readyForCommit;
            string targetStamp = "";
            Result? outcome;
            string OperationDirectory => Path.Combine(lease.WorkspaceDirectory, journal.Id);
            string StageDirectory => Path.Combine(OperationDirectory, "new");
            string BackupDirectory => Path.Combine(OperationDirectory, "old");
            public string Version => journal.Version;
            public string TargetDirectory => TargetFor(lease.BaseDirectory, Version);
            public int FileCount { get; }
            public long ExpandedBytes { get; }
            public bool ReplacesExisting => journal.OriginalFingerprint != null;

            internal PreparedImport(PatchDatabaseOperationLeaseCore.Lease lease, Journal journal,
                int fileCount, long expandedBytes, Func<string, bool> isGitOwned, Action<Checkpoint>? checkpoint,
                InventoryLimits inventory)
            {
                this.lease = lease;
                this.journal = journal;
                this.isGitOwned = isGitOwned;
                this.checkpoint = checkpoint;
                this.inventory = inventory;
                FileCount = fileCount;
                ExpandedBytes = expandedBytes;
            }

            public void ValidateForCommit(CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (finished) throw new InvalidOperationException("This import has already finished.");
                readyForCommit = false;
                RefuseGitOwnership(TargetDirectory, isGitOwned);
                if (Snapshot(TargetDirectory, Version, cancellationToken, inventory) != journal.OriginalFingerprint)
                    throw new IOException("The existing patch database changed while confirmation was open.");
                InspectOperationInventory(OperationDirectory, journal, inventory, cancellationToken);
                targetStamp = TargetStamp();
                readyForCommit = true;
            }

            string TargetStamp()
            {
                PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(TargetDirectory);
                return Directory.Exists(TargetDirectory)
                    ? Directory.GetCreationTimeUtc(TargetDirectory).Ticks + ":" +
                        Directory.GetLastWriteTimeUtc(TargetDirectory).Ticks
                    : "";
            }

            public Result Commit(CancellationToken cancellationToken = default, bool deferCleanup = false)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (finished) throw new InvalidOperationException("This import has already finished.");
                cancellationToken.ThrowIfCancellationRequested();
                bool movedBackup = false;
                try
                {
                    if (!readyForCommit) ValidateForCommit(cancellationToken);
                    if (TargetStamp() != targetStamp)
                        throw new IOException("The existing patch database directory changed before promotion.");
                    PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(TargetDirectory);
                    cancellationToken.ThrowIfCancellationRequested();
                    PatchDatabaseOperationLeaseCore.CreatePrivateDirectory(Path.GetDirectoryName(TargetDirectory)!);
                    if (ReplacesExisting)
                    {
                        Directory.Move(TargetDirectory, BackupDirectory);
                        movedBackup = true;
                    }
                    checkpoint?.Invoke(Checkpoint.AfterOldMove);
                    journal.Phase = Phase.OldMoved;
                    WriteJournal(OperationDirectory, journal);
                    Directory.Move(StageDirectory, TargetDirectory);
                    checkpoint?.Invoke(Checkpoint.AfterNewMove);
                    journal.Phase = Phase.Promoted;
                    WriteJournal(OperationDirectory, journal);
                    checkpoint?.Invoke(Checkpoint.BeforeCommitRecord);
                    journal.Phase = Phase.Committed;
                    WriteJournal(OperationDirectory, journal);
                    finished = true;
                    checkpoint?.Invoke(Checkpoint.Committed);
                }
                catch (Exception ex) when (IsExpectedFailure(ex))
                {
                    finished = true;
                    outcome = RecoverFailure(lease.BaseDirectory, OperationDirectory, ex, inventory, deferCleanup,
                        verifyBackup: !movedBackup);
                    return outcome;
                }
                outcome = new Result
                {
                    Success = true, RecoveryRequired = true, RetainedPath = OperationDirectory,
                };
                return deferCleanup ? outcome : CompleteCleanup();
            }

            public Result CompleteCleanup()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!finished || outcome == null)
                    throw new InvalidOperationException("The import has not completed its commit attempt.");
                try
                {
                    if (Directory.Exists(OperationDirectory))
                    {
                        checkpoint?.Invoke(Checkpoint.BeforeCleanup);
                        Journal durable = ReadJournal(OperationDirectory);
                        if (durable.Phase == Phase.Committed)
                            CleanupCommitted(lease.BaseDirectory, OperationDirectory, durable, inventory);
                        else CleanupUncommitted(lease.BaseDirectory, OperationDirectory, durable, inventory);
                    }
                    outcome.RecoveryRequired = false;
                    outcome.RetainedPath = "";
                }
                catch (Exception ex) when (IsExpectedFailure(ex))
                {
                    outcome.RecoveryRequired = true;
                    outcome.RetainedPath = OperationDirectory;
                    outcome.Message += (outcome.Success
                        ? "The new database is installed, but owned backup cleanup is pending: "
                        : "\nOwned workspace cleanup is pending: ") + ex.Message;
                }
                return outcome;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try
                {
                    if (!finished) CleanupUncommitted(lease.BaseDirectory, OperationDirectory, journal, inventory);
                }
                catch (Exception ex) when (IsExpectedFailure(ex))
                {
                    throw new RecoveryException(OperationDirectory, ex);
                }
                finally
                {
                    lease.Dispose();
                    ContentRepoGitService.Exit();
                }
            }
        }

        public static bool IsSupportedVersion(string version) => PatchDatabaseZipReaderCore.IsSupportedVersion(version);

        public static Task<PreparedImport> PrepareAsync(Stream source, string baseDirectory, string version,
            CancellationToken cancellationToken = default)
            => PrepareCoreAsync(source, baseDirectory, version, cancellationToken,
                path => IsGitOwned(path, cancellationToken), null, null, null);

        internal static Task<PreparedImport> PrepareForTestAsync(Stream source, string baseDirectory, string version,
            CancellationToken cancellationToken, Func<string, bool> isGitOwned, Action<Checkpoint>? checkpoint = null,
            PatchDatabaseZipReaderCore.Limits? limits = null, InventoryLimits? inventory = null)
            => PrepareCoreAsync(source, baseDirectory, version, cancellationToken, isGitOwned, checkpoint, limits, inventory);

        static async Task<PreparedImport> PrepareCoreAsync(Stream source, string baseDirectory, string version,
            CancellationToken cancellationToken, Func<string, bool> isGitOwned, Action<Checkpoint>? checkpoint,
            PatchDatabaseZipReaderCore.Limits? limits, InventoryLimits? inventory)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!source.CanRead) throw new InvalidDataException("The selected ZIP stream is not readable.");
            if (!IsSupportedVersion(version)) throw new InvalidDataException("The loaded ROM version is unsupported.");
            cancellationToken.ThrowIfCancellationRequested();
            limits ??= new PatchDatabaseZipReaderCore.Limits();
            limits.Validate();
            inventory ??= new InventoryLimits();
            inventory.Validate();
            if (!ContentRepoGitService.TryEnter())
                throw new PatchDatabaseOperationLeaseCore.BusyException(new IOException("A content operation is running."));
            PatchDatabaseOperationLeaseCore.Lease? lease = null;
            string? operation = null;
            Journal? journal = null;
            try
            {
                lease = PatchDatabaseOperationLeaseCore.Acquire(baseDirectory);
                RecoverUnderLease(lease.BaseDirectory, isGitOwned, inventory);
                string target = TargetFor(lease.BaseDirectory, version);
                RefuseGitOwnership(target, isGitOwned);
                journal = new Journal
                {
                    Owner = Owner, Schema = 1, Id = Guid.NewGuid().ToString("N"), Version = version,
                    Phase = Phase.Preparing, OriginalFingerprint = Snapshot(target, version, cancellationToken, inventory),
                };
                operation = Path.Combine(lease.WorkspaceDirectory, journal.Id);
                PatchDatabaseOperationLeaseCore.CreatePrivateDirectory(operation);
                WriteJournal(operation, journal);
                string stage = Path.Combine(operation, "new");
                PatchDatabaseOperationLeaseCore.CreatePrivateDirectory(stage);
                string spoolPath = Path.Combine(operation, "source.zip");
                using var spool = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    65536, FileOptions.Asynchronous);
                byte[] buffer = new byte[65536];
                long copied = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    if (copied > limits.MaxInputBytes - read)
                        throw new InvalidDataException("Compressed ZIP exceeds the input limit.");
                    await spool.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                }
                await spool.FlushAsync(cancellationToken).ConfigureAwait(false);
                var archive = PatchDatabaseZipReaderCore.Inspect(spool, version, limits, cancellationToken);
                checkpoint?.Invoke(Checkpoint.BeforeMaterialization);
                var manifest = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var entry in archive.Directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PatchDatabaseOperationLeaseCore.CreatePrivateDirectory(
                        Path.Combine(stage, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                }
                foreach (var entry in archive.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string destination = Path.Combine(stage, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    PatchDatabaseOperationLeaseCore.CreatePrivateDirectory(Path.GetDirectoryName(destination)!);
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous);
                    await PatchDatabaseZipReaderCore.CopyEntryAsync(spool, archive, entry, output, cancellationToken)
                        .ConfigureAwait(false);
                    output.Flush(true);
                    manifest.Add(entry.RelativePath, entry.Length);
                }
                var audit = PatchDatabaseMetadataAuditCore.Audit(stage, manifest, cancellationToken: cancellationToken);
                if (audit.DescriptorCount == 0)
                    throw new InvalidDataException("The selected version contains no patch definitions.");
                ValidateDefinitions(stage, manifest, cancellationToken);
                WriteOwnership(stage, journal);
                journal.Phase = Phase.Prepared;
                WriteJournal(operation, journal);
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Invoke(Checkpoint.Prepared);
                return new PreparedImport(lease, journal, archive.Files.Count, archive.ExpandedBytes, isGitOwned, checkpoint, inventory);
            }
            catch (Exception failure)
            {
                try
                {
                    if (operation != null && journal != null && File.Exists(Path.Combine(operation, RecordName)))
                        CleanupUncommitted(lease!.BaseDirectory, operation, journal, inventory);
                }
                catch (Exception cleanup) when (IsExpectedFailure(cleanup))
                {
                    throw new RecoveryException(operation!, new AggregateException(failure, cleanup));
                }
                finally
                {
                    lease?.Dispose();
                    ContentRepoGitService.Exit();
                }
                throw;
            }
        }

        public static Result RecoverPending(string baseDirectory)
            => RecoverPendingCore(baseDirectory, IsGitOwned, new InventoryLimits());

        internal static Result RecoverPendingForTest(string baseDirectory, Func<string, bool> isGitOwned,
            InventoryLimits? inventory = null)
            => RecoverPendingCore(baseDirectory, isGitOwned, inventory ?? new InventoryLimits());

        static Result RecoverPendingCore(string baseDirectory, Func<string, bool> isGitOwned, InventoryLimits inventory)
        {
            inventory.Validate();
            try
            {
                string root = PatchDatabaseOperationLeaseCore.CanonicalBase(baseDirectory);
                string workspace = Path.Combine(root, PatchDatabaseOperationLeaseCore.WorkspaceName);
                // Do not acquire/create a lease on ordinary startup with no pending work.
                PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(workspace);
                if (!Directory.Exists(workspace)) return new Result { Success = true };
                if (!HasPendingEntries(workspace)) return new Result { Success = true };
                if (!ContentRepoGitService.TryEnter())
                    throw new PatchDatabaseOperationLeaseCore.BusyException(new IOException("A content operation is running."));
                try
                {
                    using var lease = PatchDatabaseOperationLeaseCore.Acquire(root);
                    RecoverUnderLease(root, isGitOwned, inventory);
                    return new Result { Success = true };
                }
                finally { ContentRepoGitService.Exit(); }
            }
            catch (Exception ex) when (IsExpectedFailure(ex))
            {
                return new Result { RecoveryRequired = true, Message = ex.Message };
            }
        }

        static bool HasPendingEntries(string workspace)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(workspace))
                if (Path.GetFileName(path) != PatchDatabaseOperationLeaseCore.LeaseName) return true;
            return false;
        }

        static void RecoverUnderLease(string root, Func<string, bool> isGitOwned, InventoryLimits inventory)
        {
            string workspace = Path.Combine(root, PatchDatabaseOperationLeaseCore.WorkspaceName);
            var operations = new List<string>();
            foreach (string path in Directory.EnumerateFileSystemEntries(workspace))
            {
                string name = Path.GetFileName(path);
                if (name == PatchDatabaseOperationLeaseCore.LeaseName) continue;
                if (!Guid.TryParseExact(name, "N", out Guid id) || id.ToString("N") != name)
                    throw new IOException("An unrecognized import workspace was retained: " + path);
                if (operations.Count == MaxRecoveryOperations)
                    throw new IOException("Too many pending patch database operations; no further recovery was attempted.");
                operations.Add(path);
            }
            var records = new List<(string Operation, Journal Record)>();
            foreach (string operation in operations)
            {
                PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(operation);
                Journal record = ReadJournal(operation);
                RefuseGitOwnership(TargetFor(root, record.Version), isGitOwned);
                records.Add((operation, record));
            }
            foreach (var (operation, record) in records)
            {
                if (record.Phase == Phase.Committed) CleanupCommitted(root, operation, record, inventory);
                else CleanupUncommitted(root, operation, record, inventory);
            }
        }

        static Result RecoverFailure(string root, string operation, Exception failure, InventoryLimits inventory,
            bool deferCleanup = false, bool verifyBackup = true)
        {
            try
            {
                Journal durable = ReadJournal(operation);
                if (durable.Phase == Phase.Committed)
                    return new Result
                    {
                        Success = true, RecoveryRequired = true, RetainedPath = operation,
                        Message = "The database was committed despite an interrupted commit response: " + failure.Message,
                    };
                if (!deferCleanup)
                    CleanupUncommitted(root, operation, durable, inventory, verifyBackup: verifyBackup);
                return new Result
                {
                    Message = "Import stopped without committing: " + failure.Message,
                    RecoveryRequired = deferCleanup, RetainedPath = deferCleanup ? operation : "",
                };
            }
            catch (Exception rollback) when (IsExpectedFailure(rollback))
            {
                return new Result
                {
                    RecoveryRequired = true, RetainedPath = operation,
                    Message = "Import recovery is required; the owned workspace was retained. " +
                        failure.Message + " Recovery: " + rollback.Message,
                };
            }
        }

        static void CleanupUncommitted(string root, string operation, Journal journal, InventoryLimits inventory,
            bool verifyBackup = true)
        {
            ValidateOperation(root, operation, journal);
            InspectOperationInventory(operation, journal, inventory);
            string target = TargetFor(root, journal.Version);
            string stage = Path.Combine(operation, "new");
            string backup = Path.Combine(operation, "old");
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(target);
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(backup);
            if (Directory.Exists(target)) InspectDatabase(target, journal.Version, inventory);
            bool hasBackup = Directory.Exists(backup);
            bool ownsTarget = HasOwnership(target, journal);
            if (journal.Phase == Phase.Preparing && (hasBackup || ownsTarget))
                throw new IOException("A preparation record has unexpected promotion artifacts.");
            if (hasBackup && journal.OriginalFingerprint == null)
                throw new IOException("Unexpected backup in an import that did not replace a database.");
            if (ownsTarget)
            {
                if (Directory.Exists(stage)) throw new IOException("Both staged and live owned databases exist.");
                if (journal.OriginalFingerprint != null && !hasBackup)
                    throw new IOException("The original database backup is missing.");
                Directory.Move(target, stage);
            }
            if (hasBackup)
            {
                if (Directory.Exists(target) || File.Exists(target))
                    throw new IOException("Recovery cannot overwrite an unexpected live database.");
                if (verifyBackup && Snapshot(backup, journal.Version, inventory: inventory) != journal.OriginalFingerprint)
                    throw new IOException("The retained original database changed; it was not removed or overwritten.");
                PatchDatabaseOperationLeaseCore.CreatePrivateDirectory(Path.GetDirectoryName(target)!);
                Directory.Move(backup, target);
            }
            else if (journal.Phase >= Phase.OldMoved &&
                Snapshot(target, journal.Version, inventory: inventory) != journal.OriginalFingerprint)
            {
                throw new IOException("The original database cannot be positively identified for recovery.");
            }
            DeleteOwnedWorkspace(operation, journal, inventory);
        }

        static void CleanupCommitted(string root, string operation, Journal journal, InventoryLimits inventory)
        {
            ValidateOperation(root, operation, journal);
            if (!HasOwnership(TargetFor(root, journal.Version), journal))
                throw new IOException("The committed live database could not be identified; its backup was retained.");
            InspectDatabase(TargetFor(root, journal.Version), journal.Version, inventory);
            DeleteOwnedWorkspace(operation, journal, inventory);
        }

        static void ValidateOperation(string root, string operation, Journal journal)
        {
            string expected = Path.Combine(root, PatchDatabaseOperationLeaseCore.WorkspaceName, journal.Id);
            if (!string.Equals(operation, expected, StringComparison.Ordinal))
                throw new IOException("Import workspace identity does not match its record.");
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(operation);
            ValidateRecord(journal, Path.GetFileName(operation));
        }

        internal static int InspectDatabaseForTest(string root, string version, InventoryLimits inventory)
            => InspectDatabase(root, version, inventory);

        static int InspectDatabase(string root, string version, InventoryLimits inventory,
            CancellationToken cancellationToken = default)
        {
            inventory.Validate();
            return EnumerateDatabaseTree(root, version, inventory, cancellationToken).Count();
        }

        internal static int InspectOperationForTest(string root, string operation, InventoryLimits inventory)
        {
            inventory.Validate();
            Journal journal = ReadJournal(operation);
            ValidateOperation(root, operation, journal);
            return InspectOperationInventory(operation, journal, inventory);
        }

        static int InspectOperationInventory(string operation, Journal journal, InventoryLimits inventory,
            CancellationToken cancellationToken = default)
        {
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(operation);
            int total = 0;
            foreach (string path in Directory.EnumerateFileSystemEntries(operation))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++total > inventory.MaxOperationNodes)
                    throw new IOException("Owned operation node limit exceeded.");
                string name = Path.GetFileName(path);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An owned operation entry is a reparse point.");
                bool directory = (attributes & FileAttributes.Directory) != 0;
                if (name is "new" or "old")
                {
                    if (!directory) throw new IOException("An owned database slot is not a directory.");
                    if (name == "new")
                    {
                        string? marker = ReadOwnershipId(path, journal.Version);
                        if (marker != null && marker != journal.Id)
                            throw new IOException("The staged database marker belongs to another operation.");
                    }
                    foreach (string unused in EnumerateDatabaseTree(path, journal.Version, inventory, cancellationToken))
                        if (++total > inventory.MaxOperationNodes)
                            throw new IOException("Owned operation node limit exceeded.");
                }
                else if (name is RecordName or "state.next" or "source.zip")
                {
                    if (directory) throw new IOException("An owned bookkeeping slot is not a file.");
                }
                else throw new IOException("An unexpected file in the owned import workspace was retained.");
            }
            return total;
        }

        static IEnumerable<string> EnumerateDatabaseTree(string root, string version, InventoryLimits inventory,
            CancellationToken cancellationToken)
        {
            string? markerId = ReadOwnershipId(root, version);
            string markerPath = Path.Combine(root, PatchDatabaseZipReaderCore.OwnershipFileName);
            int userNodes = 0;
            foreach (string path in EnumerateSafeTree(root, inventory.MaxDatabaseNodes, cancellationToken))
            {
                bool marker = markerId != null && string.Equals(path, markerPath, StringComparison.Ordinal);
                if (!marker && ++userNodes > inventory.MaxUserNodes)
                    throw new IOException("Database user-data node limit exceeded.");
                yield return path;
            }
        }

        static void DeleteOwnedWorkspace(string operation, Journal journal, InventoryLimits inventory)
        {
            Journal durable = ReadJournal(operation);
            if (durable.Id != journal.Id || durable.Version != journal.Version)
                throw new IOException("Import workspace ownership changed.");
            InspectOperationInventory(operation, journal, inventory);
            DeleteChildren(operation, preserveRecord: true);
            File.Delete(Path.Combine(operation, RecordName));
            Directory.Delete(operation, false);
        }

        static void DeleteChildren(string directory, bool preserveRecord = false)
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (preserveRecord && Path.GetFileName(path) == RecordName) continue;
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("An unexpected reparse point was retained without traversal.");
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteChildren(path);
                    Directory.Delete(path, false);
                }
                else File.Delete(path);
            }
        }

        static Journal ReadJournal(string operation)
        {
            string path = Path.Combine(operation, RecordName);
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(path, allowFileLeaf: true);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxRecordBytes) throw new IOException("Oversized import recovery record.");
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1) throw new IOException("The import recovery record changed while reading.");
            Journal? journal;
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new IOException("The import recovery record is not an object.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in document.RootElement.EnumerateObject())
                    if (!keys.Add(property.Name)) throw new IOException("Duplicate import recovery record property.");
                if (keys.Count != 6) throw new IOException("An import recovery record property is missing.");
                journal = JsonSerializer.Deserialize(bytes, JournalTypeInfo);
            }
            catch (JsonException ex) { throw new IOException("Malformed import recovery record.", ex); }
            if (journal == null) throw new IOException("Missing import recovery record.");
            ValidateRecord(journal, Path.GetFileName(operation));
            return journal;
        }

        static void ValidateRecord(Journal journal, string id)
        {
            if (journal.Owner != Owner || journal.Schema != 1 || journal.Id != id ||
                !Guid.TryParseExact(id, "N", out Guid guid) || guid.ToString("N") != id ||
                !IsSupportedVersion(journal.Version) || !Enum.IsDefined(journal.Phase) ||
                (journal.OriginalFingerprint != null &&
                    (journal.OriginalFingerprint.Length != 64 || !journal.OriginalFingerprint.All(Uri.IsHexDigit))))
                throw new IOException("Unrecognized import recovery record.");
        }

        static void WriteJournal(string operation, Journal journal)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalTypeInfo);
            if (bytes.Length > MaxRecordBytes) throw new IOException("Import recovery record exceeds its limit.");
            string next = Path.Combine(operation, "state.next");
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(next, allowFileLeaf: true);
            using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(next, Path.Combine(operation, RecordName), true);
        }

        static void WriteOwnership(string stage, Journal journal)
        {
            using var stream = new FileStream(Path.Combine(stage, PatchDatabaseZipReaderCore.OwnershipFileName),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] bytes = Encoding.ASCII.GetBytes(Owner + "\n" + journal.Id + "\n" + journal.Version + "\n");
            stream.Write(bytes);
            stream.Flush(true);
        }

        static bool HasOwnership(string target, Journal journal)
        {
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(target);
            if (!Directory.Exists(target)) return false;
            return ReadOwnershipId(target, journal.Version) == journal.Id;
        }

        static string? ReadOwnershipId(string target, string version)
        {
            if (!IsSupportedVersion(version)) throw new IOException("Unsupported database inventory version.");
            string marker = Path.Combine(target, PatchDatabaseZipReaderCore.OwnershipFileName);
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(marker, allowFileLeaf: true);
            if (!File.Exists(marker)) return null;
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read);
            int expectedLength = Owner.Length + 1 + 32 + 1 + version.Length + 1;
            if (stream.Length != expectedLength) throw new IOException("Unrecognized database ownership marker.");
            byte[] actual = new byte[expectedLength];
            stream.ReadExactly(actual);
            if (stream.ReadByte() != -1) throw new IOException("Database ownership marker changed while reading.");
            string[] parts = Encoding.ASCII.GetString(actual).Split('\n');
            if (parts.Length != 4 || parts[0] != Owner || parts[2] != version || parts[3] != "" ||
                !Guid.TryParseExact(parts[1], "N", out Guid id) || id.ToString("N") != parts[1])
                throw new IOException("Unrecognized database ownership marker.");
            return parts[1];
        }

        static void ValidateDefinitions(string stage, IReadOnlyDictionary<string, long> manifest,
            CancellationToken cancellationToken)
        {
            var descriptors = manifest.Keys.Where(path =>
                FileSystemName.MatchesSimpleExpression("PATCH_*.txt", Path.GetFileName(path), ignoreCase: true)).ToArray();
            bool discoverable = descriptors.Any(path => FileSystemName.MatchesSimpleExpression(
                "PATCH_*.txt", Path.GetFileName(path), ignoreCase: OperatingSystem.IsWindows()));
            if (!discoverable) throw new InvalidDataException("No patch definition can be discovered on this platform.");
            IEnumerable<string> Files(string unused)
            {
                foreach (string descriptor in descriptors)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return Path.Combine(stage, descriptor.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            bool parsed = PatchMetadataCore.TryEnumeratePatchesBounded(stage, null!, "", Files, 10_000,
                PatchMetadataCore.MaxMetadataAggregateBytes, out var patches, out string error, out var failure);
            cancellationToken.ThrowIfCancellationRequested();
            if (!parsed || patches.Count == 0)
                throw new InvalidDataException("Bounded patch definition validation failed: " + failure + " " + error);
        }

        internal static bool IsGitOwned(string target)
            => IsGitOwned(target, CancellationToken.None);

        static bool IsGitOwned(string target, CancellationToken cancellationToken)
        {
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(target);
            for (string? path = target; path != null; path = Path.GetDirectoryName(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GitMarkerAt(path)) return true;
            }
            if (!Directory.Exists(target)) return false;
            foreach (string entry in EnumerateSafeTree(target, MaxDatabaseTreeEntries, cancellationToken))
            {
                if (Path.GetFileName(entry).Equals(".git", StringComparison.OrdinalIgnoreCase)) return true;
                if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0 && GitMarkerAt(entry)) return true;
            }
            return false;
        }

        static bool GitMarkerAt(string directory)
        {
            if (!Directory.Exists(directory)) return false;
            // Inspect only marker attributes, never .git contents or a gitdir pointer's target.
            if (ExistsWithoutFollowing(Path.Combine(directory, ".git"))) return true;
            return ExistsWithoutFollowing(Path.Combine(directory, "HEAD")) &&
                (ExistsWithoutFollowing(Path.Combine(directory, "objects")) ||
                 ExistsWithoutFollowing(Path.Combine(directory, "refs")));
        }

        static bool ExistsWithoutFollowing(string path)
        {
            try { _ = File.GetAttributes(path); return true; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        static void RefuseGitOwnership(string target, Func<string, bool> isGitOwned)
        {
            if (isGitOwned(target))
                throw new IOException("ZIP import cannot replace a Git-owned patch database. Use its Git update operation instead.");
        }

        static string? Snapshot(string root, string version, CancellationToken cancellationToken = default,
            InventoryLimits? inventory = null)
        {
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(root);
            if (!Directory.Exists(root)) return null;
            inventory ??= new InventoryLimits();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string path in EnumerateDatabaseTree(root, version, inventory, cancellationToken))
            {
                FileAttributes attributes = File.GetAttributes(path);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                string item = Path.GetRelativePath(root, path) + "\0" +
                    (directory ? "D" : new FileInfo(path).Length.ToString(System.Globalization.CultureInfo.InvariantCulture)) +
                    "\0" + File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n";
                hash.AppendData(Encoding.UTF8.GetBytes(item));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        static IEnumerable<string> EnumerateSafeTree(string root, int limit,
            CancellationToken cancellationToken = default, int maxDepth = 32)
        {
            PatchDatabaseOperationLeaseCore.EnsureSafeAncestry(root);
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            int count = 0;
            while (pending.Count != 0)
            {
                var (directory, depth) = pending.Pop();
                var children = new List<string>();
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++count > limit || depth >= maxDepth)
                        throw new IOException("Patch database filesystem entry/depth limit exceeded.");
                    children.Add(child);
                }
                children.Sort(StringComparer.Ordinal);
                foreach (string entry in children)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("A symbolic link or reparse point was retained without traversal.");
                    yield return entry;
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, depth + 1));
                }
            }
        }

        static string TargetFor(string root, string version) => Path.Combine(root, "config", "patch2", version);

        static bool IsExpectedFailure(Exception ex)
            => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    }
}
