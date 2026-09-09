using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FEBuilderGBA
{
    /// <summary>Filesystem seam for the content-repository transaction.  It is deliberately internal:
    /// callers retain the existing result-based public API while tests can deterministically exercise
    /// retry, completed-then-threw, and rollback paths.</summary>
    internal interface ContentRepoDirectoryOps
    {
        IEnumerable<string> EnumerateFileSystemEntries(string path);
        bool MoveDirectory(string source, string destination);
        bool DeleteDirectory(string path, bool recursive);
        bool DeleteFile(string path);
        void Delay(int milliseconds);
    }

    internal sealed class RealContentRepoDirectoryOps : ContentRepoDirectoryOps
    {
        public IEnumerable<string> EnumerateFileSystemEntries(string path)
            => Directory.EnumerateFileSystemEntries(path);

        public bool MoveDirectory(string source, string destination)
        {
            Directory.Move(source, destination);
            return true;
        }

        public bool DeleteDirectory(string path, bool recursive)
        {
            if (Directory.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // Delete the link/junction itself. Never enumerate or mutate its external target.
                    Directory.Delete(path, false);
                    return true;
                }
            }
            ClearReadOnly(path);
            Directory.Delete(path, recursive);
            return true;
        }

        public bool DeleteFile(string path)
        {
            if (File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
            return true;
        }

        public void Delay(int milliseconds) => System.Threading.Thread.Sleep(milliseconds);

        static void ClearReadOnly(string path)
        {
            if (!Directory.Exists(path)) return;
            FileAttributes rootAttributes = File.GetAttributes(path);
            if ((rootAttributes & FileAttributes.ReparsePoint) != 0) return;
            foreach (string entry in Directory.EnumerateFileSystemEntries(path))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0 &&
                    (attributes & FileAttributes.ReparsePoint) == 0)
                    ClearReadOnly(entry);
                File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
            }
            File.SetAttributes(path, rootAttributes & ~FileAttributes.ReadOnly);
        }
    }

    /// <summary>
    /// Cross-platform, in-app initialize (clone) / update (fetch+reset) of a git-delivered content
    /// repository.  Placeholder release trees are treated separately from real/nonempty directories:
    /// their root remains in place, their exact empty directory shape is restored on clone failure.
    /// </summary>
    public static class ContentRepoGitService
    {
        static readonly object _gate = new object();
        static bool _running;
        static readonly int[] RetryDelays = { 100, 200, 400, 800 };

        public static Patch2GitResult InitializeOrUpdate(string repoDir, string url, Action<string>? progress = null)
        {
            string gitExe = "";
            return InitializeOrUpdateWithLease(repoDir, () =>
                InitializeOrUpdateCore(repoDir, gitExe, url,
                    GitUtil.IsGitRepo, GitUtil.Clone, GitUtil.Update, progress),
                () =>
                {
                    gitExe = GitUtil.FindGitExecutable();
                    return string.IsNullOrEmpty(gitExe)
                        ? new Patch2GitResult { Kind = Patch2GitResultKind.GitNotFound } : null;
                });
        }

        internal static Patch2GitResult InitializeOrUpdateWithLease(string repoDir, Func<Patch2GitResult> operation,
            Func<Patch2GitResult?>? beforeLease = null)
        {
            if (!TryEnter())
                return new Patch2GitResult { Kind = Patch2GitResultKind.AlreadyRunning };
            try
            {
                var preliminary = beforeLease?.Invoke();
                if (preliminary != null) return preliminary;
                using var lease = PatchDatabaseOperationLeaseCore.AcquireForPatch2Repository(repoDir);
                return operation();
            }
            catch (PatchDatabaseOperationLeaseCore.BusyException)
            {
                return new Patch2GitResult { Kind = Patch2GitResultKind.AlreadyRunning };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                return new Patch2GitResult { Kind = Patch2GitResultKind.Failed, ExitCode = -1, Log = ex.Message };
            }
            finally { Exit(); }
        }

        internal static Patch2GitResult InitializeOrUpdateCore(
            string repoDir, string gitExe, string url,
            Func<string, bool> isGitRepo, Patch2GitService.CloneOp cloneOp,
            Patch2GitService.UpdateOp updateOp, Action<string>? progress)
            => InitializeOrUpdateCore(repoDir, gitExe, url, isGitRepo, cloneOp, updateOp, progress,
                new RealContentRepoDirectoryOps());

        internal static Patch2GitResult InitializeOrUpdateCore(
            string repoDir, string gitExe, string url,
            Func<string, bool> isGitRepo, Patch2GitService.CloneOp cloneOp,
            Patch2GitService.UpdateOp updateOp, Action<string>? progress, ContentRepoDirectoryOps? directoryOps)
        {
            if (string.IsNullOrEmpty(gitExe))
                return new Patch2GitResult { Kind = Patch2GitResultKind.GitNotFound };

            var log = new StringBuilder();
            directoryOps ??= new RealContentRepoDirectoryOps();
            if (isGitRepo(repoDir))
                return RunUpdate(repoDir, gitExe, url, updateOp, progress, log);

            PlaceholderTree placeholder = InspectPlaceholder(repoDir, directoryOps);
            string backupPath = null;
            if (placeholder.Kind == PlaceholderKind.Empty)
            {
                if (!ClearPlaceholder(repoDir, placeholder.RelativeDirectories, directoryOps, log))
                {
                    // No clone has run yet, so nothing of git's is on disk: only put back the stub
                    // directories we removed.  Never sweep the held root here — anything unexpected
                    // inside it is NOT ours to delete.
                    RecreatePlaceholderDirectories(repoDir, placeholder, log);
                    return Failure(log, -1, true);
                }
            }
            else if (placeholder.Kind == PlaceholderKind.NonEmptyOrUncertain)
            {
                backupPath = NewBackupPath(repoDir);
                if (!MoveWithRetry(repoDir, backupPath, directoryOps, log))
                    return Failure(log, -1, true);
            }

            int cloneCode;
            try { cloneCode = cloneOp(gitExe, url, repoDir, progress, log); }
            catch (Exception ex)
            {
                log.AppendLine(ex.ToString());
                RestoreAfterCloneFailure(repoDir, backupPath, placeholder, directoryOps, log);
                return Failure(log, -1, true);
            }

            if (cloneCode != 0)
            {
                RestoreAfterCloneFailure(repoDir, backupPath, placeholder, directoryOps, log);
                return Failure(log, cloneCode, true);
            }

            if (backupPath != null && Directory.Exists(backupPath) &&
                !DeleteDirectoryWithRetry(backupPath, true, directoryOps, log))
            {
                string warning = "Retained path: " + backupPath;
                log.AppendLine(warning);
                Log.Error("Content repository clone succeeded, but backup cleanup failed. " + warning);
            }

            return new Patch2GitResult
            {
                Kind = Patch2GitResultKind.Success, ExitCode = 0, Log = log.ToString(), WasClone = true,
            };
        }

        static Patch2GitResult RunUpdate(string repoDir, string gitExe, string url,
            Patch2GitService.UpdateOp updateOp, Action<string>? progress, StringBuilder log)
        {
            try
            {
                int code = updateOp(gitExe, repoDir, progress, log, url);
                return new Patch2GitResult
                {
                    Kind = code == 0 ? Patch2GitResultKind.Success : Patch2GitResultKind.Failed,
                    ExitCode = code, Log = log.ToString(), WasClone = false,
                };
            }
            catch (Exception ex)
            {
                log.AppendLine(ex.ToString());
                return Failure(log, -1, false);
            }
        }

        static Patch2GitResult Failure(StringBuilder log, int exitCode, bool wasClone)
            => new Patch2GitResult
            {
                Kind = Patch2GitResultKind.Failed, ExitCode = exitCode,
                Log = log.ToString(), WasClone = wasClone,
            };

        enum PlaceholderKind { Absent, Empty, NonEmptyOrUncertain }

        sealed class PlaceholderTree
        {
            public PlaceholderKind Kind;
            public List<string> RelativeDirectories = new List<string>();
        }

        // A released stub contains directories only.  Files, reparse points, enumeration failures,
        // and a path which cannot be proven to remain beneath root are deliberately conservative.
        static PlaceholderTree InspectPlaceholder(string root, ContentRepoDirectoryOps ops)
        {
            var result = new PlaceholderTree();
            FileAttributes rootAttributes;
            try
            {
                rootAttributes = File.GetAttributes(root);
            }
            catch (FileNotFoundException)
            {
                result.Kind = PlaceholderKind.Absent;
                return result;
            }
            catch (DirectoryNotFoundException)
            {
                result.Kind = PlaceholderKind.Absent;
                return result;
            }
            catch
            {
                // Directory.Exists intentionally suppresses access errors; do not turn one into
                // an "absent" target and hand it to clone.
                result.Kind = PlaceholderKind.NonEmptyOrUncertain;
                return result;
            }
            try
            {
                if ((rootAttributes & FileAttributes.Directory) == 0 ||
                    (rootAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.Kind = PlaceholderKind.NonEmptyOrUncertain;
                    return result;
                }
                string fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var pending = new Stack<string>();
                pending.Push(fullRoot);
                while (pending.Count != 0)
                {
                    string current = pending.Pop();
                    foreach (string entry in ops.EnumerateFileSystemEntries(current))
                    {
                        FileAttributes attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
                            (attributes & FileAttributes.Directory) == 0)
                        {
                            result.Kind = PlaceholderKind.NonEmptyOrUncertain;
                            return result;
                        }
                        string relative = Path.GetRelativePath(fullRoot, entry);
                        if (!IsSafeRelativeDirectory(fullRoot, relative, entry))
                        {
                            result.Kind = PlaceholderKind.NonEmptyOrUncertain;
                            return result;
                        }
                        result.RelativeDirectories.Add(relative);
                        pending.Push(entry);
                    }
                }
                result.Kind = PlaceholderKind.Empty;
                return result;
            }
            catch
            {
                result.Kind = PlaceholderKind.NonEmptyOrUncertain;
                return result;
            }
        }

        static bool IsSafeRelativeDirectory(string fullRoot, string relative, string entry)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative == "." || relative == ".." ||
                relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(p => p == ".."))
                return false;
            string candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(candidate, Path.GetFullPath(entry), comparison) &&
                   candidate.StartsWith(prefix, comparison);
        }

        static bool ClearPlaceholder(string root, IEnumerable<string> dirs,
            ContentRepoDirectoryOps ops, StringBuilder log)
        {
            // Delete bottom-up, preserving the root so `git clone URL root` performs its supported
            // clone-into-empty-directory path and a failure can recreate precisely these stubs.
            foreach (string relative in dirs.OrderByDescending(d => d.Count(c =>
                         c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar)))
            {
                string path = Path.Combine(root, relative);
                if (!DeleteDirectoryWithRetry(path, false, ops, log))
                    return false;
            }
            try
            {
                if (!Directory.Exists(root) || ops.EnumerateFileSystemEntries(root).Any())
                {
                    log.AppendLine("Placeholder root was not empty: " + root);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                log.AppendLine(ex.ToString());
                return false;
            }
        }

        static void RestoreAfterCloneFailure(string repoDir, string backupPath, PlaceholderTree placeholder,
            ContentRepoDirectoryOps ops, StringBuilder log)
        {
            // Do not leave clone-written files mixed with restored data.  Each original shape has its
            // own restore contract, so the rollback must branch instead of always deleting the root:
            //   * Empty     - the placeholder root is HELD: clear its current children only, then
            //                 recreate the validated empty directory shape.  Never delete/move root.
            //   * Absent    - remove the partial clone root and leave the target absent.
            //   * Backup    - remove the partial clone root, then move the backup back into place.
            if (backupPath != null)
            {
                if (Directory.Exists(repoDir) && !DeleteDirectoryWithRetry(repoDir, true, ops, log))
                {
                    log.AppendLine("Retained path: " + repoDir);
                    log.AppendLine("Retained path: " + backupPath);
                    return;
                }
                if (Directory.Exists(backupPath) && !MoveWithRetry(backupPath, repoDir, ops, log))
                    log.AppendLine("Retained path: " + backupPath);
                return;
            }

            if (placeholder.Kind == PlaceholderKind.Empty)
            {
                if (!EnsureSafePlaceholderRoot(repoDir, log)) return;
                if (!ClearRootChildren(repoDir, ops, log)) return;
                if (!EnsureSafePlaceholderRoot(repoDir, log)) return;
                RecreatePlaceholderDirectories(repoDir, placeholder, log);
                return;
            }

            if (Directory.Exists(repoDir) && !DeleteDirectoryWithRetry(repoDir, true, ops, log))
                log.AppendLine("Retained path: " + repoDir);
        }

        /// <summary>Deletes every current child of a held placeholder root (files first, then
        /// directories deepest-first) without ever touching the root itself.  A clone can leave
        /// nested artifacts whose names collide with stub directories (e.g. <c>FE6/file</c>), so the
        /// children are enumerated live rather than assumed from the snapshot.</summary>
        static bool ClearRootChildren(string root, ContentRepoDirectoryOps ops, StringBuilder log)
        {
            var files = new List<string>();
            var dirs = new List<string>();
            bool allDeleted = true;
            try
            {
                if (!EnsureSafePlaceholderRoot(root, log)) return false;
                CollectEntries(root, files, dirs, ops);
            }
            catch (Exception ex)
            {
                log.AppendLine(ex.ToString());
                log.AppendLine("Retained path: " + root);
                return false;
            }

            foreach (string file in files)
            {
                if (File.Exists(file) && !DeleteFileWithRetry(file, ops, log))
                {
                    log.AppendLine("Retained path: " + file);
                    allDeleted = false;
                }
            }
            foreach (string dir in dirs.OrderByDescending(PathDepth))
            {
                if (Directory.Exists(dir) && !DeleteDirectoryWithRetry(dir, false, ops, log))
                {
                    log.AppendLine("Retained path: " + dir);
                    allDeleted = false;
                }
            }
            return allDeleted;
        }

        static bool EnsureSafePlaceholderRoot(string root, StringBuilder log)
        {
            try
            {
                if (!Directory.Exists(root) && !File.Exists(root))
                    Directory.CreateDirectory(root);
                FileAttributes attributes = File.GetAttributes(root);
                if ((attributes & FileAttributes.Directory) != 0
                    && (attributes & FileAttributes.ReparsePoint) == 0)
                    return true;
                log.AppendLine("Rollback root is not a normal directory: " + root);
            }
            catch (Exception ex)
            {
                log.AppendLine(ex.ToString());
            }
            log.AppendLine("Retained path: " + root);
            return false;
        }

        static void CollectEntries(string dir, List<string> files, List<string> dirs,
            ContentRepoDirectoryOps ops)
        {
            foreach (string entry in ops.EnumerateFileSystemEntries(dir))
            {
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); }
                catch { files.Add(entry); continue; }

                if ((attributes & FileAttributes.Directory) == 0)
                {
                    files.Add(entry);
                    continue;
                }
                dirs.Add(entry);
                // Never walk through a reparse point: delete the link itself, not its target.
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    CollectEntries(entry, files, dirs, ops);
            }
        }

        /// <summary>Best-effort recreation of the validated empty stub shape inside the held root.
        /// One unwritable directory must not abort the remaining restores.</summary>
        static void RecreatePlaceholderDirectories(string root, PlaceholderTree placeholder, StringBuilder log)
        {
            if (!EnsureSafePlaceholderRoot(root, log)) return;
            try { Directory.CreateDirectory(root); }
            catch (Exception ex)
            {
                log.AppendLine(ex.ToString());
                log.AppendLine("Retained path: " + root);
            }
            foreach (string relative in placeholder.RelativeDirectories.OrderBy(PathDepth))
            {
                string path = Path.Combine(root, relative);
                try { Directory.CreateDirectory(path); }
                catch (Exception ex)
                {
                    log.AppendLine(ex.ToString());
                    log.AppendLine("Retained path: " + path);
                }
            }
        }

        static int PathDepth(string path)
            => path.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

        static string NewBackupPath(string repoDir)
            => Path.Combine(Path.GetDirectoryName(repoDir) ?? "",
                "_" + (Path.GetFileName(repoDir) ?? "repo") + "_backup_" + DateTime.Now.Ticks);

        // A move is only complete when the source is gone AND the destination exists: a half-applied
        // move that left the source behind must not be reported as a successful backup/restore.
        static bool MoveWithRetry(string source, string destination, ContentRepoDirectoryOps ops, StringBuilder log)
            => Retry(() => ops.MoveDirectory(source, destination),
                () => !Directory.Exists(source) && Directory.Exists(destination),
                "move " + source + " -> " + destination, ops, log);

        static bool DeleteDirectoryWithRetry(string path, bool recursive, ContentRepoDirectoryOps ops, StringBuilder log)
            => Retry(() => ops.DeleteDirectory(path, recursive), () => !Directory.Exists(path),
                "delete directory " + path, ops, log);

        static bool DeleteFileWithRetry(string path, ContentRepoDirectoryOps ops, StringBuilder log)
            => Retry(() => ops.DeleteFile(path), () => !File.Exists(path),
                "delete file " + path, ops, log);

        /// <summary>Bounded retry around a single filesystem step.  The postcondition — not the return
        /// value — decides success: a normal return of <c>false</c>, or a normal return of <c>true</c>
        /// whose postcondition is still unmet, is turned into a synthesized <see cref="IOException"/>
        /// and retried; an operation that completed and then threw is accepted.</summary>
        static bool Retry(Func<bool> operation, Func<bool> postcondition, string description,
            ContentRepoDirectoryOps ops, StringBuilder log)
        {
            Exception last = null;
            for (int attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
            {
                try
                {
                    if (!operation())
                        throw new IOException(description + " returned false.");
                    if (postcondition()) return true;
                    throw new IOException(description + " did not reach its postcondition.");
                }
                catch (Exception ex)
                {
                    // Some filesystem implementations complete the operation before reporting an error.
                    if (postcondition()) return true;
                    last = ex;
                    if (!(ex is IOException) && !(ex is UnauthorizedAccessException))
                        break;
                    if (attempt < RetryDelays.Length) ops.Delay(RetryDelays[attempt]);
                }
            }
            if (last != null) log.AppendLine(last.ToString());
            return false;
        }

        internal static bool TryEnter()
        {
            lock (_gate)
            {
                if (_running) return false;
                _running = true;
                return true;
            }
        }

        /// <summary>Non-acquiring guard observation for UI preconditions.</summary>
        internal static bool IsRunning()
        {
            lock (_gate) return _running;
        }

        internal static void Exit()
        {
            lock (_gate) _running = false;
        }
    }
}
