using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Platform.Storage;
using FEBuilderGBA;
using FEBuilderGBA.Avalonia.Services;

namespace FEBuilderGBA.Avalonia.Dialogs
{
    internal enum MacExternalToolPickerResultKind
    {
        Selected,
        Cancelled,
        Fallback,
    }

    internal readonly record struct MacExternalToolPickerResult(
        MacExternalToolPickerResultKind Kind,
        string? Path,
        string Message);

    /// <summary>
    /// Helper for file open/save dialogs using Avalonia 11 StorageProvider API.
    /// All user-visible strings are wrapped with R._() for i18n.
    /// </summary>
    public static class FileDialogHelper
    {
        // ===================================================================
        // SAF (Storage Access Framework) bridge — #1639
        //
        // On Android the picked IStorageFile is a content:// document with NO
        // local filesystem path: TryGetLocalPath() returns null. The historic
        // path-returning helpers below therefore collapsed a valid pick to null
        // — which every caller reads as "cancelled" — so import/export silently
        // failed. #1124 fixed the ROM open/save by switching to OpenReadAsync /
        // OpenWriteAsync streams; this bridge does the same for every other
        // editor flow.
        //
        // Two strategies, both centralized here so call sites need not change:
        //   * OPEN helpers that return a path: when there is no local path, copy
        //     the SAF stream into a temp file (extension preserved) and return
        //     THAT path. Consumers read it synchronously, so a best-effort temp
        //     is sufficient.
        //   * SAVE helpers: a *Via overload runs the caller's path-based writer
        //     on a temp file then streams the temp bytes back into the SAF
        //     target via OpenWriteAsync (truncating), so a previously-larger
        //     document leaves no stale trailing bytes.
        // ===================================================================

        /// <summary>
        /// Copy the bytes of a picked <see cref="IStorageFile"/> into a freshly
        /// created temp file (the original extension is preserved so format
        /// sniffing by extension still works) and return that local path. The
        /// caller is expected to read the file synchronously right after; the
        /// temp is left for the OS temp sweeper (best-effort, mirrors how the
        /// previous code never owned the picked file's lifetime either).
        /// </summary>
        static Task<string?> CopyToTempForReadAsync(IStorageFile file)
            => CopyStreamToTempAsync(file.OpenReadAsync, file.Name);

        /// <summary>
        /// Stream-based core of the SAF read bridge (testable without an
        /// <see cref="IStorageFile"/>): copy <c>openRead</c>'s bytes
        /// into a temp file whose extension matches <c>name</c>, and
        /// return that path. #1639.
        /// </summary>
        // Read-temps older than this are eligible for the best-effort sweep. A
        // generous window so a DEFERRED flow (browse now, Import/Reduce later —
        // possibly after picking other SAF files in between) keeps its temp:
        // only genuinely stale temps from prior sessions / abandoned picks go.
        static readonly TimeSpan ReadTempMaxAge = TimeSpan.FromHours(12);

        internal static async Task<string?> CopyStreamToTempAsync(Func<Task<Stream>> openRead, string? name)
        {
            // Read-temp files must outlive this call (deferred flows read them on a
            // later button click), so they can't be deleted here. To avoid an
            // unbounded leak (review #1639 finding 5) WITHOUT breaking deferred
            // flows (review follow-up), each new read-temp best-effort sweeps only
            // read-temps OLDER THAN ReadTempMaxAge in this session's dedicated
            // subdirectory — never a temp a current deferred flow might still use.
            // The cost is bounded (one dir listing) and never throws.
            string dir = ReadTempDir();
            SweepReadTemps(dir);
            string ext = Path.GetExtension(name ?? "");
            string tempPath = Path.Combine(dir,
                "febgba_read_" + Guid.NewGuid().ToString("N") + ext);
            await using (var src = await openRead())
            await using (var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await src.CopyToAsync(dst);
            }
            return tempPath;
        }

        static string ReadTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "febgba_saf_reads");
            try { Directory.CreateDirectory(dir); } catch { /* fall back to temp root below */ }
            return Directory.Exists(dir) ? dir : Path.GetTempPath();
        }

        // Best-effort age-based cleanup of read-temp copies from EARLIER picks.
        // Only temps last-written more than <see cref="ReadTempMaxAge"/> ago are
        // deleted, so a deferred flow's recent temp (kept for a later Import /
        // Reduce, even across other SAF picks) is never removed. A file still open
        // (a flow mid-read) cannot be deleted on Windows and is simply skipped.
        // Never throws.
        static void SweepReadTemps(string dir)
        {
            DateTime cutoff = DateTime.UtcNow - ReadTempMaxAge;
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "febgba_read_*"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff)
                            File.Delete(f);
                    }
                    catch { /* in use / gone / unreadable timestamp — skip */ }
                }
            }
            catch { /* dir missing / unlistable — ignore */ }
        }

        /// <summary>
        /// Test seam (#1639 review follow-up): force-delete read-temps older than
        /// <paramref name="maxAge"/> (defaults to the production window). Lets a
        /// unit test verify the age-based sweep without sleeping 12 hours.
        /// </summary>
        internal static void SweepReadTempsForTest(TimeSpan? maxAge = null)
        {
            string dir = ReadTempDir();
            DateTime cutoff = DateTime.UtcNow - (maxAge ?? ReadTempMaxAge);
            try
            {
                foreach (string f in Directory.EnumerateFiles(dir, "febgba_read_*"))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                    catch { /* skip */ }
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Resolve a picked file to a local path that path-based Core APIs can
        /// consume: the real local path on desktop, or a temp copy of the SAF
        /// stream on Android (#1639). Returns null only when <paramref name="file"/>
        /// is null. Public so callers that picked the handle directly (e.g. the
        /// shared TableExportImportHelper) can bridge a SAF read without
        /// re-opening the picker.
        /// </summary>
        public static async Task<string?> ResolveReadPathAsync(IStorageFile? file)
        {
            if (file == null) return null;
            string? local = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local)) return local;
            return await CopyToTempForReadAsync(file);
        }

        /// <summary>
        /// Run a path-based <paramref name="writer"/> against the picked save
        /// target. On desktop the writer runs directly on the real local path.
        /// On Android (no local path) the writer runs on a temp file whose
        /// extension matches the chosen file, then the temp bytes are streamed
        /// back into the SAF document via <see cref="IStorageFile.OpenWriteAsync"/>
        /// (truncating the target first so a previously-larger document keeps no
        /// stale trailing bytes). The temp file is always deleted. Returns the
        /// path that was written (real local path on desktop, the SAF display
        /// name on Android) or null when <paramref name="file"/> is null. (#1639)
        /// </summary>
        public static async Task<string?> WriteViaAsync(IStorageFile? file, Func<string, Task> writer)
        {
            if (file == null) return null;
            string? local = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(local))
            {
                await writer(local);
                return local;
            }
            return await WriteViaStreamsAsync(file.OpenWriteAsync, file.Name, writer);
        }

        /// <summary>
        /// Stream-based core of the SAF write bridge (testable without an
        /// <see cref="IStorageFile"/>): run <paramref name="writer"/> on a temp
        /// file (extension matched to <c>name</c>), then stream the
        /// temp bytes into <paramref name="openWrite"/>'s stream, truncating it
        /// first so a previously-larger document keeps no stale trailing bytes.
        /// The temp file is always deleted. Returns the file name on success.
        /// #1639.
        /// </summary>
        internal static async Task<string?> WriteViaStreamsAsync(Func<Task<Stream>> openWrite, string? name, Func<string, Task> writer)
        {
            string ext = Path.GetExtension(name ?? "");
            string tempPath = Path.Combine(Path.GetTempPath(),
                "febgba_" + Guid.NewGuid().ToString("N") + ext);
            try
            {
                await writer(tempPath);
                // Many VM writers signal a validation error by setting StatusText
                // and returning WITHOUT creating the output file. In that case there
                // is nothing to stream back — return null (not a crash) so the
                // caller can treat it like "no write happened" and keep the VM's
                // own error status. (Without this guard, opening the missing temp
                // would throw FileNotFoundException and crash the SAF save flow.)
                if (!File.Exists(tempPath))
                    return null;
                await using var src = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var dst = await openWrite();
                // OpenWriteAsync streams do not reliably truncate; reset length
                // so a previously-larger document has no stale trailing bytes.
                if (dst.CanSeek) dst.SetLength(0);
                await src.CopyToAsync(dst);
                return name ?? "(file)";
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            }
        }

        /// <summary>Synchronous-writer convenience over <see cref="WriteViaAsync(IStorageFile, Func{string, Task})"/>.</summary>
        public static Task<string?> WriteViaAsync(IStorageFile? file, Action<string> writer)
            => WriteViaAsync(file, path => { writer(path); return Task.CompletedTask; });
        // Reuse static pattern arrays to avoid repeated allocations
        static readonly string[] GbaPatterns = new[] { "*.gba" };
        static readonly string[] AllPatterns = new[] { "*" };
        static readonly string[] ExecutablePatterns = new[] { "*.exe", "*.app", "*" };
        static readonly string[] UpsPatterns = new[] { "*.ups" };
        static readonly string[] PngPatterns = new[] { "*.png" };
        static readonly string[] ImagePatterns = new[] { "*.png", "*.bmp" };
        static readonly string[] ProblemReportBackupPatterns = new[] { "*.gba", "*.bin" };
        static readonly string[] ProblemReportSavPatterns = new[] { "*.sav" };
        static readonly string[] PalPatterns = new[] { "*.pal" };
        static readonly string[] GbapalPatterns = new[] { "*.gbapal" };
        static readonly string[] ActPatterns = new[] { "*.act" };
        static readonly string[] GplPatterns = new[] { "*.gpl" };
        static readonly string[] TxtPatterns = new[] { "*.txt" };
        static readonly string[] AllPalettePatterns = new[] { "*.pal", "*.gbapal", "*.act", "*.gpl", "*.txt" };
        const string MacExternalToolPickerScript = """
            on run argv
                set pickerPrompt to item 1 of argv
                set appChoice to item 2 of argv
                set fileChoice to item 3 of argv
                if (count of argv) > 3 then
                    if item 4 of argv is "--validate" then
                        return "READY"
                    end if
                end if

                try
                    set pickerKind to choose from list {appChoice, fileChoice} with title pickerPrompt with prompt pickerPrompt default items {fileChoice}
                    if pickerKind is false then
                        return ""
                    end if
                    if item 1 of pickerKind is appChoice then
                        set pickedFile to choose file with prompt pickerPrompt of type {"app"}
                    else
                        set pickedFile to choose file with prompt pickerPrompt
                    end if
                    return POSIX path of pickedFile
                on error number -128
                    return ""
                end try
            end run
            """;

        static IStorageProvider? GetStorageProvider(TopLevel? owner, string operation)
        {
            var provider = owner?.StorageProvider;
            if (provider == null)
            {
                Log.Error($"FileDialogHelper.{operation}: TopLevel or StorageProvider is unavailable.");
            }
            return provider;
        }

        internal static FilePickerOpenOptions CreateExternalToolOpenOptions(string title)
            => new()
            {
                Title = R._(title),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(R._("Executables"))
                    {
                        Patterns = ExecutablePatterns,
                    },
                    MakeAllFileType(),
                },
            };

        internal static ExternalProcessRequest CreateMacExternalToolPickerRequest(
            string title,
            bool validateOnly = false)
        {
            var arguments = new List<string>
            {
                "-l",
                "AppleScript",
                "-e",
                MacExternalToolPickerScript,
                title,
                "*.app",
                R._("All Files"),
            };
            if (validateOnly)
                arguments.Add("--validate");

            return new ExternalProcessRequest("/usr/bin/osascript")
            {
                ArgumentList = arguments,
                CaptureStandardOutput = true,
                CaptureStandardError = true,
            };
        }

        internal static MacExternalToolPickerResult ResolveMacExternalToolPickerResult(
            ExternalProcessResult result,
            Func<string, bool> fileExists,
            Func<string, bool> directoryExists)
        {
            if (result.Kind != ExternalProcessResultKind.Exited || result.ExitCode != 0)
            {
                string detail = !string.IsNullOrWhiteSpace(result.Message)
                    ? result.Message
                    : !string.IsNullOrWhiteSpace(result.StandardError)
                        ? result.StandardError.Trim()
                        : $"osascript exited with code {result.ExitCode?.ToString() ?? "unknown"}.";
                return new MacExternalToolPickerResult(
                    MacExternalToolPickerResultKind.Fallback,
                    null,
                    "macOS external-tool picker failed: " + detail);
            }

            string path = result.StandardOutput.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(path))
            {
                return new MacExternalToolPickerResult(
                    MacExternalToolPickerResultKind.Cancelled,
                    null,
                    string.Empty);
            }

            string normalizedPath = path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (normalizedPath.Length == 0)
                normalizedPath = path;

            bool isAppBundle = directoryExists(normalizedPath)
                && string.Equals(Path.GetExtension(normalizedPath), ".app", StringComparison.OrdinalIgnoreCase);
            if (fileExists(normalizedPath) || isAppBundle)
            {
                return new MacExternalToolPickerResult(
                    MacExternalToolPickerResultKind.Selected,
                    normalizedPath,
                    string.Empty);
            }

            return new MacExternalToolPickerResult(
                MacExternalToolPickerResultKind.Fallback,
                null,
                $"The macOS picker returned a path that is not an executable file or .app bundle: {normalizedPath}");
        }

        static async Task<MacExternalToolPickerResult> PickMacExternalToolAsync(string title)
        {
            ExternalProcessResult processResult =
                await ExternalLauncher.Current.RunCapturedProcessAsync(
                    CreateMacExternalToolPickerRequest(title));
            MacExternalToolPickerResult result = ResolveMacExternalToolPickerResult(
                processResult,
                File.Exists,
                Directory.Exists);
            if (result.Kind == MacExternalToolPickerResultKind.Fallback)
                Log.Error($"FileDialogHelper.OpenExternalTool: {result.Message}");
            return result;
        }

        internal static async Task<string?> OpenExternalToolCoreAsync(
            bool isMacOS,
            Func<Task<MacExternalToolPickerResult>> macPicker,
            Func<Task<string?>> avaloniaPicker)
        {
            if (isMacOS)
            {
                MacExternalToolPickerResult macResult = await macPicker();
                if (macResult.Kind == MacExternalToolPickerResultKind.Selected)
                    return macResult.Path;
                if (macResult.Kind == MacExternalToolPickerResultKind.Cancelled)
                    return null;
            }

            return await avaloniaPicker();
        }

        static async Task<string?> OpenExternalToolWithStorageProviderAsync(
            TopLevel? owner,
            string title)
        {
            var provider = GetStorageProvider(owner, nameof(OpenExternalTool));
            if (provider == null)
                return null;

            var files = await provider.OpenFilePickerAsync(CreateExternalToolOpenOptions(title));
            return ResolveExternalToolLocalPath(files);
        }

        static string? ResolveExternalToolLocalPath(IReadOnlyList<IStorageFile>? files)
        {
            if (files == null || files.Count == 0)
                return null;

            string? path = files[0].TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                CoreState.Services?.ShowInfo(R._("This setting configures an external tool path and requires desktop file-system access; it is not available on this device."));
                return null;
            }
            return path;
        }

        internal static FilePickerOpenOptions CreateProblemReportBackupOpenOptions()
            => CreateSingleOpenFileOptions(R._("Select Backup File"), "GBA ROMs", ProblemReportBackupPatterns);

        internal static FilePickerOpenOptions CreateProblemReportSavOpenOptions()
            => CreateSingleOpenFileOptions(R._("Select SAV File"), "SAV Files", ProblemReportSavPatterns);

        internal static FilePickerOpenOptions CreateSubtitleTranslationDataOpenOptions()
            => CreateSingleOpenFileOptions(R._("Select Translation Data File"), null, null);

        static FilePickerOpenOptions CreateSingleOpenFileOptions(
            string title,
            string? filterName,
            IReadOnlyList<string>? patterns)
        {
            var options = new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            };

            if (!string.IsNullOrEmpty(filterName) && patterns != null)
            {
                options.FileTypeFilter = new[]
                {
                    new FilePickerFileType(filterName) { Patterns = patterns },
                };
            }

            return options;
        }

        internal static async Task<string?> ResolveSingleOpenFileResultAsync(
            IReadOnlyList<IStorageFile>? files,
            bool requireLocalPath = false)
        {
            if (files == null || files.Count == 0)
                return null;

            if (requireLocalPath)
            {
                string? local = files[0].TryGetLocalPath();
                return string.IsNullOrEmpty(local) ? null : local;
            }

            return await ResolveReadPathAsync(files[0]);
        }

        static async Task<string?> OpenSingleFile(
            TopLevel? owner,
            string operation,
            FilePickerOpenOptions options,
            bool requireLocalPath = false)
        {
            var provider = GetStorageProvider(owner, operation);
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(options);
            return await ResolveSingleOpenFileResultAsync(files, requireLocalPath);
        }

        public static Task<string?> OpenProblemReportBackupFile(TopLevel? owner)
            => OpenSingleFile(owner, nameof(OpenProblemReportBackupFile), CreateProblemReportBackupOpenOptions());

        public static Task<string?> OpenProblemReportSavFile(TopLevel? owner)
            => OpenSingleFile(owner, nameof(OpenProblemReportSavFile), CreateProblemReportSavOpenOptions());

        public static Task<string?> OpenSubtitleTranslationDataFile(TopLevel? owner)
            => OpenSingleFile(owner, nameof(OpenSubtitleTranslationDataFile), CreateSubtitleTranslationDataOpenOptions());

        static FilePickerFileType MakeGbaFileType() => new(R._("GBA ROM Files"))
        {
            Patterns = GbaPatterns,
        };

        static FilePickerFileType MakeAllFileType() => new(R._("All Files"))
        {
            Patterns = AllPatterns,
        };

        static FilePickerFileType MakeUpsFileType() => new(R._("UPS Patch Files"))
        {
            Patterns = UpsPatterns,
        };

        /// <summary>
        /// Open a GBA ROM file and return a usable local path. On Android SAF
        /// (no local path) the picked ROM is copied to a temp file so the many
        /// path-based ROM-utility tools (diff / 3-way merge / translate-ROM /
        /// UPS / header-recovery) keep working (#1639). Callers that need to
        /// retain the SAF handle for write-back (the main ROM open/save) use
        /// <see cref="OpenRomFilePick"/> instead.
        /// </summary>
        public static async Task<string?> OpenRomFile(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenRomFile));
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = R._("Open ROM"),
                AllowMultiple = false,
                FileTypeFilter = new[] { MakeGbaFileType(), MakeAllFileType() },
            });

            return files.Count > 0 ? await ResolveReadPathAsync(files[0]) : null;
        }

        /// <summary>Save a GBA ROM file.</summary>
        public static async Task<string?> SaveRomFile(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveRomFile));
            if (provider == null) return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Save ROM"),
                SuggestedFileName = suggestedName ?? "rom.gba",
                FileTypeChoices = new[] { MakeGbaFileType(), MakeAllFileType() },
            });

            return file?.TryGetLocalPath();
        }

        /// <summary>Save a UPS patch file (#1194 Save-as-UPS tool).</summary>
        public static async Task<string?> SaveUpsFile(TopLevel? owner, string? suggestedName = null)
        {
            var file = await SaveUpsFilePick(owner, suggestedName);
            return file?.TryGetLocalPath();
        }

        /// <summary>Save a UPS patch file and return the picked IStorageFile handle (#1639).</summary>
        public static async Task<IStorageFile?> SaveUpsFilePick(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveUpsFilePick));
            if (provider == null) return null;
            return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Save UPS Patch"),
                SuggestedFileName = suggestedName ?? "patch.ups",
                FileTypeChoices = new[] { MakeUpsFileType(), MakeAllFileType() },
            });
        }

        /// <summary>
        /// Open a GBA ROM and return the picked <see cref="IStorageFile"/> itself
        /// (not collapsed to a local path). On Android the SAF result usually has NO
        /// local path, so callers must read it via the stream API (#1124). Desktop
        /// callers can still call TryGetLocalPath() on the returned handle.
        /// </summary>
        public static async Task<IStorageFile?> OpenRomFilePick(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenRomFilePick));
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = R._("Open ROM"),
                AllowMultiple = false,
                FileTypeFilter = new[] { MakeGbaFileType(), MakeAllFileType() },
            });
            return files.Count > 0 ? files[0] : null;
        }

        internal static FilePickerOpenOptions CreatePatchDatabaseZipOpenOptions()
            => new FilePickerOpenOptions
            {
                Title = R._("Import Patch Database ZIP"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(R._("Patch database ZIP"))
                    {
                        Patterns = new[] { "*.zip" },
                        MimeTypes = new[] { "application/zip", "application/x-zip-compressed" },
                        AppleUniformTypeIdentifiers = new[] { "public.zip-archive" },
                    },
                    MakeAllFileType(),
                },
            };

        public static async Task<IStorageFile?> OpenPatchDatabaseZipPick(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenPatchDatabaseZipPick));
            if (provider?.CanOpen != true)
                throw new InvalidOperationException(R._("The file picker is unavailable on this platform."));
            var files = await provider.OpenFilePickerAsync(CreatePatchDatabaseZipOpenOptions());
            return files.Count > 0 ? files[0] : null;
        }

        /// <summary>Save a GBA ROM and return the picked IStorageFile (#1124).</summary>
        public static async Task<IStorageFile?> SaveRomFilePick(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveRomFilePick));
            if (provider == null) return null;
            return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Save ROM"),
                SuggestedFileName = suggestedName ?? "rom.gba",
                FileTypeChoices = new[] { MakeGbaFileType(), MakeAllFileType() },
            });
        }

        /// <summary>
        /// Open a decomp project folder (#1129 slice 1). Returns the chosen
        /// directory's local path, or null when cancelled / unavailable.
        /// </summary>
        public static async Task<string?> OpenProjectFolder(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenProjectFolder));
            if (provider == null) return null;
            var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = R._("Open Decomp Project"),
                AllowMultiple = false,
            });

            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        }

        /// <summary>Open a UPS patch file.</summary>
        public static async Task<string?> OpenPatchFile(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenPatchFile));
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = R._("Open Patch"),
                AllowMultiple = false,
                FileTypeFilter = new[] { MakeUpsFileType(), MakeAllFileType() },
            });

            return files.Count > 0 ? await ResolveReadPathAsync(files[0]) : null;
        }

        static FilePickerFileType MakePngFileType() => new(R._("PNG Image"))
        {
            Patterns = PngPatterns,
        };

        /// <summary>Save a PNG image file.</summary>
        public static async Task<string?> SaveImageFile(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveImageFile));
            if (provider == null) return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Export Image"),
                SuggestedFileName = suggestedName ?? "image.png",
                FileTypeChoices = new[] { MakePngFileType() },
            });

            return file?.TryGetLocalPath();
        }

        static FilePickerFileType MakeImageFileType() => new(R._("Image Files"))
        {
            Patterns = ImagePatterns,
        };

        /// <summary>Open an image file (PNG, BMP) for import.</summary>
        public static async Task<string?> OpenImageFile(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenImageFile));
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = R._("Import Image"),
                AllowMultiple = false,
                FileTypeFilter = new[] { MakeImageFileType(), MakeAllFileType() },
            });

            return files.Count > 0 ? await ResolveReadPathAsync(files[0]) : null;
        }

        /// <summary>Save an Animation Creator script file (.txt).</summary>
        public static async Task<string?> SaveAnimationScriptFile(TopLevel? owner, string? suggestedName = null)
        {
            var file = await SaveAnimationScriptFilePick(owner, suggestedName);
            return file?.TryGetLocalPath();
        }

        /// <summary>
        /// Pick an Animation Creator script (.txt) save target and return the
        /// <see cref="IStorageFile"/> handle (not collapsed), so a SINGLE-FILE
        /// script writer can route through <see cref="WriteViaAsync(IStorageFile, Func{string, Task})"/>
        /// (#1639). NOTE: callers whose export ALSO writes sibling PNGs must NOT
        /// bridge — they require a local path and Android-disable.
        /// </summary>
        public static async Task<IStorageFile?> SaveAnimationScriptFilePick(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveAnimationScriptFilePick));
            if (provider == null) return null;
            var fileType = new FilePickerFileType(R._("Animation Script (.txt)")) { Patterns = TxtPatterns };
            return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Save Animation Script"),
                SuggestedFileName = suggestedName ?? "anim.txt",
                FileTypeChoices = new[] { fileType, MakeAllFileType() },
            });
        }

        /// <summary>
        /// Pick a local external-tool path with one shared picker path. macOS uses
        /// the system scripting addition so application packages remain selectable;
        /// other platforms and macOS failures use Avalonia's storage-provider picker.
        /// </summary>
        public static Task<string?> OpenExternalTool(TopLevel? owner, string title)
            => OpenExternalToolCoreAsync(
                OperatingSystem.IsMacOS(),
                () => PickMacExternalToolAsync(R._(title)),
                () => OpenExternalToolWithStorageProviderAsync(owner, title));

        /// <summary>Open any file with custom filter.</summary>
        public static async Task<string?> OpenFile(TopLevel? owner, string title, string pattern, bool requireLocalPath = false)
            => await OpenFile(owner, title, new[] { pattern }, requireLocalPath);

        /// <summary>
        /// Open any file with a custom multi-pattern filter (e.g. ".ttf" + ".otf"
        /// for the Font editor's auto-generate font picker, #1232).
        ///
        /// <paramref name="requireLocalPath"/> (#1639): when the consumer resolves
        /// SIBLING files relative to the picked path (e.g. a script that loads
        /// frame PNGs from its own directory), a one-file SAF temp copy would make
        /// those siblings resolve beside the temp instead of beside the chosen
        /// document. Such callers pass <c>true</c> so a SAF pick (no local path)
        /// returns null — the caller then shows an explicit Android message — and
        /// the bridge is NEVER used for multi-file flows.
        /// </summary>
        public static async Task<string?> OpenFile(TopLevel? owner, string title, string[] patterns, bool requireLocalPath = false)
        {
            var provider = GetStorageProvider(owner, nameof(OpenFile));
            if (provider == null) return null;
            var fileType = new FilePickerFileType(title) { Patterns = patterns };
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = new[] { fileType, MakeAllFileType() },
            });

            if (files.Count == 0) return null;
            if (requireLocalPath)
            {
                string? local = files[0].TryGetLocalPath();
                return string.IsNullOrEmpty(local) ? null : local;
            }
            return await ResolveReadPathAsync(files[0]);
        }

        /// <summary>
        /// Save any file with a custom filter + optional suggested name.
        /// Used by the Map Action Animation Export button (#499) plus any
        /// future caller that needs a generic single-format SaveFilePicker.
        /// </summary>
        public static async Task<string?> SaveFile(TopLevel? owner, string title, string filterName, string pattern, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFile));
            if (provider == null) return null;
            var fileType = new FilePickerFileType(filterName) { Patterns = new[] { pattern } };
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[] { fileType, MakeAllFileType() },
            });

            return file?.TryGetLocalPath();
        }

        /// <summary>
        /// Save any file with multiple format choices (each a name + pattern pair).
        /// The OS picker shows them as a single dropdown — the user can pick the
        /// .txt or .gif variant directly without resorting to "All Files".
        /// Used by the Map Action Animation Export button (#499 Copilot bot
        /// inline review fix: single-pattern picker hid the .gif export path).
        /// </summary>
        public static async Task<string?> SaveFile(TopLevel? owner, string title,
            (string Name, string Pattern)[] filters, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFile));
            if (provider == null) return null;
            var choices = new System.Collections.Generic.List<FilePickerFileType>(filters.Length + 1);
            foreach (var (name, pattern) in filters)
            {
                choices.Add(new FilePickerFileType(name) { Patterns = new[] { pattern } });
            }
            choices.Add(MakeAllFileType());

            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = choices,
            });

            return file?.TryGetLocalPath();
        }

        /// <summary>
        /// Save any file with multiple format choices; returns both the chosen
        /// path and the zero-based index of the filter the user selected (FIX 4:
        /// drives enableComment from the chosen filter, not a filename heuristic).
        /// Returns (-1) as filterIndex when the dialog is cancelled.
        /// </summary>
        public static async Task<(string? Path, int FilterIndex)> SaveFileWithFilterIndex(
            TopLevel? owner, string title,
            (string Name, string Pattern)[] filters, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFileWithFilterIndex));
            if (provider == null) return (null, -1);
            var choices = new System.Collections.Generic.List<FilePickerFileType>(filters.Length + 1);
            foreach (var (name, pattern) in filters)
            {
                choices.Add(new FilePickerFileType(name) { Patterns = new[] { pattern } });
            }
            choices.Add(MakeAllFileType());

            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = choices,
            });

            if (file == null) return (null, -1);

            string? path = file.TryGetLocalPath();

            // Avalonia StorageProvider does not expose the selected FileTypeChoice
            // index directly. We infer it by matching the saved filename's extension
            // against the filter patterns in order (first match wins). This matches
            // the picker's own auto-append behaviour and is deterministic.
            if (path != null)
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                for (int i = 0; i < filters.Length; i++)
                {
                    string pat = filters[i].Pattern.TrimStart('*').ToLowerInvariant();
                    if (ext == pat) return (path, i);
                }
            }

            return (path, 0); // fallback to first filter
        }

        static FilePickerFileType MakeJascPalFileType() => new(R._("JASC-PAL (Aseprite/GIMP)"))
        {
            Patterns = PalPatterns,
        };

        static FilePickerFileType MakeGbaRawPalFileType() => new(R._("GBA Raw Palette"))
        {
            Patterns = GbapalPatterns,
        };

        static FilePickerFileType MakeAdobeActFileType() => new(R._("Adobe ACT (Photoshop)"))
        {
            Patterns = ActPatterns,
        };

        static FilePickerFileType MakeGimpGplFileType() => new(R._("GIMP GPL Palette"))
        {
            Patterns = GplPatterns,
        };

        static FilePickerFileType MakeHexTextPalFileType() => new(R._("Hex Text Palette"))
        {
            Patterns = TxtPatterns,
        };

        static FilePickerFileType MakeAnyPaletteFileType() => new(R._("All Palette Files"))
        {
            Patterns = AllPalettePatterns,
        };

        /// <summary>Save a palette file with multi-format choices.</summary>
        public static async Task<string?> SavePaletteFile(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SavePaletteFile));
            if (provider == null) return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Export Palette"),
                SuggestedFileName = suggestedName ?? "palette.pal",
                FileTypeChoices = new[] { MakeJascPalFileType(), MakeGbaRawPalFileType(), MakeAdobeActFileType(), MakeGimpGplFileType(), MakeHexTextPalFileType() },
            });

            return file?.TryGetLocalPath();
        }

        /// <summary>Open a palette file (any supported format) for import.</summary>
        public static async Task<string?> OpenPaletteFile(TopLevel? owner)
        {
            var provider = GetStorageProvider(owner, nameof(OpenPaletteFile));
            if (provider == null) return null;
            var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = R._("Import Palette"),
                AllowMultiple = false,
                FileTypeFilter = new[] { MakeAnyPaletteFileType(), MakeJascPalFileType(), MakeGbaRawPalFileType(), MakeAdobeActFileType(), MakeGimpGplFileType(), MakeHexTextPalFileType(), MakeAllFileType() },
            });

            return files.Count > 0 ? await ResolveReadPathAsync(files[0]) : null;
        }

        // ===================================================================
        // SAVE "*Via" overloads — pick the target, then run a path-based writer
        // through WriteViaAsync so the output reaches the SAF document on
        // Android too (#1639). These let palette/image/file save flows keep
        // their existing path-based write code unchanged.
        // ===================================================================

        /// <summary>Pick a PNG save target and run <paramref name="writer"/> via the SAF bridge.</summary>
        public static async Task<string?> SaveImageFileVia(TopLevel? owner, string? suggestedName, Func<string, Task> writer)
        {
            var provider = GetStorageProvider(owner, nameof(SaveImageFileVia));
            if (provider == null) return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Export Image"),
                SuggestedFileName = suggestedName ?? "image.png",
                FileTypeChoices = new[] { MakePngFileType() },
            });
            return await WriteViaAsync(file, writer);
        }

        /// <summary>Synchronous-writer overload of <see cref="SaveImageFileVia(TopLevel?, string, Func{string, Task})"/>.</summary>
        public static Task<string?> SaveImageFileVia(TopLevel? owner, string? suggestedName, Action<string> writer)
            => SaveImageFileVia(owner, suggestedName, p => { writer(p); return Task.CompletedTask; });

        /// <summary>
        /// Pick a PNG save target and return the <see cref="IStorageFile"/> handle
        /// (not collapsed), for callers that need to inspect the chosen path before
        /// writing (e.g. the desktop overwrite-confirmation in ImageExportService)
        /// and then bridge the write via <see cref="WriteViaAsync(IStorageFile, Func{string, Task})"/>. #1639.
        /// </summary>
        public static async Task<IStorageFile?> SaveImageFilePick(TopLevel? owner, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveImageFilePick));
            if (provider == null) return null;
            return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Export Image"),
                SuggestedFileName = suggestedName ?? "image.png",
                FileTypeChoices = new[] { MakePngFileType() },
            });
        }

        /// <summary>Pick a palette save target (multi-format) and run <paramref name="writer"/> via the SAF bridge.</summary>
        public static async Task<string?> SavePaletteFileVia(TopLevel? owner, string? suggestedName, Func<string, Task> writer)
        {
            var provider = GetStorageProvider(owner, nameof(SavePaletteFileVia));
            if (provider == null) return null;
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = R._("Export Palette"),
                SuggestedFileName = suggestedName ?? "palette.pal",
                FileTypeChoices = new[] { MakeJascPalFileType(), MakeGbaRawPalFileType(), MakeAdobeActFileType(), MakeGimpGplFileType(), MakeHexTextPalFileType() },
            });
            return await WriteViaAsync(file, writer);
        }

        /// <summary>Synchronous-writer overload of <see cref="SavePaletteFileVia(TopLevel?, string, Func{string, Task})"/>.</summary>
        public static Task<string?> SavePaletteFileVia(TopLevel? owner, string? suggestedName, Action<string> writer)
            => SavePaletteFileVia(owner, suggestedName, p => { writer(p); return Task.CompletedTask; });

        /// <summary>Pick a single-format save target and run <paramref name="writer"/> via the SAF bridge.</summary>
        public static async Task<string?> SaveFileVia(TopLevel? owner, string title, string filterName, string pattern, string? suggestedName, Func<string, Task> writer)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFileVia));
            if (provider == null) return null;
            var fileType = new FilePickerFileType(filterName) { Patterns = new[] { pattern } };
            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[] { fileType, MakeAllFileType() },
            });
            return await WriteViaAsync(file, writer);
        }

        /// <summary>Synchronous-writer overload of <see cref="SaveFileVia(TopLevel?, string, string, string, string, Func{string, Task})"/>.</summary>
        public static Task<string?> SaveFileVia(TopLevel? owner, string title, string filterName, string pattern, string? suggestedName, Action<string> writer)
            => SaveFileVia(owner, title, filterName, pattern, suggestedName, p => { writer(p); return Task.CompletedTask; });

        /// <summary>
        /// Pick a single-format save target and return the <see cref="IStorageFile"/>
        /// handle (not collapsed to a path), for callers that must run a complex /
        /// awaited write and then bridge it via <see cref="WriteViaAsync(IStorageFile, Func{string, Task})"/>
        /// themselves (#1639).
        /// </summary>
        public static async Task<IStorageFile?> SaveFilePick(TopLevel? owner, string title, string filterName, string pattern, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFilePick));
            if (provider == null) return null;
            var fileType = new FilePickerFileType(filterName) { Patterns = new[] { pattern } };
            return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = new[] { fileType, MakeAllFileType() },
            });
        }

        /// <summary>
        /// Multi-format-choice save picker returning the <see cref="IStorageFile"/>
        /// handle (not collapsed). For callers where SOME formats are single-file
        /// (bridgeable) and others write siblings (must require a local path), so
        /// they branch by the chosen extension themselves (#1639).
        /// </summary>
        public static async Task<IStorageFile?> SaveFilePick(TopLevel? owner, string title,
            (string Name, string Pattern)[] filters, string? suggestedName = null)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFilePick));
            if (provider == null) return null;
            var choices = new System.Collections.Generic.List<FilePickerFileType>(filters.Length + 1);
            foreach (var (name, pattern) in filters)
                choices.Add(new FilePickerFileType(name) { Patterns = new[] { pattern } });
            choices.Add(MakeAllFileType());
            return await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = choices,
            });
        }

        /// <summary>
        /// Multi-format-choice save target + SAF bridge (#1639). The
        /// <paramref name="writer"/> receives the path (real local on desktop, a
        /// temp matched to the chosen extension on Android) and the chosen filter
        /// index, so callers can branch by format (e.g. .txt vs .gif). Single-file
        /// writers only — multi-file/sibling exports must require a local path.
        /// </summary>
        public static async Task<string?> SaveFileVia(TopLevel? owner, string title,
            (string Name, string Pattern)[] filters, string? suggestedName, Func<string, int, Task> writer)
        {
            var provider = GetStorageProvider(owner, nameof(SaveFileVia));
            if (provider == null) return null;
            var choices = new System.Collections.Generic.List<FilePickerFileType>(filters.Length + 1);
            foreach (var (name, pattern) in filters)
                choices.Add(new FilePickerFileType(name) { Patterns = new[] { pattern } });
            choices.Add(MakeAllFileType());

            var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName,
                FileTypeChoices = choices,
            });
            if (file == null) return null;

            // Infer the chosen filter index from the saved name's extension
            // (matches SaveFileWithFilterIndex). The picker does not expose it.
            int filterIndex = 0;
            string nameExt = Path.GetExtension(file.Name ?? "").ToLowerInvariant();
            for (int i = 0; i < filters.Length; i++)
            {
                string pat = filters[i].Pattern.TrimStart('*').ToLowerInvariant();
                if (nameExt == pat) { filterIndex = i; break; }
            }

            return await WriteViaAsync(file, p => writer(p, filterIndex));
        }
    }
}
