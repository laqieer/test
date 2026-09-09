using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace FEBuilderGBA
{
    /// <summary>
    /// Core patch metadata parsing and detection — no UI dependencies.
    /// Reads PATCH_*.txt files from config/patch2/{version}/ directories,
    /// extracts metadata, and checks installation status via PATCHED_IF conditions.
    /// </summary>
    public static class PatchMetadataCore
    {
        public enum PatchStatus
        {
            Unknown,
            Installed,
            NotInstalled
        }

        internal enum BoundedPatchReadFailureKind
        {
            None,
            ResourceLimit,
            ContentChanged,
            FileSystem,
        }

        /// <summary>Metadata extracted from a single patch directory.</summary>
        public class PatchInfo
        {
            public string Name { get; set; } = "";
            public string DirectoryName { get; set; } = "";
            public string DirectoryPath { get; set; } = "";
            public string Description { get; set; } = "";
            public string Author { get; set; } = "";
            public string Tags { get; set; } = "";
            public string Type { get; set; } = "";
            public PatchStatus Status { get; set; } = PatchStatus.Unknown;
            public string PatchFilePath { get; set; } = "";
            /// <summary>Number of IF: dependency conditions in the patch file.</summary>
            public int DependencyCount { get; set; }
            /// <summary>Number of unsatisfied IF: dependencies. 0 = all met.</summary>
            public int UnsatisfiedDependencyCount { get; set; }
            /// <summary>Unsatisfied dependency details (only populated when > 0).</summary>
            public List<PatchDependency> UnsatisfiedDependencies { get; set; } = new();
        }

        /// <summary>
        /// Desktop empty-state message for the Patch Manager when <c>config/patch2/{version}</c>
        /// has not been downloaded yet (#1811). Since #1766 the patch library is delivered over
        /// git (repo <c>laqieer/FEBuilderGBA-patch2</c>) rather than bundled, so a fresh install
        /// ships five empty <c>FE6/FE7J/FE7U/FE8J/FE8U</c> stub dirs. Unlike the Android-only
        /// <see cref="AndroidResourceNoticeCore.PatchLibraryUnavailableMessage"/>, this points the
        /// user at the in-app Initialize / Check-for-Updates flow. Bilingual (JA+EN) to match the
        /// startup patch2 prompt.
        /// </summary>
        public const string NotInitializedMessage =
            "パッチデータがまだダウンロードされていません。\r\n" +
            "「更新の確認」からパッチデータベースをダウンロードしてください。\r\n\r\n" +
            "The patch database has not been downloaded yet.\r\n" +
            "Use Check for Updates / Initialize Repository to fetch it.";

        /// <summary>
        /// True when the patch library for a version has no installable patches — the directory is
        /// missing, or exists but a successful scan finds no <c>PATCH_*.txt</c> (the fresh-install
        /// state, #1811). Never throws. An <em>existing</em> directory that fails to enumerate
        /// (permission / path-too-long) returns <c>false</c>: that is a real error, not the
        /// not-initialized state, so callers won't mislead the user with the download notice.
        /// </summary>
        /// <param name="patchBaseDir">The <c>config/patch2/{version}</c> directory.</param>
        public static bool IsPatchLibraryEmpty(string patchBaseDir)
        {
            if (string.IsNullOrEmpty(patchBaseDir))
                return true;
            try
            {
                return Directory.GetFiles(patchBaseDir, "PATCH_*.txt", SearchOption.AllDirectories).Length == 0;
            }
            catch (DirectoryNotFoundException)
            {
                // Genuinely missing directory -> the fresh-install / not-initialized state.
                return true;
            }
            catch
            {
                // An existing directory that fails to enumerate (permission / path-too-long) is a real
                // error, not the fresh-install state — return false so callers do NOT mislead the user
                // with the not-downloaded-yet notice and mask the actual failure. (Directory.Exists is
                // deliberately NOT used to gate this: it also returns false on an existence-probe error,
                // which would misclassify an inaccessible dir as "not initialized".)
                return false;
            }
        }

        /// <summary>
        /// Enumerate all patch directories for a given ROM version and parse metadata.
        /// </summary>
        /// <param name="patchBaseDir">The config/patch2/{version} directory.</param>
        /// <param name="rom">Current ROM for installation detection.</param>
        /// <param name="lang">Language suffix ("en", "zh", or "" for Japanese).</param>
        /// <returns>List of parsed patches, sorted by directory name.</returns>
        public static List<PatchInfo> EnumeratePatches(string patchBaseDir, ROM rom, string lang)
            => EnumeratePatches(patchBaseDir, rom, lang, File.ReadAllLines, null);

        /// <summary>Internal read/listing seam for legacy per-file tolerance coverage.</summary>
        internal static List<PatchInfo> EnumeratePatches(string patchBaseDir, ROM rom, string lang,
            Func<string, string[]> readAllLines, Func<string, string[]> listPatchFiles)
        {
            var patches = new List<PatchInfo>();
            if (string.IsNullOrEmpty(patchBaseDir))
                return patches;

            Func<string, string[]> list = listPatchFiles
                ?? (dir => Directory.GetFiles(dir, "PATCH_*.txt", SearchOption.AllDirectories));

            string[] patchFiles;
            try
            {
                patchFiles = list(patchBaseDir);
            }
            catch (DirectoryNotFoundException)
            {
                return patches;
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                Log.Error("PatchMetadataCore.EnumeratePatches failed for '" + patchBaseDir + "': " + ex.Message);
                return patches;
            }

            foreach (string file in patchFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                string defaultName = GetDefaultPatchName(file);
                PatchInfo info = ParsePatchFileTolerant(file, defaultName, rom, lang, readAllLines);
                SetContainingDirectory(info, file);
                patches.Add(info);
            }
            return patches;
        }

        /// <summary>
        /// Enumerate + parse patch metadata, distinguishing a genuinely EMPTY directory
        /// (returns <c>true</c> with an empty list) from an enumeration/parse FAILURE (returns
        /// <c>false</c> with an explicit <paramref name="error"/>). Only documented filesystem/
        /// access/path exceptions are caught — programmer defects (NullReference, argument-null,
        /// index-out-of-range, invalid-operation, …) propagate so real bugs are not hidden.
        /// </summary>
        public static bool TryEnumeratePatches(string patchBaseDir, ROM rom, string lang,
            out List<PatchInfo> patches, out string error)
            => TryEnumeratePatches(patchBaseDir, rom, lang, File.ReadAllLines, out patches, out error);

        /// <summary>Internal read seam for deterministic enumeration-failure coverage.</summary>
        internal static bool TryEnumeratePatches(string patchBaseDir, ROM rom, string lang,
            Func<string, string[]> readAllLines, out List<PatchInfo> patches, out string error)
            => TryEnumeratePatches(patchBaseDir, rom, lang, readAllLines, null, out patches, out error);

        /// <summary>
        /// Internal read + directory-listing seam. <paramref name="listPatchFiles"/> defaults to
        /// a recursive <c>PATCH_*.txt</c> scan when <c>null</c>; injecting it lets tests simulate
        /// a directory-enumeration ACCESS failure deterministically (no flaky real permission
        /// changes needed).
        /// </summary>
        internal static bool TryEnumeratePatches(string patchBaseDir, ROM rom, string lang,
            Func<string, string[]> readAllLines, Func<string, string[]> listPatchFiles,
            out List<PatchInfo> patches, out string error)
        {
            patches = new List<PatchInfo>();
            error = "";
            // A null/empty patchBaseDir never touches the filesystem — legacy callers rely on
            // this resolving to "successful empty" (preserved on purpose; see remarks below).
            if (string.IsNullOrEmpty(patchBaseDir))
                return true;

            Func<string, string[]> list = listPatchFiles
                ?? (dir => Directory.GetFiles(dir, "PATCH_*.txt", SearchOption.AllDirectories));

            string[] patchFiles;
            try
            {
                // Guard the ACTUAL enumeration — NOT a separate Directory.Exists probe. An
                // existing-but-inaccessible directory (permission/IO/path-too-long) must be
                // reported as a real failure, never silently downgraded to "empty" (Copilot
                // review finding: Directory.Exists inaccessible=>empty). A genuinely MISSING
                // directory still resolves to "successful empty" via DirectoryNotFoundException,
                // matching the historical contract and mirroring IsPatchLibraryEmpty's pattern.
                patchFiles = list(patchBaseDir);
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                error = ex.Message;
                return false;
            }

            try
            {
                // Enumerate EVERY PATCH_*.txt recursively, matching WinForms PatchForm.ScanPatchs
                // (SearchOption.AllDirectories); each patch is named by its NAME param (fallback =
                // filename minus the PATCH_ prefix).
                foreach (string file in patchFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string defaultName = GetDefaultPatchName(file);
                    var info = ParsePatchFileStrict(file, defaultName, rom, lang, readAllLines);
                    SetContainingDirectory(info, file);
                    patches.Add(info);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                patches = new List<PatchInfo>();
                error = ex.Message;
                return false;
            }
            return true;
        }

        /// <summary>
        /// True for documented filesystem/access/path/format exceptions that advisory patch
        /// enumeration may legitimately encounter. Excludes programmer defects (argument-null,
        /// null-reference, index-out-of-range, invalid-operation) so they are never swallowed.
        /// </summary>
        internal static bool IsExpectedFileSystemException(Exception ex)
            => ex is IOException
            || ex is UnauthorizedAccessException
            || ex is System.Security.SecurityException
            || ex is NotSupportedException
            || ex.GetType() == typeof(ArgumentException);

        /// <summary>
        /// Exporter-only bounded discovery seam (#1936 review remediation). Identical contract
        /// to the unbounded internal <see cref="TryEnumeratePatches(string,ROM,string,Func{string,string[]},Func{string,string[]},out List{PatchInfo},out string)"/>
        /// overload except patch-FILE discovery is bounded to <paramref name="maxFiles"/>:
        /// <list type="bullet">
        /// <item>A tri-state ENTRY probe (shared fail-closed
        /// <see cref="BuildfileExportCore.ProbePathAttributes"/>, never <c>Directory.Exists</c> —
        /// which swallows access/security faults into a bare <c>false</c>) classifies
        /// <paramref name="patchBaseDir"/> exactly once, up front: ABSENT ⇒ a root that was never
        /// downloaded / already removed BEFORE discovery began ⇒ the SAME successful-empty result
        /// as an empty root (<paramref name="patches"/> empty, <paramref name="error"/> empty,
        /// <paramref name="failureKind"/> <see cref="BoundedPatchReadFailureKind.None"/>);
        /// UNKNOWN (an inspection fault) ⇒ the inventory is unavailable (<c>false</c>,
        /// <paramref name="error"/> = fault detail, <paramref name="failureKind"/>
        /// <see cref="BoundedPatchReadFailureKind.FileSystem"/>) — a fault is never inferred as
        /// absence; PRESENT ⇒ continue to bounded discovery.</item>
        /// <item>Discovery then flows through ONE shared bounded <c>foreach</c> over an
        /// <see cref="IEnumerable{T}"/> seam: production binds the LAZY
        /// <see cref="Directory.EnumerateFiles(string,string,SearchOption)"/> enumerable; an
        /// injected test lister binds any <see cref="IEnumerable{T}"/> (e.g. a custom iterator).
        /// BOTH iterate the IDENTICAL lazy path — there is no separate eager-array length guard —
        /// and the loop stops the instant more than <paramref name="maxFiles"/> entries are seen,
        /// so a pathological directory can never force an unbounded read before the bound is even
        /// checked. Any expected filesystem fault — thrown SYNCHRONOUSLY by the lister factory OR
        /// LAZILY during a later <c>MoveNext</c>, INCLUDING a <see cref="DirectoryNotFoundException"/>
        /// raised AFTER the successful entry probe (a genuine discovery race where the root
        /// disappears mid-scan) — degrades the whole inventory to unavailable (<c>false</c>,
        /// <paramref name="error"/> populated, <paramref name="failureKind"/>
        /// <see cref="BoundedPatchReadFailureKind.FileSystem"/>); it is NEVER reshaped into
        /// successful-empty, and a null injected enumerable is left as a programmer defect
        /// rather than success-shaped. Discovery always finishes fully before the per-file
        /// metadata parse begins, so a partially enumerated set never publishes any
        /// <see cref="PatchInfo"/>.</item>
        /// </list>
        /// On any typed false path, <paramref name="patches"/> is the empty list (never partial) and
        /// <paramref name="error"/> is a stable, path-free reason for resource/content-drift
        /// false paths. <paramref name="failureKind"/> is the TYPED classification the caller
        /// uses instead of string-sniffing <paramref name="error"/>:
        /// <see cref="BoundedPatchReadFailureKind.ResourceLimit"/> for discovery/item/byte/line
        /// bounds, <see cref="BoundedPatchReadFailureKind.ContentChanged"/> when a discovered
        /// metadata file changes length during the bounded read, and
        /// <see cref="BoundedPatchReadFailureKind.FileSystem"/> for a real filesystem/access
        /// fault (missing/unreadable directory contents, I/O error, etc.) that has nothing to
        /// do with the resource budget. A definition file that WAS already discovered but is
        /// missing or faulting when the bounded metadata pass opens it (a
        /// <see cref="FileNotFoundException"/>/<see cref="DirectoryNotFoundException"/> or other
        /// expected filesystem fault raised by <see cref="TryParsePatchFileStrictBounded"/>) is
        /// exactly this case: the whole inventory degrades to <c>false</c> with
        /// <paramref name="failureKind"/> <see cref="BoundedPatchReadFailureKind.FileSystem"/>
        /// — it is NEVER re-interpreted as the successful-empty missing-root result nor as a
        /// resource bound. Per-file metadata scanning also uses the bounded LAZY
        /// <see cref="TryParsePatchFileStrictBounded"/> (capped at <paramref name="maxFiles"/> raw
        /// lines, at most <see cref="MaxPatchDefinitionBytes"/> bytes per file, and at most
        /// <paramref name="maxAggregateBytes"/> bytes summed across every file in this scan)
        /// rather than the unbounded eager <see cref="ParsePatchFileStrict"/> used by the
        /// legacy <see cref="TryEnumeratePatches(string,ROM,string,Func{string,string[]},Func{string,string[]},out List{PatchInfo},out string)"/>
        /// path, so no advisory-eligible patch file is ever fully materialized into memory before
        /// its bounded raw-parameter pass runs. The production call site always binds
        /// <paramref name="maxAggregateBytes"/> to the immutable
        /// <see cref="MaxMetadataAggregateBytes"/> constant (64 MiB); it is an explicit parameter
        /// (rather than hardcoded) purely so deterministic tests can exercise an aggregate-budget
        /// breach with small fixtures instead of real 64 MiB files.
        /// </summary>
        internal static bool TryEnumeratePatchesBounded(
            string patchBaseDir,
            ROM rom,
            string lang,
            Func<string, IEnumerable<string>> listPatchFiles,
            int maxFiles,
            long maxAggregateBytes,
            out List<PatchInfo> patches,
            out string error,
            out BoundedPatchReadFailureKind failureKind)
            => TryEnumeratePatchesBounded(
                patchBaseDir,
                rom,
                lang,
                listPatchFiles,
                maxFiles,
                maxAggregateBytes,
                null,
                out patches,
                out error,
                out failureKind);

        internal static bool TryEnumeratePatchesBounded(
            string patchBaseDir,
            ROM rom,
            string lang,
            Func<string, IEnumerable<string>> listPatchFiles,
            int maxFiles,
            long maxAggregateBytes,
            Func<string, FileStream> openMetadataFileStreamForTest,
            out List<PatchInfo> patches,
            out string error,
            out BoundedPatchReadFailureKind failureKind)
        {
            // #1965 L3 correction: the parameterized aggregate budget exists so deterministic
            // tests can exercise a breach with small fixtures — it must NEVER be usable to
            // WIDEN the immutable production ceiling. Any caller (production or test) passing
            // more than MaxMetadataAggregateBytes is a programmer defect, not a legitimate small
            // deterministic value, so it throws rather than silently accepting an oversized
            // aggregate budget.
            if (maxAggregateBytes < 0 || maxAggregateBytes > MaxMetadataAggregateBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(maxAggregateBytes),
                    "maxAggregateBytes must be within 0.." + MaxMetadataAggregateBytes
                        + " (the immutable MaxMetadataAggregateBytes ceiling).");
            patches = new List<PatchInfo>();
            error = "";
            failureKind = BoundedPatchReadFailureKind.None;
            if (string.IsNullOrEmpty(patchBaseDir))
                return true;

            // #1965 discovery-race preflight: classify the patch root EXACTLY ONCE at method
            // entry with the shared fail-closed tri-state probe (never Directory.Exists, which
            // swallows access/security faults into a bare false and would misclassify an
            // unreadable-but-present root as "successfully empty"):
            //   * ABSENT  — a root never downloaded / already removed BEFORE discovery began =>
            //               the SAME successful-empty result as an empty root (patches empty,
            //               error empty, failureKind None).
            //   * UNKNOWN — an inspection fault => the inventory is unavailable (false, error =
            //               fault detail, failureKind FileSystem); a fault is never inferred as
            //               absence.
            //   * PRESENT — the root exists at entry; fall through to bounded discovery. Any
            //               LATER disappearance/fault (a genuine race) is handled by the shared
            //               discovery loop below and degrades unavailable, NOT successful-empty.
            switch (BuildfileExportCore.ProbePathAttributes(
                patchBaseDir, File.GetAttributes, out _, out string probeFault))
            {
                case BuildfileExportCore.PathAttributeProbeResult.Absent:
                    return true;
                case BuildfileExportCore.PathAttributeProbeResult.Unknown:
                    failureKind = BoundedPatchReadFailureKind.FileSystem;
                    error = probeFault;
                    return false;
                // PathAttributeProbeResult.Present => continue.
            }

            var discovered = new List<string>();
            // Shared bounded discovery seam (#1965): production binds the LAZY
            // Directory.EnumerateFiles enumerable; a test binds an injected IEnumerable<string>
            // (e.g. a custom iterator). BOTH flow through the SAME bounded foreach so the injected
            // seam exercises the exact lazy-iteration path production uses — there is no separate
            // eager-array length guard. The loop stops the instant more than maxFiles entries are
            // seen, so a pathological directory can never force an unbounded read before the bound
            // is checked. Any expected filesystem fault — thrown SYNCHRONOUSLY by the lister
            // factory OR LAZILY during a later MoveNext, INCLUDING a DirectoryNotFoundException
            // raised AFTER the successful entry probe (a genuine discovery race where the root
            // disappears mid-scan) — degrades the whole inventory to unavailable (false, error
            // populated, failureKind FileSystem) and is NEVER reshaped into successful-empty.
            // A null injected enumerable is left as a programmer defect (NullReferenceException
            // from foreach), deliberately NOT success-shaped. Discovery always finishes fully
            // before the per-file metadata parse below, so a partially enumerated set never
            // publishes any PatchInfo.
            try
            {
                IEnumerable<string> source = listPatchFiles != null
                    ? listPatchFiles(patchBaseDir)
                    : Directory.EnumerateFiles(patchBaseDir, "PATCH_*.txt", SearchOption.AllDirectories);
                foreach (string file in source)
                {
                    if (discovered.Count >= maxFiles)
                    {
                        failureKind = BoundedPatchReadFailureKind.ResourceLimit;
                        error = "advisory patch source exceeds the internal file-discovery bound";
                        return false;
                    }
                    discovered.Add(file);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                failureKind = BoundedPatchReadFailureKind.FileSystem;
                error = ex.Message;
                return false;
            }

            try
            {
                // Separate AGGREGATE byte budget for this whole metadata scan (#1965; the
                // production call site always binds maxAggregateBytes to the immutable
                // MaxMetadataAggregateBytes constant, 64 MiB), independent of the per-file 16 MiB
                // cap and independent of the exporter's own params-pass aggregate. Each file's
                // effective cap is whichever of the two remaining budgets (per-file, aggregate)
                // is smaller, so neither a single huge file nor many moderately-sized files can
                // exceed the aggregate budget read in total.
                long aggregateBytesUsed = 0;
                foreach (string file in discovered.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string defaultName = GetDefaultPatchName(file);
                    long remainingAggregate = maxAggregateBytes - aggregateBytesUsed;
                    long perFileCap = Math.Min(MaxPatchDefinitionBytes, Math.Max(0, remainingAggregate));
                    // Bounded LAZY metadata scan (see TryParsePatchFileStrictBounded) — never an
                    // eager whole-file read — so a pathological file can't force an unbounded
                    // read just to extract NAME/TYPE/PATCHED_IF metadata. A byte/line-bound
                    // breach rejects the whole inventory rather than accepting a truncated
                    // PatchInfo.
                    if (!TryParsePatchFileStrictBounded(
                        file,
                        defaultName,
                        rom,
                        lang,
                        maxFiles,
                        perFileCap,
                        PatchMacroAddressResolverCore.Resolve,
                        openMetadataFileStreamForTest,
                        out PatchInfo info,
                        out long bytesRead,
                        out failureKind))
                    {
                        patches = new List<PatchInfo>();
                        error = failureKind switch
                        {
                            BoundedPatchReadFailureKind.ResourceLimit
                                => "advisory patch metadata exceeds the internal line-scan bound",
                            BoundedPatchReadFailureKind.ContentChanged
                                => BuildfileExportCore.AdvisorySourceChangedReason,
                            BoundedPatchReadFailureKind.FileSystem
                                => BuildfileExportCore.AdvisoryPatchInventoryFileSystemReason,
                            BoundedPatchReadFailureKind.None
                                => throw new InvalidOperationException(
                                    "TryParsePatchFileStrictBounded returned false with failure kind None."),
                            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
                        };
                        return false;
                    }
                    aggregateBytesUsed += bytesRead;
                    SetContainingDirectory(info, file);
                    patches.Add(info);
                }
            }
            catch (Exception ex) when (IsExpectedFileSystemException(ex))
            {
                patches = new List<PatchInfo>();
                failureKind = BoundedPatchReadFailureKind.FileSystem;
                error = ex.Message;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Parse a PATCH_*.txt metadata file.
        /// </summary>
        public static PatchInfo ParsePatchFile(string patchFilePath, string dirName, ROM rom, string lang)
            => ParsePatchFileTolerant(patchFilePath, dirName, rom, lang, File.ReadAllLines);

        static PatchInfo ParsePatchFileTolerant(string patchFilePath, string dirName, ROM rom, string lang,
            Func<string, string[]> readAllLines)
        {
            try
            {
                return ParsePatchFileStrict(patchFilePath, dirName, rom, lang, readAllLines);
            }
            catch (Exception ex)
            {
                Log.ErrorF("PatchMetadataCore: Failed to parse {0}: {1}", patchFilePath, ex.Message);
                return CreatePatchInfo(patchFilePath, dirName);
            }
        }

        static string GetDefaultPatchName(string file)
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            return fileName.StartsWith("PATCH_", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring("PATCH_".Length)
                : fileName;
        }

        static void SetContainingDirectory(PatchInfo info, string file)
        {
            // Group by the patch's real containing folder (e.g. "SYSTEM") so the CLI
            // --patch-name folder filter keeps working with recursion.
            string containingDir = Path.GetFileName(Path.GetDirectoryName(file) ?? "") ?? "";
            if (!string.IsNullOrEmpty(containingDir))
                info.DirectoryName = containingDir;
        }

        static PatchInfo ParsePatchFileStrict(string patchFilePath, string dirName, ROM rom, string lang,
            Func<string, string[]> readAllLines)
            // Legacy unbounded parser: full public/WinForms parity — file-backed $FGREP install
            // markers still resolve through the real resolver (shipped patches depend on it).
            => ParsePatchFileStrictFromLines(
                patchFilePath, dirName, rom, lang, readAllLines(patchFilePath),
                allowFileBackedConditions: true, PatchMacroAddressResolverCore.Resolve);

        /// <summary>
        /// Exporter-only bounded metadata-scan seam (#1965 PR feedback remediation, companion to
        /// <see cref="TryEnumeratePatchesBounded"/>/<see cref="TryParsePatchParamsBounded"/>):
        /// identical NAME/INFO/AUTHOR/TAG/TYPE/PATCHED_IF/dependency parsing semantics to
        /// <see cref="ParsePatchFileStrict"/>, but the file is read through the shared
        /// byte-first <see cref="TryReadBoundedFileLines"/>
        /// helper instead of the unbounded eager <see cref="File.ReadAllLines(string)"/>/lazy
        /// <see cref="File.ReadLines(string)"/> APIs. <paramref name="maxBytes"/> (bound to
        /// <see cref="MaxPatchDefinitionBytes"/>, further capped by the caller's remaining
        /// <see cref="MaxMetadataAggregateBytes"/> aggregate budget) rejects an oversized file
        /// BEFORE a single line is decoded — closing the actual OOM finding (a single
        /// arbitrarily-large raw line was previously materialized in full by
        /// <see cref="File.ReadLines(string)"/> before any line-count guard ran).
        /// <paramref name="maxLines"/> is the pre-existing, still-independent DISTINCT bound
        /// from the shared <see cref="BuildfileFormat.MaxAdvisoryItems"/> advisory-item budget
        /// (it caps raw lines scanned for metadata, not advisory POCOs/list entries) — reusing
        /// the same constant is simply a convenient, already-reviewed, generously-sized ceiling.
        /// Either bound breaching — or a content-drift length change detected during the bounded
        /// read — returns <c>false</c> and no partial <see cref="PatchInfo"/>; a pathological
        /// patch file can neither force an unbounded read nor masquerade as an accepted
        /// truncated record. Any legitimate patch file (always far smaller than either cap) is
        /// DECODED byte-for-byte and line-for-line identically to
        /// <see cref="ParsePatchFileStrict"/>. The ONE deliberate behavioral difference is
        /// install-marker CLASSIFICATION: a file-backed <c>$FGREP</c> install marker — which the
        /// shared resolver would otherwise resolve by opening the external file named in the
        /// marker — is classified <see cref="PatchStatus.Unknown"/> here BEFORE any
        /// <see cref="Path.Combine(string,string)"/>/<see cref="File.Exists(string)"/>/
        /// <see cref="File.ReadAllBytes(string)"/> touches that external filename (#1936),
        /// whereas <see cref="ParsePatchFileStrict"/> and every public/unbounded Patch Manager /
        /// CLI / scanner / rebuild path still resolves it (256 shipped patches depend on that
        /// legacy behavior). <c>$GREP</c>/<c>$XGREP</c>, pointer/text macros, fixed/bare-hex
        /// addresses, and the bounded raw advisory params read from THIS patch definition are
        /// all unchanged.
        /// </summary>
        internal static bool TryParsePatchFileStrictBounded(
            string patchFilePath,
            string dirName,
            ROM rom,
            string lang,
            int maxLines,
            long maxBytes,
            out PatchInfo info,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
            => TryParsePatchFileStrictBounded(
                patchFilePath, dirName, rom, lang, maxLines, maxBytes,
                PatchMacroAddressResolverCore.Resolve, null, out info, out bytesRead, out failureKind);

        /// <summary>
        /// #1936 NON-PRODUCTION test seam. Behaves EXACTLY like the production
        /// <see cref="TryParsePatchFileStrictBounded"/>
        /// overload (same byte/line caps, same bounded read, same file-backed <c>$FGREP</c>
        /// Unknown carve-out) except the shared macro address <paramref name="resolver"/> is
        /// injected instead of hard-bound to
        /// <see cref="PatchMacroAddressResolverCore.Resolve(ROM,string,string,uint)"/>. It exists
        /// solely so a test can PROVE the resolver is never invoked for a file-backed
        /// <c>$FGREP</c> marker (inject a delegate that throws/increments and assert zero calls)
        /// and IS invoked for a <c>$GREP</c> marker (inject a delegate returning a valid address
        /// and assert it ran) — making the escape a positive, non-vacuous guarantee rather than
        /// an inferred one. <paramref name="resolver"/> is internal-only, never reachable through
        /// options / public API / mutable state, and — like the production overload — validates
        /// and therefore cannot widen <paramref name="maxLines"/>/<paramref name="maxBytes"/>.
        /// </summary>
        internal static bool TryParsePatchFileStrictBounded(
            string patchFilePath,
            string dirName,
            ROM rom,
            string lang,
            int maxLines,
            long maxBytes,
            MacroAddressResolver resolver,
            out PatchInfo info,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
            => TryParsePatchFileStrictBounded(
                patchFilePath,
                dirName,
                rom,
                lang,
                maxLines,
                maxBytes,
                resolver,
                null,
                out info,
                out bytesRead,
                out failureKind);

        static bool TryParsePatchFileStrictBounded(
            string patchFilePath,
            string dirName,
            ROM rom,
            string lang,
            int maxLines,
            long maxBytes,
            MacroAddressResolver resolver,
            Func<string, FileStream> openFileStreamForTest,
            out PatchInfo info,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
        {
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (maxLines < 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            info = null;
            failureKind = BoundedPatchReadFailureKind.None;
            // maxLines is enforced INSIDE the shared helper, during line splitting, before a
            // breaching line is ever appended to the returned list (#1965 L2 correction) — no
            // separate post-hoc List.Count check is needed (or safe: that would have already
            // materialized every line into memory first).
            if (!TryReadBoundedFileLines(
                    patchFilePath,
                    maxBytes,
                    maxLines,
                    openFileStreamForTest,
                    out List<string> lines,
                    out bytesRead,
                    out failureKind))
                return false; // byte-cap / raw-line-cap / content-drift breach — no partial
                              // PatchInfo is ever produced.
            // Exporter-bounded metadata scan (#1936): never let an install-marker classification
            // open/read an external file. A file-backed $FGREP marker is classified Unknown
            // before any Path.Combine/File.Exists/File.ReadAllBytes runs.
            info = ParsePatchFileStrictFromLines(
                patchFilePath, dirName, rom, lang, lines, allowFileBackedConditions: false, resolver);
            return true;
        }

        static PatchInfo ParsePatchFileStrictFromLines(
            string patchFilePath, string dirName, ROM rom, string lang, IReadOnlyList<string> lines,
            bool allowFileBackedConditions, MacroAddressResolver resolver)
        {
            var info = CreatePatchInfo(patchFilePath, dirName);
            string? patchedIf = null;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("//")) continue;

                // Localized NAME
                if (!string.IsNullOrEmpty(lang) && line.StartsWith($"NAME.{lang}=", StringComparison.OrdinalIgnoreCase))
                    info.Name = line.Substring($"NAME.{lang}=".Length).Trim();
                else if (line.StartsWith("NAME=", StringComparison.OrdinalIgnoreCase) && info.Name == dirName)
                    info.Name = line.Substring(5).Trim();

                // Localized INFO
                if (!string.IsNullOrEmpty(lang) && line.StartsWith($"INFO.{lang}=", StringComparison.OrdinalIgnoreCase))
                    info.Description = CleanDescription(line.Substring($"INFO.{lang}=".Length));
                else if (line.StartsWith("INFO=", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(info.Description))
                    info.Description = CleanDescription(line.Substring(5));

                if (line.StartsWith("AUTHOR=", StringComparison.OrdinalIgnoreCase))
                    info.Author = line.Substring(7).Trim();

                if (line.StartsWith("TAG=", StringComparison.OrdinalIgnoreCase))
                    info.Tags = line.Substring(4).Trim();

                if (TryParsePatchParamLine(rawLine, out PatchParam metadataParam) &&
                    string.Equals(metadataParam.RawKey, "TYPE", StringComparison.OrdinalIgnoreCase))
                    info.Type = metadataParam.Value;

                if (line.StartsWith("PATCHED_IF:", StringComparison.OrdinalIgnoreCase))
                    patchedIf = line.Substring(11);
            }

            if (!string.IsNullOrEmpty(patchedIf))
                info.Status = CheckPatchInstalled(
                    patchedIf, rom, Path.GetDirectoryName(patchFilePath) ?? "", allowFileBackedConditions, resolver);

            var allDeps = ParsePatchDependencies(lines, lang);
            info.DependencyCount = allDeps.Count;
            if (allDeps.Count > 0)
            {
                var missing = new List<PatchDependency>();
                foreach (var dep in allDeps)
                {
                    dep.IsSatisfied = EvaluateIfCondition(dep.Condition, rom);
                    if (!dep.IsSatisfied)
                        missing.Add(dep);
                }
                info.UnsatisfiedDependencyCount = missing.Count;
                info.UnsatisfiedDependencies = missing;
            }
            return info;
        }

        static PatchInfo CreatePatchInfo(string patchFilePath, string dirName)
            => new PatchInfo
            {
                Name = dirName,
                DirectoryName = dirName,
                DirectoryPath = Path.GetDirectoryName(patchFilePath) ?? "",
                PatchFilePath = patchFilePath,
            };

        /// <summary>
        /// Shared-resolver injection point (#1936 NON-PRODUCTION test seam). Matches
        /// <see cref="PatchMacroAddressResolverCore.Resolve(ROM,string,string,uint)"/> exactly.
        /// Every production path binds that real resolver; the ONLY code that ever passes a
        /// different delegate is the internal test-only
        /// <see cref="TryParsePatchFileStrictBounded"/>
        /// overload, which lets a bounded-metadata test PROVE the resolver is never invoked for a
        /// file-backed <c>$FGREP</c> marker. It is never surfaced through options, public API, or
        /// mutable state, and cannot influence any byte/line cap.
        /// </summary>
        internal delegate uint MacroAddressResolver(ROM rom, string addrString, string basedir, uint startOffset);

        /// <summary>
        /// Check if a patch is installed by evaluating a PATCHED_IF condition string.
        /// Fixed <c>0xADDR</c> / bare-hex addresses are hex-parsed directly. Any
        /// <c>$</c>-prefixed address macro — the full family handled by
        /// <see cref="PatchMacroAddressResolverCore.Resolve"/>: <c>$GREP</c>/<c>$XGREP</c>/
        /// <c>$FGREP</c> (with <c>END</c>/<c>ENDA</c>/<c>+skip</c>), <c>$GREP_ENABLE_POINTER</c>,
        /// <c>$P32</c>/<c>$P32+4</c>, <c>$TEXTID</c>/<c>$TEXTID_P</c>, and the
        /// <c>$&lt;hexaddr&gt;</c> pointer-indirection form (e.g. <c>$0x0812345</c> reads the
        /// 32-bit GBA pointer stored at that offset — there is no literal <c>$deref</c> keyword)
        /// — is resolved through that shared, tested resolver, mirroring WinForms install
        /// detection (#1919). <paramref name="basedir"/> is the patch's own directory, needed
        /// to resolve <c>$FGREP</c> (external .bin) patterns.
        /// Returns <c>Unknown</c> only for a malformed condition (no <c>=</c>, no expected
        /// bytes, or a fixed address that isn't valid hex). A macro that doesn't resolve
        /// (<see cref="U.NOT_FOUND"/>, incl. a missing <c>$FGREP</c> file), an out-of-bounds
        /// address, or a byte mismatch is reported as <c>NotInstalled</c>.
        /// </summary>
        public static PatchStatus CheckPatchInstalled(string condition, ROM rom)
            => CheckPatchInstalled(condition, rom, "");

        public static PatchStatus CheckPatchInstalled(string condition, ROM rom, string basedir)
            => CheckPatchInstalled(
                condition, rom, basedir, allowFileBackedConditions: true,
                PatchMacroAddressResolverCore.Resolve);

        // Shared install-marker evaluator. Public overloads (and the legacy unbounded parser)
        // pass allowFileBackedConditions: true, preserving full WinForms parity — including
        // opening the external .bin named by a file-backed $FGREP marker. The exporter's
        // bounded metadata scan (TryParsePatchFileStrictBounded) passes false: a file-backed
        // $FGREP marker is classified Unknown BEFORE the resolver, Path.Combine, or any external
        // File.Exists/File.ReadAllBytes runs (#1936). $GREP/$XGREP, pointer/text macros, and
        // fixed/bare-hex addresses are unaffected by the flag. `resolver` is the shared macro
        // address resolver: production always binds PatchMacroAddressResolverCore.Resolve; only
        // the internal test seam injects a fake to prove file-backed markers never reach it.
        static PatchStatus CheckPatchInstalled(
            string condition, ROM rom, string basedir, bool allowFileBackedConditions,
            MacroAddressResolver resolver)
        {
            try
            {
                int eqIdx = condition.IndexOf('=');
                if (eqIdx < 0) return PatchStatus.Unknown;

                string addrStr = condition.Substring(0, eqIdx).Trim();
                string dataStr = condition.Substring(eqIdx + 1).Trim();

                byte[] expected = ParseByteArray(dataStr);
                if (expected.Length == 0) return PatchStatus.Unknown;

                // #1936 bounded-exporter escape: a file-backed $FGREP marker would make the
                // shared resolver do arbitrary Path.Combine/File.Exists/File.ReadAllBytes on an
                // external file. In the exporter's bounded metadata pass that is refused up
                // front — classified Unknown BEFORE the resolver is ever invoked. Detection
                // mirrors the resolver's own case-sensitive $FGREP grammar exactly; $GREP,
                // $XGREP, pointer/text macros stay on the normal resolver path.
                if (!allowFileBackedConditions && IsFileBackedResolverCondition(addrStr))
                    return PatchStatus.Unknown;

                // Fixed addresses keep the original hex parse: patch metadata contains
                // BARE hex like "2C2F0" that the resolver's atoi0x reads as DECIMAL, so
                // only $-prefixed macros ($GREP/$XGREP/$FGREP/$P32/$TEXTID/$<addr> deref)
                // go through the shared resolver (#1919).
                uint addr;
                if (addrStr.StartsWith("$", StringComparison.Ordinal))
                {
                    addr = resolver(rom, addrStr, basedir, 0x100);
                    // NOT_FOUND (0xFFFFFFFF) — the macro/pattern didn't resolve (e.g. a GREP
                    // pattern absent from the ROM) → the patch is simply not installed.
                    if (addr == U.NOT_FOUND) return PatchStatus.NotInstalled;
                }
                else
                {
                    string hex = addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? addrStr.Substring(2) : addrStr;
                    if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr))
                        return PatchStatus.Unknown;
                }

                // (long) widens the add so a huge addr can't wrap past the bounds check.
                if ((long)addr + expected.Length > rom.Data.Length) return PatchStatus.NotInstalled;

                byte[] actual = rom.getBinaryData(addr, expected.Length);
                return U.memcmp(expected, actual) == 0
                    ? PatchStatus.Installed
                    : PatchStatus.NotInstalled;
            }
            catch
            {
                return PatchStatus.Unknown;
            }
        }

        /// <summary>
        /// True only for a <c>$</c>-prefixed install-marker address whose value matches the
        /// file-backed <c>$FGREP</c> form of the shared
        /// <see cref="PatchMacroAddressResolverCore"/> GREP grammar — i.e. one that would make
        /// the resolver do arbitrary <see cref="Path.Combine(string,string)"/> /
        /// <see cref="File.Exists(string)"/> / <see cref="File.ReadAllBytes(string)"/> against an
        /// external <c>.bin</c>. Detection is deliberately identical (same regex, same
        /// case-sensitive <c>F</c> capture group) to the resolver's own grammar so the bounded
        /// exporter refuses exactly the file-backed markers the resolver would otherwise open —
        /// no more, no less. <c>$GREP</c>/<c>$XGREP</c>, pointer/text macros, and fixed/bare-hex
        /// addresses all return <c>false</c> (they never open an external file).
        /// </summary>
        static bool IsFileBackedResolverCondition(string addrStr)
        {
            if (string.IsNullOrEmpty(addrStr)) return false;
            if (addrStr[0] != '$') return false;
            string value = addrStr.Substring(1);
            // Mirrors PatchMacroAddressResolverCore's GREP-family grammar exactly; the
            // file-backed variant is the "F" capture group ($FGREP<align> <filename>).
            Match m = RegexCache.Match(value, @"^(F|X)?GREP([0-9]+)(ENDA|END)?\+?([0-9]+)? ");
            return m.Success && m.Groups.Count >= 5
                && string.Equals(m.Groups[1].Value, "F", StringComparison.Ordinal);
        }

        /// <summary>
        /// Parse a space-separated hex byte string like "0xAB 0xCD 0xEF" into a byte array.
        /// </summary>
        public static byte[] ParseByteArray(string dataStr)
        {
            var result = new List<byte>();
            string[] parts = dataStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string hex = part.Trim();
                if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    hex = hex.Substring(2);
                if (byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                    result.Add(b);
                else
                    break; // Stop at first non-byte token
            }
            return result.ToArray();
        }

        /// <summary>Clean description text from patch metadata.</summary>
        public static string CleanDescription(string desc)
        {
            return desc.Replace("\\r\\n", "\n").Replace("\\n", "\n").Trim();
        }

        /// <summary>
        /// Get the language suffix for localized patch metadata.
        /// </summary>
        public static string GetLanguageSuffix()
        {
            string lang = CoreState.Language ?? "";
            if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
            if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh";
            if (lang.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "";
            return "en";
        }

        /// <summary>A single dependency condition from a patch file (IF: line).</summary>
        public class PatchDependency
        {
            /// <summary>The raw condition string (e.g. "0x02BA4=0x00 0xB5 0xC2 0x0F").</summary>
            public string Condition { get; set; } = "";
            /// <summary>Human-readable comment from IF_COMMENT (localized), or empty.</summary>
            public string Comment { get; set; } = "";
            /// <summary>Whether this dependency is satisfied in the current ROM.</summary>
            public bool IsSatisfied { get; set; }
        }

        /// <summary>
        /// Extract IF: dependency conditions from a PATCH_*.txt file.
        /// These are preconditions that must be met (other patches installed) before this patch can be applied.
        /// </summary>
        /// <param name="patchFilePath">Path to the PATCH_*.txt file.</param>
        /// <param name="lang">Language suffix for localized IF_COMMENT.</param>
        /// <returns>List of dependency conditions.</returns>
        public static List<PatchDependency> GetPatchDependencies(string patchFilePath, string lang = "")
        {
            if (!File.Exists(patchFilePath)) return new List<PatchDependency>();

            try
            {
                return ParsePatchDependencies(File.ReadAllLines(patchFilePath), lang);
            }
            catch (Exception ex)
            {
                Log.ErrorF("PatchMetadataCore.GetPatchDependencies: {0}: {1}", patchFilePath, ex.Message);
                return new List<PatchDependency>();
            }
        }

        static List<PatchDependency> ParsePatchDependencies(IReadOnlyList<string> lines, string lang)
        {
            var result = new List<PatchDependency>();
            string ifComment = "";
            string ifCommentLocalized = "";

            // First pass: collect IF_COMMENT values
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("//")) continue;

                int sep = line.IndexOf('=');
                if (sep < 0) continue;

                string key = line.Substring(0, sep).Trim();
                string value = line.Substring(sep + 1).Trim();

                if (!string.IsNullOrEmpty(lang) &&
                    key.Equals($"IF_COMMENT.{lang}", StringComparison.OrdinalIgnoreCase))
                    ifCommentLocalized = value;
                else if (key.Equals("IF_COMMENT", StringComparison.OrdinalIgnoreCase) &&
                         string.IsNullOrEmpty(ifCommentLocalized))
                    ifComment = value;
            }

            string resolvedComment = !string.IsNullOrEmpty(ifCommentLocalized) ? ifCommentLocalized : ifComment;

            // Second pass: collect IF: conditions
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.StartsWith("//")) continue;
                if (!line.StartsWith("IF:", StringComparison.OrdinalIgnoreCase)) continue;

                string condition = line.Substring(3).Trim();
                int commentIdx = condition.IndexOf("//");
                string inlineComment = "";
                if (commentIdx >= 0)
                {
                    inlineComment = condition.Substring(commentIdx + 2).Trim();
                    condition = condition.Substring(0, commentIdx).Trim();
                }

                string depComment = !string.IsNullOrEmpty(resolvedComment) ? resolvedComment
                    : !string.IsNullOrEmpty(inlineComment) ? inlineComment : "";

                result.Add(new PatchDependency
                {
                    Condition = condition,
                    Comment = depComment,
                });
            }
            return result;
        }

        /// <summary>
        /// Check all IF: dependencies for a patch and return those that are NOT satisfied.
        /// </summary>
        /// <param name="rom">The ROM to check against.</param>
        /// <param name="patchFilePath">Path to the PATCH_*.txt file.</param>
        /// <param name="lang">Language suffix for localized IF_COMMENT.</param>
        /// <returns>List of unsatisfied dependencies. Empty means all dependencies are met.</returns>
        public static List<PatchDependency> CheckDependencies(ROM rom, string patchFilePath, string lang = "")
        {
            var deps = GetPatchDependencies(patchFilePath, lang);
            var missing = new List<PatchDependency>();

            foreach (var dep in deps)
            {
                dep.IsSatisfied = EvaluateIfCondition(dep.Condition, rom);
                if (!dep.IsSatisfied)
                    missing.Add(dep);
            }

            return missing;
        }

        /// <summary>
        /// Evaluate a single IF: condition against a ROM.
        /// Supports fixed-address checks (0xADDR=0xBB 0xBB ...).
        /// Returns true if the condition is satisfied, false otherwise.
        /// $GREP/$FGREP conditions are treated as satisfied (we can't check them simply).
        /// </summary>
        public static bool EvaluateIfCondition(string condition, ROM rom)
        {
            if (rom == null) return false;

            try
            {
                // $GREP/$FGREP conditions require searching the entire ROM — skip (assume met)
                if (condition.Contains("$GREP", StringComparison.OrdinalIgnoreCase) ||
                    condition.Contains("$FGREP", StringComparison.OrdinalIgnoreCase))
                    return true;

                int eqIdx = condition.IndexOf('=');
                if (eqIdx < 0) return true; // Malformed, assume OK

                string addrStr = condition.Substring(0, eqIdx).Trim();
                string dataStr = condition.Substring(eqIdx + 1).Trim();

                if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    addrStr = addrStr.Substring(2);
                if (!uint.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
                    return true; // Can't parse, assume OK

                byte[] expected = ParseByteArray(dataStr);
                if (expected.Length == 0) return true;

                if (addr + expected.Length > rom.Data.Length)
                    return false;

                byte[] actual = rom.getBinaryData(addr, expected.Length);
                return U.memcmp(expected, actual) == 0;
            }
            catch
            {
                return true; // On error, don't block
            }
        }

        /// <summary>Result of a patch apply/uninstall operation.</summary>
        public class PatchApplyResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = "";
            public int BytesWritten { get; set; }

            public static PatchApplyResult Ok(string msg, int bytesWritten = 0)
                => new PatchApplyResult { Success = true, Message = msg, BytesWritten = bytesWritten };
            public static PatchApplyResult Fail(string msg)
                => new PatchApplyResult { Success = false, Message = msg };
        }

        /// <summary>
        /// Immutable production cap (16 MiB; #1965 review remediation) on the number of BYTES
        /// read from a single PATCH_*.txt definition file by the bounded metadata/params scans
        /// below. This is the primary OOM fix: <see cref="File.ReadLines(string)"/> materializes
        /// each RAW LINE as a fully-formed string before any line-count/entry-count guard ever
        /// runs, so a pathological single-line patch file could allocate an arbitrarily large
        /// string. Every bounded read now rejects on BYTES first, before a single line is ever
        /// decoded. Never mutable/nullable and never overridden in production; only a
        /// parameterized internal test helper may substitute a smaller cap for deterministic
        /// coverage.
        /// </summary>
        public const long MaxPatchDefinitionBytes = 16L * 1024 * 1024;

        /// <summary>
        /// Immutable production cap (64 MiB; #1965) on the COMBINED bytes read across every
        /// file during one bounded metadata scan (<see cref="TryEnumeratePatchesBounded"/>).
        /// Independent from the params-pass aggregate enforced by the exporter
        /// (<c>BuildfileExportOptions.MaxPatchParamsAggregateBytes</c>) — the two passes read
        /// the same files for different purposes and never share a budget.
        /// </summary>
        public const long MaxMetadataAggregateBytes = 64L * 1024 * 1024;

        /// <summary>
        /// Immutable raw-line cap for a single raw-parameter read (#1965 L2 correction).
        /// Aliases the shared <see cref="BuildfileFormat.MaxAdvisoryItems"/> constant so a
        /// pathological patch file consisting of millions of tiny/blank/comment lines — NONE of
        /// which ever parse as a KEY=VALUE entry, so the pre-existing <c>maxEntries</c> bound
        /// (which only counts PARSED entries) never trips — still cannot force an unbounded raw
        /// <see cref="List{T}"/> of decoded line strings to be materialized. Independent of
        /// <c>maxEntries</c>; enforced by <see cref="TryReadBoundedFileLines"/> itself, during
        /// line splitting, before the line is ever added to the returned list.
        /// </summary>
        internal const int MaxRawParamLines = BuildfileFormat.MaxAdvisoryItems;

        /// <summary>
        /// Fixed, small read-request size used by <see cref="TryReadBoundedFileLines"/> (#1965
        /// L2 correction). Deliberately INDEPENDENT of <c>maxBytes</c> — a naive implementation
        /// that allocates a single buffer sized to the full per-file cap (16 MiB) for EVERY
        /// file, even a 1 KB one, produces tens of gigabytes of cumulative large-object-heap
        /// allocation/GC churn across a real patch library (4,346 files). Every read request
        /// against the underlying stream is capped at this chunk size (or less, near the
        /// remaining budget), regardless of how large <c>maxBytes</c> is.
        /// </summary>
        internal const int ReadChunkBytes = 64 * 1024;

        /// <summary>
        /// Shared byte-first, CHUNKED bounded line reader (#1965 review remediation, L2
        /// hardening) used by both <see cref="TryParsePatchFileStrictBounded"/> (metadata) and
        /// <see cref="TryParsePatchParamsBounded"/> (raw params). Contract:
        /// <list type="bullet">
        /// <item>Opens the file through the shared no-follow, exact-regular-file primitive
        /// <see cref="ProjectionFileSystemSafety.OpenRegularFileForRead(string)"/> — the SAME
        /// production opener used for ROM/manifest ingestion elsewhere in Core — via an overload
        /// that accepts a test-only opener for deterministic fault injection. A final PATCH_*.txt
        /// path entry that is a symlink/reparse point, or any other non-plain-regular-file type,
        /// is refused before a single byte is ever read (closing a #1965 finding: the prior plain
        /// <see cref="FileStream"/> constructor transparently followed a final symlink, letting an
        /// external target's bytes flow into advisory <c>patches.installed[].params</c>). A
        /// genuinely missing final file or missing parent directory surfaces as the typed
        /// <see cref="FileNotFoundException"/>/<see cref="DirectoryNotFoundException"/> pair, which
        /// this shared reader deliberately PROPAGATES rather than swallowing — each caller decides
        /// what a missing file means: the raw-PARAMS pass (<see cref="TryParsePatchParamsBounded"/>)
        /// maps it to a successful empty result (matching the historical
        /// <see cref="File.Exists(string)"/> contract), whereas the METADATA pass
        /// (<see cref="TryParsePatchFileStrictBounded"/>) leaves it to propagate so an
        /// already-discovered-then-missing definition degrades the whole advisory inventory to
        /// unavailable. This open never precedes that check with a separate
        /// <see cref="File.Exists(string)"/> probe, so no TOCTOU gap is reintroduced.
        /// Ancestor-directory symlinks earlier in the path are outside this
        /// final-entry guarantee by design.</item>
        /// <item>Reads <see cref="FileStream.Length"/> FIRST and rejects immediately when it
        /// already exceeds <paramref name="maxBytes"/> — a sparse/huge reported length is never
        /// used to size an allocation, only compared as a plain <c>long</c>. That SAME captured
        /// <c>length</c> value is then held for the rest of the read as the exact byte count this
        /// call must observe through EOF (#1965 length-drift correction) — it is read from the
        /// handle exactly once and never re-queried.</item>
        /// <item>Bytes are read through a small, FIXED, pooled (<see cref="ArrayPool{T}"/>)
        /// chunk buffer (<see cref="ReadChunkBytes"/>, 64 KiB) — NEVER a buffer sized to
        /// <paramref name="maxBytes"/>. Each individual read request is additionally clamped to
        /// the remaining byte budget (+1, to still detect a one-byte overrun) so the stream is
        /// never asked for more than <c>maxBytes + 1</c> bytes in total, and never more than the
        /// fixed chunk size in a single call. The accepting <see cref="MemoryStream"/> starts at
        /// a size hint taken from the ALREADY-VALIDATED (≤ <paramref name="maxBytes"/>)
        /// <see cref="FileStream.Length"/>, or a small default if that length is non-positive —
        /// it grows only as bytes actually arrive, never pre-sized to the cap. After EVERY
        /// positive read, <paramref name="bytesRead"/> is updated first and the accumulated total
        /// is compared against the captured <c>length</c> FIRST, then against
        /// <paramref name="maxBytes"/> — a breach of EITHER bound rejects the whole read BEFORE
        /// the surplus chunk is ever written into the accepting buffer or decoded (#1965
        /// length-drift correction: a prior version only checked <paramref name="maxBytes"/>
        /// here, silently accepting a handle that grew past its own captured <c>Length</c> as
        /// long as the growth still fit under the caller's byte budget). Because the captured
        /// <c>length</c> already passed the initial <c>length &lt;= maxBytes</c> check, any
        /// in-loop over-budget read also proves growth and therefore classifies as content
        /// change, not resource limit. Symmetrically, reaching genuine EOF
        /// (a zero-byte read — the shared no-follow opener guarantees a regular, synchronous
        /// <see cref="FileStream"/>, so a short positive read is never itself EOF and the loop
        /// keeps requesting more) with a running total LESS than the captured <c>length</c> — a
        /// premature EOF, e.g. the file was truncated after <c>Length</c> was read — is likewise
        /// rejected before any decode: accepted bytes reaching the decoder must equal the
        /// captured <c>length</c> EXACTLY, never merely "at or under it". This is a length-drift
        /// detector, not an immutable-snapshot guarantee: an in-place mutation that leaves the
        /// file's length unchanged, or bytes appended strictly after the final observed EOF,
        /// are both outside what this check can detect.</item>
        /// <item><paramref name="bytesRead"/> is updated INCREMENTALLY after every individual
        /// successful chunk read (not just once at the end) — so if a LATER read on the same
        /// handle throws (a genuine I/O fault partway through), the bytes genuinely consumed
        /// before that fault are still visible to the caller through this <c>out</c> parameter
        /// (an <c>out</c>/<c>ref</c> parameter is a direct alias to the caller's storage, so
        /// assignments made before a thrown exception persist even though the method never
        /// reaches a <c>return</c>). Callers MUST NOT reset this value to zero in a catch block —
        /// doing so would let genuinely-read bytes evade an aggregate byte budget.</item>
        /// <item>A zero-budget cap (<paramref name="maxBytes"/> == 0) succeeds for a genuinely
        /// empty file and fails for any non-empty file.</item>
        /// <item>Decoded lines are appended to the result list ONE AT A TIME while reading via
        /// <see cref="StreamReader.ReadLine"/>, checked against <paramref name="maxLines"/>
        /// BEFORE each line is added — never after decoding the complete file into an unbounded
        /// list first. A file within the byte budget but consisting of millions of tiny/blank
        /// lines (which would never trip a downstream parsed-entry count) is rejected the
        /// instant the raw line count would exceed <paramref name="maxLines"/>, with
        /// <paramref name="lines"/> cleared to <c>null</c> — no partial line list is ever kept.</item>
        /// <item>Accepted bytes are decoded via a plain <see cref="StreamReader"/> over the
        /// accepted in-memory buffer: BOM auto-detection (UTF-8/UTF-16 LE/BE) plus the default
        /// non-strict UTF-8 fallback (U+FFFD replacement on invalid sequences) — identical
        /// decode/line-splitting contract (CRLF/LF/CR, unterminated final line) to
        /// <see cref="File.ReadLines(string)"/>, so any within-budget file yields a raw line list
        /// byte-identical to the legacy unbounded path (this shared reader performs no install-marker
        /// classification — the exporter-bounded file-backed <c>$FGREP</c> Unknown carve-out is
        /// applied later, by <see cref="TryParsePatchFileStrictBounded"/>).</item>
        /// </list>
        /// On any byte-cap/raw-line-cap/content-drift breach, returns <c>false</c> with
        /// <paramref name="lines"/> left <c>null</c> and <paramref name="failureKind"/>
        /// describing the reason — no partial line list is ever produced.
        /// </summary>
        internal static bool TryReadBoundedFileLines(
            string patchFilePath,
            long maxBytes,
            int maxLines,
            out List<string> lines,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
            => TryReadBoundedFileLines(
                patchFilePath,
                maxBytes,
                maxLines,
                null,
                out lines,
                out bytesRead,
                out failureKind);

        /// <summary>Internal stream-opener seam for deterministic byte-bound tests (sparse/huge
        /// reported length, growth-after-length races, fault-after-N-bytes) without needing real
        /// multi-MiB files on disk. Production always calls the parameterless overload above,
        /// which binds the production <see cref="ProjectionFileSystemSafety.OpenRegularFileForRead(string)"/>
        /// no-follow opener — this seam is never reachable from any production code path.</summary>
        internal static bool TryReadBoundedFileLines(
            string patchFilePath,
            long maxBytes,
            int maxLines,
            Func<string, FileStream> openFileStreamForTest,
            out List<string> lines,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
        {
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            // Immutable per-file cap enforcement (#1965 L3 correction): no internal caller —
            // production OR test — may widen the byte budget past the immutable
            // MaxPatchDefinitionBytes (16 MiB) ceiling through this shared helper. Production
            // call sites already clamp their effective per-file cap to at most this constant
            // (see TryEnumeratePatchesBounded/BuildPatchInventoryBounded), but this defense checks
            // it here too, directly on the seam that actually allocates the read buffer — so a
            // future caller cannot accidentally (or intentionally) bypass the immutable bound.
            // This also removes the need for a "long.MaxValue" overflow-safe branch below: since
            // maxBytes can never exceed 16 MiB here, `maxBytes + 1` can never overflow a `long`.
            if (maxBytes > MaxPatchDefinitionBytes)
                throw new ArgumentOutOfRangeException(
                    nameof(maxBytes),
                    "maxBytes must not exceed the immutable MaxPatchDefinitionBytes per-file cap.");
            if (maxLines < 0) throw new ArgumentOutOfRangeException(nameof(maxLines));
            lines = null;
            bytesRead = 0;
            failureKind = BoundedPatchReadFailureKind.None;

            // #1965/#1936 information-disclosure remediation: the production default MUST open
            // the final path entry through the shared no-follow, exact-regular-file primitive
            // (never a plain FileStream constructor, which transparently follows a final
            // symlink) so a PATCH_*.txt replaced with a symlink to an arbitrary external file
            // can never have its target bytes read into an advisory record. Only a test may
            // substitute a different opener, for deterministic fault injection.
            Func<string, FileStream> open = openFileStreamForTest
                ?? ProjectionFileSystemSafety.OpenRegularFileForRead;

            using FileStream stream = open(patchFilePath);

            long length = stream.Length;
            if (length > maxBytes)
            {
                failureKind = BoundedPatchReadFailureKind.ResourceLimit;
                return false; // early Length reject — never allocate a buffer sized by an
                              // untrusted/sparse reported Length.
            }

            // Trusted size HINT only (Length has already passed the <= maxBytes check above, so
            // it is safe to use as a starting capacity) — NEVER the full byte budget. A file
            // that lies about its Length (sparse/huge, or grows afterward) simply falls back to
            // MemoryStream's own incremental doubling growth, proportional to bytes ACTUALLY
            // accepted, never to maxBytes.
            int initialCapacityHint = length > 0 && length <= int.MaxValue ? (int)length : 0;
            using var ms = new MemoryStream(initialCapacityHint);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
            try
            {
                long total = 0;
                while (true)
                {
                    // Never request more than the fixed chunk size, and never more than the
                    // remaining budget+1 (so a one-byte overrun is still detected without ever
                    // reading/allocating anything close to the full cap in one shot). maxBytes is
                    // now guaranteed <= MaxPatchDefinitionBytes (checked above), so `maxBytes + 1`
                    // can never overflow — the prior long.MaxValue special case is gone.
                    long remainingCapPlusOne = maxBytes + 1 - total;
                    if (remainingCapPlusOne <= 0)
                    {
                        failureKind = BoundedPatchReadFailureKind.ResourceLimit;
                        return false; // already at/over budget from a prior iteration.
                    }
                    int requestSize = (int)Math.Min(ReadChunkBytes, remainingCapPlusOne);


                    int read = stream.Read(chunk, 0, requestSize);
                    if (read <= 0)
                        break; // Genuine EOF — the shared no-follow opener guarantees a regular,
                               // synchronous FileStream, so a zero-byte read here can only mean
                               // true end-of-file, never a transient short read that could still
                               // yield more bytes on a later call (a short but POSITIVE read,
                               // handled below, is therefore never treated as EOF — the loop
                               // keeps requesting more).

                    total += read;
                    bytesRead = total; // incremental — visible even if the NEXT read throws.
                    if (total > length)
                    {
                        failureKind = BoundedPatchReadFailureKind.ContentChanged;
                        return false; // growth-after-Length / sparse-length lie; reject BEFORE
                                      // writing the surplus chunk into the accepted buffer. The
                                      // captured Length has already passed the <= maxBytes
                                      // precheck, so any in-loop over-budget read NECESSARILY
                                      // also proves growth beyond that captured Length and must
                                      // therefore classify ContentChanged first.
                    }
                    if (total > maxBytes)
                    {
                        failureKind = BoundedPatchReadFailureKind.ResourceLimit;
                        return false;
                    }
                    ms.Write(chunk, 0, read);
                }

                // #1965 length-drift correction: EOF was just reached (read <= 0 above) with
                // `total` guaranteed <= `length` by the in-loop check above — the only
                // remaining drift this equality check can still catch is a SHORT read relative
                // to the Length observed when the handle was opened (the file shrank/was
                // truncated between that Length read and EOF). Accepting a shorter file here
                // would let a silently-truncated/swapped file decode as if it were the exact
                // file whose Length was already validated against maxBytes above.
                if (total != length)
                {
                    failureKind = BoundedPatchReadFailureKind.ContentChanged;
                    return false; // premature EOF — bytes genuinely consumed before this
                                  // rejection remain visible through `bytesRead` above.
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            ms.Position = 0;
            using var reader = new StreamReader(ms);
            var result = new List<string>();
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (result.Count >= maxLines)
                {
                    lines = null;
                    failureKind = BoundedPatchReadFailureKind.ResourceLimit;
                    return false; // raw-line-cap breach — no partial line list is ever produced.
                }
                result.Add(line);
            }
            lines = result;
            return true;
        }

        /// <summary>
        /// A parsed key=value entry from a PATCH_*.txt file, where the key
        /// can have colon-separated parts (e.g. "BIN:0x2900").
        /// </summary>
        public class PatchParam
        {
            public string RawKey { get; set; } = "";
            public string Value { get; set; } = "";
            /// <summary>Key split by ':'.</summary>
            public string[] KeyParts { get; set; } = Array.Empty<string>();
            /// <summary>The keyword (first part before ':').</summary>
            public string Keyword => KeyParts.Length > 0 ? KeyParts[0] : "";
        }

        /// <summary>
        /// Parse a PATCH_*.txt file into a list of key=value params (for installation).
        /// </summary>
        public static List<PatchParam> ParsePatchParams(string patchFilePath)
        {
            var result = new List<PatchParam>();
            if (!File.Exists(patchFilePath)) return result;

            string[] lines = File.ReadAllLines(patchFilePath);
            foreach (string rawLine in lines)
            {
                if (TryParsePatchParamLine(rawLine, out PatchParam param))
                    result.Add(param);
            }
            return result;
        }

        static bool TryParsePatchParamLine(string rawLine, out PatchParam param)
        {
            param = null!;
            string line = rawLine.Trim();
            if (line.StartsWith("//")) return false;

            int sep = line.IndexOf('=');
            if (sep < 0) return false;

            string key = line.Substring(0, sep).Trim();
            param = new PatchParam
            {
                RawKey = key,
                Value = line.Substring(sep + 1).Trim(),
                KeyParts = key.Split(':'),
            };
            return true;
        }

        static string GetPatchType(IEnumerable<PatchParam> patchParams)
            => patchParams.LastOrDefault(p =>
                string.Equals(p.RawKey, "TYPE", StringComparison.OrdinalIgnoreCase))?.Value ?? "";

        /// <summary>
        /// Exporter-only bounded seam (#1936 review remediation, byte-bounded per #1965).
        /// Identical key=value parsing semantics to <see cref="ParsePatchParams"/> but the file
        /// is read through the shared byte-first
        /// <see cref="TryReadBoundedFileLines"/> helper
        /// instead of the unbounded eager <see cref="File.ReadAllLines(string)"/>/lazy
        /// <see cref="File.ReadLines(string)"/> APIs — a pathological patch file with one
        /// arbitrarily-large raw line can never be materialized before <paramref name="maxBytes"/>
        /// (bound to <see cref="MaxPatchDefinitionBytes"/>, further capped by the caller's
        /// remaining params-pass aggregate budget) rejects it. The same shared bounded read also
        /// fail-closes on detected length drift (growth/truncation between the captured
        /// <see cref="FileStream.Length"/> and EOF) with
        /// <paramref name="failureKind"/> = <see cref="BoundedPatchReadFailureKind.ContentChanged"/>,
        /// still without publishing partial params. <paramref name="maxEntries"/> is the
        /// pre-existing, still-independent parsed-entry advisory bound: once the accepted
        /// (within-<paramref name="maxBytes"/>) lines are parsed, accepting the next entry past
        /// <paramref name="maxEntries"/> returns <c>false</c> WITHOUT keeping any partial params
        /// list built so far — the whole record is degraded by the caller, never truncated.
        /// There is deliberately no <see cref="File.Exists(string)"/> precheck: a missing file
        /// is instead detected by <see cref="FileNotFoundException"/>/<see cref="DirectoryNotFoundException"/>
        /// raised while opening it, both of which resolve to a successful EMPTY result (matching
        /// the historical <c>File.Exists</c> contract exactly, without the TOCTOU gap a separate
        /// existence probe would reintroduce). The open itself goes through the shared no-follow,
        /// exact-regular-file primitive (<see cref="ProjectionFileSystemSafety.OpenRegularFileForRead(string)"/>),
        /// so a final PATCH_*.txt path entry that is a symlink/reparse point or any other
        /// non-plain-regular-file type is refused (a non-missing <see cref="IOException"/>) before
        /// a single byte of whatever it points at is ever read (#1965/#1936: closes the
        /// information-disclosure path where a plain <see cref="FileStream"/> would transparently
        /// follow such a link). On Browser, the platform-unsupported native check itself raises
        /// <see cref="PlatformNotSupportedException"/> instead of silently falling back to an
        /// unsafe open. Every OTHER expected filesystem/access exception
        /// (<see cref="IOException"/> and subclasses, <see cref="UnauthorizedAccessException"/>,
        /// <see cref="System.Security.SecurityException"/>, <see cref="NotSupportedException"/>)
        /// is left to PROPAGATE to the caller, which degrades the record with a stable, path-free
        /// reason instead of silently reporting "no params". Ancestor-directory symlinks earlier
        /// in <paramref name="patchFilePath"/> are outside this final-entry guarantee by design.
        /// </summary>
        internal static bool TryParsePatchParamsBounded(
            string patchFilePath,
            int maxEntries,
            long maxBytes,
            out List<PatchParam> result,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
            => TryParsePatchParamsBounded(
                patchFilePath,
                maxEntries,
                maxBytes,
                null,
                out result,
                out bytesRead,
                out failureKind);

        /// <summary>Internal stream-opener seam for deterministic fault-injection tests (e.g. a
        /// fault raised after N genuine bytes have already been read, proving the aggregate byte
        /// accounting in <paramref name="bytesRead"/> survives the exception). Production always
        /// calls the parameterless overload above, which binds the production
        /// <see cref="ProjectionFileSystemSafety.OpenRegularFileForRead(string)"/> no-follow
        /// opener — this seam is never reachable from any production code path.</summary>
        internal static bool TryParsePatchParamsBounded(
            string patchFilePath, int maxEntries, long maxBytes,
            Func<string, FileStream> openFileStreamForTest,
            out List<PatchParam> result,
            out long bytesRead,
            out BoundedPatchReadFailureKind failureKind)
        {
            result = new List<PatchParam>();
            bytesRead = 0;
            failureKind = BoundedPatchReadFailureKind.None;
            if (maxEntries < 0) throw new ArgumentOutOfRangeException(nameof(maxEntries));
            if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

            List<string> lines;
            try
            {
                if (!TryReadBoundedFileLines(
                        patchFilePath,
                        maxBytes,
                        MaxRawParamLines,
                        openFileStreamForTest,
                        out lines,
                        out bytesRead,
                        out failureKind))
                    return false; // byte-cap / raw-line-cap / content-drift breach — no partial
                                  // params list is ever produced.
            }
            catch (FileNotFoundException)
            {
                return true; // successful empty — matches the legacy File.Exists()==false contract.
            }
            catch (DirectoryNotFoundException)
            {
                return true; // successful empty — same contract for a missing parent directory.
            }

            // Parse into a LOCAL list first — `result` is only ever assigned the fully-parsed
            // list on a genuine success. On any maxEntries breach below, `result` is left as the
            // empty list constructed above (never partially populated), matching both the XML
            // doc contract ("WITHOUT mutating rec.Params further") and the caller's
            // whole-record/whole-inventory degradation expectation (#1965 L3 correction).
            var parsed = new List<PatchParam>();
            foreach (string rawLine in lines)
            {
                if (!TryParsePatchParamLine(rawLine, out PatchParam param)) continue;

                if (parsed.Count >= maxEntries)
                {
                    failureKind = BoundedPatchReadFailureKind.ResourceLimit;
                    return false; // would exceed the bound — `result` stays the empty list above,
                                  // the locally-built `parsed` list is simply discarded.
                }

                parsed.Add(param);
            }
            result = parsed;
            return true;
        }

        /// <summary>
        /// Apply a BIN-type patch to a ROM. Handles BIN: lines with fixed addresses
        /// and BIN:$FREEAREA with JUMP: hookup. Returns success/failure result.
        /// EA-type patches are not supported (require external assembler).
        /// </summary>
        /// <param name="rom">The ROM to modify.</param>
        /// <param name="patchFilePath">Full path to the PATCH_*.txt file.</param>
        /// <param name="undoData">Undo data for rollback support (optional, pass null to skip undo tracking).</param>
        public static PatchApplyResult ApplyPatch(ROM rom, string patchFilePath, Undo.UndoData? undoData = null)
        {
            if (rom == null) return PatchApplyResult.Fail("No ROM loaded.");
            if (!File.Exists(patchFilePath)) return PatchApplyResult.Fail("Patch file not found: " + patchFilePath);

            string patchDir = Path.GetDirectoryName(patchFilePath) ?? "";
            var allParams = ParsePatchParams(patchFilePath);

            // Check TYPE
            string type = GetPatchType(allParams);
            if (type == "EA")
                return PatchApplyResult.Fail("EA-type patches require an external assembler and are not yet supported in the Avalonia port.");

            if (!string.IsNullOrEmpty(type) && type != "BIN")
                return PatchApplyResult.Fail($"Unsupported patch type: '{type}'. Only BIN patches are currently supported.");

            // Collect BIN/JUMP entries in order
            var binKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BIN", "BINP", "BINAP", "BINF" };
            var actionParams = allParams.Where(p =>
                binKeywords.Contains(p.Keyword) || p.Keyword == "JUMP").ToList();

            if (actionParams.Count == 0)
                return PatchApplyResult.Fail("No BIN or JUMP entries found in patch file.");

            // Track where binary files are placed (for JUMP resolution)
            var binBlocks = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
            int totalBytesWritten = 0;

            // Collect regions that will be overwritten (for backup)
            var regionsToBackup = new List<(uint address, int length)>();

            try
            {
                // Process BIN entries first, then JUMP entries
                var binEntries = actionParams.Where(p => binKeywords.Contains(p.Keyword)).ToList();
                var jumpEntries = actionParams.Where(p => p.Keyword == "JUMP").ToList();

                // Pre-scan to collect regions for backup
                CollectBinRegions(rom, patchDir, binEntries, regionsToBackup);
                CollectJumpRegions(rom, patchDir, jumpEntries, binEntries, regionsToBackup);

                // Save backup of original bytes before writing
                if (regionsToBackup.Count > 0)
                    SaveBackup(rom, patchFilePath, regionsToBackup);

                foreach (var param in binEntries)
                {
                    var result = ApplyBinEntry(rom, patchDir, param, binBlocks, undoData);
                    if (!result.Success) return result;
                    totalBytesWritten += result.BytesWritten;
                }

                foreach (var param in jumpEntries)
                {
                    var result = ApplyJumpEntry(rom, patchDir, param, binBlocks, undoData);
                    if (!result.Success) return result;
                    totalBytesWritten += result.BytesWritten;
                }
            }
            catch (Exception ex)
            {
                return PatchApplyResult.Fail("Patch install error: " + ex.Message);
            }

            return PatchApplyResult.Ok(
                $"Patch installed successfully. {totalBytesWritten} bytes written.",
                totalBytesWritten);
        }

        /// <summary>
        /// Get the backup file path for a given patch file.
        /// Format: {patchDir}/.backup_{patchFileName}.txt
        /// </summary>
        public static string GetBackupFilePath(string patchFilePath)
        {
            string dir = Path.GetDirectoryName(patchFilePath) ?? "";
            string patchName = Path.GetFileNameWithoutExtension(patchFilePath);
            return Path.Combine(dir, $".backup_{patchName}.txt");
        }

        /// <summary>
        /// Check whether a backup file exists for the given patch, enabling uninstall.
        /// </summary>
        public static bool HasBackup(string patchFilePath)
        {
            return File.Exists(GetBackupFilePath(patchFilePath));
        }

        /// <summary>
        /// Save a backup of original ROM bytes before they are overwritten.
        /// Each record is one line: "0xADDRESS:LENGTH:HH HH HH ..."
        /// </summary>
        public static void SaveBackup(ROM rom, string patchFilePath, List<(uint address, int length)> regions)
        {
            string backupPath = GetBackupFilePath(patchFilePath);
            var lines = new List<string>();
            foreach (var (address, length) in regions)
            {
                if (address + length > rom.Data.Length)
                    continue;
                byte[] original = rom.getBinaryData(address, length);
                string hexBytes = string.Join(" ", original.Select(b => b.ToString("X2")));
                lines.Add($"0x{address:X}:{length}:{hexBytes}");
            }
            File.WriteAllLines(backupPath, lines);
        }

        /// <summary>
        /// Parse a backup file into a list of (address, originalBytes) records.
        /// Returns null if the file doesn't exist or is malformed.
        /// </summary>
        public static List<(uint address, byte[] data)>? ParseBackupFile(string backupPath)
        {
            if (!File.Exists(backupPath))
                return null;

            var records = new List<(uint address, byte[] data)>();
            string[] lines = File.ReadAllLines(backupPath);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                // Format: 0xADDRESS:LENGTH:HH HH HH ...
                string[] parts = line.Split(':', 3);
                if (parts.Length < 3) return null;

                string addrStr = parts[0].Trim();
                if (addrStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    addrStr = addrStr.Substring(2);
                if (!uint.TryParse(addrStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
                    return null;

                if (!int.TryParse(parts[1].Trim(), out int length) || length < 0)
                    return null;

                byte[] data = ParseBackupHexBytes(parts[2].Trim());
                if (data.Length != length) return null;

                records.Add((addr, data));
            }
            return records;
        }

        /// <summary>Parse space-separated hex bytes (no 0x prefix) from backup file.</summary>
        static byte[] ParseBackupHexBytes(string hexStr)
        {
            if (string.IsNullOrWhiteSpace(hexStr))
                return Array.Empty<byte>();

            string[] tokens = hexStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var result = new byte[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i]))
                    return Array.Empty<byte>();
            }
            return result;
        }

        /// <summary>
        /// Uninstall a patch by restoring original ROM bytes from a backup file.
        /// The backup file is created during patch installation. The backup file is
        /// PRESERVED across uninstall so that undoing the uninstall (which reapplies the
        /// patched ROM bytes) leaves the patch uninstallable again; a re-install overwrites it.
        /// </summary>
        public static PatchApplyResult UninstallPatch(ROM rom, string patchFilePath)
            => UninstallPatch(rom, patchFilePath, null);

        /// <summary>
        /// Uninstall a patch by restoring original ROM bytes from a backup file.
        /// The backup file is created during patch installation.
        /// When <paramref name="undoData"/> is supplied, every restored ROM region is
        /// recorded into it (via the recording <c>write_range</c> overload) BEFORE the
        /// write so the caller can <c>Push</c> on success or <c>Rollback</c> to byte-
        /// identity on failure — even a partial restore (the records written before a
        /// later record fails validation) is captured. The backup file is PRESERVED
        /// across uninstall so that undoing the uninstall (which reapplies the patched
        /// ROM bytes) leaves the patch uninstallable again; a re-install overwrites it.
        /// </summary>
        public static PatchApplyResult UninstallPatch(ROM rom, string patchFilePath, Undo.UndoData? undoData)
        {
            if (rom == null) return PatchApplyResult.Fail("No ROM loaded.");
            if (!File.Exists(patchFilePath)) return PatchApplyResult.Fail("Patch file not found.");

            string backupPath = GetBackupFilePath(patchFilePath);
            if (!File.Exists(backupPath))
            {
                return PatchApplyResult.Fail(
                    "No backup file found for this patch. Uninstall is only possible for patches " +
                    "installed with a backup (SaveBackup) (which saves a backup automatically). " +
                    "Restore from a ROM backup instead.");
            }

            var records = ParseBackupFile(backupPath);
            if (records == null || records.Count == 0)
            {
                return PatchApplyResult.Fail("Backup file is empty or malformed.");
            }

            try
            {
                int totalBytes = 0;
                foreach (var (address, data) in records)
                {
                    if (address + data.Length > rom.Data.Length)
                    {
                        return PatchApplyResult.Fail(
                            $"Backup record at 0x{address:X} ({data.Length} bytes) exceeds ROM size. " +
                            "The ROM may have been modified since the patch was installed.");
                    }
                    if (undoData != null)
                        rom.write_range(address, data, undoData);
                    else
                        rom.write_range(address, data);
                    totalBytes += data.Length;
                }

                // Backup is intentionally PRESERVED across uninstall: undoing the uninstall reapplies the patched ROM bytes, and keeping the backup lets the user uninstall again. It is harmless while not installed (the GUI gates uninstall on Status==Installed, though UninstallPatch itself does not guard on status and is safe to call idempotently) and a re-install overwrites it via SaveBackup.

                return PatchApplyResult.Ok(
                    $"Patch uninstalled successfully. {totalBytes} bytes restored.",
                    totalBytes);
            }
            catch (Exception ex)
            {
                return PatchApplyResult.Fail("Patch uninstall error: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // Clean-ROM-diff uninstall (#1462)
        //
        // Ports the WinForms PatchForm.UninstallPatchInner engine so a BIN
        // patch installed in a PRIOR session (no per-patch backup file) — the
        // common case for a freshly loaded ROM that already contains patches —
        // can still be uninstalled by diffing against a user-supplied patch-free
        // ("clean") ROM. Mirrors PatchForm.UnInstallPatch ->
        // PatchFormUninstallDialogForm -> UninstallPatchInner.
        //
        // Scope: fixed-address BIN entries + JUMP injection points (the same
        // regions the install path tracks for SaveBackup).
        //
        // CORRECTION-ONLY RESTORE (review #1462): the engine restores ONLY the
        // bytes that actually DIFFER between the current (patched) ROM and the
        // clean ROM inside each traced region. This is the key safety property —
        // an over-estimated region/JUMP length never clobbers an adjacent unrelated
        // patch, because bytes that already match the clean ROM are written as
        // no-ops; and it faithfully removes exactly this patch's edits.
        //
        // HONEST PARTIAL REPORTING: entries we cannot trace from the patch text
        // alone — $FREEAREA payloads, $GREP/$FGREP/$XGREP/pointer-deref address
        // forms, and EA patches (TYPE=EA) — are surfaced as an untraceable count
        // so the result is reported as a PARTIAL/incomplete uninstall, never an
        // over-claimed success. WinForms' GREP/mask trace (TraceBINPatchedMapping)
        // and CalcAutoLength/StripROM remain WF-only (documented non-goals).
        // ------------------------------------------------------------------

        /// <summary>
        /// A traced patch region: where the patch wrote, how many bytes, and (for fixed-address
        /// BIN entries) the patch's OWN recorded bytes — the content of the patch's <c>.bin</c>
        /// sidecar, i.e. WinForms <c>BinMapping.bin</c>. <see cref="PatchBytes"/> is <c>null</c>
        /// for JUMP regions whose injected code we cannot synthesise from the patch text alone.
        /// </summary>
        public readonly struct PatchRegion
        {
            public readonly uint Address;
            public readonly int Length;
            public readonly byte[]? PatchBytes; // null when the patch's own bytes are unknown (JUMP)
            public PatchRegion(uint address, int length, byte[]? patchBytes)
            {
                Address = address; Length = length; PatchBytes = patchBytes;
            }
        }

        /// <summary>
        /// Collect the fixed-address regions a BIN patch touches WITH each region's own patch
        /// bytes (the <c>.bin</c> sidecar content = WinForms <c>BinMapping.bin</c>), and report
        /// how many entries could NOT be traced from the patch text alone (EA patches, $FREEAREA
        /// payloads, $GREP/macro/pointer address forms). Returns an empty list (never null) for
        /// non-BIN/EA patches or on parse failure.
        /// </summary>
        public static List<PatchRegion> CollectPatchRegionsWithBytes(
            ROM rom, string patchFilePath, out int untraceableCount)
            => CollectPatchRegionsWithBytesCore(rom, patchFilePath, out untraceableCount, File.Exists, File.ReadAllBytes);

        internal static List<PatchRegion> CollectPatchRegionsWithBytesForTest(ROM rom, string patchFilePath,
            out int untraceableCount, Func<string, bool> fileExists, Func<string, byte[]> readBytes)
            => CollectPatchRegionsWithBytesCore(rom, patchFilePath, out untraceableCount, fileExists, readBytes);

        static List<PatchRegion> CollectPatchRegionsWithBytesCore(ROM rom, string patchFilePath,
            out int untraceableCount, Func<string, bool> fileExists, Func<string, byte[]> readBytes)
        {
            untraceableCount = 0;
            var regions = new List<PatchRegion>();
            if (rom == null || !fileExists(patchFilePath)) return regions;

            var allParams = ParsePatchParams(patchFilePath);
            string type = GetPatchType(allParams);
            // BIN patches have a portable fixed-address region map. An EMPTY/missing TYPE is
            // treated as BIN — matching the Avalonia Patch Manager's CanInstall/CanUninstall
            // convention (string.IsNullOrEmpty(Type)) so legacy BIN patches that omit TYPE= are
            // still uninstallable. EA tracing is WF-only.
            bool isBin = string.IsNullOrEmpty(type) || type == "BIN";
            if (!isBin)
            {
                // Count every action-bearing line as untraceable so EA is reported as not supported.
                untraceableCount = allParams.Count(p =>
                    p.Keyword == "ORG" || p.Keyword == "ASM" || p.Keyword == "PROCS" || p.Keyword == "JUMP" ||
                    p.Keyword == "BIN" || p.Keyword == "BINP" || p.Keyword == "BINAP" || p.Keyword == "BINF");
                if (untraceableCount == 0) untraceableCount = 1; // unknown type: never claim a clean trace
                return regions;
            }

            string patchDir = Path.GetDirectoryName(patchFilePath) ?? "";
            var binKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BIN", "BINP", "BINAP", "BINF" };
            var binEntries = allParams.Where(p => binKeywords.Contains(p.Keyword)).ToList();
            var jumpEntries = allParams.Where(p => p.Keyword == "JUMP").ToList();

            // Fixed-address BIN entries: record the address AND the patch's own bytes (the .bin
            // file content) so the patch-absence check can compare candidate-vs-patch-bytes
            // exactly like WinForms SearchContainThisPatchBy (memcmp against t.bin). $FREEAREA /
            // $GREP / non-literal address forms cannot be traced from text -> untraceable.
            foreach (var param in binEntries)
            {
                string addrPart = param.KeyParts.Length > 1 ? param.KeyParts[1] : "";
                string filePath = Path.Combine(patchDir, param.Value);

                if (addrPart.StartsWith("$", StringComparison.OrdinalIgnoreCase)) { untraceableCount++; continue; }
                uint addr = ParseHexAddress(addrPart);
                if (addr == U.NOT_FOUND) { untraceableCount++; continue; }
                if (!fileExists(filePath)) { untraceableCount++; continue; }

                byte[] binData;
                try { binData = readBytes(filePath); }
                catch { untraceableCount++; continue; }
                if (binData.Length == 0) continue;
                if (addr + binData.Length > rom.Data.Length) continue;

                regions.Add(new PatchRegion(addr, binData.Length, binData));
            }

            // JUMP entries: the injected code is synthesised at install from register/offset,
            // which we cannot reproduce from text alone. Keep the region (with its estimated
            // length) for the restore-coverage size gate, but PatchBytes=null so the
            // patch-absence check skips it (we can't assert "still contains the patch" for it).
            foreach (var param in jumpEntries)
            {
                if (param.KeyParts.Length < 2) continue;
                uint injectionAddr = ParseHexAddress(param.KeyParts[1]);
                if (injectionAddr == U.NOT_FOUND) { untraceableCount++; continue; }

                int jumpSize = 8; // $NONE/$B/$BL=4, register-based=8 (conservative)
                if (param.KeyParts.Length > 2)
                {
                    string regStr = param.KeyParts[2];
                    if (regStr == "$NONE" || regStr == "$BL" || regStr == "$B") jumpSize = 4;
                }
                if (injectionAddr + jumpSize <= rom.Data.Length)
                    regions.Add(new PatchRegion(injectionAddr, jumpSize, null));
            }

            return regions;
        }

        /// <summary>
        /// Backward-compatible (address, length) view of <see cref="CollectPatchRegionsWithBytes"/>.
        /// </summary>
        public static List<(uint address, int length)> CollectPatchRegions(
            ROM rom, string patchFilePath, out int untraceableCount)
        {
            var rich = CollectPatchRegionsWithBytes(rom, patchFilePath, out untraceableCount);
            var regions = new List<(uint address, int length)>(rich.Count);
            foreach (var r in rich) regions.Add((r.Address, r.Length));
            return regions;
        }

        /// <summary>Overload without the untraceable-count out parameter.</summary>
        public static List<(uint address, int length)> CollectPatchRegions(ROM rom, string patchFilePath)
            => CollectPatchRegions(rom, patchFilePath, out _);

        /// <summary>
        /// Faithful port of WinForms <c>PatchFormUninstallDialogForm.SearchContainThisPatchBy</c>:
        /// returns <c>true</c> if <paramref name="candidateRomBytes"/> still holds the PATCH'S OWN
        /// bytes (<see cref="PatchRegion.PatchBytes"/> == WinForms <c>t.bin</c>) at ANY traced
        /// region — i.e. the candidate STILL CONTAINS THE PATCH and must be rejected as not-clean.
        /// Regions whose patch bytes are unknown (JUMP, PatchBytes==null) are skipped — we cannot
        /// assert containment for them. Returns <c>false</c> when no region's own bytes are present
        /// (a genuine pre-patch ROM, vanilla OR otherwise-modified, even with edits elsewhere).
        /// </summary>
        public static bool RomContainsPatch(List<PatchRegion> regions, byte[] candidateRomBytes)
        {
            if (regions == null || candidateRomBytes == null) return false;
            foreach (var r in regions)
            {
                byte[]? patchBytes = r.PatchBytes;
                if (patchBytes == null || patchBytes.Length == 0) continue; // unknown own-bytes -> skip
                if (r.Address + patchBytes.Length > candidateRomBytes.Length) continue;

                bool match = true;
                for (int i = 0; i < patchBytes.Length; i++)
                {
                    if (candidateRomBytes[r.Address + i] != patchBytes[i]) { match = false; break; }
                }
                if (match)
                    return true; // candidate still contains the patch's own bytes -> not clean
            }
            return false;
        }

        /// <summary>
        /// Backward-compatible overload. Builds patch-byte info from the loaded ROM when only
        /// (address, length) regions are supplied: each region's patch bytes are taken from the
        /// CURRENT ROM (best-effort), then routed through the faithful
        /// <see cref="RomContainsPatch(List{PatchRegion}, byte[])"/>. Prefer the
        /// <see cref="PatchRegion"/> overload, which carries the patch's true recorded bytes.
        /// </summary>
        public static bool RomContainsPatch(ROM rom, List<(uint address, int length)> regions, byte[] candidateRomBytes)
        {
            if (rom == null || regions == null || candidateRomBytes == null) return false;
            var rich = new List<PatchRegion>(regions.Count);
            foreach (var (address, length) in regions)
            {
                if (length <= 0 || address + length > rom.Data.Length) continue;
                rich.Add(new PatchRegion(address, length, rom.getBinaryData(address, length)));
            }
            return RomContainsPatch(rich, candidateRomBytes);
        }

        /// <summary>
        /// Returns <c>true</c> when <paramref name="candidateRomBytes"/> matches the
        /// ROM's pristine <c>orignal_crc32</c> (a vanilla, never-modified ROM).
        /// Advisory only — a clean-but-otherwise-modified pre-patch ROM is also a
        /// valid uninstall source (the real gate is <see cref="RomContainsPatch"/>).
        /// </summary>
        public static bool IsVanillaRom(ROM rom, byte[] candidateRomBytes)
        {
            if (rom == null || candidateRomBytes == null) return false;
            uint orignalCrc32 = rom.RomInfo.orignal_crc32;
            if (orignalCrc32 == 0) return false; // unknown baseline -> can't assert vanilla
            var crc32 = new U.CRC32();
            return crc32.Calc(candidateRomBytes) == orignalCrc32;
        }

        /// <summary>
        /// Compatibility gate (review #1462): the candidate clean ROM must be the SAME
        /// game/version family as the loaded ROM. Compares the 4-byte GBA header game
        /// code at 0xAC and the 12-byte game title at 0xA0. A wrong game/version that
        /// merely lacks the patch bytes must fail closed BEFORE any mutation.
        /// </summary>
        public static bool IsCompatibleRom(ROM rom, byte[] candidateRomBytes)
        {
            if (rom == null || candidateRomBytes == null) return false;
            // GBA cartridge header: game title @0xA0 (12 bytes) + game code @0xAC (4 bytes).
            const uint HEADER_END = 0xB0;
            if (rom.Data.Length < HEADER_END || candidateRomBytes.Length < HEADER_END) return false;
            for (uint a = 0xA0; a < HEADER_END; a++)
            {
                if (rom.Data[a] != candidateRomBytes[a]) return false;
            }
            return true;
        }

        /// <summary>
        /// Uninstall a BIN patch by diff-restoring its touched regions from a
        /// user-supplied patch-free ("clean") ROM. Port of WinForms
        /// <c>PatchForm.UninstallPatchInner</c> (correction-only restore).
        ///
        /// Validation (ALL before any mutation):
        ///  * the patch must expose at least one fixed-address region;
        ///  * the clean ROM must be header-compatible (<see cref="IsCompatibleRom"/>) —
        ///    fail closed for a wrong game/version;
        ///  * the clean ROM must COVER every traced region (preflight size gate) — fail closed
        ///    for a truncated / pre-expansion ROM rather than silently skipping regions;
        ///  * the clean ROM must NOT still contain the patch's OWN bytes
        ///    (<see cref="RomContainsPatch(List{PatchRegion}, byte[])"/>, faithful to WF
        ///    SearchContainThisPatchBy's memcmp-against-t.bin).
        ///
        /// Only bytes that DIFFER between the current ROM and the clean ROM inside each
        /// traced region are written (so an over-estimated length is a safe no-op).
        /// When <paramref name="undoData"/> is supplied every restored region is recorded
        /// so the caller can <c>Push</c> on success or <c>Rollback</c> to byte-identity on failure.
        /// If the patch has untraceable entries the result is reported as PARTIAL, never
        /// an over-claimed full success.
        /// </summary>
        public static PatchApplyResult UninstallPatchWithCleanRom(
            ROM rom, string patchFilePath, byte[] cleanRomBytes, Undo.UndoData? undoData)
        {
            if (rom == null) return PatchApplyResult.Fail("No ROM loaded.");
            if (!File.Exists(patchFilePath)) return PatchApplyResult.Fail("Patch file not found.");
            if (cleanRomBytes == null || cleanRomBytes.Length == 0)
                return PatchApplyResult.Fail("Clean ROM is empty or could not be read.");

            var regions = CollectPatchRegionsWithBytes(rom, patchFilePath, out int untraceableCount);
            if (regions.Count == 0)
            {
                return PatchApplyResult.Fail(
                    "This patch exposes no fixed-address regions to uninstall via a clean ROM. " +
                    "EA patches and patches that use only $FREEAREA/$GREP regions need the WinForms patch manager.");
            }

            // Compatibility gate: wrong game/version must fail closed before any mutation.
            if (!IsCompatibleRom(rom, cleanRomBytes))
            {
                return PatchApplyResult.Fail(
                    "The selected ROM is a different game or version (GBA header mismatch). " +
                    "Choose the patch-free ROM that matches the loaded game.");
            }

            // PREFLIGHT SIZE GATE (before any mutation): the clean ROM must cover EVERY traced
            // region. A truncated / pre-expansion clean ROM would otherwise silently skip whole
            // regions yet still report success, leaving the patch effectively installed.
            foreach (var r in regions)
            {
                if (r.Length <= 0) continue;
                if (r.Address + r.Length > cleanRomBytes.Length)
                {
                    return PatchApplyResult.Fail(
                        $"The selected clean ROM is too small: it does not cover the patched region at " +
                        $"0x{r.Address:X} ({r.Length} bytes). It may predate a ROM expansion. " +
                        "Choose a patch-free ROM at least as large as the current one.");
                }
            }

            // Patch-absence check: the picked ROM must NOT still contain the patch's OWN bytes.
            if (RomContainsPatch(regions, cleanRomBytes))
            {
                return PatchApplyResult.Fail(
                    "The selected ROM still contains this patch. Choose the ROM from BEFORE the patch was installed.");
            }

            try
            {
                int totalBytes = 0;
                foreach (var r in regions)
                {
                    uint address = r.Address;
                    int length = r.Length;
                    if (length <= 0) continue;
                    // Clamp to bytes that exist in BOTH ROMs. (The preflight gate already
                    // guaranteed the clean ROM covers each region; the current ROM is the
                    // only remaining bound and matches in the common case.)
                    int safeLen = length;
                    if (address + safeLen > rom.Data.Length)
                        safeLen = (int)(rom.Data.Length - address);
                    if (address + safeLen > cleanRomBytes.Length)
                        safeLen = (int)(cleanRomBytes.Length - address);
                    if (safeLen <= 0) continue;

                    // CORRECTION-ONLY: restore only the bytes that actually differ. Identical
                    // bytes are skipped, so an over-estimated length never clobbers neighbours.
                    // Consecutive differing bytes are batched into a single write_range so a
                    // large region produces few undo records / write calls (not one per byte).
                    byte[] current = rom.getBinaryData(address, safeLen);
                    int run = 0; // length of the current differing run
                    for (int i = 0; i <= safeLen; i++)
                    {
                        bool differs = i < safeLen && cleanRomBytes[address + i] != current[i];
                        if (differs)
                        {
                            run++;
                            continue;
                        }
                        if (run > 0)
                        {
                            uint runStart = address + (uint)(i - run);
                            byte[] block = new byte[run];
                            Array.Copy(cleanRomBytes, runStart, block, 0, run);
                            if (undoData != null)
                                rom.write_range(runStart, block, undoData);
                            else
                                rom.write_range(runStart, block);
                            totalBytes += run;
                            run = 0;
                        }
                    }
                }

                if (untraceableCount > 0)
                {
                    return PatchApplyResult.Ok(
                        $"Patch partially uninstalled (clean-ROM diff): {totalBytes} bytes restored. " +
                        $"{untraceableCount} entr{(untraceableCount == 1 ? "y" : "ies")} could not be traced " +
                        "(EA/$FREEAREA/$GREP); a few hundred bytes of residual data may remain. " +
                        "Use the WinForms patch manager for a complete uninstall.",
                        totalBytes);
                }

                return PatchApplyResult.Ok(
                    $"Patch uninstalled successfully (clean-ROM diff). {totalBytes} bytes restored.",
                    totalBytes);
            }
            catch (Exception ex)
            {
                return PatchApplyResult.Fail("Patch uninstall error: " + ex.Message);
            }
        }

        /// <summary>Process a single BIN: entry.</summary>
        static PatchApplyResult ApplyBinEntry(ROM rom, string patchDir, PatchParam param,
            Dictionary<string, uint> binBlocks, Undo.UndoData? undoData)
        {
            // param.KeyParts: ["BIN", "0x2900"] or ["BIN", "$FREEAREA"] or ["BIN", "$FREEAREA", "1"]
            // param.Value: filename (e.g. "Arena_NotDie.dmp" or "2900.bin")
            string addrPart = param.KeyParts.Length > 1 ? param.KeyParts[1] : "";
            string filename = param.Value;
            string filePath = Path.Combine(patchDir, filename);

            if (!File.Exists(filePath))
                return PatchApplyResult.Fail($"Binary file not found: {filePath}");

            byte[] binData = File.ReadAllBytes(filePath);
            if (binData.Length == 0)
                return PatchApplyResult.Fail($"Binary file is empty: {filePath}");

            uint addr = ResolveBinAddress(rom, addrPart, (uint)binData.Length);
            if (addr == U.NOT_FOUND)
                return PatchApplyResult.Fail($"Could not resolve address '{addrPart}' for {filename}.");

            // Expand ROM if needed
            if (addr + binData.Length > rom.Data.Length)
            {
                bool ok = rom.write_resize_data((uint)(addr + binData.Length));
                if (!ok)
                    return PatchApplyResult.Fail($"Cannot expand ROM to fit data at 0x{addr:X}.");
            }

            // Write data
            if (undoData != null)
                rom.write_range(addr, binData, undoData);
            else
                rom.write_range(addr, binData);

            // Track placement for JUMP resolution
            binBlocks[filePath] = addr;

            return PatchApplyResult.Ok("", binData.Length);
        }

        /// <summary>Process a single JUMP: entry.</summary>
        static PatchApplyResult ApplyJumpEntry(ROM rom, string patchDir, PatchParam param,
            Dictionary<string, uint> binBlocks, Undo.UndoData? undoData)
        {
            // JUMP:0x032984:$r3=Arena_NotDie.dmp
            // KeyParts: ["JUMP", "0x032984", "$r3"]
            // Value: "Arena_NotDie.dmp"
            if (param.KeyParts.Length < 2)
                return PatchApplyResult.Fail("JUMP entry missing address.");

            string addrStr = param.KeyParts[1];
            uint injectionAddr = ParseHexAddress(addrStr);
            if (injectionAddr == U.NOT_FOUND)
                return PatchApplyResult.Fail($"Invalid JUMP injection address: {addrStr}");

            if (injectionAddr % 2 != 0)
                return PatchApplyResult.Fail($"JUMP address 0x{injectionAddr:X} must be even.");

            // Parse register (default r3)
            uint useReg = 3;
            bool isNone = false, isBL = false, isB = false;
            if (param.KeyParts.Length > 2)
            {
                string regStr = param.KeyParts[2];
                if (regStr == "$NONE") isNone = true;
                else if (regStr == "$BL") isBL = true;
                else if (regStr == "$B") isB = true;
                else if (regStr.StartsWith("$r") && regStr.Length >= 3 &&
                         regStr[2] >= '0' && regStr[2] <= '7')
                    useReg = (uint)(regStr[2] - '0');
                else
                    return PatchApplyResult.Fail($"Invalid JUMP register: {regStr}");
            }

            // Parse optional address offset
            int addOffset = 0;
            if (param.KeyParts.Length > 3)
            {
                string offsetStr = param.KeyParts[3];
                if (!string.IsNullOrEmpty(offsetStr))
                {
                    if (offsetStr[0] == '+')
                        addOffset = (int)U.atoi0x(offsetStr.Substring(1));
                    else if (offsetStr[0] == '-')
                        addOffset = -(int)U.atoi0x(offsetStr.Substring(1));
                    else
                        addOffset = (int)U.atoi0x(offsetStr);
                }
            }

            // Resolve jump target: look up where the BIN file was placed
            string filename = param.Value;
            string filePath = Path.Combine(patchDir, filename);
            uint routineAddr;
            if (binBlocks.TryGetValue(filePath, out uint blockAddr))
            {
                routineAddr = (uint)((int)blockAddr + addOffset);
            }
            else
            {
                // Try as a hex address
                routineAddr = ParseHexAddress(filename);
                if (routineAddr == U.NOT_FOUND)
                    return PatchApplyResult.Fail($"JUMP target file '{filename}' was not placed by a BIN entry.");
            }

            // Generate jump code
            byte[] jumpCode;
            if (isNone)
            {
                jumpCode = new byte[4];
                U.write_p32(jumpCode, 0, routineAddr);
            }
            else if (isB)
            {
                jumpCode = DisassemblerTrumb.MakeBJump(injectionAddr, routineAddr);
            }
            else if (isBL)
            {
                jumpCode = DisassemblerTrumb.MakeBLJump(injectionAddr, routineAddr);
            }
            else
            {
                jumpCode = DisassemblerTrumb.MakeInjectJump(injectionAddr, routineAddr, useReg);
            }

            if (jumpCode == null || jumpCode.Length == 0)
                return PatchApplyResult.Fail("Failed to generate jump code.");

            // Write jump code
            if (undoData != null)
                rom.write_range(injectionAddr, jumpCode, undoData);
            else
                rom.write_range(injectionAddr, jumpCode);

            return PatchApplyResult.Ok("", jumpCode.Length);
        }

        /// <summary>
        /// Resolve a BIN address string. Handles "0xADDR" (fixed) and "$FREEAREA" (auto-find).
        /// </summary>
        static uint ResolveBinAddress(ROM rom, string addrStr, uint dataSize)
        {
            if (string.IsNullOrEmpty(addrStr)) return U.NOT_FOUND;

            // $FREEAREA or $FREEAREA:1 etc.
            if (addrStr.StartsWith("$FREEAREA", StringComparison.OrdinalIgnoreCase))
            {
                return FindFreeSpace(rom, dataSize);
            }

            return ParseHexAddress(addrStr);
        }

        /// <summary>Parse a hex address string like "0x2900" or "2900".</summary>
        static uint ParseHexAddress(string addrStr)
        {
            if (string.IsNullOrEmpty(addrStr)) return U.NOT_FOUND;

            string hex = addrStr.Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);

            if (uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint addr))
                return addr;

            return U.NOT_FOUND;
        }

        /// <summary>
        /// Find free space in the ROM for patch data. Searches for runs of 0x00 or 0xFF.
        /// Adds padding for safety (16 bytes lead-in).
        /// </summary>
        public static uint FindFreeSpace(ROM rom, uint dataSize)
        {
            const uint LEAD_IN = 16;
            uint needSize = U.Padding4(LEAD_IN + dataSize);

            // First try the ROM's built-in search
            uint addr = rom.FindFreeSpace(0x100, needSize);
            if (addr != U.NOT_FOUND)
                return addr + LEAD_IN;

            // Fallback: append at end of ROM
            uint endAddr = U.Padding4((uint)rom.Data.Length);
            if (endAddr + dataSize < 0x02000000) // 32MB limit
                return endAddr;

            return U.NOT_FOUND;
        }

        /// <summary>
        /// Pre-scan BIN entries to collect (address, length) regions for backup.
        /// Skips $FREEAREA entries since those write to free space (no valuable data to back up).
        /// </summary>
        static void CollectBinRegions(ROM rom, string patchDir, List<PatchParam> binEntries,
            List<(uint address, int length)> regions)
        {
            foreach (var param in binEntries)
            {
                string addrPart = param.KeyParts.Length > 1 ? param.KeyParts[1] : "";
                string filename = param.Value;
                string filePath = Path.Combine(patchDir, filename);

                // Skip $FREEAREA — those write to empty space, nothing to back up
                if (addrPart.StartsWith("$FREEAREA", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!File.Exists(filePath)) continue;

                uint addr = ParseHexAddress(addrPart);
                if (addr == U.NOT_FOUND) continue;

                int dataLen = (int)new FileInfo(filePath).Length;
                if (dataLen > 0 && addr + dataLen <= rom.Data.Length)
                    regions.Add((addr, dataLen));
            }
        }

        /// <summary>
        /// Pre-scan JUMP entries to collect injection-point regions for backup.
        /// Jump code is typically 4-8 bytes at the injection address.
        /// We use a conservative 8-byte estimate since exact size depends on register choice.
        /// </summary>
        static void CollectJumpRegions(ROM rom, string patchDir, List<PatchParam> jumpEntries,
            List<PatchParam> binEntries, List<(uint address, int length)> regions)
        {
            foreach (var param in jumpEntries)
            {
                if (param.KeyParts.Length < 2) continue;

                uint injectionAddr = ParseHexAddress(param.KeyParts[1]);
                if (injectionAddr == U.NOT_FOUND) continue;

                // Estimate jump code size: $NONE=4, $B/$BL=4, register-based=8
                int jumpSize = 8;
                if (param.KeyParts.Length > 2)
                {
                    string regStr = param.KeyParts[2];
                    if (regStr == "$NONE" || regStr == "$BL" || regStr == "$B")
                        jumpSize = 4;
                }

                if (injectionAddr + jumpSize <= rom.Data.Length)
                    regions.Add((injectionAddr, jumpSize));
            }
        }
    }
}
