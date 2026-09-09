// SPDX-License-Identifier: GPL-3.0-or-later
// Core tests for PatchHardCodeScanner + CoreAsmMapCache (#1035).
//
// PatchHardCodeScanner ports the patch-scan part of WinForms
// PatchForm.MakeHardCodeWarning: enumerate PATCH_*.txt, skip CANONICAL_SKIP,
// skip when the CheckIF tri-state gate returns "E", then read the ADDRESS-param
// 8-bit id and set the matching UNIT/CLASS/ITEM hardcode array slot.
//
// Tests seed temporary PATCH_*.txt files under a temp config/patch2/{ver}
// directory (pointed at via CoreState.BaseDirectory) so the scanner exercises
// the real enumeration + parse + gate + address-read path without depending on
// the config/patch2 submodule being checked out.
using System;
using System.IO;
using System.Reflection;
using FEBuilderGBA;
using Xunit;

namespace FEBuilderGBA.Core.Tests
{
    [Collection("SharedState")]
    public class PatchHardCodeScannerTests
    {
        public static System.Collections.Generic.IEnumerable<object[]> ImportedConditionCases()
        {
            foreach (string key in new[] { "IF", "IFNOT", "PATCHED_IF", "PATCHED_IFNOT", "CONFLICT_IF" })
                foreach (string macro in new[] { "$FGREP4", "$FGREP4END", "$FGREP4ENDA", "$FGREP4END+1" })
                    yield return new object[] { key, macro };
        }

        [Theory]
        [MemberData(nameof(ImportedConditionCases))]
        public void AuditedConditionsPreserveTheIndependentReadersSemantics(string key, string macro)
        {
            var (root, versionRoot) = MakeTempPatchDir();
            try
            {
                Directory.CreateDirectory(Path.Combine(versionRoot, "nested"));
                Directory.CreateDirectory(Path.Combine(versionRoot, "shared"));
                string definition = Path.Combine(versionRoot, "nested", "PATCH_audit.txt");
                string pattern = Path.Combine(versionRoot, "shared", "pattern.bin");
                byte[] bytes = { 0xAA, 0xBB, 0xCC, 0xDD };
                File.WriteAllBytes(pattern, bytes);
                File.WriteAllText(definition, "TYPE=BIN\n" + key + ":" + macro + " ../shared/pattern.bin=0xAA 0xBB");
                PatchDatabaseMetadataAuditCore.Audit(versionRoot, new System.Collections.Generic.Dictionary<string, long>
                {
                    ["nested/PATCH_audit.txt"] = new FileInfo(definition).Length,
                    ["shared/pattern.bin"] = bytes.Length,
                });
                ROM rom = MakeFe8uRom();
                foreach (int offset in new[] { 0x2000, 0x3000 })
                {
                    bytes.CopyTo(rom.Data, offset);
                    rom.Data[offset + 4] = 0xAA;
                    rom.Data[offset + 5] = 0xBB;
                }
                var patch = PatchHardCodeScanner.LoadPatch(rom, definition, "en");
                int existsCalls = 0, readCalls = 0;
                bool Exists(string path)
                {
                    existsCalls++;
                    Assert.Equal(pattern, Path.GetFullPath(path));
                    return true;
                }
                byte[] Read(string path)
                {
                    readCalls++;
                    Assert.Equal(pattern, Path.GetFullPath(path));
                    return bytes;
                }
                Assert.Equal(PatchHardCodeScanner.CheckIF(rom, patch),
                    PatchHardCodeScanner.CheckIFWithFileReadsForTest(rom, patch, Exists, Read));
                Assert.Equal(1, existsCalls);
                Assert.Equal(1, readCalls);
                Assert.Equal(PatchHardCodeScanner.EaBinInstallStatus(rom, patch),
                    PatchHardCodeScanner.EaBinInstallStatusWithFileReadsForTest(rom, patch, Exists, Read));
                int expected = key is "PATCHED_IF" or "PATCHED_IFNOT" ? 2 : 1;
                Assert.Equal(expected, existsCalls);
                Assert.Equal(expected, readCalls);
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [MemberData(nameof(ImportedConditionCases))]
        public void RejectedNetworkConditionNeverCallsTheIndependentReader(string key, string macro)
        {
            var (root, versionRoot) = MakeTempPatchDir();
            int existsCalls = 0, readCalls = 0, consumerCalls = 0;
            try
            {
                string definition = Path.Combine(versionRoot, "PATCH_rejected.txt");
                File.WriteAllText(definition, "TYPE=BIN\n" + key + ":" + macro +
                    @" \\unused.invalid\share\pattern.bin=0xAA 0xBB");
                Assert.Throws<InvalidDataException>(() =>
                {
                    PatchDatabaseMetadataAuditCore.Audit(versionRoot,
                        new System.Collections.Generic.Dictionary<string, long>
                        {
                            ["PATCH_rejected.txt"] = new FileInfo(definition).Length,
                        });
                    consumerCalls++;
                    var patch = PatchHardCodeScanner.LoadPatch(MakeFe8uRom(), definition, "en");
                    _ = PatchHardCodeScanner.CheckIFWithFileReadsForTest(MakeFe8uRom(), patch,
                        _ => { existsCalls++; return false; },
                        _ => { readCalls++; return Array.Empty<byte>(); });
                });
                Assert.Equal(0, consumerCalls);
                Assert.Equal(0, existsCalls);
                Assert.Equal(0, readCalls);
            }
            finally { Directory.Delete(root, true); }
        }

        // ---- helpers -------------------------------------------------------

        // Build a synthetic FE8U ROM (version != 0, VersionToFilename == "FE8U").
        // The byte at offset `idAddr` is set to `idValue` so an ADDRESS-param read
        // there returns that id. start_offset for safe reads is >= 0x200.
        static ROM MakeFe8uRom(uint idAddr = 0x1000, byte idValue = 5)
        {
            var data = new byte[0x1000000];
            data[idAddr] = idValue;
            var rom = new ROM();
            bool ok = rom.LoadLow("hardcode-fe8u.gba", data, "BE8E01");
            Assert.True(ok);
            Assert.Equal(8, rom.RomInfo.version);
            Assert.Equal("FE8U", rom.RomInfo.VersionToFilename);
            return rom;
        }

        // Build a synthetic FE8J ROM (multibyte == true) — used to prove the
        // scanner's {J}/{U} language-line filter reads the PASSED rom, not the
        // ambient CoreState.ROM. VersionToFilename == "FE8J".
        static ROM MakeFe8jRom(uint idAddr = 0x1000, byte idValue = 5)
        {
            var data = new byte[0x1000000];
            data[idAddr] = idValue;
            var rom = new ROM();
            bool ok = rom.LoadLow("hardcode-fe8j.gba", data, "BE8J01");
            Assert.True(ok);
            Assert.True(rom.RomInfo.is_multibyte);
            return rom;
        }

        // Create a temp config/patch2/FE8U dir, return (tempRoot, versionDir).
        static (string root, string verDir) MakeTempPatchDir()
        {
            string root = Path.Combine(Path.GetTempPath(),
                "fe_hc_" + Guid.NewGuid().ToString("N"));
            string verDir = Path.Combine(root, "config", "patch2", "FE8U");
            Directory.CreateDirectory(verDir);
            return (root, verDir);
        }

        static void WritePatch(string verDir, string fileNameNoExt, params string[] lines)
        {
            string path = Path.Combine(verDir, "PATCH_" + fileNameNoExt + ".txt");
            File.WriteAllLines(path, lines);
        }

        // Run the scanner with CoreState.BaseDirectory pointed at `root` so the
        // resolver finds {root}/config/patch2/FE8U first.
        static (bool[] unit, bool[] cls, bool[] item) RunScan(ROM rom, string root)
        {
            string saved = CoreState.BaseDirectory;
            string savedLang = CoreState.Language;
            try
            {
                CoreState.BaseDirectory = root;
                CoreState.Language = "en";
                var unit = new bool[256];
                var cls = new bool[256];
                var item = new bool[256];
                PatchHardCodeScanner.ScanHardCodes(rom, unit, cls, item);
                return (unit, cls, item);
            }
            finally
            {
                CoreState.BaseDirectory = saved;
                CoreState.Language = savedLang;
            }
        }

        // ---- seeded UNIT/CLASS/ITEM -> correct array bit -------------------

        [Fact]
        public void SeededUnitPatch_SetsUnitBit()
        {
            var rom = MakeFe8uRom(0x2000, 7);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "MyUnit",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, cls, item) = RunScan(rom, root);
                Assert.True(unit[7]);
                Assert.False(cls[7]);
                Assert.False(item[7]);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void SeededClassPatch_SetsClassBit()
        {
            var rom = MakeFe8uRom(0x2400, 0x0C);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "MyClass",
                    "TYPE=ADDR",
                    "ADDRESS=0x2400",
                    "ADDRESS_TYPE=CLASS");
                var (unit, cls, item) = RunScan(rom, root);
                Assert.True(cls[0x0C]);
                Assert.False(unit[0x0C]);
                Assert.False(item[0x0C]);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void SeededItemPatch_SetsItemBit()
        {
            var rom = MakeFe8uRom(0x2800, 0x21);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "MyItem",
                    "TYPE=ADDR",
                    "ADDRESS=0x2800",
                    "ADDRESS_TYPE=ITEM");
                var (unit, cls, item) = RunScan(rom, root);
                Assert.True(item[0x21]);
                Assert.False(unit[0x21]);
                Assert.False(cls[0x21]);
            }
            finally { TryDelete(root); }
        }

        // ---- no patch dir -> all false ------------------------------------

        [Fact]
        public void NoPatchDir_AllFalse()
        {
            var rom = MakeFe8uRom(0x2000, 7);
            string root = Path.Combine(Path.GetTempPath(),
                "fe_hc_missing_" + Guid.NewGuid().ToString("N"));
            // intentionally do NOT create the dir
            var (unit, cls, item) = RunScan(rom, root);
            Assert.DoesNotContain(true, unit);
            Assert.DoesNotContain(true, cls);
            Assert.DoesNotContain(true, item);
        }

        // ---- GetAddr: id == 0 skipped -------------------------------------

        [Fact]
        public void IdZero_IsSkipped()
        {
            var rom = MakeFe8uRom(0x3000, 0); // byte is 0 at the address
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "ZeroUnit",
                    "TYPE=ADDR",
                    "ADDRESS=0x3000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit);
            }
            finally { TryDelete(root); }
        }

        // ---- GetAddr: unsafe / out-of-range ADDRESS guarded ---------------

        [Fact]
        public void UnsafeAddress_Guarded_NoFlag_NoThrow()
        {
            var rom = MakeFe8uRom(0x2000, 7);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                // 0x09000000 is past the 0x02000000 safety ceiling AND off the ROM.
                WritePatch(verDir, "OOR",
                    "TYPE=ADDR",
                    "ADDRESS=0x09000000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit); // guarded -> id 0 -> skipped
            }
            finally { TryDelete(root); }
        }

        // ---- GetAddr: multi-token ADDRESS (first token used) --------------

        [Fact]
        public void MultiTokenAddress_UsesFirstToken()
        {
            // Eirika's Tale-style: ADDRESS=0x037B86 0x33270 -> first token only.
            var rom = MakeFe8uRom(0x037B86, 0x01);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "EirikaTale",
                    "TYPE=ADDR",
                    "ADDRESS=0x037B86 0x33270",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.True(unit[0x01]);
            }
            finally { TryDelete(root); }
        }

        // ---- GetAddr: pointer-style 0x08...... ADDRESS --------------------

        [Fact]
        public void PointerStyleAddress_ResolvesToOffset()
        {
            // 0x08037000 -> offset 0x037000 (toOffset). Place the id there.
            var rom = MakeFe8uRom(0x037000, 9);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "PtrStyle",
                    "TYPE=ADDR",
                    "ADDRESS=0x08037000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.True(unit[9]);
            }
            finally { TryDelete(root); }
        }

        // ---- CheckIF gate parity ------------------------------------------

        [Fact]
        public void NoCondition_Included()
        {
            var rom = MakeFe8uRom(0x2000, 7);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "NoCond",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.True(unit[7]); // no IF gate -> included
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void IF_PositiveMatch_Included()
        {
            // ROM byte at 0x4000 == 0xAB so the IF matches -> not "E" -> included.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x4000] = 0xAB;
            rom.Data[0x4001] = 0xCD;
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "IfPos",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "IF:0x4000=0xAB 0xCD");
                var (unit, _, _) = RunScan(rom, root);
                Assert.True(unit[7]);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void IF_PositiveMismatch_ExcludedWithE()
        {
            // ROM byte differs from the IF need -> positive IF not-found -> "E" -> skip.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x4000] = 0x00; // need is 0xAB -> mismatch
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "IfMiss",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "IF:0x4000=0xAB 0xCD");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit); // gated out by "E"
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void IFNOT_Inverted_MatchExcludes()
        {
            // IFNOT matches the ROM -> inverted -> "E" -> skip.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x4000] = 0xAB;
            rom.Data[0x4001] = 0xCD;
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "IfNot",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "IFNOT:0x4000=0xAB 0xCD");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit); // IFNOT matched -> excluded
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void IFNOT_Inverted_MismatchIncludes()
        {
            // IFNOT does NOT match the ROM -> inverted not-found -> passes -> included.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x4000] = 0x11; // differs from need 0xAB
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "IfNotMiss",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "IFNOT:0x4000=0xAB 0xCD");
                var (unit, _, _) = RunScan(rom, root);
                Assert.True(unit[7]);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void PATCHED_IF_MatchYieldsI_NotE_Included()
        {
            // PATCHED_IF is inverted (isnot=true). When it MATCHES (notFound==false)
            // CheckIF returns "I" (installed) — NOT "E" — so the patch is included.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x5000] = 0xDE;
            rom.Data[0x5001] = 0xAD;
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "PatchedIf",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "PATCHED_IF:0x5000=0xDE 0xAD");
                var (unit, _, _) = RunScan(rom, root);
                Assert.True(unit[7]); // "I" is not "E" -> included
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void PATCHED_IFNOT_PositiveMismatch_ExcludedWithE()
        {
            // PATCHED_IFNOT is treated as a POSITIVE condition (isnot=false). When
            // the ROM does NOT match -> notFound==true -> "E" -> skipped.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x5000] = 0x00; // need 0xDE -> mismatch
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "PatchedIfNot",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "PATCHED_IFNOT:0x5000=0xDE 0xAD");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit);
            }
            finally { TryDelete(root); }
        }

        [Fact]
        public void CONFLICT_IF_Inverted_MatchExcludes()
        {
            // CONFLICT_IF is inverted (isnot=true). When it MATCHES (notFound==false)
            // and the key is not PATCHED_IF -> "E" -> excluded.
            var rom = MakeFe8uRom(0x2000, 7);
            rom.Data[0x6000] = 0xC0;
            rom.Data[0x6001] = 0xDE;
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "ConflictIf",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "CONFLICT_IF:0x6000=0xC0 0xDE");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit);
            }
            finally { TryDelete(root); }
        }

        // ---- CANONICAL_SKIP -----------------------------------------------

        [Fact]
        public void CanonicalSkip_Excludes()
        {
            var rom = MakeFe8uRom(0x2000, 7);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "Canon",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT",
                    "CANONICAL_SKIP=true");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit);
            }
            finally { TryDelete(root); }
        }

        // ---- patch without TYPE is not a patch ----------------------------

        [Fact]
        public void NoType_NotAPatch_Ignored()
        {
            var rom = MakeFe8uRom(0x2000, 7);
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "NoType",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit);
            }
            finally { TryDelete(root); }
        }

        // ---- language-line filter uses the PASSED rom, NOT CoreState.ROM --

        // Regression for the Copilot review on PR #1105: LoadPatch's {J}/{U}
        // language-line filter must use U.OtherLangLine(line, rom) with the rom
        // PASSED to ScanHardCodes — not the global U.OtherLangLine(line) overload
        // that reads CoreState.ROM. Here CoreState.ROM is a multibyte FE8J ROM but
        // the scanner is handed a non-multibyte FE8U ROM; the filter outcome must
        // follow the FE8U ROM.
        [Fact]
        public void LangLineFilter_UsesPassedRom_NotCoreStateRom_TwoTaggedAddresses()
        {
            // FE8U (non-multibyte) keeps {U} lines and skips {J} lines.
            // FE8J (multibyte) would do the OPPOSITE. We seed two ADDRESS lines:
            //   ADDRESS=0x2000 {J}  -> id 0xAA (skipped on FE8U)
            //   ADDRESS=0x3000 {U}  -> id 0xBB (kept    on FE8U)
            // and assert 0xBB is flagged (FE8U path), 0xAA is NOT.
            var fe8u = new ROM();
            var data = new byte[0x1000000];
            data[0x2000] = 0xAA; // the {J} id
            data[0x3000] = 0xBB; // the {U} id
            fe8u.LoadLow("hc-lang-fe8u.gba", data, "BE8E01");
            Assert.False(fe8u.RomInfo.is_multibyte);

            var fe8jCoreState = MakeFe8jRom(); // multibyte; deliberately the WRONG rom

            var (root, verDir) = MakeTempPatchDir();
            string savedBase = CoreState.BaseDirectory;
            string savedLang = CoreState.Language;
            var savedRom = CoreState.ROM;
            try
            {
                CoreState.BaseDirectory = root;
                CoreState.Language = "en";
                CoreState.ROM = fe8jCoreState; // ambient ROM = multibyte FE8J

                // Note the real TAB before {J}/{U} — OtherLangLine matches "\t{J}".
                WritePatch(verDir, "LangTwo",
                    "TYPE=ADDR",
                    "ADDRESS_TYPE=UNIT",
                    "ADDRESS=0x2000\t{J}",
                    "ADDRESS=0x3000\t{U}");

                var unit = new bool[256];
                var cls = new bool[256];
                var item = new bool[256];
                PatchHardCodeScanner.ScanHardCodes(fe8u, unit, cls, item);

                Assert.True(unit[0xBB]);  // {U} line survived -> FE8U path
                Assert.False(unit[0xAA]); // {J} line was skipped on FE8U
            }
            finally
            {
                CoreState.BaseDirectory = savedBase;
                CoreState.Language = savedLang;
                CoreState.ROM = savedRom;
                TryDelete(root);
            }
        }

        // Single {J}-tagged ADDRESS: on the PASSED FE8U rom the only ADDRESS line
        // is skipped -> no ADDRESS -> id 0 -> no flag. If the scanner had used the
        // multibyte CoreState.ROM (FE8J) it would have KEPT the line and flagged
        // the id — so a non-flag here proves the passed rom drove the filter.
        [Fact]
        public void LangLineFilter_UsesPassedRom_SingleJTaggedAddressSkippedOnFE8U()
        {
            var fe8u = new ROM();
            var data = new byte[0x1000000];
            data[0x2000] = 0x4C; // would be flagged if the {J} line were kept
            fe8u.LoadLow("hc-lang2-fe8u.gba", data, "BE8E01");

            var fe8jCoreState = MakeFe8jRom(); // multibyte; the WRONG rom

            var (root, verDir) = MakeTempPatchDir();
            string savedBase = CoreState.BaseDirectory;
            string savedLang = CoreState.Language;
            var savedRom = CoreState.ROM;
            try
            {
                CoreState.BaseDirectory = root;
                CoreState.Language = "en";
                CoreState.ROM = fe8jCoreState;

                WritePatch(verDir, "LangOne",
                    "TYPE=ADDR",
                    "ADDRESS_TYPE=UNIT",
                    "ADDRESS=0x2000\t{J}");

                var unit = new bool[256];
                var cls = new bool[256];
                var item = new bool[256];
                PatchHardCodeScanner.ScanHardCodes(fe8u, unit, cls, item);

                Assert.False(unit[0x4C]); // {J} skipped on the passed FE8U rom
                Assert.DoesNotContain(true, unit);
            }
            finally
            {
                CoreState.BaseDirectory = savedBase;
                CoreState.Language = savedLang;
                CoreState.ROM = savedRom;
                TryDelete(root);
            }
        }

        // Mirror image: on a PASSED multibyte FE8J rom the {J} line is KEPT (while
        // CoreState.ROM is a non-multibyte FE8U that would skip it). Proves the
        // filter follows the passed rom in BOTH directions. The temp patch dir is
        // FE8J here because the FE8J rom resolves config/patch2/FE8J.
        [Fact]
        public void LangLineFilter_PassedFE8J_KeepsJLine_WhileCoreStateFE8U()
        {
            var fe8j = MakeFe8jRom(0x2000, 0x5D); // multibyte; the PASSED rom

            var fe8uCoreState = new ROM();
            fe8uCoreState.LoadLow("hc-lang3-fe8u.gba", new byte[0x1000000], "BE8E01");

            string root = Path.Combine(Path.GetTempPath(),
                "fe_hc_j_" + Guid.NewGuid().ToString("N"));
            string verDir = Path.Combine(root, "config", "patch2", "FE8J");
            Directory.CreateDirectory(verDir);

            string savedBase = CoreState.BaseDirectory;
            string savedLang = CoreState.Language;
            var savedRom = CoreState.ROM;
            try
            {
                CoreState.BaseDirectory = root;
                CoreState.Language = "en";
                CoreState.ROM = fe8uCoreState; // ambient = non-multibyte FE8U

                File.WriteAllLines(Path.Combine(verDir, "PATCH_LangJ.txt"), new[]
                {
                    "TYPE=ADDR",
                    "ADDRESS_TYPE=UNIT",
                    "ADDRESS=0x2000\t{J}",
                });

                var unit = new bool[256];
                var cls = new bool[256];
                var item = new bool[256];
                PatchHardCodeScanner.ScanHardCodes(fe8j, unit, cls, item);

                Assert.True(unit[0x5D]); // {J} kept on the passed multibyte FE8J rom
            }
            finally
            {
                CoreState.BaseDirectory = savedBase;
                CoreState.Language = savedLang;
                CoreState.ROM = savedRom;
                TryDelete(root);
            }
        }

        // ---- null/guard safety --------------------------------------------

        [Fact]
        public void NullRom_NoThrow()
        {
            var unit = new bool[256];
            var cls = new bool[256];
            var item = new bool[256];
            PatchHardCodeScanner.ScanHardCodes(null!, unit, cls, item);
            Assert.DoesNotContain(true, unit);
        }

        [Fact]
        public void Version0Rom_ReturnsEmpty()
        {
            // ROMFE0 ("NAZO") -> version 0 -> WF ScanPatchs returns empty.
            var rom = new ROM();
            rom.LoadLow("nazo.gba", new byte[0x1000000], "NAZO");
            var (root, verDir) = MakeTempPatchDir();
            try
            {
                WritePatch(verDir, "MyUnit",
                    "TYPE=ADDR",
                    "ADDRESS=0x2000",
                    "ADDRESS_TYPE=UNIT");
                var (unit, _, _) = RunScan(rom, root);
                Assert.DoesNotContain(true, unit);
            }
            finally { TryDelete(root); }
        }

        // ---- REAL ROM (skip-if-missing, matches Core.Tests convention) ----

        [Fact]
        public void RealRom_FE8U_FlagsAtLeastOneUnit()
        {
            string? romPath = FindRom("FE8U.gba");
            if (romPath == null) return; // skip when ROM fixture absent

            string? patchDir = FindRepoPatch2("FE8U");
            if (patchDir == null) return; // skip when config/patch2 submodule absent

            string savedBase = CoreState.BaseDirectory;
            string savedLang = CoreState.Language;
            try
            {
                // point BaseDirectory at the repo root so the resolver finds the
                // real config/patch2/FE8U.
                CoreState.BaseDirectory = TestRequire.NotNull(RepoRoot(), "repo root");
                CoreState.Language = "en";

                var rom = new ROM();
                if (!rom.Load(romPath, out string _)) return;

                var unit = new bool[256];
                var cls = new bool[256];
                var item = new bool[256];
                PatchHardCodeScanner.ScanHardCodes(rom, unit, cls, item);

                int unitCount = CountTrue(unit);
                Assert.True(unitCount >= 1,
                    "vanilla FE8U + config/patch2 must flag at least one hardcoded unit");
            }
            finally
            {
                CoreState.BaseDirectory = savedBase;
                CoreState.Language = savedLang;
            }
        }

        static int CountTrue(bool[] arr)
        {
            int n = 0;
            foreach (bool b in arr) if (b) n++;
            return n;
        }

        static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }

        static string? RepoRoot()
        {
            string? dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            for (int i = 0; i < 10 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "FEBuilderGBA.sln")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        static string? FindRom(string romName)
        {
            string? root = RepoRoot();
            if (root == null) return null;
            string path = Path.Combine(root, "roms", romName);
            return File.Exists(path) ? path : null;
        }

        static string? FindRepoPatch2(string version)
        {
            string? root = RepoRoot();
            if (root == null) return null;
            string path = Path.Combine(root, "config", "patch2", version);
            return Directory.Exists(path) ? path : null;
        }
    }
}
