using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FEBuilderGBA;
using Xunit;

namespace FEBuilderGBA.Core.Tests
{
    /// <summary>
    /// Desktop unit tests for the pure Android config extraction helper
    /// (<see cref="AndroidConfigExtractorCore"/>, #1123). These exercise the full
    /// extraction logic (fresh extract, version-stamp skip, version-bump re-extract,
    /// partial/corrupt re-extract, crash-before-stamp recovery, manifest-completeness,
    /// nested paths, path-traversal rejection, unrelated-dir isolation) WITHOUT any
    /// Android dependency, via a synthetic directory-backed <see cref="AndroidConfigExtractorCore.IAssetSource"/>.
    /// </summary>
    public class AndroidConfigExtractorCoreTests
    {
        [Theory]
        [InlineData("version")]
        [InlineData("missing")]
        [InlineData("incomplete")]
        public void ProtectedPatchDatabaseSurvivesConfigRefresh(string cause)
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                var source = new TestDirAssetSource(src);
                AndroidConfigExtractorCore.EnsureExtracted(source, target, "one", preservePatchDatabase: true);
                WriteAsset(target, "config/patch2/FE8U/PATCH_owned.txt", "IMPORTED");
                WriteAsset(target, ".patch2-import/retained/old/keep.txt", "BACKUP");
                WriteAsset(target, "config/obsolete/old.txt", "OBSOLETE");
                string version = "one";
                if (cause == "version") version = "two";
                if (cause == "missing") File.Delete(Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName));
                if (cause == "incomplete") File.Delete(Path.Combine(target, "config", "data", "foo.txt"));

                AndroidConfigExtractorCore.EnsureExtracted(source, target, version, preservePatchDatabase: true);

                Assert.Equal("IMPORTED", File.ReadAllText(Path.Combine(target, "config", "patch2", "FE8U", "PATCH_owned.txt")));
                Assert.Equal("BACKUP", File.ReadAllText(Path.Combine(target, ".patch2-import", "retained", "old", "keep.txt")));
                Assert.False(Directory.Exists(Path.Combine(target, "config", "obsolete")));
                Assert.Equal("FOO", File.ReadAllText(Path.Combine(target, "config", "data", "foo.txt")));
            }
            finally { Cleanup(src, target); }
        }

        [Theory]
        [InlineData("config")]
        [InlineData("config/patch2")]
        [InlineData("config/patch2/FE8U/bundled.txt")]
        [InlineData(".patch2-import/state.json")]
        public void ProtectedRuntimeManifestOverlapFailsBeforeAnyMutation(string conflictingPath)
        {
            string target = NewTempDir();
            try
            {
                WriteAsset(target, "config/patch2/FE8U/keep.txt", "KEEP");
                WriteAsset(target, "config/data/old.txt", "OLD");
                var source = new InMemoryAssetSource(new()
                {
                    [conflictingPath] = Encoding.UTF8.GetBytes("forbidden"),
                    ["config/data/new.txt"] = Encoding.UTF8.GetBytes("new"),
                });
                Assert.Throws<IOException>(() => AndroidConfigExtractorCore.EnsureExtracted(
                    source, target, "two", preservePatchDatabase: true));
                Assert.Equal("KEEP", File.ReadAllText(Path.Combine(target, "config", "patch2", "FE8U", "keep.txt")));
                Assert.Equal("OLD", File.ReadAllText(Path.Combine(target, "config", "data", "old.txt")));
            }
            finally { Cleanup(target); }
        }

        [Theory]
        [InlineData("config")]
        [InlineData("config/patch2")]
        public void ProtectedAncestorFileIsRefusedBeforeDeletingIt(string conflict)
        {
            string target = NewTempDir();
            try
            {
                WriteAsset(target, conflict, "not a directory");
                var source = new InMemoryAssetSource(new() { ["config/data/new.txt"] = Encoding.UTF8.GetBytes("new") });
                Assert.Throws<IOException>(() => AndroidConfigExtractorCore.EnsureExtracted(
                    source, target, "one", preservePatchDatabase: true));
                Assert.Equal("not a directory", File.ReadAllText(Path.Combine(target, conflict.Replace('/', Path.DirectorySeparatorChar))));
            }
            finally { Cleanup(target); }
        }

        [Fact]
        public void DefaultExtractionDoesNotOptOtherHeadsIntoPreservation()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                WriteAsset(target, "config/patch2/FE8U/old.txt", "legacy");
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "one");
                Assert.False(Directory.Exists(Path.Combine(target, "config", "patch2")));
            }
            finally { Cleanup(src, target); }
        }

        /// <summary>Synthetic asset source backed by a real on-disk directory.</summary>
        sealed class TestDirAssetSource : AndroidConfigExtractorCore.IAssetSource
        {
            readonly string _root;
            public TestDirAssetSource(string root) { _root = root; }

            public IEnumerable<string> EnumerateAssetFiles()
            {
                foreach (string f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    yield return Path.GetRelativePath(_root, f).Replace('\\', '/');
                }
            }

            public Stream OpenAsset(string relativePath)
            {
                string p = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }

        /// <summary>In-memory source for path-traversal / explicit-list cases.</summary>
        sealed class InMemoryAssetSource : AndroidConfigExtractorCore.IAssetSource
        {
            readonly Dictionary<string, byte[]> _files;
            public InMemoryAssetSource(Dictionary<string, byte[]> files) { _files = files; }
            public IEnumerable<string> EnumerateAssetFiles() => _files.Keys;
            public Stream OpenAsset(string relativePath) => new MemoryStream(_files[relativePath], writable: false);
        }

        static string NewTempDir()
        {
            string d = Path.Combine(Path.GetTempPath(), "feb_extract_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            return d;
        }

        static void WriteAsset(string root, string rel, string content)
        {
            string p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(TestRequire.DirectoryName(p));
            File.WriteAllText(p, content);
        }

        static (string srcRoot, string target) MakeSampleConfig()
        {
            string src = NewTempDir();
            WriteAsset(src, "config/data/foo.txt", "FOO");
            WriteAsset(src, "config/data/bar.txt", "BAR");
            WriteAsset(src, "config/translate/ja.txt", "JA");
            string target = NewTempDir();
            return (src, target);
        }

        // ---- Case 1: fresh extract ----
        [Fact]
        public void FreshExtract_CopiesAllFiles_AndStamps()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.Extracted, result);
                Assert.Equal("FOO", File.ReadAllText(Path.Combine(target, "config", "data", "foo.txt")));
                Assert.Equal("BAR", File.ReadAllText(Path.Combine(target, "config", "data", "bar.txt")));
                Assert.Equal("JA", File.ReadAllText(Path.Combine(target, "config", "translate", "ja.txt")));

                string stamp = Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName);
                Assert.True(File.Exists(stamp));
                string[] lines = File.ReadAllLines(stamp);
                Assert.Equal("1.0-1", lines[0]);
                Assert.Equal("3", lines[1]);
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 2: skip when stamp matches (no re-copy) ----
        [Fact]
        public void SecondCall_SameVersion_SkipsAndDoesNotRecopy()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                // Mutate an extracted file with a sentinel; a skip must leave it intact.
                string foo = Path.Combine(target, "config", "data", "foo.txt");
                File.WriteAllText(foo, "SENTINEL");

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.SkippedUpToDate, result);
                Assert.Equal("SENTINEL", File.ReadAllText(foo)); // not re-copied
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 3: re-extract on version bump, stale file gone ----
        [Fact]
        public void VersionBump_ReExtracts_AndWipesStaleFiles()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                // Remove a file from the source (simulating a new app version's config).
                File.Delete(Path.Combine(src, "config", "data", "bar.txt"));

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "2.0-2");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                Assert.False(File.Exists(Path.Combine(target, "config", "data", "bar.txt"))); // stale file wiped
                Assert.True(File.Exists(Path.Combine(target, "config", "data", "foo.txt")));

                string[] lines = File.ReadAllLines(Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName));
                Assert.Equal("2.0-2", lines[0]);
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 4: partial/corrupt target (config dir deleted) re-extracts ----
        [Fact]
        public void MatchingStamp_ButConfigDirDeleted_ReExtracts()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                // Corrupt: delete the whole extracted config dir but keep the stamp.
                Directory.Delete(Path.Combine(target, "config"), recursive: true);

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                Assert.True(File.Exists(Path.Combine(target, "config", "data", "foo.txt")));
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 5: crash-safety / stamp-written-last (stamp deleted, files present) ----
        [Fact]
        public void StampMissing_FilesPresent_ReExtracts()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                File.Delete(Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName));

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                // No stamp existed at decision time -> treated as a first extract.
                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.Extracted, result);
                Assert.True(File.Exists(Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName)));
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 6: never touches an unrelated dir under target root ----
        [Fact]
        public void DoesNotTouch_UnrelatedSiblingState()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                // App-private state unrelated to config (e.g. ROM autosaves, logs).
                string unrelatedDir = Path.Combine(target, "user_data");
                Directory.CreateDirectory(unrelatedDir);
                string unrelatedFile = Path.Combine(unrelatedDir, "autosave.gba");
                File.WriteAllText(unrelatedFile, "ROMDATA");

                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");
                // Version bump -> re-extract wipes only config roots, not user_data.
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "2.0-2");

                Assert.True(File.Exists(unrelatedFile));
                Assert.Equal("ROMDATA", File.ReadAllText(unrelatedFile));
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 7: matching stamp + a manifest-listed file deleted -> re-extract ----
        [Fact]
        public void MatchingStamp_ButOneAssetFileMissing_ReExtracts()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                // Delete a single extracted file but leave the stamp + the rest intact.
                File.Delete(Path.Combine(target, "config", "translate", "ja.txt"));

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                Assert.True(File.Exists(Path.Combine(target, "config", "translate", "ja.txt"))); // restored
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 8: nested POSIX asset paths extract to nested dirs ----
        [Fact]
        public void NestedPosixPaths_ExtractToNestedDirs()
        {
            string src = NewTempDir();
            string target = NewTempDir();
            try
            {
                WriteAsset(src, "config/data/sub/deep/leaf.txt", "DEEP");
                WriteAsset(src, "config/top.txt", "TOP");

                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal("DEEP", File.ReadAllText(Path.Combine(target, "config", "data", "sub", "deep", "leaf.txt")));
                Assert.Equal("TOP", File.ReadAllText(Path.Combine(target, "config", "top.txt")));
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 9: path-traversal entries are rejected (not written outside target) ----
        [Fact]
        public void PathTraversalEntries_AreRejected()
        {
            string target = NewTempDir();
            string outside = NewTempDir();
            try
            {
                var files = new Dictionary<string, byte[]>
                {
                    ["config/data/ok.txt"] = Encoding.UTF8.GetBytes("OK"),
                    ["../escape.txt"] = Encoding.UTF8.GetBytes("HACK"),
                    ["config/../../escape2.txt"] = Encoding.UTF8.GetBytes("HACK2"),
                };

                var result = AndroidConfigExtractorCore.EnsureExtracted(new InMemoryAssetSource(files), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.Extracted, result);
                // The safe file landed.
                Assert.True(File.Exists(Path.Combine(target, "config", "data", "ok.txt")));
                // No file escaped the target root.
                Assert.False(File.Exists(Path.Combine(TestRequire.DirectoryName(target), "escape.txt")));
                Assert.False(File.Exists(Path.Combine(target, "..", "escape.txt")));

                // Manifest count reflects only the safe file.
                string[] lines = File.ReadAllLines(Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName));
                Assert.Equal("1", lines[1]);
            }
            finally { Cleanup(target, outside); }
        }

        // ---- Case 10: tampered stamp with a rooted entry -> does NOT skip ----
        [Fact]
        public void TamperedStamp_RootedManifestEntry_ReExtracts_NotSkip()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                // Forge a stamp whose version + count match but whose manifest lists
                // a ROOTED path that exists OUTSIDE the target root. Path.Combine
                // would resolve to that outside file; IsStampValid must reject it.
                string outsideFile = Path.Combine(Path.GetTempPath(), "feb_outside_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(outsideFile, "OUTSIDE");
                try
                {
                    string stamp = Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName);
                    File.WriteAllText(stamp, "1.0-1\n1\n" + outsideFile.Replace('\\', '/') + "\n");

                    // Put a sentinel into a real extracted file; a (wrongly) skipped
                    // run would leave it, a correct re-extract overwrites it.
                    string foo = Path.Combine(target, "config", "data", "foo.txt");
                    File.WriteAllText(foo, "SENTINEL");

                    var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                    Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                    Assert.Equal("FOO", File.ReadAllText(foo)); // overwritten -> NOT skipped
                }
                finally { try { File.Delete(outsideFile); } catch { } }
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 11: tampered stamp with a parent-traversal entry -> does NOT skip ----
        [Fact]
        public void TamperedStamp_TraversalManifestEntry_ReExtracts_NotSkip()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                string stamp = Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName);
                File.WriteAllText(stamp, "1.0-1\n1\n../escape.txt\n");

                string foo = Path.Combine(target, "config", "data", "foo.txt");
                File.WriteAllText(foo, "SENTINEL");

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                Assert.Equal("FOO", File.ReadAllText(foo)); // re-extracted, not skipped
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 12: tampered stamp with a LEADING-SLASH in-root entry -> does NOT skip ----
        // Regression for the Copilot CLI re-review finding: NormalizeRelative strips a
        // leading '/', so "/config/data/foo.txt" must be rejected on the RAW entry
        // BEFORE normalization, otherwise it would validate against the existing
        // in-root file and incorrectly skip.
        [Fact]
        public void TamperedStamp_LeadingSlashInRootEntry_ReExtracts_NotSkip()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                string stamp = Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName);
                // count=1 + a single rooted entry that, if naively normalized, points
                // at the real in-root config/data/foo.txt.
                File.WriteAllText(stamp, "1.0-1\n1\n/config/data/foo.txt\n");

                string foo = Path.Combine(target, "config", "data", "foo.txt");
                File.WriteAllText(foo, "SENTINEL");

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                Assert.Equal("FOO", File.ReadAllText(foo)); // re-extracted, NOT skipped
            }
            finally { Cleanup(src, target); }
        }

        // ---- Case 13: tampered stamp with a backslash-separator entry -> does NOT skip ----
        [Fact]
        public void TamperedStamp_BackslashEntry_ReExtracts_NotSkip()
        {
            var (src, target) = MakeSampleConfig();
            try
            {
                AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                string stamp = Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName);
                File.WriteAllText(stamp, "1.0-1\n1\n..\\escape.txt\n");

                string foo = Path.Combine(target, "config", "data", "foo.txt");
                File.WriteAllText(foo, "SENTINEL");

                var result = AndroidConfigExtractorCore.EnsureExtracted(new TestDirAssetSource(src), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.ReExtracted, result);
                Assert.Equal("FOO", File.ReadAllText(foo));
            }
            finally { Cleanup(src, target); }
        }

        // ---- stampFileName path-traversal guard ----
        [Theory]
        [InlineData("../stamp")]
        [InlineData("sub/stamp")]
        [InlineData("sub\\stamp")]
        [InlineData("..")]
        public void UnsafeStampFileName_Throws(string badStamp)
        {
            var files = new Dictionary<string, byte[]>();
            string target = NewTempDir();
            try
            {
                Assert.Throws<ArgumentException>(() =>
                    AndroidConfigExtractorCore.EnsureExtracted(new InMemoryAssetSource(files), target, "1.0-1", badStamp));
            }
            finally { Cleanup(target); }
        }

        // ---- Case 14: source yielding a '.'-segment path is dropped, target root preserved ----
        // Regression for the Copilot bot finding: a "./config/..." (or any '.'-segment)
        // source entry must be rejected, NOT treated as a root of "." whose clean
        // re-extract would delete Path.Combine(targetRootDir, ".") = the whole target.
        [Fact]
        public void Source_WithDotSegmentEntry_DropsIt_AndDoesNotWipeTargetRoot()
        {
            string target = NewTempDir();
            try
            {
                // Unrelated app-private state that must survive.
                string unrelated = Path.Combine(target, "user_data", "rom.gba");
                Directory.CreateDirectory(TestRequire.DirectoryName(unrelated));
                File.WriteAllText(unrelated, "ROM");

                var files = new Dictionary<string, byte[]>
                {
                    ["./config/data/ok.txt"] = System.Text.Encoding.UTF8.GetBytes("OK"),
                    ["config/data/real.txt"] = System.Text.Encoding.UTF8.GetBytes("REAL"),
                };

                var result = AndroidConfigExtractorCore.EnsureExtracted(new InMemoryAssetSource(files), target, "1.0-1");

                Assert.Equal(AndroidConfigExtractorCore.ExtractionResult.Extracted, result);
                // The unrelated dir/file is intact — target root was NOT wiped.
                Assert.True(File.Exists(unrelated));
                Assert.Equal("ROM", File.ReadAllText(unrelated));
                // The safe entry landed; the '.'-segment entry was dropped (count == 1).
                Assert.True(File.Exists(Path.Combine(target, "config", "data", "real.txt")));
                string[] lines = File.ReadAllLines(Path.Combine(target, AndroidConfigExtractorCore.DefaultStampFileName));
                Assert.Equal("1", lines[1]);
            }
            finally { Cleanup(target); }
        }

        // ---- Argument guards ----
        [Fact]
        public void NullSource_Throws()
        {
            string target = NewTempDir();
            try
            {
                Assert.Throws<ArgumentNullException>(() =>
                    AndroidConfigExtractorCore.EnsureExtracted(null!, target, "1.0-1"));
            }
            finally { Cleanup(target); }
        }

        [Fact]
        public void EmptyTarget_Throws()
        {
            var files = new Dictionary<string, byte[]>();
            Assert.Throws<ArgumentException>(() =>
                AndroidConfigExtractorCore.EnsureExtracted(new InMemoryAssetSource(files), "", "1.0-1"));
        }

        // ---- IsWithinRoot containment guard (Zip Slip defense, #1915 / CodeQL cs/zipslip) ----
        // The inline guard in EnsureExtracted is unreachable through the public API (the manifest
        // filter drops '..'/rooted entries upstream — see Case 9 for the end-to-end behavior), so
        // these exercise the containment math directly, including the trailing-separator and
        // sibling-prefix ("root" vs "root-evil") edge cases.
        [Fact]
        public void IsWithinRoot_AcceptsPathsInsideRoot()
        {
            string root = NewTempDir();
            try
            {
                Assert.True(AndroidConfigExtractorCore.IsWithinRoot(root, Path.Combine(root, "config", "data", "foo.txt")));
                Assert.True(AndroidConfigExtractorCore.IsWithinRoot(root, Path.Combine(root, "a.txt")));
                // A trailing separator on the root must not change the verdict.
                Assert.True(AndroidConfigExtractorCore.IsWithinRoot(root + Path.DirectorySeparatorChar, Path.Combine(root, "x", "y.bin")));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void IsWithinRoot_RejectsEscapesAndSiblingPrefix()
        {
            string root = NewTempDir();
            try
            {
                // Parent-traversal escapes (GetFullPath resolves the '..').
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, Path.Combine(root, "..", "escape.txt")));
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, Path.Combine(root, "config", "..", "..", "escape.txt")));
                // A sibling dir merely sharing the root's name as a string prefix must be rejected
                // (the classic "C:\root" vs "C:\root-evil" bypass the trailing separator prevents).
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, root + "-evil" + Path.DirectorySeparatorChar + "x.txt"));
                // The root itself is not strictly "within" the root (a file entry must never target it),
                // in either the bare or the trailing-separator form.
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, root));
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, root + Path.DirectorySeparatorChar));
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root + Path.DirectorySeparatorChar, root));
            }
            finally { Cleanup(root); }
        }

        [Fact]
        public void IsWithinRoot_RejectsEmptyInputs()
        {
            string root = NewTempDir();
            try
            {
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot("", Path.Combine(root, "a.txt")));
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, ""));
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(null!, "x"));
                Assert.False(AndroidConfigExtractorCore.IsWithinRoot(root, null!));
            }
            finally { Cleanup(root); }
        }

        static void Cleanup(params string[] dirs)
        {
            foreach (string d in dirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); }
                catch { /* best-effort temp cleanup */ }
            }
        }
    }
}
