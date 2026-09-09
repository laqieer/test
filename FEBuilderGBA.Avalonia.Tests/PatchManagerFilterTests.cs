// SPDX-License-Identifier: GPL-3.0-or-later
// Avalonia parity tests for PatchManagerViewModel.ApplyFilter HARDCODING_*/"!"
// token handling (#1376).
//
// The HardCoding links on the Unit/Class/Item editors seed the Patch Manager
// filter box with "HARDCODING_{UNIT|CLASS|ITEM}=NN"; ApplyFilter must filter the
// list to the patches that hard-code that id (via PatchFilterCore), not show 0.
// These tests seed the VM's private _allPatches with PatchEntry rows pointing at
// temp PATCH_*.txt files, set FilterText (which triggers ApplyFilter), and assert
// FilteredPatches reflects the token semantics.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using global::Avalonia.Controls;
using global::Avalonia.Headless.XUnit;
using global::Avalonia.Threading;
using FEBuilderGBA;
using FEBuilderGBA.Avalonia.ViewModels;
using FEBuilderGBA.Avalonia.Views;
using Xunit;

namespace FEBuilderGBA.Avalonia.Tests
{
    [Collection("SharedState")]
    public class PatchManagerFilterTests
    {
        [Fact]
        public async Task ImportedLibrarySupportsActualLegacyReloadSelectionAndInstalledFilter()
        {
            await WithImportedLibrary((rom, root) =>
            {
                var vm = new PatchManagerViewModel();
                vm.LoadPatchList();
                Assert.Equal(2, vm.TotalCount);
                vm.FilterText = "!";
                var installed = Assert.Single(vm.FilteredPatches);
                Assert.Equal("Contained pattern", installed.Name);
                Assert.Equal(PatchMetadataCore.PatchStatus.Installed, installed.Status);
                vm.SelectedPatch = installed;
                rom.LoadLow("reloaded-synthetic.gba", new byte[0x1000000], "BE8E01");
                var refresh = typeof(PatchManagerViewModel).GetMethod("RefreshSelectedPatchStatus",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(refresh);
                refresh.Invoke(vm, null);
                Assert.Equal(PatchMetadataCore.PatchStatus.NotInstalled, installed.Status);
                vm.FilterText = "";
                vm.LoadPatchList();
                var unsupported = Assert.Single(vm.FilteredPatches, p => p.Type == "EA");
                Assert.False(unsupported.IsInstallTypeSupported);
                return Task.CompletedTask;
            });
        }

        [AvaloniaFact]
        public async Task ImportedLibraryIsActuallyReloadedWhenThePatchViewReopens()
        {
            await WithImportedLibrary((rom, root) =>
            {
                for (int i = 0; i < 2; i++)
                {
                    var view = new PatchManagerView();
                    var host = new Window { Content = view };
                    try
                    {
                        host.Show();
                        Dispatcher.UIThread.RunJobs();
                        var list = view.FindControl<ListBox>("PatchListBox")!;
                        Assert.Equal(2, list.ItemCount);
                        var installed = list.Items.Cast<PatchEntry>().Single(p => p.Name == "Contained pattern");
                        Assert.Equal(PatchMetadataCore.PatchStatus.Installed, installed.Status);
                        list.SelectedItem = installed;
                        Dispatcher.UIThread.RunJobs();
                        Assert.Equal("Installed", view.FindControl<TextBlock>("DetailStatus")!.Text);
                    }
                    finally { host.Close(); }
                }
                return Task.CompletedTask;
            });
        }

        [Fact]
        public async Task RejectedExternalMetadataNeverBecomesTheLibraryReopenedByTheViewModel()
        {
            await WithImportedLibrary(async (rom, root) =>
            {
                string definition = Path.Combine(root, "config", "patch2", "FE8U", "nested", "PATCH_bin.txt");
                byte[] before = File.ReadAllBytes(definition);
                using var zip = CreateImportZip(("FE8U/PATCH_bad.txt",
                    Encoding.UTF8.GetBytes("TYPE=BIN\nPATCHED_IF:$FGREP4 \\\\unused.invalid\\share\\pattern.bin=0xAA")));
                PatchDatabaseImportCore.PreparedImport? unexpected = null;
                try
                {
                    await Assert.ThrowsAsync<InvalidDataException>(async () =>
                    {
                        unexpected = await PatchDatabaseImportCore.PrepareForTestAsync(zip, root, "FE8U", default, _ => false);
                    });
                }
                finally { unexpected?.Dispose(); }
                Assert.Equal(before, File.ReadAllBytes(definition));
                var reopened = new PatchManagerViewModel();
                reopened.LoadPatchList();
                Assert.Equal(2, reopened.TotalCount);
                Assert.Contains(reopened.FilteredPatches,
                    patch => patch.Name == "Contained pattern" && patch.Status == PatchMetadataCore.PatchStatus.Installed);
            });
        }

        static async Task WithImportedLibrary(Func<ROM, string, Task> body)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "imported-view-" + Guid.NewGuid().ToString("N"));
            var savedRom = CoreState.ROM;
            string savedBase = CoreState.BaseDirectory;
            string savedLanguage = CoreState.Language;
            Directory.CreateDirectory(root);
            try
            {
                var rom = MakeFe8uRom(data => new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }.CopyTo(data, 0x2000));
                CoreState.ROM = rom;
                CoreState.BaseDirectory = root;
                CoreState.Language = "en";
                using var zip = CreateImportZip(
                    ("FE8U/nested/PATCH_bin.txt", Encoding.UTF8.GetBytes(
                        "NAME=Contained pattern\nTYPE=BIN\nPATCHED_IF:$FGREP4 ../shared/pattern.bin=0xAA 0xBB\nBIN:0x3000=../shared/payload.bin")),
                    ("FE8U/nested/PATCH_ea.txt", Encoding.UTF8.GetBytes(
                        "NAME=Unsupported EA\nTYPE=EA\nPATCHED_IF:0x500=0x99 0x42")),
                    ("FE8U/shared/pattern.bin", new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }),
                    ("FE8U/shared/payload.bin", new byte[] { 0x33, 0x44 }));
                using (var prepared = await PatchDatabaseImportCore.PrepareForTestAsync(zip, root, "FE8U", default, _ => false))
                    Assert.True(prepared.Commit().Success);
                Assert.Equal(0, rom.Data[0x3000]);
                await body(rom, root);
            }
            finally
            {
                CoreState.ROM = savedRom;
                CoreState.BaseDirectory = savedBase;
                CoreState.Language = savedLanguage;
                Directory.Delete(root, true);
            }
        }

        static MemoryStream CreateImportZip(params (string Name, byte[] Bytes)[] files)
        {
            var stream = new MemoryStream();
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                foreach (var file in files)
                {
                    using var output = zip.CreateEntry(file.Name, CompressionLevel.NoCompression).Open();
                    output.Write(file.Bytes);
                }
            stream.Position = 0;
            return stream;
        }

        static ROM MakeFe8uRom(Action<byte[]>? seed)
        {
            var data = new byte[0x1000000];
            seed?.Invoke(data);
            var rom = new ROM();
            rom.LoadLow("pm-filter.gba", data, "BE8E01"); // FE8U
            return rom;
        }

        static string MakeTempVerDir(out string root)
        {
            root = Path.Combine(Path.GetTempPath(), "fe_pmf_" + Guid.NewGuid().ToString("N"));
            string verDir = Path.Combine(root, "config", "patch2", "FE8U");
            Directory.CreateDirectory(verDir);
            return verDir;
        }

        static string WritePatch(string verDir, string name, params string[] lines)
        {
            string path = Path.Combine(verDir, "PATCH_" + name + ".txt");
            File.WriteAllLines(path, lines);
            return path;
        }

        // Seed the VM's private _allPatches list with entries pointing at the given files.
        static void SeedAllPatches(PatchManagerViewModel vm, IEnumerable<(string name, string file)> entries)
        {
            var field = typeof(PatchManagerViewModel)
                .GetField("_allPatches", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("Patch list field was not found.");
            if (field.GetValue(vm) is not List<PatchEntry> list)
            {
                Assert.Fail("Patch list field had an unexpected value.");
                return;
            }
            list.Clear();
            foreach (var (name, file) in entries)
                list.Add(new PatchEntry { Name = name, PatchFilePath = file });
        }

        static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        [Fact]
        public void HardCodingUnitToken_FiltersToMatchingPatch()
        {
            string verDir = MakeTempVerDir(out string root);
            var savedRom = CoreState.ROM;
            var savedLang = CoreState.Language;
            try
            {
                var rom = MakeFe8uRom(d => { d[0x2000] = 0x01; d[0x2400] = 0x05; });
                CoreState.ROM = rom;
                CoreState.Language = "en";

                string unitFile = WritePatch(verDir, "EirikaUnit",
                    "TYPE=ADDR", "ADDRESS=0x2000", "ADDRESS_TYPE=UNIT");   // unit id 0x01
                string classFile = WritePatch(verDir, "SomeClass",
                    "TYPE=ADDR", "ADDRESS=0x2400", "ADDRESS_TYPE=CLASS");  // class id 0x05
                string plainFile = WritePatch(verDir, "Plain",
                    "TYPE=ADDR", "ADDRESS=0x2400", "ADDRESS_TYPE=ITEM");   // item id 0x05

                var vm = new PatchManagerViewModel();
                SeedAllPatches(vm, new[]
                {
                    ("EirikaUnit", unitFile),
                    ("SomeClass", classFile),
                    ("Plain", plainFile),
                });

                vm.FilterText = "HARDCODING_UNIT=01";

                Assert.Single(vm.FilteredPatches);
                Assert.Equal("EirikaUnit", vm.FilteredPatches[0].Name);
            }
            finally
            {
                CoreState.ROM = savedRom;
                CoreState.Language = savedLang;
                TryDelete(root);
            }
        }

        [Fact]
        public void HardCodingClassToken_FiltersToMatchingPatch()
        {
            string verDir = MakeTempVerDir(out string root);
            var savedRom = CoreState.ROM;
            var savedLang = CoreState.Language;
            try
            {
                var rom = MakeFe8uRom(d => { d[0x2400] = 0x0C; });
                CoreState.ROM = rom;
                CoreState.Language = "en";

                string classFile = WritePatch(verDir, "MyClass",
                    "TYPE=ADDR", "ADDRESS=0x2400", "ADDRESS_TYPE=CLASS");

                var vm = new PatchManagerViewModel();
                SeedAllPatches(vm, new[] { ("MyClass", classFile) });

                vm.FilterText = "HARDCODING_CLASS=0C";

                Assert.Single(vm.FilteredPatches);
                Assert.Equal("MyClass", vm.FilteredPatches[0].Name);
            }
            finally
            {
                CoreState.ROM = savedRom;
                CoreState.Language = savedLang;
                TryDelete(root);
            }
        }

        [Fact]
        public void HardCodingItemToken_FiltersToMatchingPatch()
        {
            string verDir = MakeTempVerDir(out string root);
            var savedRom = CoreState.ROM;
            var savedLang = CoreState.Language;
            try
            {
                var rom = MakeFe8uRom(d => { d[0x2800] = 0x21; });
                CoreState.ROM = rom;
                CoreState.Language = "en";

                string itemFile = WritePatch(verDir, "MyItem",
                    "TYPE=ADDR", "ADDRESS=0x2800", "ADDRESS_TYPE=ITEM");

                var vm = new PatchManagerViewModel();
                SeedAllPatches(vm, new[] { ("MyItem", itemFile) });

                vm.FilterText = "HARDCODING_ITEM=21";

                Assert.Single(vm.FilteredPatches);
                Assert.Equal("MyItem", vm.FilteredPatches[0].Name);
            }
            finally
            {
                CoreState.ROM = savedRom;
                CoreState.Language = savedLang;
                TryDelete(root);
            }
        }

        [Fact]
        public void InstalledOnlyToken_FiltersToInstalledPatches()
        {
            string verDir = MakeTempVerDir(out string root);
            var savedRom = CoreState.ROM;
            var savedLang = CoreState.Language;
            try
            {
                // installed: PATCHED_IF bytes match ROM; not-installed: mismatch.
                var rom = MakeFe8uRom(d => { d[0x5000] = 0xDE; d[0x5001] = 0xAD; });
                CoreState.ROM = rom;
                CoreState.Language = "en";

                string installed = WritePatch(verDir, "Installed",
                    "TYPE=ADDR", "ADDRESS=0x2000", "ADDRESS_TYPE=UNIT",
                    "PATCHED_IF:0x5000=0xDE 0xAD");
                string notInstalled = WritePatch(verDir, "NotInstalled",
                    "TYPE=ADDR", "ADDRESS=0x2000", "ADDRESS_TYPE=UNIT",
                    "PATCHED_IF:0x5000=0x00 0x00");

                var vm = new PatchManagerViewModel();
                SeedAllPatches(vm, new[]
                {
                    ("Installed", installed),
                    ("NotInstalled", notInstalled),
                });

                vm.FilterText = "!";

                Assert.Single(vm.FilteredPatches);
                Assert.Equal("Installed", vm.FilteredPatches[0].Name);
            }
            finally
            {
                CoreState.ROM = savedRom;
                CoreState.Language = savedLang;
                TryDelete(root);
            }
        }

        [Fact]
        public void PlainSubstringFilter_StillWorks()
        {
            var savedRom = CoreState.ROM;
            try
            {
                CoreState.ROM = MakeFe8uRom(null);

                var vm = new PatchManagerViewModel();
                SeedAllPatches(vm, new[]
                {
                    ("Skill System", ""),
                    ("Eirika Campaign", ""),
                });

                vm.FilterText = "skill";

                Assert.Single(vm.FilteredPatches);
                Assert.Equal("Skill System", vm.FilteredPatches[0].Name);
            }
            finally { CoreState.ROM = savedRom; }
        }

        [Fact]
        public void EmptyFilter_ReturnsAll()
        {
            var savedRom = CoreState.ROM;
            try
            {
                CoreState.ROM = MakeFe8uRom(null);

                var vm = new PatchManagerViewModel();
                SeedAllPatches(vm, new[] { ("A", ""), ("B", ""), ("C", "") });

                // Trigger a filter pass (FilterText defaults to "" so an immediate
                // "" set would be a no-op), then clear it to assert "all".
                vm.FilterText = "A";
                Assert.Single(vm.FilteredPatches);

                vm.FilterText = "";
                Assert.Equal(3, vm.FilteredPatches.Count);
            }
            finally { CoreState.ROM = savedRom; }
        }
    }
}
