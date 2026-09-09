using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using FEBuilderGBA.Avalonia.Services;

namespace FEBuilderGBA.Avalonia.ViewModels
{
    /// <summary>Represents one patch entry for UI display, wrapping PatchMetadataCore.PatchInfo.</summary>
    public class PatchEntry
    {
        public string Name { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string Description { get; set; } = "";
        public string Author { get; set; } = "";
        public string Tags { get; set; } = "";
        public string Type { get; set; } = "";
        public PatchMetadataCore.PatchStatus Status { get; set; } = PatchMetadataCore.PatchStatus.Unknown;
        public string PatchFilePath { get; set; } = "";
        public int DependencyCount { get; set; }
        public int UnsatisfiedDependencyCount { get; set; }
        public List<PatchMetadataCore.PatchDependency> UnsatisfiedDependencies { get; set; } = new();

        public bool IsInstallTypeSupported =>
            string.IsNullOrEmpty(Type) ||
            Type == "BIN";

        public string StatusText => Status switch
        {
            PatchMetadataCore.PatchStatus.Installed => R._("Installed"),
            PatchMetadataCore.PatchStatus.NotInstalled when !IsInstallTypeSupported => R._("Unsupported"),
            PatchMetadataCore.PatchStatus.NotInstalled => R._("Available"),
            _ => R._("Unknown")
        };

        public string ActionRestrictionMessage
        {
            get
            {
                if (IsInstallTypeSupported)
                    return "";
                if (string.Equals(Type, "EA", StringComparison.OrdinalIgnoreCase))
                {
                    return R._(
                        "EA patches require Event Assembler and are not currently supported by the Avalonia Patch Manager.");
                }
                return R._(
                    "Patch type '{0}' is not currently supported by the Avalonia Patch Manager.",
                    Type);
            }
        }

        /// <summary>True when this patch has unmet dependencies.</summary>
        public bool HasUnmetDependencies => UnsatisfiedDependencyCount > 0;

        /// <summary>Human-readable summary of unmet dependencies.</summary>
        public string DependencyWarning
        {
            get
            {
                if (UnsatisfiedDependencyCount == 0) return "";
                var parts = new List<string>();
                foreach (var dep in UnsatisfiedDependencies)
                {
                    if (!string.IsNullOrEmpty(dep.Comment))
                        parts.Add(dep.Comment);
                    else
                        parts.Add($"Condition: {dep.Condition}");
                }
                return string.Join("\n", parts);
            }
        }

        /// <summary>Create from core PatchInfo.</summary>
        public static PatchEntry FromPatchInfo(PatchMetadataCore.PatchInfo info)
        {
            return new PatchEntry
            {
                Name = info.Name,
                DirectoryPath = info.DirectoryPath,
                Description = info.Description,
                Author = info.Author,
                Tags = info.Tags,
                Type = info.Type,
                Status = info.Status,
                PatchFilePath = info.PatchFilePath,
                DependencyCount = info.DependencyCount,
                UnsatisfiedDependencyCount = info.UnsatisfiedDependencyCount,
                UnsatisfiedDependencies = info.UnsatisfiedDependencies,
            };
        }
    }

    public class PatchManagerViewModel : ViewModelBase
    {
        bool _isLoaded;
        string _filterText = "";
        PatchEntry? _selectedPatch;
        int _totalCount;
        int _installedCount;
        string _statusMessage = "";

        readonly List<PatchEntry> _allPatches = new();
        readonly ObservableCollection<PatchEntry> _filteredPatches = new();

        public bool IsLoaded { get => _isLoaded; set => SetField(ref _isLoaded, value); }
        public bool CanImportPatchDatabase => PatchDatabaseImportService.CanImportLoadedRom;

        /// <summary>
        /// Label for the in-app patch2 Initialize/Update button (#1817). "Update Patch Database" when the
        /// repo-root <c>config/patch2</c> is already a git repo, otherwise "Initialize Patch Database".
        /// Uses <see cref="Patch2GitService.GetPatch2Dir"/> on <see cref="CoreState.BaseDirectory"/> (the
        /// repo root) — NOT the per-version <c>ResolvePatchDirectory</c> subfolder, which is never itself a
        /// git repo and would permanently mislabel the button.
        /// </summary>
        public string Patch2ButtonText
        {
            get
            {
                string baseDir = CoreState.BaseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
                return GitUtil.IsGitRepo(Patch2GitService.GetPatch2Dir(baseDir))
                    ? "Update Patch Database"
                    : "Initialize Patch Database";
            }
        }

        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetField(ref _filterText, value))
                    ApplyFilter();
            }
        }

        public PatchEntry? SelectedPatch
        {
            get => _selectedPatch;
            set
            {
                if (SetField(ref _selectedPatch, value))
                {
                    OnPropertyChanged(nameof(CanInstall));
                    OnPropertyChanged(nameof(CanUninstall));
                }
            }
        }

        public int TotalCount { get => _totalCount; set => SetField(ref _totalCount, value); }
        public int InstalledCount { get => _installedCount; set => SetField(ref _installedCount, value); }
        public string StatusMessage { get => _statusMessage; set => SetField(ref _statusMessage, value); }

        /// <summary>True when a patch is selected and not already installed.</summary>
        public bool CanInstall =>
            _selectedPatch != null &&
            _selectedPatch.Status != PatchMetadataCore.PatchStatus.Installed &&
            !string.IsNullOrEmpty(_selectedPatch.PatchFilePath) &&
            _selectedPatch.IsInstallTypeSupported;

        /// <summary>
        /// True when a selected BIN patch is installed and can be uninstalled — either from a
        /// per-patch backup file (fast path) OR via the clean-ROM-diff dialog (#1462) when no
        /// backup exists (patch installed in a prior/WinForms session or already in the ROM).
        /// </summary>
        public bool CanUninstall =>
            _selectedPatch != null &&
            _selectedPatch.Status == PatchMetadataCore.PatchStatus.Installed &&
            !string.IsNullOrEmpty(_selectedPatch.PatchFilePath) &&
            _selectedPatch.IsInstallTypeSupported;

        public ObservableCollection<PatchEntry> FilteredPatches => _filteredPatches;

        /// <summary>
        /// Load the list of patches from config/patch2/{version}/.
        /// </summary>
        public void LoadPatchList() => LoadPatchListCore(requireCompleteRead: false);

        public bool ReloadImportedPatchList() => LoadPatchListCore(requireCompleteRead: true);

        bool LoadPatchListCore(bool requireCompleteRead)
        {
            _allPatches.Clear();
            _filteredPatches.Clear();
            TotalCount = 0;
            InstalledCount = 0;
            SelectedPatch = null;

            ROM rom = CoreState.ROM;
            if (rom?.RomInfo == null)
            {
                StatusMessage = PatchDatabaseImportService.AvailabilityMessage;
                IsLoaded = true;
                return false;
            }

            string version = rom.RomInfo.VersionToFilename;
            string patchDir = ResolvePatchDirectory(version);
            string lang = PatchMetadataCore.GetLanguageSuffix();

            List<PatchMetadataCore.PatchInfo> infos;
            if (requireCompleteRead)
            {
                if (!PatchMetadataCore.TryEnumeratePatches(patchDir, rom, lang, out infos, out string error))
                {
                    StatusMessage = R._("The installed database could not be refreshed: {0}", error);
                    IsLoaded = true;
                    return false;
                }
            }
            else
            {
                infos = PatchMetadataCore.EnumeratePatches(patchDir, rom, lang);
            }
            foreach (var info in infos)
                _allPatches.Add(PatchEntry.FromPatchInfo(info));

            TotalCount = _allPatches.Count;
            InstalledCount = _allPatches.Count(p => p.Status == PatchMetadataCore.PatchStatus.Installed);

            ApplyEmptyStateNotice(patchDir);

            ApplyFilter();
            IsLoaded = true;
            return TotalCount != 0;
        }

        /// <summary>
        /// Empty-state notice for the Patch Manager. When the patch list resolves empty, explain WHY
        /// instead of showing a silent blank list: on desktop (#1811) <c>config/patch2</c> has not
        /// been downloaded yet (git-delivered since #1766) → <see cref="PatchMetadataCore.NotInitializedMessage"/>;
        /// on Android (#1641) patch2 is not delivered on-device → <see cref="AndroidResourceNoticeCore.PatchLibraryUnavailableMessage"/>.
        /// When the list is populated, any stale empty-state notice we set is cleared (never clobbers
        /// an unrelated status). Platform is routed through the test-injectable
        /// <see cref="AndroidResourceNoticeCore.IsAndroidOverride"/> seam.
        /// </summary>
        public void ApplyEmptyStateNotice(string patchDir)
        {
            if (_allPatches.Count != 0)
            {
                // Populated: clear our own stale empty-state notice (VM reuse / list refilled).
                if (StatusMessage == AndroidResourceNoticeCore.PatchLibraryUnavailableMessage ||
                    StatusMessage == PatchMetadataCore.NotInitializedMessage)
                {
                    StatusMessage = "";
                }
                return;
            }

            // Only claim "not initialized" when the library is genuinely empty/missing (#1811) — an
            // empty list from an enumeration failure (files present but unreadable) must not be
            // mislabelled as not-downloaded-yet.
            if (!PatchMetadataCore.IsPatchLibraryEmpty(patchDir))
            {
                return;
            }

            StatusMessage = AndroidResourceNoticeCore.IsResourceDeliverySupported
                ? PatchMetadataCore.NotInitializedMessage                    // desktop: patch2 not downloaded yet (#1811)
                : AndroidResourceNoticeCore.PatchLibraryUnavailableMessage;  // Android: not available on-device (#1641)
        }

        /// <summary>
        /// Check dependencies for the currently selected patch.
        /// Returns a non-empty warning string if dependencies are unmet, or empty if all OK.
        /// </summary>
        public string CheckSelectedPatchDependencies()
        {
            if (_selectedPatch == null) return "";

            ROM rom = CoreState.ROM;
            if (rom == null) return "";

            string lang = PatchMetadataCore.GetLanguageSuffix();
            var missing = PatchMetadataCore.CheckDependencies(rom, _selectedPatch.PatchFilePath, lang);
            if (missing.Count == 0) return "";

            var parts = new List<string>();
            parts.Add($"This patch has {missing.Count} unmet dependency condition(s):");
            foreach (var dep in missing)
            {
                if (!string.IsNullOrEmpty(dep.Comment))
                    parts.Add("  - " + dep.Comment);
                else
                    parts.Add("  - Condition not met: " + dep.Condition);
            }
            parts.Add("");
            parts.Add("Install the required patches first, or proceed at your own risk.");
            return string.Join("\n", parts);
        }

        /// <summary>
        /// Install the currently selected patch. Returns the result message.
        /// Set forceIgnoreDependencies to true to skip dependency checks.
        /// </summary>
        public string InstallPatch(bool forceIgnoreDependencies = false)
        {
            if (_selectedPatch == null)
                return "No patch selected.";

            ROM rom = CoreState.ROM;
            if (rom == null)
                return "No ROM loaded.";

            // Check dependencies unless forced
            if (!forceIgnoreDependencies)
            {
                string depWarning = CheckSelectedPatchDependencies();
                if (!string.IsNullOrEmpty(depWarning))
                {
                    StatusMessage = depWarning;
                    return depWarning;
                }
            }

            Undo? undo = CoreState.Undo;
            Undo.UndoData? undoData = null;
            if (undo != null)
                undoData = undo.NewUndoData("PatchInstall", _selectedPatch.Name);

            var result = PatchMetadataCore.ApplyPatch(rom, _selectedPatch.PatchFilePath, undoData);

            if (result.Success)
            {
                if (undo != null && undoData != null)
                    undo.Push(undoData);

                // Refresh the status of this patch
                RefreshSelectedPatchStatus();
                StatusMessage = result.Message;
            }
            else
            {
                // Rollback on failure
                if (undo != null && undoData != null)
                    undo.Rollback(undoData);
                StatusMessage = "Install failed: " + result.Message;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            return StatusMessage;
        }

        /// <summary>
        /// Uninstall the currently selected patch by restoring original bytes from backup.
        /// </summary>
        public string UninstallPatch()
        {
            if (_selectedPatch == null)
                return "No patch selected.";

            ROM rom = CoreState.ROM;
            if (rom == null)
                return "No ROM loaded.";

            Undo? undo = CoreState.Undo;
            Undo.UndoData? undoData = null;
            if (undo != null)
                undoData = undo.NewUndoData("PatchUninstall", _selectedPatch.Name);

            var result = PatchMetadataCore.UninstallPatch(rom, _selectedPatch.PatchFilePath, undoData);

            if (result.Success)
            {
                // only record when at least one region was recorded into UndoData (avoid a no-op Undo History entry)
                if (undo != null && undoData != null && undoData.list.Count > 0)
                    undo.Push(undoData);

                RefreshSelectedPatchStatus();
                StatusMessage = result.Message;
            }
            else
            {
                // Rollback any partial restore on failure.
                // only rollback when at least one region was recorded into UndoData (avoid a no-op Undo History entry)
                if (undo != null && undoData != null && undoData.list.Count > 0)
                    undo.Rollback(undoData);
                StatusMessage = "Uninstall failed: " + result.Message;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            return StatusMessage;
        }

        /// <summary>
        /// #1462: true when the selected patch CANNOT be uninstalled from a per-patch
        /// backup file (installed in a prior/WinForms session or already present in the
        /// loaded ROM) — the View must open the clean-ROM-diff dialog instead.
        /// </summary>
        public bool SelectedPatchNeedsCleanRom
        {
            get
            {
                if (_selectedPatch == null) return false;
                return !PatchMetadataCore.HasBackup(_selectedPatch.PatchFilePath);
            }
        }

        /// <summary>The currently selected patch's display name (for the dialog title).</summary>
        public string SelectedPatchName => _selectedPatch?.Name ?? "";

        /// <summary>
        /// #1462: Uninstall the selected patch by diff-restoring its touched regions from a
        /// user-supplied patch-free ("clean") ROM file. Used when no per-patch backup exists.
        /// The actual ROM validation (GBA-header compatibility gate + patch-absence check, both
        /// BEFORE any mutation) happens inside <see cref="PatchMetadataCore.UninstallPatchWithCleanRom"/>;
        /// this method only reads the file and runs the restore under an undo scope, mirroring the
        /// backup-based path's Push/Rollback discipline.
        /// </summary>
        public string UninstallPatchWithCleanRom(string cleanRomPath)
        {
            if (_selectedPatch == null)
                return "No patch selected.";

            ROM rom = CoreState.ROM;
            if (rom == null)
                return "No ROM loaded.";

            if (string.IsNullOrEmpty(cleanRomPath) || !File.Exists(cleanRomPath))
                return StatusMessage = "Uninstall failed: clean ROM file not found.";

            byte[] cleanRomBytes;
            try
            {
                cleanRomBytes = File.ReadAllBytes(cleanRomPath);
            }
            catch (Exception ex)
            {
                return StatusMessage = "Uninstall failed: could not read clean ROM. " + ex.Message;
            }

            Undo? undo = CoreState.Undo;
            Undo.UndoData? undoData = null;
            if (undo != null)
                undoData = undo.NewUndoData("PatchUninstallCleanRom", _selectedPatch.Name);

            var result = PatchMetadataCore.UninstallPatchWithCleanRom(
                rom, _selectedPatch.PatchFilePath, cleanRomBytes, undoData);

            if (result.Success)
            {
                if (undo != null && undoData != null && undoData.list.Count > 0)
                    undo.Push(undoData);

                RefreshSelectedPatchStatus();
                StatusMessage = result.Message;
            }
            else
            {
                if (undo != null && undoData != null && undoData.list.Count > 0)
                    undo.Rollback(undoData);
                StatusMessage = "Uninstall failed: " + result.Message;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            OnPropertyChanged(nameof(SelectedPatchNeedsCleanRom));
            return StatusMessage;
        }

        /// <summary>Re-check installation status of the selected patch and update counts.</summary>
        void RefreshSelectedPatchStatus()
        {
            if (_selectedPatch == null) return;

            ROM rom = CoreState.ROM;
            if (rom == null) return;

            string lang = PatchMetadataCore.GetLanguageSuffix();
            var refreshed = PatchMetadataCore.ParsePatchFile(
                _selectedPatch.PatchFilePath,
                Path.GetFileName(_selectedPatch.DirectoryPath),
                rom, lang);

            _selectedPatch.Status = refreshed.Status;

            // Update the corresponding entry in _allPatches
            var match = _allPatches.FirstOrDefault(p => p.PatchFilePath == _selectedPatch.PatchFilePath);
            if (match != null)
                match.Status = refreshed.Status;

            InstalledCount = _allPatches.Count(p => p.Status == PatchMetadataCore.PatchStatus.Installed);
        }

        void ApplyFilter()
        {
            _filteredPatches.Clear();
            string filter = _filterText.Trim();

            if (string.IsNullOrEmpty(filter))
            {
                foreach (var p in _allPatches)
                    _filteredPatches.Add(p);
                return;
            }

            // Special-case the synthetic Patch Manager tokens BEFORE the substring
            // search, mirroring WinForms PatchForm.MakeFiltedPatchs (PatchForm.cs:143):
            //   "!"                      -> installed-only
            //   "HARDCODING_{type}=NN"   -> patches that hard-code that id (#1376)
            // The HardCoding links on the Unit/Class/Item editors seed the second form.
            ROM rom = CoreState.ROM;
            if (rom != null)
            {
                // WinForms-style scanner language (ja/en/zh) — NOT GetLanguageSuffix(),
                // which returns "" for Japanese and would drift CleanupKey fallback.
                string lang = PatchFilterCore.ScanLang(CoreState.Language);

                if (PatchFilterCore.IsInstalledOnlyToken(filter))
                {
                    foreach (var p in _allPatches)
                    {
                        if (PatchFilterCore.IsInstalledForFilter(rom, p.PatchFilePath, lang))
                            _filteredPatches.Add(p);
                    }
                    return;
                }

                if (PatchFilterCore.TryParseHardCodingToken(filter, out string typeNameUpper, out uint value))
                {
                    foreach (var p in _allPatches)
                    {
                        if (PatchFilterCore.IsHardCodingTokenMatch(rom, p.PatchFilePath, lang, value, typeNameUpper))
                            _filteredPatches.Add(p);
                    }
                    return;
                }
            }

            // Plain substring search (Name/Tags/Author/Description) — unchanged.
            foreach (var p in _allPatches)
            {
                if (p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    p.Tags.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    p.Author.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    p.Description.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    _filteredPatches.Add(p);
                }
            }
        }

        /// <summary>
        /// Resolve the config/patch2/{version} directory (exe dir, cwd, then repo
        /// root in development). Public so other views (e.g. the ROM Translation
        /// Tool's ChapterNameToText install) can reuse the single resolution path.
        /// </summary>
        public static string ResolvePatchDirectory(string version)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string baseDir = string.IsNullOrEmpty(CoreState.BaseDirectory) ? exeDir : CoreState.BaseDirectory;
            string canonical = Path.Combine(baseDir, "config", "patch2", version);
            if (Directory.Exists(canonical)) return canonical;

            string path = Path.Combine(exeDir, "config", "patch2", version);
            if (Directory.Exists(path)) return path;

            path = Path.Combine(Directory.GetCurrentDirectory(), "config", "patch2", version);
            if (Directory.Exists(path)) return path;

            // Development: find repo root
            string dir = exeDir;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    path = Path.Combine(dir, "config", "patch2", version);
                    if (Directory.Exists(path)) return path;
                    break;
                }
                string parent = Path.GetDirectoryName(dir) ?? "";
                if (parent == dir) break;
                dir = parent;
            }

            return canonical;
        }
    }
}
