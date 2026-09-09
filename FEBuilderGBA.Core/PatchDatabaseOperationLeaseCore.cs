using System;
using System.IO;

namespace FEBuilderGBA
{
    internal static class PatchDatabaseOperationLeaseCore
    {
        internal const string WorkspaceName = ".patch2-import";
        internal const string LeaseName = "lease.lock";

        internal sealed class BusyException : IOException
        {
            internal BusyException(Exception inner)
                : base("A patch database operation is already running for this application directory.", inner) { }
        }

        internal sealed class Lease : IDisposable
        {
            readonly FileStream stream;
            internal string BaseDirectory { get; }
            internal string WorkspaceDirectory => Path.Combine(BaseDirectory, WorkspaceName);

            internal Lease(string baseDirectory, FileStream stream)
            {
                BaseDirectory = baseDirectory;
                this.stream = stream;
            }

            public void Dispose() => stream.Dispose();
        }

        internal static Lease Acquire(string baseDirectory)
        {
            string root = CanonicalBase(baseDirectory);
            EnsureSafeAncestry(root);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The application directory does not exist.");
            string workspace = Path.Combine(root, WorkspaceName);
            EnsureSafeAncestry(workspace);
            CreatePrivateDirectory(workspace);
            string lockPath = Path.Combine(workspace, LeaseName);
            EnsureSafeAncestry(lockPath, allowFileLeaf: true);
            var options = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                // The inode/path remains in place after disposal. Removing a lock file permits
                // two cooperating processes to lock different files bearing the same name.
                return new Lease(root, new FileStream(lockPath, options));
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
            {
                throw new BusyException(ex);
            }
        }

        internal static Lease? AcquireForPatch2Repository(string repoDirectory)
        {
            string? root = BaseForPatch2Repository(repoDirectory);
            return root == null ? null : Acquire(root);
        }

        internal static string? BaseForPatch2Repository(string repoDirectory)
        {
            if (string.IsNullOrEmpty(repoDirectory)) return null;
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoDirectory));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string? config = Path.GetDirectoryName(path);
            if (!Path.GetFileName(path).Equals("patch2", comparison) || config == null ||
                !Path.GetFileName(config).Equals("config", comparison))
                return null;
            return Path.GetDirectoryName(config);
        }

        internal static string CanonicalBase(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
                throw new IOException("Patch database storage requires a local absolute application directory.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }

        internal static void CreatePrivateDirectory(string path)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
            else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            EnsureSafeAncestry(path);
        }

        internal static void EnsureSafeAncestry(string path, bool allowFileLeaf = false)
        {
            string full = CanonicalBase(path);
            string volume = Path.GetPathRoot(full)!;
            string current = volume;
            CheckComponent(current, false);
            string relative = full.Substring(volume.Length);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                CheckComponent(current, allowFileLeaf && i == parts.Length - 1);
            }
        }

        static void CheckComponent(string path, bool allowFile)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Patch database storage cannot traverse a reparse point or symbolic link.");
            if (!allowFile && (attributes & FileAttributes.Directory) == 0)
                throw new IOException("A patch database directory component is a file.");
            if (allowFile && (attributes & FileAttributes.Directory) != 0)
                throw new IOException("A patch database file component is a directory.");
        }
    }
}
