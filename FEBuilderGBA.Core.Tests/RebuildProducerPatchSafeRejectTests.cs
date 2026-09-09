// SPDX-License-Identifier: GPL-3.0-or-later
// Tests for RebuildProducerCore slice s2pf-12 (#1261) — the PatchForm EA/BIN
// per-ROM SAFE-REJECT BACKSTOP (option-B epic, sub-slice 12 of 17, the FIRST of
// the EA/BIN phase).
//
// This slice adds:
//   - PatchHardCodeScanner.EaBinInstallStatus(ROM, PatchSt): a SOUND tri-state
//     install check (Installed / NotInstalled / Unknown) — a conservative
//     over-approximation of WF PatchForm.IsInstalled. Refuses on Installed OR
//     Unknown so it never reports "safe" while a patch could be present.
//   - RebuildProducerCore.PatchFormHasUnportableInstalledPatch(ROM) -> bool:
//     true iff the ROM carries an EA/BIN patch the producer can't emit that is
//     Installed or Unknown.
//   - MakeWithProducer wiring: REFUSE the rebuild (InvalidOperationException naming
//     the offending patch) when the predicate is true — IN ADDITION to the existing
//     IsComplete gate (the token still keeps that gate closed today, so this is
//     belt-and-suspenders now / load-bearing at the gate-open in s2pf-17).
//
// SOUNDNESS: the dangerous direction is a false-negative ("safe" when actually
// unsafe). The naive CheckIF=="I" predicate is UNSOUND (Copilot plan review, issue
// #1325): it misses PATCHED_IFNOT-installed (e.g. FE8U/MNC2Fix). EaBinInstallStatus
// fixes that (PATCHED_IFNOT match => Installed).
//
// s2pf-18 (#1261) GATE-USEFULNESS: the install-detection path now reads the $FGREP
// <file> file-inclusion VERBATIM (port of WF MakeGrepData(value, basedir)), and — for
// the BYTE-FAITHFUL GREP/FGREP family — treats a NOT_FOUND grep as NOT-MATCHING
// (NotInstalled), exactly as WF's CheckIF does (the PATCHED_IF unsafe-address
// `continue`). This narrows the Unknown set so vanilla FE8U's 159 absent-signature
// $GREP/$FGREP EA/BIN rows resolve to NotInstalled and MakeWithProducer PROCEEDS.
// FALSE-NEGATIVE still IMPOSSIBLE: Core's grep is byte-identical to WF's, so Core says
// NotInstalled only where WF does. The un-ported macros ($XGREP/$FREEAREA) STAY
// Unknown => refuse. These tests pin every branch: PATCHED_IF-installed,
// PATCHED_IFNOT-installed, resolvable-not-installed, $FGREP file present (match +
// mismatch) / absent-signature / missing-file, inline-$GREP absent-signature,
// $XGREP-unknown, TYPE/CANONICAL_SKIP/no-dir/version-0 boundaries.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using FEBuilderGBA;

namespace FEBuilderGBA.Core.Tests
{
    [Collection("SharedState")]
    public class RebuildProducerPatchSafeRejectTests : IDisposable
    {
        readonly ROM _savedRom = CoreState.ROM;
        readonly string _savedLang = CoreState.Language;
        readonly string _savedBaseDir = CoreState.BaseDirectory;
        readonly string _tempDir;

        public RebuildProducerPatchSafeRejectTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "RebuildProducerPatchSafeReject_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            CoreState.Language = "en";
        }

        public void Dispose()
        {
            CoreState.ROM = _savedRom;
            CoreState.Language = _savedLang;
            CoreState.BaseDirectory = _savedBaseDir;
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
        }

        // 16 MiB = FE8 LoadLow minimum (Rom.cs requires >= 0x0100_0000). Keeping it at the
        // minimum cuts per-test allocation vs a 32 MiB buffer (same idiom as the scaffold tests).
        static ROM MakeVersionedRom(string versionString, int size = 0x0100_0000)
        {
            var rom = new ROM();
            bool ok = rom.LoadLow("fake.gba", new byte[size], versionString);
            Assert.True(ok, "LoadLow did not recognize version string: " + versionString);
            return rom;
        }

        // Build a PatchSt directly from key/value lines (mirrors a real LoadPatch result) for the
        // unit-level EaBinInstallStatus tests that don't need a staged directory.
        static PatchInstallCore.PatchSt MakePatch(string fileName, params (string key, string value)[] kv)
        {
            var p = new PatchInstallCore.PatchSt
            {
                Name = fileName,
                PatchFileName = fileName,
                Param = new Dictionary<string, string>(),
            };
            foreach (var (key, value) in kv) p.Param[key] = value;
            return p;
        }

        string StageFe8uPatchDir()
        {
            string patchDir = Path.Combine(_tempDir, "config", "patch2", "FE8U");
            Directory.CreateDirectory(patchDir);
            return patchDir;
        }

        [Theory]
        [InlineData("installed", true)]
        [InlineData("unknown", true)]
        [InlineData("absent", false)]
        public void AuditedLibraryRetainsActualRebuildSafeRejectClassification(string status, bool refuses)
        {
            var rom = MakeVersionedRom("BE8E01");
            string macro = status == "unknown" ? "$XGREP4 0xDE 0xAD" : "$FGREP4 ../shared/pattern.bin";
            string definition = WriteAuditedDefinition("TYPE=EA\nPATCHED_IF:" + macro + "=0xDE 0xAD");
            if (status == "installed") new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(rom.Data, 0x1000);
            CoreState.ROM = rom;
            var patch = PatchHardCodeScanner.LoadPatch(rom, definition, "en");
            var expected = status switch
            {
                "installed" => PatchHardCodeScanner.InstallStatusEnum.Installed,
                "unknown" => PatchHardCodeScanner.InstallStatusEnum.Unknown,
                _ => PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
            };
            Assert.Equal(expected, PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
            Assert.Equal(refuses, RebuildProducerCore.PatchFormHasUnportableInstalledPatch(rom));
        }

        [Theory]
        [InlineData("WIDTH")]
        [InlineData("HEIGHT")]
        [InlineData("PALETTE")]
        public void AuditedImageDimensionsUseTheActualRebuildCommandReader(string key)
        {
            var rom = MakeVersionedRom("BE8E01");
            CoreState.ROM = rom;
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(rom.Data, 0x100);
            rom.write_p32(0x200, 0x4000);
            string definition = WriteAuditedDefinition("TYPE=IMAGE\nIMAGE_POINTER=0x200\nPALETTE_ADDRESS=0x8000\n" +
                key + "=$FGREP4 ../shared/pattern.bin");
            var patch = PatchHardCodeScanner.LoadPatch(rom, definition, "en");
            var addresses = new List<Address>();
            RebuildProducerCore.EmitPatchImage(rom, addresses, patch, false);
            var image = Assert.Single(addresses, a => a.DataType == Address.DataTypeEnum.IMG);
            var palette = Assert.Single(addresses, a => a.DataType == Address.DataTypeEnum.PAL);
            Assert.Equal(0x4000u, image.Addr);
            Assert.Equal(key == "PALETTE" ? 32u : 1024u, image.Length);
            Assert.Equal(key == "PALETTE" ? 8192u : 32u, palette.Length);
        }

        [Theory]
        [InlineData("WIDTH=$FGREP4 {0}")]
        [InlineData("HEIGHT=$FGREP4 {0}")]
        [InlineData("PALETTE=$FGREP4 {0}")]
        [InlineData("NEW_TARGET_SELECTION_STRUCT=$FGREP4 {0}")]
        [InlineData("BIN:$FGREP4 {0}=payload.bin")]
        [InlineData("BINP:$FGREP4 {0}=payload.bin")]
        [InlineData("BINF:$FGREP4 {0}=payload.bin")]
        [InlineData("BINAP:$FGREP4 {0}=payload.bin")]
        public void RejectedRebuildOperandsNeverCallItsActualResolverReader(string field)
        {
            const string operand = @"\\unused.invalid\share\pattern.bin";
            int resolvers = 0, exists = 0, reads = 0;
            Assert.Throws<InvalidDataException>(() =>
            {
                string definition = WriteAuditedDefinition("TYPE=IMAGE\n" + string.Format(field, operand));
                resolvers++;
                _ = RebuildProducerCore.ResolvePatchAddressWithFileReadsForTest(
                    MakeVersionedRom("BE8E01"), "$FGREP4 " + operand, 0, 0x100, Path.GetDirectoryName(definition)!,
                    _ => { exists++; return false; },
                    _ => { reads++; return Array.Empty<byte>(); });
            });
            Assert.Equal(0, resolvers);
            Assert.Equal(0, exists);
            Assert.Equal(0, reads);
        }

        [Theory]
        [InlineData("BIN")]
        [InlineData("BINP")]
        [InlineData("BINAP")]
        [InlineData("BINF")]
        public void AuditedBinarySidecarsRemainContainedInBothActualRegionReaders(string keyword)
        {
            var rom = MakeVersionedRom("BE8E01");
            CoreState.ROM = rom;
            string definition = WriteAuditedDefinition("TYPE=BIN\n" + keyword + ":0x1000=../shared/pattern.bin");
            string pattern = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(definition)!, "..", "shared", "pattern.bin"));
            int exists = 0, reads = 0;
            bool Exists(string path)
            {
                exists++;
                Assert.Contains(Path.GetFullPath(path), new[] { definition, pattern });
                return File.Exists(path);
            }
            byte[] Read(string path)
            {
                reads++;
                Assert.Equal(pattern, Path.GetFullPath(path));
                return File.ReadAllBytes(path);
            }
            var regions = PatchMetadataCore.CollectPatchRegionsWithBytesForTest(rom, definition,
                out int untraceable, Exists, Read);
            Assert.Equal(0, untraceable);
            var region = Assert.Single(regions);
            Assert.Equal(0x1000u, region.Address);
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, region.PatchBytes);
            Assert.Equal(2, exists);
            Assert.Equal(1, reads);
            byte[] actual = EventAssemblerUninstallCore.ReadModWithFileReadsForTest(
                new[] { keyword, "0x1000", "0" }, pattern, out bool[] mask, rom, Exists, Read);
            Assert.Equal(region.PatchBytes, actual);
            Assert.Equal(actual.Length, mask.Length);
            Assert.Equal(3, exists);
            Assert.Equal(2, reads);
        }

        [Theory]
        [InlineData("BIN")]
        [InlineData("BINP")]
        [InlineData("BINAP")]
        [InlineData("BINF")]
        public void RejectedSidecarNeverReachesRegionOrReadModFileSinks(string keyword)
        {
            const string operand = @"\\unused.invalid\share\pattern.bin";
            int exists = 0, reads = 0, consumers = 0;
            Assert.Throws<InvalidDataException>(() =>
            {
                string definition = WriteAuditedDefinition("TYPE=BIN\n" + keyword + ":0x1000=" + operand);
                consumers++;
                var rom = MakeVersionedRom("BE8E01");
                _ = PatchMetadataCore.CollectPatchRegionsWithBytesForTest(rom, definition, out _,
                    _ => { exists++; return false; }, _ => { reads++; return Array.Empty<byte>(); });
                _ = EventAssemblerUninstallCore.ReadModWithFileReadsForTest(
                    new[] { keyword, "0x1000" }, operand, out _, rom,
                    _ => { exists++; return false; }, _ => { reads++; return Array.Empty<byte>(); });
            });
            Assert.Equal(0, consumers);
            Assert.Equal(0, exists);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void AuditedSymbolAndNestedNonPatchDescriptorUseTheirActualReaders()
        {
            var rom = MakeVersionedRom("BE8E01");
            CoreState.ROM = rom;
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }.CopyTo(rom.Data, 0x1000);
            string definition = WriteAuditedDefinition(
                "TYPE=BIN\nSYMBOL=../shared/symbols.data\nEDIT_PATCH=../shared/nested.data",
                ("shared/symbols.data", "00001000 imported_label"),
                ("shared/nested.data", "TYPE=BIN\nBIN:0x1000=pattern.bin"));
            string root = StageFe8uPatchDir();
            string symbol = Path.Combine(root, "shared", "symbols.data");
            string nested = Path.Combine(root, "shared", "nested.data");
            var patch = PatchHardCodeScanner.LoadPatch(rom, definition, "en");
            int probes = 0, symbols = 0, definitions = 0;
            bool Exists(string path)
            {
                probes++;
                Assert.Contains(Path.GetFullPath(path), new[] { symbol, nested });
                return File.Exists(path);
            }
            var addresses = new List<Address>();
            RebuildProducerCore.ProcessPatchSymbolWithFileReadsForTest(addresses, patch, Exists, path =>
            {
                symbols++;
                Assert.Equal(symbol, Path.GetFullPath(path));
                return File.ReadAllText(path);
            });
            Assert.Contains(addresses, a => a.Addr == 0x1000 && a.Info.Contains("imported_label"));
            var untraceable = new List<string>();
            var child = RebuildProducerCore.LoadEditPatchWithFileReadsForTest("../shared/nested.data", patch,
                untraceable, Exists, path =>
                {
                    definitions++;
                    Assert.Equal(nested, Path.GetFullPath(path));
                    return PatchInstallCore.LoadPatch(path);
                });
            Assert.Equal("pattern.bin", child.Param["BIN:0x1000"]);
            Assert.Empty(untraceable);
            Assert.Equal(2, probes);
            Assert.Equal(1, symbols);
            Assert.Equal(1, definitions);
            RebuildProducerCore.EmitPatchBIN(rom, addresses, patch, false);
            Assert.Contains(addresses, a => a.Addr == 0x1000 && a.DataType == Address.DataTypeEnum.MIX);
        }

        [Theory]
        [InlineData("SYMBOL")]
        [InlineData("EDIT_PATCH")]
        public void RejectedSymbolOrNestedDescriptorNeverCallsItsActualFileReader(string key)
        {
            const string operand = @"\\unused.invalid\share\sidecar";
            int probes = 0, reads = 0, consumers = 0;
            Assert.Throws<InvalidDataException>(() =>
            {
                string definition = WriteAuditedDefinition("TYPE=BIN\n" + key + "=" + operand);
                consumers++;
                var patch = PatchHardCodeScanner.LoadPatch(MakeVersionedRom("BE8E01"), definition, "en");
                RebuildProducerCore.ProcessPatchSymbolWithFileReadsForTest(new(), patch,
                    _ => { probes++; return false; }, _ => { reads++; return ""; });
                _ = RebuildProducerCore.LoadEditPatchWithFileReadsForTest(operand, patch, new(),
                    _ => { probes++; return false; }, _ => { reads++; return null!; });
            });
            Assert.Equal(0, consumers);
            Assert.Equal(0, probes);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void CanonicalModDiscoveryRetainsLiteralOnlyIfSemantics()
        {
            var rom = MakeVersionedRom("BE8E01");
            CoreState.ROM = rom;
            new byte[] { 0x41, 0x42, 0x43, 0x44 }.CopyTo(rom.Data, 0x1000);
            WriteAuditedDefinition("TYPE=BIN",
                ("nested/MOD_literal.txt", "FORM=LiteralMatch\nIF:0x1000=0x41 0x42"),
                ("nested/MOD_mismatch.txt", "FORM=LiteralMismatch\nIF:0x1000=0xFE 0xEF"),
                ("nested/MOD_no_macro.txt", "FORM=MacroRemainsIgnored\nIF:$FGREP4 pattern=0xFE 0xEF"),
                ("nested/pattern", "ABCD"));
            var mod = new Mod();
            mod.Load();
            Assert.Contains(mod.Mods, m => m.Form == "LiteralMatch");
            Assert.DoesNotContain(mod.Mods, m => m.Form == "LiteralMismatch");
            Assert.Contains(mod.Mods, m => m.Form == "MacroRemainsIgnored");
        }

        string WriteAuditedDefinition(string text, params (string Name, string Text)[] sidecars)
        {
            CoreState.BaseDirectory = _tempDir;
            string version = StageFe8uPatchDir();
            Directory.CreateDirectory(Path.Combine(version, "nested"));
            Directory.CreateDirectory(Path.Combine(version, "shared"));
            string definition = Path.Combine(version, "nested", "PATCH_audited.txt");
            File.WriteAllText(definition, text);
            File.WriteAllBytes(Path.Combine(version, "shared", "pattern.bin"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            var manifest = new Dictionary<string, long>
            {
                ["nested/PATCH_audited.txt"] = new FileInfo(definition).Length,
                ["shared/pattern.bin"] = 4,
            };
            foreach (var file in sidecars)
            {
                string path = Path.Combine(version, file.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file.Text);
                manifest[file.Name] = new FileInfo(path).Length;
            }
            PatchDatabaseMetadataAuditCore.Audit(version, manifest);
            return definition;
        }

        // ====================================================================
        // 1. PatchHardCodeScanner.EaBinInstallStatus — the SOUND tri-state.
        //    The all-zero synthetic ROM reads 0x00 at every offset, so:
        //      PATCHED_IF:...=0x00 0x00   matches   -> Installed
        //      PATCHED_IF:...=0xFF 0xFF   mismatch  -> NotInstalled
        //      PATCHED_IFNOT:...=0xFF 0xFF mismatch -> Installed (the un-patched sig is absent)
        //      PATCHED_IFNOT:...=0x00 0x00 match    -> NotInstalled (un-patched sig present)
        //      PATCHED_IF:$GREP/$FGREP absent-sig   -> NotInstalled (NOT_FOUND = not-matching, mirrors WF; s2pf-18)
        //      PATCHED_IF:$XGREP/$FREEAREA          -> Unknown (un-ported macro; stays conservative)
        // ====================================================================

        [Fact]
        public void EaBinInstallStatus_PatchedIfMatch_Installed()
        {
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "EA"), ("PATCHED_IF:0x001000", "0x00 0x00"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Installed,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_PatchedIfNotMismatch_Installed()
        {
            // The KEY false-negative the naive CheckIF=="I" predicate missed (Copilot #1325): a patch
            // installed via PATCHED_IFNOT only (e.g. FE8U/MNC2Fix). WF IsInstalled deems it installed;
            // CheckIF returns "E"/"" there. EaBinInstallStatus correctly returns Installed.
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "EA"), ("PATCHED_IFNOT:0x001000", "0xFF 0xFF"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Installed,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
            // Sanity: the OLD predicate basis (CheckIF) does NOT report "I" here — proving the gap is real.
            Assert.NotEqual("I", PatchHardCodeScanner.CheckIF(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_PatchedIfMismatch_NotInstalled()
        {
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "EA"), ("PATCHED_IF:0x001000", "0xFF 0xFF"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_PatchedIfNotMatch_NotInstalled()
        {
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "EA"), ("PATCHED_IFNOT:0x001000", "0x00 0x00"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_MissingFgrepFileSignature_NotInstalled_MatchesWf()
        {
            // s2pf-18 (#1261): a $FGREP <file> install marker whose file is MISSING. The address resolves
            // there with basedir = Path.GetDirectoryName("p.txt") = "" -> MakeFGrepData returns an empty
            // pattern -> U.Grep -> NOT_FOUND. WF behaves IDENTICALLY (MakeGrepData(value, basedir) returns
            // new byte[0] -> U.Grep NOT_FOUND -> CheckIF's unsafe-address `continue` -> the PATCHED_IF does
            // NOT contribute -> WF IsInstalled is FALSE). Because Core's grep result is byte-identical to
            // WF's for the GREP/FGREP family, Core faithfully classifies this absent signature as
            // NotInstalled (NOT Unknown). FALSE-NEGATIVE IMPOSSIBLE: Core agrees with WF exactly here.
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "BIN"), ("PATCHED_IF:$FGREP4 nope_missing.bin", "0x00 0xB5"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_XgrepSignature_Unknown_StaysSound()
        {
            // SOUNDNESS preserved: $XGREP (masked GREP) is NOT byte-faithfully ported, so its NOT_FOUND does
            // NOT prove the signature absent -> the marker STAYS Unknown -> the gate refuses conservatively.
            // (This is the residual deferral that keeps the false-negative impossible for un-ported macros.)
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "BIN"), ("PATCHED_IF:$XGREP4 0x00 X 0xB5", "0x00 0xB5"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Unknown,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_NoInstallMarker_NotInstalled()
        {
            // An EA/BIN patch with NO PATCHED_IF/PATCHED_IFNOT marker -> NotInstalled (inherits WF's own
            // blind spot: WF IsInstalled is likewise false for a marker-less patch). Plain IF is a
            // PREREQUISITE, not an install marker, so it does not count toward install status.
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "EA"), ("IF:0x001000", "0x00 0x00"), ("ADDRESS", "0x800100"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_NullParam_Unknown()
        {
            // A null Param cannot be inspected at all -> the only SOUND answer is Unknown (NOT
            // NotInstalled): uninspectable proves nothing, so the safe-reject gate must not treat it as
            // safe (Copilot PR #1326 review). LoadPatch never produces a null Param; this guards a direct
            // caller's contract.
            var rom = MakeVersionedRom("BE8E01");
            var patch = new PatchInstallCore.PatchSt { Name = "p.txt", PatchFileName = "p.txt", Param = null! };
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Unknown,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_InstalledMarkerWins_OverUnknownMarker()
        {
            // A patch with BOTH an unresolvable $XGREP marker (Unknown alone) AND a resolvable matching
            // marker is Installed (the precise answer), not Unknown — the resolvable match proves presence.
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatch("p.txt", ("TYPE", "EA"),
                ("PATCHED_IF:$XGREP4 0x00 X 0xB5", "0x00 0xB5"),   // un-ported macro -> Unknown alone
                ("PATCHED_IF:0x001000", "0x00 0x00"));             // resolvable + matches -> Installed
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Installed,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        // ====================================================================
        // 1b. $FGREP <file> install-resolution (s2pf-18 #1261). The install-detection
        //     path now reads the basedir-relative file's bytes VERBATIM as the GREP
        //     pattern (port of WF MakeGrepData(value, basedir)), so a $FGREP-based
        //     PATCHED_IF/PATCHED_IFNOT resolves to a REAL ROM offset and classifies
        //     Installed/NotInstalled — not Unknown — when the file is present.
        //     The marker's PatchFileName must point at a real path so basedir =
        //     Path.GetDirectoryName(PatchFileName) finds the planted .bin.
        // ====================================================================

        // Build a PatchSt whose PatchFileName lives in _tempDir (so basedir resolves there)
        // and stage a binary signature file alongside it.
        PatchInstallCore.PatchSt MakePatchInTempDir(string patchFileName, params (string key, string value)[] kv)
        {
            var p = new PatchInstallCore.PatchSt
            {
                Name = patchFileName,
                PatchFileName = Path.Combine(_tempDir, patchFileName),
                Param = new Dictionary<string, string>(),
            };
            foreach (var (key, value) in kv) p.Param[key] = value;
            return p;
        }

        [Fact]
        public void EaBinInstallStatus_FgrepFilePresent_PatchedIfMatch_Installed()
        {
            // Plant a unique 4-byte signature at a 4-aligned ROM offset and an identical .bin file
            // next to the patch. $FGREP4 finds the signature at 0x2000; the PATCHED_IF byte window
            // (the first two signature bytes) MATCHES the ROM there -> Installed (NOT Unknown).
            var rom = MakeVersionedRom("BE8E01");
            rom.Data[0x2000] = 0xAA; rom.Data[0x2001] = 0xBB; rom.Data[0x2002] = 0xCC; rom.Data[0x2003] = 0xDD;
            File.WriteAllBytes(Path.Combine(_tempDir, "sig.bin"), new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

            var patch = MakePatchInTempDir("p.txt", ("TYPE", "BIN"),
                ("PATCHED_IF:$FGREP4 sig.bin", "0xAA 0xBB"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Installed,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_FgrepFilePresent_PatchedIfMismatch_NotInstalled()
        {
            // Same planted signature/file, but the PATCHED_IF byte window does NOT match the ROM at the
            // resolved offset -> NotInstalled (provably absent). This proves the resolution is REAL (a
            // garbage offset could not deterministically mismatch) and that the gate can now say "safe".
            var rom = MakeVersionedRom("BE8E01");
            rom.Data[0x2000] = 0xAA; rom.Data[0x2001] = 0xBB; rom.Data[0x2002] = 0xCC; rom.Data[0x2003] = 0xDD;
            File.WriteAllBytes(Path.Combine(_tempDir, "sig.bin"), new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

            var patch = MakePatchInTempDir("p.txt", ("TYPE", "BIN"),
                ("PATCHED_IF:$FGREP4 sig.bin", "0xFF 0xFF"));   // window mismatches the ROM at 0x2000
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_FgrepFilePresent_PatchedIfNotMismatch_Installed()
        {
            // PATCHED_IFNOT is "installed when the un-patched signature is ABSENT". The $FGREP file
            // resolves to 0x2000; the byte window 0xFF 0xFF mismatches the ROM there -> the un-patched
            // sig is absent -> Installed. (Mirrors WF's detail-string "PATCHED_IF" rule for IFNOT.)
            var rom = MakeVersionedRom("BE8E01");
            rom.Data[0x2000] = 0xAA; rom.Data[0x2001] = 0xBB; rom.Data[0x2002] = 0xCC; rom.Data[0x2003] = 0xDD;
            File.WriteAllBytes(Path.Combine(_tempDir, "sig.bin"), new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

            var patch = MakePatchInTempDir("p.txt", ("TYPE", "BIN"),
                ("PATCHED_IFNOT:$FGREP4 sig.bin", "0xFF 0xFF"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Installed,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_FgrepFilePresentButPatternNotInRom_NotInstalled()
        {
            // The $FGREP file IS present, but its signature (the post-install byte pattern) is NOT anywhere
            // in the (zero-filled) ROM. U.Grep returns NOT_FOUND. WF's CheckIF treats this exactly as
            // NOT-MATCHING (the PATCHED_IF unsafe-address `continue`), so WF IsInstalled is FALSE. Because
            // Core's GREP/FGREP resolution is byte-identical to WF, Core classifies it NotInstalled too
            // (s2pf-18 #1261). This is the case that makes the gate USEFUL on vanilla FE8U (its 125
            // absent-signature $FGREP rows). FALSE-NEGATIVE IMPOSSIBLE: Core agrees with WF exactly.
            var rom = MakeVersionedRom("BE8E01"); // all zero — the signature below appears nowhere
            File.WriteAllBytes(Path.Combine(_tempDir, "sig.bin"), new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            var patch = MakePatchInTempDir("p.txt", ("TYPE", "BIN"),
                ("PATCHED_IF:$FGREP4 sig.bin", "0x00 0x00"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_InlineGrepAbsentSignature_NotInstalled_MatchesWf()
        {
            // The inline $GREP family (no file) gets the SAME WF-faithful treatment: an absent post-install
            // signature -> NOT_FOUND -> NotInstalled, NOT Unknown. (34 of FE8U's blocking rows are inline
            // $GREP, e.g. PATCH_256 colors titlebackground 20180611.) Byte-faithful to WF; sound.
            var rom = MakeVersionedRom("BE8E01"); // all zero — the 0xDEADBEEF signature appears nowhere
            var patch = MakePatch("p.txt", ("TYPE", "EA"),
                ("PATCHED_IF:$GREP4 0xDE 0xAD 0xBE 0xEF", "0x00 0x00"));
            Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                PatchHardCodeScanner.EaBinInstallStatus(rom, patch));
        }

        [Fact]
        public void EaBinInstallStatus_FgrepFileMissing_NotInstalled_NoThrow()
        {
            // The $FGREP file is MISSING. WF MakeGrepData(value, basedir) returns new byte[0] -> U.Grep
            // returns NOT_FOUND -> CheckIF's PATCHED_IF unsafe-address `continue` -> NOT-MATCHING ->
            // WF IsInstalled FALSE. Core mirrors this byte-faithfully -> NotInstalled. Headless-safe:
            // the missing file degrades to the empty pattern, never throws / ShowErrors.
            var rom = MakeVersionedRom("BE8E01");
            var patch = MakePatchInTempDir("p.txt", ("TYPE", "BIN"),
                ("PATCHED_IF:$FGREP4 absent_sig.bin", "0x00 0xB5"));
            var ex = Record.Exception(() =>
                Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
                    PatchHardCodeScanner.EaBinInstallStatus(rom, patch)));
            Assert.Null(ex);  // no throw + NotInstalled
        }

        // ====================================================================
        // 2. PatchFormHasUnportableInstalledPatch — the per-ROM predicate.
        // ====================================================================

        [Fact]
        public void Predicate_InstalledEaAndBin_ReturnsTrue()
        {
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_EA.txt"), new[]
            {
                "NAME=PatchEA", "TYPE=EA", "PATCHED_IF:0x001000=0x00 0x00", "ADDRESS=0x800100",
            });
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_BIN.txt"), new[]
            {
                "NAME=PatchBIN", "TYPE=BIN", "PATCHED_IF:0x001000=0x00 0x00", "ADDRESS=0x800200",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.True(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_PatchedIfNotInstalledEa_ReturnsTrue()
        {
            // Regression for Copilot #1325 false-negative #1: a PATCHED_IFNOT-installed EA patch must be
            // caught (the naive CheckIF=="I" predicate would MISS it).
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_IFNOT.txt"), new[]
            {
                "NAME=PatchIfNot", "TYPE=EA", "PATCHED_IFNOT:0x001000=0xFF 0xFF", "ADDRESS=0x800100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.True(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_UnknownXgrepEa_ReturnsTrue()
        {
            // Regression: an EA patch whose ONLY install marker uses an un-ported $XGREP (masked GREP)
            // signature is Unknown -> the gate refuses conservatively (soundness preserved for the macro
            // forms Core does NOT byte-faithfully resolve). NOTE: as of s2pf-18 (#1261) a $FGREP signature
            // is NO LONGER auto-Unknown — it resolves byte-faithfully like WF — so this regression uses
            // $XGREP, the form that genuinely stays Unknown.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_XGREP.txt"), new[]
            {
                "NAME=PatchXgrep", "TYPE=EA", "PATCHED_IF:$XGREP4 0x00 X 0xB5=0x00 0xB5", "ADDRESS=0x800100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.True(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_FgrepAbsentSignatureEa_ReturnsFalse_GateUseful()
        {
            // s2pf-18 GATE-USEFULNESS (#1261): an EA patch whose ONLY install marker is a $FGREP whose
            // file IS present but whose post-install signature is NOT in the (vanilla-like) ROM -> NOT_FOUND
            // -> NotInstalled (byte-faithful to WF's CheckIF `continue`). The predicate returns FALSE (the
            // rebuild PROCEEDS). This is the synthetic analog of the 125 absent-signature $FGREP rows that
            // formerly OVER-REFUSED vanilla FE8U.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllBytes(Path.Combine(patchDir, "sig.bin"),
                new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }); // present file, signature absent from the zero ROM
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_FGREP.txt"), new[]
            {
                "NAME=PatchFgrep", "TYPE=EA", "PATCHED_IF:$FGREP4 sig.bin=0x00 0xB5", "ADDRESS=0x800100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01"); // all zero -> 0xDEADBEEF appears nowhere
            CoreState.ROM = fe8;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_ResolvablyNotInstalledEaBin_ReturnsFalse()
        {
            // EA/BIN patches with a RESOLVABLE PATCHED_IF marker that does NOT match -> NotInstalled ->
            // the predicate allows (the patch's bytes are provably absent, so a rebuild drops nothing).
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_EA.txt"), new[]
            {
                "NAME=PatchEA", "TYPE=EA", "PATCHED_IF:0x001000=0xFF 0xFF", "ADDRESS=0x800100",
            });
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_BIN.txt"), new[]
            {
                "NAME=PatchBIN", "TYPE=BIN", "PATCHED_IF:0x001000=0xFF 0xFF", "ADDRESS=0x800200",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_FgrepFilePresent_ResolvablyNotInstalled_ReturnsFalse_GateUseful()
        {
            // s2pf-18 GATE-USEFULNESS (#1261): an EA/BIN patch whose ONLY install marker is a $FGREP
            // file-inclusion now RESOLVES (the planted .bin signature lives next to the patch, in the same
            // dir LoadPatch derives basedir from). The signature IS in the ROM (at 0x2000), but the
            // PATCHED_IF byte window does NOT match there -> NotInstalled -> the predicate returns FALSE
            // (the rebuild PROCEEDS). BEFORE this slice the $FGREP marker forced Unknown -> the predicate
            // returned true and OVER-REFUSED. This is the synthetic analog of the real-FE8U gate-open.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllBytes(Path.Combine(patchDir, "sig.bin"), new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_FGREP.txt"), new[]
            {
                "NAME=PatchFgrep", "TYPE=EA", "PATCHED_IF:$FGREP4 sig.bin=0xFF 0xFF", "ADDRESS=0x800100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            fe8.Data[0x2000] = 0xAA; fe8.Data[0x2001] = 0xBB; fe8.Data[0x2002] = 0xCC; fe8.Data[0x2003] = 0xDD;
            CoreState.ROM = fe8;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_FgrepFilePresent_PatchedIfMatch_StillRefuses_Installed()
        {
            // SOUNDNESS not weakened: when the $FGREP file resolves AND the PATCHED_IF window MATCHES the
            // ROM, the patch IS installed -> the predicate STILL returns true (the producer cannot emit an
            // installed EA/BIN patch's bytes, so the rebuild must refuse). $FGREP resolution narrows the
            // Unknown set; it never turns an actually-installed patch into a "safe" proceed.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllBytes(Path.Combine(patchDir, "sig.bin"), new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_FGREP.txt"), new[]
            {
                "NAME=PatchFgrep", "TYPE=EA", "PATCHED_IF:$FGREP4 sig.bin=0xAA 0xBB", "ADDRESS=0x800100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            fe8.Data[0x2000] = 0xAA; fe8.Data[0x2001] = 0xBB; fe8.Data[0x2002] = 0xCC; fe8.Data[0x2003] = 0xDD;
            CoreState.ROM = fe8;

            Assert.True(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_InstalledStruct_ReturnsFalse()
        {
            // A TYPE=STRUCT installed patch must NOT trip the predicate — STRUCT is faithfully emitted
            // (s2pf-5..10), so it is never a reason to refuse. Only EA/BIN are un-ported.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_STRUCT.txt"), new[]
            {
                "NAME=PatchStruct", "TYPE=STRUCT", "PATCHED_IF:0x001000=0x00 0x00", "P0:POINTER=0x100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_CanonicalSkipInstalledEa_ReturnsFalse()
        {
            // CANONICAL_SKIP=1 short-circuits the patch (isCanonicalSkip) BEFORE the TYPE/install gate —
            // exactly as the orchestrator skips it — so even an "installed" EA patch with CANONICAL_SKIP
            // set is not a reason to refuse.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_EA_SKIP.txt"), new[]
            {
                "NAME=PatchEaSkip", "TYPE=EA", "CANONICAL_SKIP=1", "PATCHED_IF:0x001000=0x00 0x00", "ADDRESS=0x800100",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_MixedTree_TrueWhenAnyInstalledEaBin()
        {
            // A realistic mix: an installed STRUCT (emittable, ignored), a resolvably-not-installed EA
            // (ignored), and ONE installed BIN (the offender) -> predicate true.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_STRUCT.txt"), new[]
            {
                "NAME=PatchStruct", "TYPE=STRUCT", "PATCHED_IF:0x001000=0x00 0x00", "P0:POINTER=0x100",
            });
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_EA_NOTINSTALLED.txt"), new[]
            {
                "NAME=PatchEaNot", "TYPE=EA", "PATCHED_IF:0x001000=0xFF 0xFF", "ADDRESS=0x800100",
            });
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_BIN_INSTALLED.txt"), new[]
            {
                "NAME=PatchBinInst", "TYPE=BIN", "PATCHED_IF:0x001000=0x00 0x00", "ADDRESS=0x800200",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.True(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_NoPatchDir_ReturnsFalse()
        {
            // An EMPTY config/patch2/FE8U tree -> ScanPatchs returns empty -> nothing installed -> false.
            // (The dir EXISTS but is empty so ResolvePatchDirectory pins it instead of falling through to
            // the build-copied real FE8U tree in bin/, same as the scaffold test.)
            StageFe8uPatchDir(); // exists, no PATCH_*.txt
            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));
        }

        [Fact]
        public void Predicate_Version0Rom_ReturnsFalse()
        {
            // An unrecognized ROM -> version 0 -> SafePatchVersionFolder "" -> no patch dir -> false.
            // Even with a populated FE8U tree present, the version-0 guard short-circuits the scan.
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_EA.txt"), new[]
            {
                "NAME=PatchEA", "TYPE=EA", "PATCHED_IF:0x001000=0x00 0x00", "ADDRESS=0x800100",
            });

            var rom = new ROM();
            rom.SwapNewROMDataDirect(new byte[0x8000]);
            CoreState.ROM = rom;
            CoreState.BaseDirectory = _tempDir;

            Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(rom));
        }

        [Fact]
        public void Predicate_NullRom_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RebuildProducerCore.PatchFormHasUnportableInstalledPatch(null!));
        }

        // ====================================================================
        // 3. MakeWithProducer — the per-ROM backstop refuses (naming the patch).
        // ====================================================================

        [Fact]
        public void MakeWithProducer_InstalledEaBin_Throws_NamingThePatch()
        {
            // The public MakeWithProducer(rom, vanilla, ...) overload must REFUSE before running the
            // producers when the rom carries an installed EA/BIN patch — and the message must NAME the
            // offending patch file. This is the per-ROM backstop path (distinct from the IsComplete-gate).
            string patchDir = StageFe8uPatchDir();
            File.WriteAllLines(Path.Combine(patchDir, "PATCH_BIN.txt"), new[]
            {
                "NAME=PatchBIN", "TYPE=BIN", "PATCHED_IF:0x001000=0x00 0x00", "ADDRESS=0x800200",
            });

            CoreState.BaseDirectory = _tempDir;
            var fe8 = MakeVersionedRom("BE8E01");
            CoreState.ROM = fe8;

            var vanilla = MakeVersionedRom("BE8E01");
            string manifest = Path.Combine(_tempDir, "out.rebuild");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                RebuildProducerCore.MakeWithProducer(
                    fe8, vanilla, 0x800000u, manifest,
                    isUseOtherGraphics: false, isUseOAMSP: false));

            Assert.Contains("EA/BIN", ex.Message);
            Assert.Contains("PATCH_BIN.txt", ex.Message);     // names the offending patch file
            Assert.False(File.Exists(manifest));              // refused before Make -> no manifest
        }

        [Fact]
        public void MakeWithProducer_NoEaBin_DisasmUnwired_RefusesOnDisasmGate_NotPatchFormToken()
        {
            // s2pf-17 (CAPSTONE): the "PatchForm(MakePatchStructDataList)" token is REMOVED (EA/BIN arms
            // wired LIVE), so the IsComplete gate no longer refuses for that reason. With NO installed/
            // unknown EA/BIN patch the per-ROM backstop passes (false). On this synthetic ROM the event-
            // script disassembler is NOT wired (CoreState.EventScript == null), so BOTH producers re-report
            // their disasm-gated forms at runtime (EventCondForm / EventScript(MakeEventASMMAPList)) and the
            // IsComplete gate STILL refuses — but it now names the DISASM forms, NOT the PatchForm token.
            StageFe8uPatchDir(); // empty -> no installed/unknown EA/BIN -> backstop passes
            CoreState.BaseDirectory = _tempDir;
            EventScript? savedEs = CoreState.EventScript;
            try
            {
                CoreState.EventScript = null!; // disasm unwired -> EventCond/EventScript re-reported
                var fe8 = MakeVersionedRom("BE8E01");
                CoreState.ROM = fe8;

                // Sanity: the per-ROM backstop is NOT the reason for the refusal here.
                Assert.False(RebuildProducerCore.PatchFormHasUnportableInstalledPatch(fe8));

                var vanilla = MakeVersionedRom("BE8E01");
                string manifest = Path.Combine(_tempDir, "out.rebuild");

                var ex = Assert.Throws<InvalidOperationException>(() =>
                    RebuildProducerCore.MakeWithProducer(
                        fe8, vanilla, 0x800000u, manifest,
                        isUseOtherGraphics: false, isUseOAMSP: false));

                // The IsComplete-gate refusal now names the runtime DISASM forms, NOT the removed token.
                Assert.DoesNotContain("PatchForm(MakePatchStructDataList)", ex.Message);
                Assert.Contains("not yet ported", ex.Message);
                Assert.True(
                    ex.Message.Contains("EventScript(MakeEventASMMAPList)")
                    || ex.Message.Contains("EventCondForm"),
                    "expected the disasm-gate forms in the refusal message: " + ex.Message);
                Assert.False(File.Exists(manifest));
            }
            finally
            {
                CoreState.EventScript = savedEs!;
            }
        }

        [Fact]
        public void Token_RemovedFromAsmNotYetPorted_GateOpened()
        {
            // s2pf-17 (CAPSTONE) OPENS the gate: the "PatchForm(MakePatchStructDataList)" token is REMOVED
            // (the EA/BIN arms are wired LIVE), so the static ASM-path deferred list is empty and
            // AsmProducerResult.IsComplete can flip true. The per-ROM s2pf-12 backstop remains the soundness
            // net for an installed-but-unemittable EA/BIN patch (proven by the other tests in this file).
            string[] liveAsmNotYet = RebuildProducerCore.GetAsmNotYetPortedForms();
            Assert.DoesNotContain("PatchForm(MakePatchStructDataList)", liveAsmNotYet);
            Assert.Empty(liveAsmNotYet);
        }
    }
}
