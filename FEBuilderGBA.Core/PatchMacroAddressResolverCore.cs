// SPDX-License-Identifier: GPL-3.0-or-later
// #1027 — READ-ONLY faithful port of WF PatchForm.convertBinAddressString.
// Resolves $GREP/$XGREP/$FGREP/$GREP_ENABLE_POINTER/$P32/$TEXTID macro addresses
// from patch definition files so grep-resolved patch TEXT/SONG/EVENT addresses
// contribute their ids to the Text-Editor free-area used-ref union.
//
// Carve-outs (return U.NOT_FOUND, never throw):
//   $FREEAREA               — write-time allocator, meaningless read-only
//   $EndWeaponDebuffTable3/4/5 — weapon-debuff-only PatchUtil/Form-bound macros,
//                                never a TEXT/SONG id table
//
// Platform: net10.0, no WinForms, no System.Drawing dependency. Never throws.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace FEBuilderGBA
{
    /// <summary>
    /// READ-ONLY faithful port of WF <c>PatchForm.convertBinAddressString</c>.
    /// Resolves <c>$GREP</c>/<c>$XGREP</c>/<c>$FGREP</c>/<c>$GREP_ENABLE_POINTER</c>/
    /// <c>$P32</c>/<c>$TEXTID</c> macro addresses from patch definition files.
    /// Wired into <see cref="PatchTextRefScannerCore"/> (ADDR/STRUCT address +
    /// STRUCT DATACOUNT end-delta + GREP-based install-IF) so grep-resolved patch
    /// TEXT/SONG/EVENT addresses contribute their ids to the Text-Editor free-area
    /// used-ref union.
    ///
    /// <para>Carve-outs (return <see cref="U.NOT_FOUND"/>, never throw):
    /// <c>$FREEAREA</c> — write-time allocator; <c>$EndWeaponDebuffTable3/4/5</c> —
    /// weapon-debuff-only PatchUtil/Form-bound macros.</para>
    /// </summary>
    internal static class PatchMacroAddressResolverCore
    {
        /// <summary>
        /// Resolves a patch address string (literal hex, or $MACRO syntax) to a ROM offset.
        /// </summary>
        /// <param name="rom">ROM instance to search.</param>
        /// <param name="addrstring">The address string from the patch file
        /// (e.g. "0x12345", "$GREP4 01 02 03 04").</param>
        /// <param name="basedir">Directory of the patch file, used for $FGREP file lookups.</param>
        /// <param name="startOffset">Start offset for GREP scans (default 0x100;
        /// DATACOUNT passes struct_address).</param>
        /// <returns>ROM offset, or <see cref="U.NOT_FOUND"/> on any failure.</returns>
        public static uint Resolve(ROM rom, string addrstring, string basedir, uint startOffset = 0x100)
            => ResolveCore(rom, addrstring, basedir, startOffset, File.Exists, File.ReadAllBytes);

        internal static uint ResolveWithFileReadsForTest(ROM rom, string addrstring, string basedir, uint startOffset,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
            => ResolveCore(rom, addrstring, basedir, startOffset, fileExists, readFile);

        static uint ResolveCore(ROM rom, string addrstring, string basedir, uint startOffset,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            try
            {
                if (rom == null || rom.Data == null) return U.NOT_FOUND;
                if (string.IsNullOrEmpty(addrstring)) return U.NOT_FOUND;

                if (addrstring[0] != '$')
                {
                    // Plain hex address literal
                    return U.toOffset(U.atoi0x(addrstring));
                }

                string value = addrstring.Substring(1);
                if (value.Length == 0) return U.NOT_FOUND;

                // $0xNNN — pointer dereference: read the 32-bit GBA pointer at that offset
                if (U.isnum(value[0]))
                {
                    uint pa = U.toOffset(U.atoi0x(value));
                    if (!U.isSafetyOffset(pa, rom)) return U.NOT_FOUND;
                    if (pa + 4 > (uint)rom.Data.Length) return U.NOT_FOUND;
                    return rom.p32(pa);
                }

                // $FREEAREA — write-time allocator macro; carved out
                if (value == "FREEAREA") return U.NOT_FOUND;

                // GREP family: (F|X)?GREP<align>(ENDA|END)?+<skip>? <bytes...>
                Match m = RegexCache.Match(value, @"^(F|X)?GREP([0-9]+)(ENDA|END)?\+?([0-9]+)? ");
                if (m.Success && m.Groups.Count >= 5)
                {
                    uint align = U.atoi(m.Groups[2].Value);
                    // A zero alignment would make U.Grep's `i += blocksize` step never
                    // advance -> infinite loop / hang. Reject it (a $GREP0 is malformed).
                    if (align == 0) return U.NOT_FOUND;
                    // Groups[4] is the optional +<skip> integer; empty string -> 0
                    uint skip = string.IsNullOrEmpty(m.Groups[4].Value) ? 0 : U.atoi(m.Groups[4].Value);
                    string endMode = m.Groups[3].Value;  // "ENDA", "END", or ""
                    string variant = m.Groups[1].Value;  // "F", "X", or ""

                    if (variant == "X")
                    {
                        // XGREP — wildcard pattern match
                        bool[] mask;
                        byte[] need = MakeXGrepData(value, out mask);
                        if (need.Length == 0) return U.NOT_FOUND;
                        if (endMode == "ENDA")
                            return U.GrepPatternMatchEnd(rom.Data, need, mask, startOffset, 0, align, skip, false);
                        if (endMode == "END")
                            return U.GrepPatternMatchEnd(rom.Data, need, mask, startOffset, 0, align, skip, true);
                        return U.GrepPatternMatchBegin(rom.Data, need, mask, startOffset, 0, align, skip, false);
                    }
                    else
                    {
                        // GREP or FGREP — exact byte match
                        byte[] need = (variant == "F")
                            ? MakeGrepDataFromFile(value, basedir, fileExists, readFile)
                            : MakeGrepDataFromHex(value);
                        if (need.Length == 0) return U.NOT_FOUND;
                        if (endMode == "ENDA")
                            return U.GrepEnd(rom.Data, need, startOffset, 0, align, skip, false);
                        if (endMode == "END")
                            return U.GrepEnd(rom.Data, need, startOffset, 0, align, skip, true);
                        return U.Grep(rom.Data, need, startOffset, 0, align);
                    }
                }

                // $GREP_ENABLE_POINTER
                if (value.StartsWith("GREP_ENABLE_POINTER ", StringComparison.Ordinal))
                    return U.GrepEnablePointer(rom.Data, startOffset, 0);

                // $P32+4 — dereference pointer at (literal_addr + 4)
                if (value.StartsWith("P32+4 ", StringComparison.Ordinal))
                    return ReadPointer(rom, value, 4);

                // $P32 — dereference pointer at literal_addr
                if (value.StartsWith("P32 ", StringComparison.Ordinal))
                    return ReadPointer(rom, value, 0);

                // $TEXTID — resolve text address from text id
                if (value.StartsWith("TEXTID ", StringComparison.Ordinal))
                    return GetTextAddress(rom, value);

                // $TEXTID_P — resolve text pointer-table entry address from text id
                if (value.StartsWith("TEXTID_P ", StringComparison.Ordinal))
                    return GetTextPointer(rom, value);

                // $EndWeaponDebuffTable3/4/5 — weapon-debuff-only; carved out
                if (value.StartsWith("EndWeaponDebuffTable3 ", StringComparison.Ordinal) ||
                    value.StartsWith("EndWeaponDebuffTable4 ", StringComparison.Ordinal) ||
                    value.StartsWith("EndWeaponDebuffTable5 ", StringComparison.Ordinal))
                    return U.NOT_FOUND;

                return U.NOT_FOUND;
            }
            catch
            {
                return U.NOT_FOUND;
            }
        }

        // ---- helpers -------------------------------------------------------

        // Parse hex byte tokens from the macro value string.
        // Token[0] is the macro name (e.g. "GREP4"); tokens [1..] are hex bytes.
        private static byte[] MakeGrepDataFromHex(string value)
        {
            string[] sp = value.Split(' ');
            var grep = new List<byte>();
            // Verbatim WF parity: every token [1..] contributes a byte; empty token == atoi0x("") == 0x00 (NOT skipped).
            for (int i = 1; i < sp.Length; i++)
            {
                grep.Add((byte)U.atoi0x(sp[i]));
            }
            return grep.ToArray();
        }

        // Read raw bytes from the file named in the FGREP macro.
        // Value format: "FGREP<align> <filename>"
        private static byte[] MakeGrepDataFromFile(string value, string basedir,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            int firstSp = value.IndexOf(' ');
            if (firstSp < 0) return Array.Empty<byte>();
            string filename = value.Substring(firstSp + 1).Trim();
            if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(basedir))
                return Array.Empty<byte>();
            string fullpath = Path.Combine(basedir, filename);
            try
            {
                if (!fileExists(fullpath)) return Array.Empty<byte>();
                return readFile(fullpath);
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        // Parse XGREP tokens: "X" prefix = wildcard (skip), otherwise hex byte.
        private static byte[] MakeXGrepData(string value, out bool[] out_mask)
        {
            string[] sp = value.Split(' ');
            var gd = new List<byte>();
            var md = new List<bool>();
            // Verbatim WF parity: empty token falls to the else branch (mask=false + 0x00 byte), NOT skipped.
            for (int i = 1; i < sp.Length; i++)
            {
                if (sp[i].Length > 0 && sp[i][0] == 'X')
                {
                    md.Add(true);
                    gd.Add(0xFF);
                }
                else
                {
                    md.Add(false);
                    gd.Add((byte)U.atoi0x(sp[i]));
                }
            }
            out_mask = md.ToArray();
            return gd.ToArray();
        }

        // $P32[+4] <addr> — read and convert the 32-bit GBA pointer at (addr + plus).
        private static uint ReadPointer(ROM rom, string value, uint plus)
        {
            string[] sp = value.Split(' ');
            if (sp.Length < 2) return U.NOT_FOUND;
            uint p = U.atoi0x(sp[1]);
            if (!U.isSafetyOffset(p, rom)) return U.NOT_FOUND;
            if (p + plus + 4 > (uint)rom.Data.Length) return U.NOT_FOUND;
            uint pp = rom.u32(p + plus);
            if (!U.isSafetyPointer(pp, rom)) return U.NOT_FOUND;
            if (U.IsValueOdd(pp)) pp--;
            return U.toOffset(pp);
        }

        // $TEXTID_P <textid> — return the ROM offset of the pointer-table entry
        // for the given text id (i.e. the slot address, not the text data address).
        // Faithful port of WF PatchForm.GetTextPointer via TextForm.GetTextIDToDataPointer.
        private static uint GetTextPointer(ROM rom, string value)
        {
            string[] sp = value.Split(' ');
            if (sp.Length < 2) return U.NOT_FOUND;
            uint textid = U.atoi0x(sp[1]);
            // Resolve the text pointer table base (prefer the pointer, fall back to recover address)
            uint baseAddr = rom.p32(rom.RomInfo.text_pointer);
            if (!U.isSafetyOffset(baseAddr, rom))
                baseAddr = rom.RomInfo.text_recover_address;
            if (!U.isSafetyOffset(baseAddr, rom)) return U.NOT_FOUND;
            uint pointer = baseAddr + textid * 4;
            if (!U.isSafetyOffset(pointer, rom)) return U.NOT_FOUND;
            if (pointer + 4 > (uint)rom.Data.Length) return U.NOT_FOUND;
            return pointer;
        }

        // $TEXTID <textid> — return the ROM offset of the actual text data for the
        // given text id. Faithful port of WF PatchForm.GetTextAddress via
        // TextForm.GetTextIDToDataAddr (which calls GetTextIDToDataPointer + deref).
        private static uint GetTextAddress(ROM rom, string value)
        {
            uint pointer = GetTextPointer(rom, value);
            if (pointer == U.NOT_FOUND) return U.NOT_FOUND;
            if (pointer + 4 > (uint)rom.Data.Length) return U.NOT_FOUND;
            uint writeAddr = rom.u32(pointer);
            // Handle Un-Huffman patch pointer range (0x88000000–0x8A000000)
            if (FETextEncode.IsUnHuffmanPatchPointer(writeAddr))
                writeAddr = FETextEncode.ConvertUnHuffmanPatchToPointer(writeAddr);
            return U.toOffset(writeAddr);
        }
    }
}
