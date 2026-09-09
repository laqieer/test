// SPDX-License-Identifier: GPL-3.0-or-later
// #1027 — Tests for PatchMacroAddressResolverCore and the new
// GrepPatternMatchBegin/GrepPatternMatchEnd helpers added to Core U.cs.
//
// All tests use synthetic in-memory ROMs (rom.LoadLow) so they run without
// any real GBA ROM file. They cover:
//   - Plain literal address
//   - $0x pointer-deref
//   - $FREEAREA carved out (NOT_FOUND)
//   - $GREP family (exact-match, ENDA, END) — byte tokens must use "0xNN" format
//   - $XGREP wildcard — "X" is the wildcard token, other tokens use "0xNN"
//   - $GREP_ENABLE_POINTER
//   - $P32 / $P32+4
//   - $TEXTID / $TEXTID_P
//   - $EndWeaponDebuffTable3/4/5 carved out
//   - Null/empty guards
//   - U.GrepPatternMatchEnd / U.GrepPatternMatchBegin core helpers

using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using FEBuilderGBA;

namespace FEBuilderGBA.Core.Tests
{
    [Collection("SharedState")]
    public class PatchMacroAddressResolverCoreTests
    {
        [Theory]
        [InlineData(@"\\unused.invalid\share\pattern.bin")]
        [InlineData("//unused.invalid/share/pattern.bin")]
        [InlineData("C:/outside/pattern.bin")]
        [InlineData("../../outside.bin")]
        public void ImportAuditRejectsExternalOperandsBeforeTheRealResolverAndFileSpies(string operand)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "macro-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            int resolverCalls = 0, existsCalls = 0, readCalls = 0;
            try
            {
                string descriptor = Path.Combine(root, "PATCH_x.txt");
                File.WriteAllText(descriptor, "TYPE=BIN\nPATCHED_IF:$FGREP4 " + operand + "=0xAA");
                var manifest = new System.Collections.Generic.Dictionary<string, long>
                {
                    ["PATCH_x.txt"] = new FileInfo(descriptor).Length,
                };
                Assert.Throws<InvalidDataException>(() =>
                {
                    PatchDatabaseMetadataAuditCore.Audit(root, manifest);
                    resolverCalls++;
                    _ = PatchMacroAddressResolverCore.ResolveWithFileReadsForTest(MakeRom(), "$FGREP4 " + operand,
                        root, 0x100, _ => { existsCalls++; return false; },
                        _ => { readCalls++; return Array.Empty<byte>(); });
                });
                Assert.Equal(0, resolverCalls);
                Assert.Equal(0, existsCalls);
                Assert.Equal(0, readCalls);
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData("$FGREP4", 0x2000u)]
        [InlineData("$FGREP4ENDA", 0x2004u)]
        [InlineData("$FGREP4END", 0x2004u)]
        public void AuditedInternalSiblingUsesTheActualFileBackedResolver(string macro, uint expected)
        {
            string root = Path.Combine(AppContext.BaseDirectory, "TestResults", "macro-sibling-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            Directory.CreateDirectory(Path.Combine(root, "shared"));
            try
            {
                string descriptor = Path.Combine(root, "nested", "PATCH_x.txt");
                string pattern = Path.Combine(root, "shared", "pattern.bin");
                byte[] data = { 0xAA, 0xBB, 0xCC, 0xDD };
                File.WriteAllBytes(pattern, data);
                File.WriteAllText(descriptor, "PATCHED_IF:" + macro + " ../shared/pattern.bin=0xAA");
                PatchDatabaseMetadataAuditCore.Audit(root, new System.Collections.Generic.Dictionary<string, long>
                {
                    ["nested/PATCH_x.txt"] = new FileInfo(descriptor).Length,
                    ["shared/pattern.bin"] = data.Length,
                });
                ROM rom = MakeRom();
                data.CopyTo(rom.Data, 0x2000);
                int existsCalls = 0, readCalls = 0;
                uint actual = PatchMacroAddressResolverCore.ResolveWithFileReadsForTest(rom,
                    macro + " ../shared/pattern.bin", Path.GetDirectoryName(descriptor)!, 0x100,
                    path =>
                    {
                        existsCalls++;
                        Assert.Equal(pattern, Path.GetFullPath(path));
                        return true;
                    },
                    path =>
                    {
                        readCalls++;
                        Assert.Equal(pattern, Path.GetFullPath(path));
                        return data;
                    });
                Assert.Equal(expected, actual);
                Assert.Equal(1, existsCalls);
                Assert.Equal(1, readCalls);
            }
            finally { Directory.Delete(root, true); }
        }

        // ---- ROM builder ---------------------------------------------------

        // Build a minimal synthetic FE8U ROM. LoadLow requires >= 0x1000000 (16 MB)
        // bytes for BE8E01. We create a 16 MB zero-filled array.
        static ROM MakeRom()
        {
            var rom = new ROM();
            byte[] data = new byte[0x1000000]; // 16 MB
            rom.LoadLow("x.gba", data, "BE8E01");
            return rom;
        }

        // ---- 1. Plain literal address --------------------------------------

        [Fact]
        public void Literal_HexAddress_ReturnsOffset()
        {
            var rom = MakeRom();
            // Plain hex literal: 0x00001234 -> toOffset returns the same
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "0x00001234", "", 0x100);
            Assert.Equal(0x00001234u, result);
        }

        [Fact]
        public void Literal_GbaPointerAddress_StripsBase()
        {
            var rom = MakeRom();
            // GBA pointer 0x08001234 -> toOffset -> 0x1234
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "0x08001234", "", 0x100);
            Assert.Equal(0x00001234u, result);
        }

        // ---- 2. $0x pointer dereference ------------------------------------

        [Fact]
        public void DollarHex_DerefPointer_ReturnsPointedOffset()
        {
            var rom = MakeRom();
            // Plant a GBA pointer at ROM offset 0x1000 pointing to 0x2000
            const uint slotOffset = 0x1000;
            const uint targetOffset = 0x2000;
            U.write_u32(rom.Data, slotOffset, U.toPointer(targetOffset));

            // $0x1000 means: read the 32-bit GBA pointer at ROM[0x1000]
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$0x1000", "", 0x100);
            Assert.Equal(targetOffset, result);
        }

        [Fact]
        public void DollarHex_NullPointer_DoesNotThrow()
        {
            var rom = MakeRom();
            // ROM[0x1000] is all zeros; should not throw
            _ = PatchMacroAddressResolverCore.Resolve(rom, "$0x1000", "", 0x100);
        }

        // ---- 3. $FREEAREA carved out ----------------------------------------

        [Fact]
        public void FreeArea_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$FREEAREA", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 4. $GREP exact-match -------------------------------------------
        // Note: byte tokens in GREP macros use "0xNN" format (e.g. "0xAA 0xBB").
        // Bare hex ("AA BB") is parsed by U.atoi0x as decimal->0, matching WF behavior.

        [Fact]
        public void Grep1_FindsExactPattern_ReturnsMatchOffset()
        {
            var rom = MakeRom();
            // Plant distinctive bytes at 0x2000 (well above startOffset=0x100)
            // Pattern: 0xAA 0xBB 0xCC — unique in zero-filled ROM
            rom.Data[0x2000] = 0xAA;
            rom.Data[0x2001] = 0xBB;
            rom.Data[0x2002] = 0xCC;

            // Byte tokens use 0xNN format; $GREP1 alignment=1, no END mode -> match-begin
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$GREP1 0xAA 0xBB 0xCC", "", 0x100);
            Assert.Equal(0x2000u, result);
        }

        [Fact]
        public void Grep1_PatternNotInRom_ReturnsNotFound()
        {
            var rom = MakeRom();
            // Pattern 0xDE 0xAD 0xBE 0xEF: not present in a zero-filled ROM
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$GREP1 0xDE 0xAD 0xBE 0xEF", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public async Task Grep0_ZeroAlignment_ReturnsNotFound_WithoutHanging()
        {
            // $GREP0 has alignment 0 — U.Grep's `i += blocksize` step would never advance
            // and hang. The resolver must reject it up front and return NOT_FOUND. The
            // 5s timeout fails the test if the guard regresses (infinite loop).
            var rom = MakeRom();
            var task = System.Threading.Tasks.Task.Run(() =>
                PatchMacroAddressResolverCore.Resolve(rom, "$GREP0 0xAB", "", 0x100));
            Task completedTask = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(
                ReferenceEquals(task, completedTask),
                "Resolve($GREP0) hung — zero-alignment guard missing");
            Assert.Equal(U.NOT_FOUND, await task);
        }

        // ---- 5. $GREP ENDA (returns address AFTER the match, no pointer check) ---

        [Fact]
        public void GrepEnda_FindsAddressJustAfterPattern_ReturnsNextOffset()
        {
            var rom = MakeRom();
            // Plant 0x55 0x66 at 0x4000
            rom.Data[0x4000] = 0x55;
            rom.Data[0x4001] = 0x66;

            // $GREP1ENDA: GrepEnd(needPointer=false); returns offset just after pattern
            // = 0x4000 + 2 = 0x4002
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$GREP1ENDA 0x55 0x66", "", 0x100);
            Assert.Equal(0x4002u, result);
        }

        // ---- 6. $GREP END (returns address AFTER match, requires pointer at that addr) ---

        [Fact]
        public void GrepEnd_FindsPointerAfterPattern_ReturnsPointerSlot()
        {
            var rom = MakeRom();
            // Pattern 0x11 0x22 0x33 at 0x3000, followed at 0x3003 by valid GBA pointer
            rom.Data[0x3000] = 0x11;
            rom.Data[0x3001] = 0x22;
            rom.Data[0x3002] = 0x33;
            const uint target = 0x5000u;
            U.write_u32(rom.Data, 0x3003, U.toPointer(target));

            // $GREP1END = GrepEnd with needPointer=true; returns 0x3003
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$GREP1END 0x11 0x22 0x33", "", 0x100);
            Assert.Equal(0x3003u, result);
        }

        // ---- 7. $XGREP wildcard ---------------------------------------------
        // "X" = wildcard token (matches any byte); all other tokens use "0xNN" format.

        [Fact]
        public void Xgrep_Wildcard_MatchesAnyByte()
        {
            var rom = MakeRom();
            // Pattern: 0xAA <any> 0xCC at 0x5000.
            rom.Data[0x5000] = 0xAA;
            rom.Data[0x5001] = 0x42; // wildcard, any value
            rom.Data[0x5002] = 0xCC;

            // Ensure 0xAA doesn't appear before 0x5000 by starting search from 0x4FFF
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$XGREP1 0xAA X 0xCC", "", 0x4FFF);
            Assert.Equal(0x5000u, result);
        }

        [Fact]
        public void Xgrep_ExactByteMismatch_ReturnsNotFound()
        {
            var rom = MakeRom();
            // Pattern: 0xAA 0x42 0xBB but ROM has 0xAA 0x42 0xFF at 0x5100
            rom.Data[0x5100] = 0xAA;
            rom.Data[0x5101] = 0x42;
            rom.Data[0x5102] = 0xFF; // does not match 0xBB

            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$XGREP1 0xAA 0x42 0xBB", "", 0x5099);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 8. $GREP_ENABLE_POINTER ----------------------------------------

        [Fact]
        public void GrepEnablePointer_StopsAtFirstNonPointer_ReturnsStopAddr()
        {
            var rom = MakeRom();
            // Plant a valid GBA pointer at pStart; next slot (pStart+4) is zero (non-pointer)
            const uint pStart = 0x1000u;
            U.write_u32(rom.Data, pStart, U.toPointer(0x80000u)); // valid pointer at 0x1000
            // 0x1004 is zeros (0x00000000), not a GBA pointer -> stop

            // GrepEnablePointer scans from startOffset while entries are valid pointers;
            // 0x1000 contains a valid pointer -> advance to 0x1004;
            // 0x1004 = 0 (not pointer or null = 0 is allowed) -> let's see what isPointer says
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$GREP_ENABLE_POINTER ", "", pStart);
            // U.isPointer(0) -> false (needs 0x08000000..0x09FFFFFF)
            // So the loop stops at 0x1004 since 0 is not a pointer
            // Wait: GrepEnablePointer checks isPointer (not isPointerOrNull)
            Assert.Equal(pStart + 4, result);
        }

        // ---- 9. $P32 --------------------------------------------------------

        [Fact]
        public void P32_DerefsPointerAtAddress_ReturnsOffset()
        {
            var rom = MakeRom();
            const uint slot = 0x1100u;
            const uint target = 0x2200u;
            U.write_u32(rom.Data, slot, U.toPointer(target));

            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$P32 0x1100", "", 0x100);
            Assert.Equal(target, result);
        }

        [Fact]
        public void P32Plus4_DerefsPointerAtAddressPlusFour_ReturnsOffset()
        {
            var rom = MakeRom();
            const uint slot = 0x1200u;
            const uint target = 0x3300u;
            // Pointer is at slot + 4
            U.write_u32(rom.Data, slot + 4, U.toPointer(target));

            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$P32+4 0x1200", "", 0x100);
            Assert.Equal(target, result);
        }

        [Fact]
        public void P32_OddPointer_Decremented_ReturnsEvenOffset()
        {
            var rom = MakeRom();
            const uint slot = 0x1300u;
            const uint target = 0x4400u;
            // Plant an odd GBA pointer (Thumb code indicator +1)
            U.write_u32(rom.Data, slot, U.toPointer(target) + 1);

            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$P32 0x1300", "", 0x100);
            Assert.Equal(target, result);
        }

        [Fact]
        public void P32_NoPointerAtAddress_ReturnsNotFound()
        {
            var rom = MakeRom();
            // ROM[0x1400] is all zeros — not a valid GBA pointer
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$P32 0x1400", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 10. $TEXTID / $TEXTID_P -----------------------------------------

        [Fact]
        public void TextidP_ReturnsPointerTableEntryAddress()
        {
            var rom = MakeRom();
            // FE8U: text_pointer = 0x00A2A0; plant a valid text base there
            uint textPointerSlot = rom.RomInfo.text_pointer;
            uint textBase = 0x50000u; // arbitrary safe ROM offset
            U.write_u32(rom.Data, textPointerSlot, U.toPointer(textBase));

            // Pointer for text id 5 is at textBase + 5*4
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$TEXTID_P 0x5", "", 0x100);
            uint expected = textBase + 5 * 4;
            Assert.Equal(expected, result);
        }

        [Fact]
        public void Textid_ReturnsActualTextDataAddress()
        {
            var rom = MakeRom();
            uint textPointerSlot = rom.RomInfo.text_pointer;
            uint textBase = 0x60000u;
            U.write_u32(rom.Data, textPointerSlot, U.toPointer(textBase));

            // Plant a text data pointer for text id 3 at textBase + 12
            uint textDataOffset = 0x70000u;
            U.write_u32(rom.Data, textBase + 3 * 4, U.toPointer(textDataOffset));

            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$TEXTID 0x3", "", 0x100);
            Assert.Equal(textDataOffset, result);
        }

        // ---- 11. $EndWeaponDebuffTable carved out ----------------------------

        [Fact]
        public void EndWeaponDebuffTable3_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$EndWeaponDebuffTable3 blah", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void EndWeaponDebuffTable4_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$EndWeaponDebuffTable4 blah", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void EndWeaponDebuffTable5_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$EndWeaponDebuffTable5 blah", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 12. Null/empty guards ------------------------------------------

        [Fact]
        public void NullRom_ReturnsNotFound_NoThrow()
        {
            uint result = PatchMacroAddressResolverCore.Resolve(null!, "$GREP1 0xAA", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void EmptyAddrstring_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void NullAddrstring_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, null!, "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void DollarOnly_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 13. U.GrepPatternMatchEnd helpers (Core U.cs additions) --------

        [Fact]
        public void GrepPatternMatchEnd_ReturnsOffsetAfterPattern()
        {
            // 256-byte buffer; pattern at offset 0x20; result = 0x20 + 3 = 0x23
            byte[] data = new byte[0x100];
            data[0x20] = 0xAA;
            data[0x21] = 0xBB;
            data[0x22] = 0xCC;
            bool[] mask = new bool[3]; // all false = exact match
            uint result = U.GrepPatternMatchEnd(data, new byte[] { 0xAA, 0xBB, 0xCC }, mask, 0, 0, 1, 0, false);
            Assert.Equal(0x23u, result);
        }

        [Fact]
        public void GrepPatternMatchEnd_WithPlusSkip_ReturnsOffsetAfterPatternPlusSkip()
        {
            byte[] data = new byte[0x100];
            data[0x30] = 0x11;
            data[0x31] = 0x22;
            // result = 0x30 + 2 (pattern length) + 5 (plus/skip) = 0x37
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchEnd(data, new byte[] { 0x11, 0x22 }, mask, 0, 0, 1, 5, false);
            Assert.Equal(0x37u, result);
        }

        [Fact]
        public void GrepPatternMatchEnd_NeedPointer_SkipsNonPointerSlots()
        {
            byte[] data = new byte[0x200];
            // First occurrence at 0x40: result addr = 0x42. Plant a non-null, non-pointer
            // value there so isPointerOrNULL returns false and the match is skipped.
            data[0x40] = 0xDD;
            data[0x41] = 0xEE;
            U.write_u32(data, 0x42, 0x01020304u); // neither null nor GBA pointer

            // Second occurrence at 0x60: result addr = 0x62. Plant a valid GBA pointer there.
            data[0x60] = 0xDD;
            data[0x61] = 0xEE;
            const uint target = 0x100000u;
            U.write_u32(data, 0x62, U.toPointer(target));
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchEnd(data, new byte[] { 0xDD, 0xEE }, mask, 0, 0, 1, 0, true);
            Assert.Equal(0x62u, result);
        }

        [Fact]
        public void GrepPatternMatchEnd_PatternNotFound_ReturnsNotFound()
        {
            byte[] data = new byte[0x100];
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchEnd(data, new byte[] { 0xFF, 0xFE }, mask, 0, 0, 1, 0, false);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void GrepPatternMatchEnd_NullData_ReturnsNotFound_NoThrow()
        {
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchEnd(null!, new byte[] { 0xAA, 0xBB }, mask, 0, 0, 1, 0, false);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 14. U.GrepPatternMatchBegin helpers (Core U.cs additions) ------

        [Fact]
        public void GrepPatternMatchBegin_ReturnsMatchStartOffset()
        {
            byte[] data = new byte[0x100];
            data[0x50] = 0x77;
            data[0x51] = 0x88;
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchBegin(data, new byte[] { 0x77, 0x88 }, mask, 0, 0, 1, 0, false);
            Assert.Equal(0x50u, result);
        }

        [Fact]
        public void GrepPatternMatchBegin_WithPlus_ReturnsOffsetFromMatchStartPlusSkip()
        {
            byte[] data = new byte[0x100];
            data[0x50] = 0x77;
            data[0x51] = 0x88;
            // result = 0x50 + 2 (plus) = 0x52
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchBegin(data, new byte[] { 0x77, 0x88 }, mask, 0, 0, 1, 2, false);
            Assert.Equal(0x52u, result);
        }

        [Fact]
        public void GrepPatternMatchBegin_NeedPointer_AcceptsNullPointerOrNull()
        {
            // With needPointer=true and plus=0: isPointerOrNULL(u32(data, grepresult+0))
            // is checked. null (0x00000000) IS allowed (isPointerOrNULL accepts 0).
            // So first match whose slot value is 0 (zero-filled) is accepted.
            byte[] data = new byte[0x100];
            data[0x70] = 0x99; // Pattern first byte
            data[0x71] = 0xAA; // Pattern second byte
            // data[0x70..0x73] = [0x99, 0xAA, 0x00, 0x00] = u32 = 0x0000AA99
            // isPointerOrNULL(0x0000AA99) -> not 0 and not 0x08..0x09 -> false -> skip
            data[0x90] = 0x99;
            data[0x91] = 0xAA;
            // data[0x90..0x93] = same, also not a pointer -> skip
            // No valid match -> NOT_FOUND
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchBegin(data, new byte[] { 0x99, 0xAA }, mask, 0, 0, 1, 0, true);
            // Both occurrences at 0x70 and 0x90 have u32(data, resultAddr) = 0x0000AA99 (non-pointer, non-null)
            // -> needPointer search keeps advancing: 0x70 fails -> tries 0x70+1=0x71, then...
            // Actually: GrepPatternMatchBegin recurses with start = resultAddr + blocksize = 0x70+1
            // (since needPointer=true, restarting from resultAddr+blocksize for the next try)
            // With this tiny buffer and only 2 occurrences both non-pointer, result = NOT_FOUND
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void GrepPatternMatchBegin_PatternNotFound_ReturnsNotFound()
        {
            byte[] data = new byte[0x100];
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchBegin(data, new byte[] { 0xCB, 0xCA }, mask, 0, 0, 1, 0, false);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void GrepPatternMatchBegin_NullData_ReturnsNotFound_NoThrow()
        {
            bool[] mask = new bool[2];
            uint result = U.GrepPatternMatchBegin(null!, new byte[] { 0xAA, 0xBB }, mask, 0, 0, 1, 0, false);
            Assert.Equal(U.NOT_FOUND, result);
        }

        // ---- 14b. needPointer +4 bounds guard (PR #1117 Copilot review) -----
        // The "never throws" contract requires that when needPointer==true and the
        // computed resultAddr lands within the last 3 bytes of the buffer, the u32
        // read (which consumes 4 bytes) does NOT run off the end. The old
        // `if (resultAddr > data.Length)` guard is insufficient because u32 reads
        // 4 bytes; the added `resultAddr + 4 > data.Length` guard fixes it.

        [Fact]
        public void GrepPatternMatchEnd_NeedPointer_NearEndOfBuffer_ReturnsNotFound_NoThrow()
        {
            // N = 0x200. Plant a unique 2-byte pattern at N-4 = 0x1FC.
            // resultAddr = grepresult + need.Length + plus = 0x1FC + 2 + 0 = 0x1FE = N-2.
            // resultAddr <= data.Length (0x1FE <= 0x200) so the old guard passes,
            // but resultAddr + 4 = 0x202 > 0x200 -> u32 would read bytes 0x1FE..0x201
            // (IndexOutOfRange) without the new +4 guard. Must return NOT_FOUND, no throw.
            const int N = 0x200;
            byte[] data = new byte[N];
            data[N - 4] = 0xA3;
            data[N - 3] = 0x5C;
            bool[] mask = new bool[2]; // exact match
            // GrepPatternMatch can match at i = N-4 since N-4 <= data.Length - need.Length.
            var ex = Record.Exception(() =>
            {
                uint result = U.GrepPatternMatchEnd(data, new byte[] { 0xA3, 0x5C }, mask, 0x100, 0, 1, 0, true);
                Assert.Equal(U.NOT_FOUND, result);
            });
            Assert.Null(ex);
        }

        [Fact]
        public void GrepPatternMatchBegin_NeedPointer_NearEndOfBuffer_ReturnsNotFound_NoThrow()
        {
            // N = 0x200. Plant a unique 2-byte pattern at the very end, N-2 = 0x1FE.
            // resultAddr = grepresult + plus = 0x1FE + 0 = 0x1FE = N-2.
            // resultAddr <= data.Length (0x1FE <= 0x200) so the old guard passes,
            // but resultAddr + 4 = 0x202 > 0x200 -> u32 would read bytes 0x1FE..0x201
            // (IndexOutOfRange) without the new +4 guard. Must return NOT_FOUND, no throw.
            const int N = 0x200;
            byte[] data = new byte[N];
            data[N - 2] = 0xB7;
            data[N - 1] = 0x9E;
            bool[] mask = new bool[2]; // exact match
            // GrepPatternMatch matches at i = N-2 (N-2 <= data.Length - need.Length = 0x1FE).
            var ex = Record.Exception(() =>
            {
                uint result = U.GrepPatternMatchBegin(data, new byte[] { 0xB7, 0x9E }, mask, 0x100, 0, 1, 0, true);
                Assert.Equal(U.NOT_FOUND, result);
            });
            Assert.Null(ex);
        }

        // ---- 15. $FGREP file-content search (no real file -> NOT_FOUND) ----

        [Fact]
        public void Fgrep_MissingFile_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$FGREP1 nosuchfile.bin", Path.GetTempPath(), 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }

        [Fact]
        public void Fgrep_EmptyBasedir_ReturnsNotFound()
        {
            var rom = MakeRom();
            uint result = PatchMacroAddressResolverCore.Resolve(rom, "$FGREP1 somefile.bin", "", 0x100);
            Assert.Equal(U.NOT_FOUND, result);
        }
    }
}
