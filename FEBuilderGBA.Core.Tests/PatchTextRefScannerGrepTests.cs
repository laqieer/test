// SPDX-License-Identifier: GPL-3.0-or-later
// #1027 — End-to-end scanner tests for the grep-resolved patch TEXT/SONG/EVENT
// reference path. Complements PatchMacroAddressResolverCoreTests (resolver-level)
// by exercising the FULL PatchTextRefScannerCore.CollectUsedRefs pipeline against
// synthetic on-disk PATCH_*.txt files:
//
//   A — GREP-resolved TEXT ADDRESS (ENDA variant) contributes its u16 id.
//   B — GREP-based PATCHED_IF install detection (positive + negative).
//   C — FGREP-resolved TEXT ADDRESS with a REAL on-disk pattern file (basedir read).
//   D — $GREP<align> blocksize: align=2 only matches even offsets (resolver-level).
//
// Each test uses a FRESH unique temp BaseDirectory (so planted patches never leak
// across tests) and saves/restores CoreState (BaseDirectory, Language, ROM) exactly
// like TextFreeAreaCoreTests. The patch directory is keyed by the ROM's
// VersionToFilename ("FE8U" for the synthetic BE8E01 ROM) — verified against
// PatchTextRefScannerCore.ResolvePatchDirectory.

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using FEBuilderGBA;

namespace FEBuilderGBA.Core.Tests
{
    [Collection("SharedState")]
    public class PatchTextRefScannerGrepTests
    {
        [Theory]
        [InlineData("text")]
        [InlineData("song")]
        [InlineData("pointer")]
        [InlineData("count")]
        [InlineData("condition")]
        public void AuditedSiblingOperandsReachEveryActualTextReferenceReader(string role)
        {
            WithIsolatedPatchDir((rom, versionRoot) =>
            {
                byte[] pattern = { 0xDE, 0xAD, 0xBE, 0xEF };
                pattern.CopyTo(rom.Data, 0x6000);
                pattern.CopyTo(rom.Data, 0x7002);
                rom.Data[0x500] = 0x99;
                rom.Data[0x501] = 0x42;
                rom.Data[0x6004] = rom.Data[0x7000] = 0x34;
                rom.Data[0x6005] = rom.Data[0x7001] = 0x12;
                string condition = "PATCHED_IF:0x500=0x99 0x42";
                string fields = role switch
                {
                    "text" => "TYPE=ADDR\nADDRESS_TYPE=TEXT\nADDRESS=$FGREP4ENDA ../shared/pattern.bin",
                    "song" => "TYPE=ADDR\nADDRESS_TYPE=SONG\nADDRESS=$FGREP4ENDA ../shared/pattern.bin",
                    "pointer" => "TYPE=STRUCT\nPOINTER=$FGREP4ENDA ../shared/pattern.bin\nDATASIZE=2\nDATACOUNT=1\nW0:TEXT=Text",
                    "count" => "TYPE=STRUCT\nADDRESS=0x7000\nDATASIZE=2\nDATACOUNT=$FGREP1 ../shared/pattern.bin\nW0:TEXT=Text",
                    _ => "TYPE=ADDR\nADDRESS_TYPE=TEXT\nADDRESS=0x7000",
                };
                if (role == "pointer") rom.write_p32(0x6004, 0x7000);
                if (role == "condition") condition = "PATCHED_IF:$FGREP4 ../shared/pattern.bin=0xDE 0xAD";
                string dir = WritePatch(versionRoot, "nested", fields + "\n" + condition);
                string definition = Path.Combine(dir, "PATCH_TEST.txt");
                string patternPath = Path.Combine(versionRoot, "shared", "pattern.bin");
                Directory.CreateDirectory(Path.GetDirectoryName(patternPath)!);
                File.WriteAllBytes(patternPath, pattern);
                var audit = PatchDatabaseMetadataAuditCore.Audit(versionRoot, new Dictionary<string, long>
                {
                    ["nested/PATCH_TEST.txt"] = new FileInfo(definition).Length,
                    ["shared/pattern.bin"] = pattern.Length,
                });
                Assert.Single(audit.References);
                var ids = new HashSet<uint>();
                var songs = new HashSet<uint>();
                PatchTextRefScannerCore.CollectUsedRefs(rom, ids, songs);
                if (role == "song") Assert.Contains(0x34u, songs);
                else Assert.Contains(0x1234u, ids);
            });
        }

        [Theory]
        [InlineData("ADDRESS=$FGREP4 {0}")]
        [InlineData("POINTER=$FGREP4 {0}")]
        [InlineData("DATACOUNT=$FGREP4 {0}")]
        [InlineData("PATCHED_IF:$FGREP4 {0}=0xDE 0xAD")]
        public void RejectedTextReferenceOperandsNeverReachSharedResolverFileReads(string format)
        {
            WithIsolatedPatchDir((rom, versionRoot) =>
            {
                const string operand = @"\\unused.invalid\share\pattern.bin";
                string dir = WritePatch(versionRoot, "nested", "TYPE=STRUCT\n" + string.Format(format, operand));
                string definition = Path.Combine(dir, "PATCH_TEST.txt");
                int consumers = 0, existsCalls = 0, readCalls = 0;
                Assert.Throws<InvalidDataException>(() =>
                {
                    PatchDatabaseMetadataAuditCore.Audit(versionRoot, new Dictionary<string, long>
                    {
                        ["nested/PATCH_TEST.txt"] = new FileInfo(definition).Length,
                    });
                    consumers++;
                    _ = PatchMacroAddressResolverCore.ResolveWithFileReadsForTest(rom, "$FGREP4 " + operand, dir, 0x100,
                        _ => { existsCalls++; return false; },
                        _ => { readCalls++; return Array.Empty<byte>(); });
                });
                Assert.Equal(0, consumers);
                Assert.Equal(0, existsCalls);
                Assert.Equal(0, readCalls);
            });
        }

        // ---- ROM builder (mirrors PatchMacroAddressResolverCoreTests) -------
        // Minimal synthetic FE8U ROM. LoadLow requires >= 0x1000000 (16 MB) for
        // BE8E01. Zero-filled so planted patterns are unique.
        static ROM MakeRom()
        {
            var rom = new ROM();
            byte[] data = new byte[0x1000000]; // 16 MB
            rom.LoadLow("x.gba", data, "BE8E01");
            return rom;
        }

        // Run `body` with a fresh isolated temp BaseDirectory + CoreState save/restore.
        // `body` receives (rom, patchVersionDir) where patchVersionDir is
        // <tmp>/config/patch2/<VersionToFilename>/ — the directory CollectUsedRefs scans.
        static void WithIsolatedPatchDir(Action<ROM, string> body)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "fegreptest_" + Guid.NewGuid().ToString("N"));

            var savedRom = CoreState.ROM;
            var savedLang = CoreState.Language;
            var savedBaseDir = CoreState.BaseDirectory;
            try
            {
                Directory.CreateDirectory(tmp);
                CoreState.BaseDirectory = tmp;
                CoreState.Language = ""; // Japanese suffix "" — synthetic patches use no NAME.{lang}

                var rom = MakeRom();
                CoreState.ROM = rom; // realism: ScanStruct EVENT-follow path gates on active ROM

                // Verified: PatchTextRefScannerCore.ResolvePatchDirectory keys on
                // rom.RomInfo.VersionToFilename ("FE8U"), NOT the language suffix.
                string version = rom.RomInfo.VersionToFilename;
                string patchVersionDir = Path.Combine(tmp, "config", "patch2", version);
                Directory.CreateDirectory(patchVersionDir);

                body(rom, patchVersionDir);
            }
            finally
            {
                CoreState.ROM = savedRom;
                CoreState.Language = savedLang;
                CoreState.BaseDirectory = savedBaseDir;   // unconditional restore (was: if (savedBaseDir != null) ...)
                                                          // — restoring to null is REQUIRED so a deleted tmp dir
                                                          //   never leaks into downstream SharedState tests.
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }
        }

        // Write a PATCH_*.txt under <patchVersionDir>/<subdir>/PATCH_TEST.txt.
        // Returns the patch sub-directory (basedir for $FGREP file lookups).
        static string WritePatch(string patchVersionDir, string subdir, string content)
        {
            string dir = Path.Combine(patchVersionDir, subdir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "PATCH_TEST.txt"), content);
            return dir;
        }

        // =================================================================
        // Test A — End-to-end GREP TEXT param -> used-ref union (ENDA variant).
        // =================================================================
        [Fact]
        public void A_GrepEnda_TextAddress_ContributesU16Id()
        {
            WithIsolatedPatchDir((rom, patchVersionDir) =>
            {
                // Pattern DE AD BE EF at 0x6000; ADDRESS = $GREP1ENDA resolves to
                // match_begin + 4 (= 0x6004), with NO pointer requirement (ENDA).
                rom.Data[0x6000] = 0xDE;
                rom.Data[0x6001] = 0xAD;
                rom.Data[0x6002] = 0xBE;
                rom.Data[0x6003] = 0xEF;
                // u16 text id 0x1234 (little-endian bytes 34 12) at 0x6004.
                rom.Data[0x6004] = 0x34;
                rom.Data[0x6005] = 0x12;
                // PATCHED_IF install marker: bytes 99 42 at 0x500. WF CheckIF requires
                // a value with >= 2 space-separated tokens to be decisive, so the marker
                // is two bytes (a single byte is skipped as indecisive).
                rom.Data[0x500] = 0x99;
                rom.Data[0x501] = 0x42;

                WritePatch(patchVersionDir, "MyGrepPatch",
                    "NAME=MyGrepPatch\n" +
                    "TYPE=ADDR\n" +
                    "ADDRESS_TYPE=TEXT\n" +
                    "ADDRESS=$GREP1ENDA 0xDE 0xAD 0xBE 0xEF\n" +
                    "PATCHED_IF:0x500=0x99 0x42\n");

                var ids = new HashSet<uint>();
                PatchTextRefScannerCore.CollectUsedRefs(rom, ids, null!);

                Assert.Contains(0x1234u, ids);
            });
        }

        // =================================================================
        // Test B (positive) — GREP-based PATCHED_IF resolves + byte-matches ->
        // patch detected installed -> ADDR TEXT ref counted.
        // =================================================================
        [Fact]
        public void B_GrepInstallIf_Positive_RefCounted()
        {
            WithIsolatedPatchDir((rom, patchVersionDir) =>
            {
                // PATCHED_IF: $GREP1 77 88 99 = 77 88 99. Plant the pattern at 0x6500
                // so the GREP resolves to 0x6500 (match-begin) AND the byte-compare of
                // the value tokens 77 88 99 at that address matches -> CheckIF returns "I".
                rom.Data[0x6500] = 0x77;
                rom.Data[0x6501] = 0x88;
                rom.Data[0x6502] = 0x99;
                // Literal ADDRESS 0x7000 holds u16 text id 0x3456 (bytes 56 34).
                rom.Data[0x7000] = 0x56;
                rom.Data[0x7001] = 0x34;

                WritePatch(patchVersionDir, "MyGrepIfPatch",
                    "NAME=MyGrepIfPatch\n" +
                    "TYPE=ADDR\n" +
                    "ADDRESS_TYPE=TEXT\n" +
                    "ADDRESS=0x7000\n" +
                    "PATCHED_IF:$GREP1 0x77 0x88 0x99=0x77 0x88 0x99\n");

                var ids = new HashSet<uint>();
                PatchTextRefScannerCore.CollectUsedRefs(rom, ids, null!);

                Assert.Contains(0x3456u, ids);
            });
        }

        // =================================================================
        // Test B (negative) — GREP-based PATCHED_IF cannot resolve (pattern absent)
        // -> patch NOT detected installed -> ADDR TEXT ref NOT counted.
        //
        // WF-parity trace (CheckIF): key "PATCHED_IF" => isnot=true. An unresolvable
        // address (GREP NOT_FOUND) on an isnot condition is SKIPPED (continue), so no
        // decisive IF fires -> CheckIF == "" -> ScanOnePatch requires "I" for ADDR ->
        // patch skipped -> ref not added. This is the CORRECT behavior (verified).
        // =================================================================
        [Fact]
        public void B_GrepInstallIf_Negative_RefNotCounted()
        {
            WithIsolatedPatchDir((rom, patchVersionDir) =>
            {
                // Do NOT plant 77 88 99 anywhere — the zero-filled ROM cannot contain it,
                // so the GREP-IF is unresolvable. Use a distinct id (0x3457) to be
                // unambiguous vs the positive test.
                rom.Data[0x7000] = 0x57;
                rom.Data[0x7001] = 0x34; // u16 0x3457

                WritePatch(patchVersionDir, "MyGrepIfPatchNeg",
                    "NAME=MyGrepIfPatch\n" +
                    "TYPE=ADDR\n" +
                    "ADDRESS_TYPE=TEXT\n" +
                    "ADDRESS=0x7000\n" +
                    "PATCHED_IF:$GREP1 0x77 0x88 0x99=0x77 0x88 0x99\n");

                var ids = new HashSet<uint>();
                PatchTextRefScannerCore.CollectUsedRefs(rom, ids, null!);

                Assert.DoesNotContain(0x3457u, ids);
            });
        }

        // =================================================================
        // Test C — FGREP with a REAL on-disk pattern file (basedir read end-to-end).
        // =================================================================
        [Fact]
        public void C_Fgrep_RealFile_TextAddress_ContributesU16Id()
        {
            WithIsolatedPatchDir((rom, patchVersionDir) =>
            {
                // Pattern 01 02 03 04 in ROM at 0x6800; ADDRESS = $FGREP1ENDA pat.bin
                // resolves to match_begin + 4 (= 0x6804).
                rom.Data[0x6800] = 0x01;
                rom.Data[0x6801] = 0x02;
                rom.Data[0x6802] = 0x03;
                rom.Data[0x6803] = 0x04;
                // u16 text id 0x1357 (bytes 57 13) at 0x6804.
                rom.Data[0x6804] = 0x57;
                rom.Data[0x6805] = 0x13;
                // PATCHED_IF install marker: 2-byte value (WF CheckIF needs >= 2 tokens).
                rom.Data[0x500] = 0x99;
                rom.Data[0x501] = 0x42;

                string patchDir = WritePatch(patchVersionDir, "MyFgrepPatch",
                    "NAME=MyFgrepPatch\n" +
                    "TYPE=ADDR\n" +
                    "ADDRESS_TYPE=TEXT\n" +
                    "ADDRESS=$FGREP1ENDA pat.bin\n" +
                    "PATCHED_IF:0x500=0x99 0x42\n");

                // pat.bin lives in the patch dir (the resolver's basedir).
                File.WriteAllBytes(Path.Combine(patchDir, "pat.bin"),
                    new byte[] { 0x01, 0x02, 0x03, 0x04 });

                var ids = new HashSet<uint>();
                PatchTextRefScannerCore.CollectUsedRefs(rom, ids, null!);

                Assert.Contains(0x1357u, ids);
            });
        }

        // =================================================================
        // Test D — $GREP<align> blocksize (resolver-level). align=2 only scans even
        // offsets, so a pattern at an ODD offset is NOT_FOUND while the same pattern
        // at an EVEN offset is found.
        // =================================================================
        [Fact]
        public void D_Grep2_Blocksize_OnlyMatchesEvenOffsets()
        {
            // ODD offset 0x6901 — align=2 scan (0x100, 0x102, ...) skips it.
            var oddRom = MakeRom();
            oddRom.Data[0x6901] = 0xC3;
            oddRom.Data[0x6902] = 0xD4;
            uint oddResult = PatchMacroAddressResolverCore.Resolve(oddRom, "$GREP2 0xC3 0xD4", "", 0x100);
            Assert.Equal(U.NOT_FOUND, oddResult);

            // EVEN offset 0x6902 — reachable by the align=2 scan.
            var evenRom = MakeRom();
            evenRom.Data[0x6902] = 0xC3;
            evenRom.Data[0x6903] = 0xD4;
            uint evenResult = PatchMacroAddressResolverCore.Resolve(evenRom, "$GREP2 0xC3 0xD4", "", 0x100);
            Assert.Equal(0x6902u, evenResult);
        }
    }
}
