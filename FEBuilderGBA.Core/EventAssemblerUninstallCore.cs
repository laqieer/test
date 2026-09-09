using System;
using System.Collections.Generic;
using System.IO;

namespace FEBuilderGBA
{
    /// <summary>
    /// GUI-free in-place <b>Uninstall</b> for an applied Event Assembler patch
    /// (#1242, follow-up to #1170's <see cref="EventAssemblerCompileCore"/>). Ported
    /// from the WinForms patch-uninstall path
    /// (<c>EventAssemblerForm.UninstallButton_Click</c> →
    /// <c>PatchForm.MakeInstantEAToPatch</c> → <c>PatchForm.UnInstallPatch</c> →
    /// <c>TracePatchedMapping</c>/<c>TraceEAPatchedMapping</c> →
    /// <c>UninstallPatchInner</c>), keeping the exact trace semantics, the
    /// clean-original-ROM restore, the auto-length fallback and the ROM-tail strip.
    ///
    /// Flow: parse the EA file with <see cref="EAUtilCore"/> into written ranges
    /// (<see cref="BinMapping"/>s) against the CURRENT (patched) ROM, then overwrite
    /// each traced byte with the corresponding byte from a user-supplied
    /// <b>clean original</b> ROM (the ROM as it was before the patch), recording every
    /// write into an explicit <see cref="Undo.UndoData"/> so the uninstall is fully
    /// undoable. Never throws on bad input — a wrong / mismatched clean ROM returns a
    /// localized error string instead (the live ROM is left untouched in that case).
    ///
    /// Scope vs the WinForms patch uninstall (intentional, documented):
    ///  - This operates on a SINGLE loaded .event (the Avalonia EA tool's input),
    ///    matching <c>MakeInstantEAToPatch</c> which builds a bare
    ///    <c>{TYPE=EA, EA=filename}</c> patch with NO extra params. The WinForms
    ///    trailing helpers driven by patch params — <c>TraceEditPatch</c> (EDIT_PATCH),
    ///    <c>AppendMenuPatch</c> (MENU), <c>AppendNewTargetSelectionStruct</c>,
    ///    <c>AppnedInstallMapping</c> (PATCHED_IF/IF) — have no params to act on for an
    ///    instant-EA patch, so they add nothing and are omitted here.
    ///  - PROCS (<c>HINT=PROCS</c>) length detection uses the WinForms-only
    ///    <c>ProcsScriptForm.CalcLengthAndCheck</c>; this Core path skips a PROCS range
    ///    whose length it cannot determine — identical observable behaviour to the
    ///    WinForms tracer, which <c>continue</c>s when that returns NOT_FOUND.
    ///
    /// NEVER silently incomplete: any block the trace cannot reconstruct (a GREP miss,
    /// the guarded PROCS, an un-hinted inline PNG raster) is recorded on
    /// <see cref="TraceResult.Untraceable"/> / <see cref="UninstallResult.UntraceableBlocks"/>
    /// and flips <see cref="UninstallResult.FullyTraced"/> to false. The View MUST warn
    /// the user when FullyTraced is false — a revert that quietly leaves patch residue
    /// while reporting success is the one outcome this design avoids.
    /// </summary>
    public static class EventAssemblerUninstallCore
    {
        /// <summary>
        /// One traced ROM range written by the EA patch. Mirrors WinForms
        /// <c>PatchForm.BinMapping</c> (the subset the EA path populates).
        /// </summary>
        public sealed class BinMapping
        {
            public string key;
            public string filename;
            public uint addr;
            public uint length;     // 0 == unknown (auto-length on revert)
            public Address.DataTypeEnum type;
            public byte[] bin;
            public bool[] mask;
        }

        /// <summary>
        /// Result of tracing an EA file: the ranges we COULD reconstruct, plus notes
        /// about blocks we could NOT (so an uninstall is never silently incomplete).
        /// </summary>
        public sealed class TraceResult
        {
            /// <summary>The ranges the EA patch wrote that we reconstructed.</summary>
            public List<BinMapping> Mappings { get; } = new List<BinMapping>();
            /// <summary>
            /// Human-readable descriptions of blocks we could NOT trace (GREP miss,
            /// guarded PROCS length, un-hinted inline PNG raster, …). Each entry is one
            /// piece of patch residue a revert will leave behind.
            /// </summary>
            public List<string> Untraceable { get; } = new List<string>();
            /// <summary>True when every emitting block was reconstructed (no residue).</summary>
            public bool FullyTraced => Untraceable.Count == 0;
            /// <summary>
            /// Count of WRITE-BEARING blocks the trace could not reconstruct (a GREP
            /// miss, the guarded PROCS, an un-hinted inline PNG raster). Empty/comment
            /// blocks that write nothing are NOT counted — they leave no residue.
            /// Equals <see cref="Untraceable"/>.Count.
            /// </summary>
            public int UntracedCount => Untraceable.Count;
        }

        /// <summary>Structured result of an uninstall run.</summary>
        public sealed class UninstallResult
        {
            /// <summary>True when the patch ranges were restored from the clean ROM.</summary>
            public bool Success { get; set; }
            /// <summary>Localized error (set when <see cref="Success"/> is false).</summary>
            public string ErrorMessage { get; set; } = "";
            /// <summary>Number of traced ranges that were reverted.</summary>
            public int RangeCount { get; set; }
            /// <summary>Total bytes written back from the clean ROM.</summary>
            public uint BytesReverted { get; set; }
            /// <summary>
            /// False when the trace could NOT account for every block the EA patch
            /// wrote, so the revert may leave patch residue. The View MUST warn the user
            /// when this is false (a "success" with residue is the outcome to avoid).
            /// </summary>
            public bool FullyTraced { get; set; } = true;
            /// <summary>
            /// Count of WRITE-BEARING blocks the trace could not revert (a GREP miss,
            /// the guarded PROCS, an un-hinted inline PNG raster). > 0 means the revert
            /// is INCOMPLETE — those bytes remain patched. <see cref="Success"/> can
            /// still be true (the traced ranges WERE reverted); the View must surface
            /// this so the user can confirm/verify. Equals <see cref="UntraceableBlocks"/>.Count.
            /// </summary>
            public int UntracedCount { get; set; }
            /// <summary>Descriptions of the blocks that could not be traced (residue).</summary>
            public List<string> UntraceableBlocks { get; set; } = new List<string>();
        }

        /// <summary>
        /// Trace the ranges an EA file writes, against the CURRENT (patched) ROM in
        /// <see cref="CoreState.ROM"/>. Faithful port of the EA branch of
        /// <c>TraceEAPatchedMapping</c>, walking a single .event's
        /// <see cref="EAUtilCore"/> DataList. The result carries BOTH the reconstructed
        /// ranges AND notes about any block that could NOT be traced (GREP miss, guarded
        /// PROCS, un-hinted inline PNG raster) so a revert is never silently incomplete.
        /// Returns an empty result (with one Untraceable note) when the ROM is not loaded
        /// or the file cannot be parsed.
        /// </summary>
        public static TraceResult TraceEAFile(string eaFilePath)
        {
            var result = new TraceResult();
            List<BinMapping> binMappings = result.Mappings;
            ROM rom = CoreState.ROM;
            // RomInfo is needed for the GREP search baseline
            // (compress_image_borderline_address); it is null only for a not-yet-
            // identified ROM, which the EA tool never uninstalls against. Guard so a
            // bad caller gets an empty trace (→ clean "could not trace" error) rather
            // than an NRE.
            if (rom == null || rom.RomInfo == null
                || string.IsNullOrEmpty(eaFilePath) || !File.Exists(eaFilePath))
            {
                result.Untraceable.Add(R._("No identified ROM, or the event file is missing."));
                return result;
            }
            if (EAUtilCore.IsFBGTemp(eaFilePath))
            {
                result.Untraceable.Add(R._("Temporary wrapper file is not a traceable patch."));
                return result;
            }

            EAUtilCore ea;
            try
            {
                ea = new EAUtilCore(eaFilePath);
            }
            catch (Exception ex)
            {
                // The parser can throw more than IOException (UnauthorizedAccessException,
                // ArgumentException for a bad path, parse errors). Honour the "never
                // throws" contract: turn any failure into an untraceable note + empty
                // trace, which the caller surfaces as a clean "could not trace" error.
                result.Untraceable.Add(R._("Could not read the event file: {0}", ex.Message));
                return result;
            }

            // Carry over parser-level untraceable blocks (un-hinted inline PNG rasters).
            result.Untraceable.AddRange(ea.UntraceableNotes);

            EmitEaDataList(rom, ea, binMappings, result.Untraceable);

            return result;
        }

        /// <summary>
        /// Result of the producer-only PROCS handler (<see cref="ProcsEmitHandler"/>):
        /// the reconstructed PROCS <see cref="BinMapping"/> (or <c>null</c> when its length
        /// could not be determined — i.e. <c>CalcProcsLengthAndCheck == NOT_FOUND</c>, the
        /// WF skip-on-NOT_FOUND case), plus the advanced <c>lastMatchAddr</c> baseline the
        /// walker must adopt for the next block. Mirrors the tail of WF
        /// <c>PatchForm.TraceEAPatchedMapping</c>'s PROCS branch (5437-5471).
        /// </summary>
        internal struct ProcsEmitResult
        {
            /// <summary>The PROCS mapping, or null to skip (WF <c>continue</c> on NOT_FOUND).</summary>
            public BinMapping Mapping;
            /// <summary>The new <c>lastMatchAddr</c> baseline for the next block.</summary>
            public uint NewLastMatchAddr;
        }

        /// <summary>
        /// Producer-only hook that resolves a PROCS block's address+length and builds its
        /// <see cref="BinMapping"/>. The uninstall path passes <c>null</c> (PROCS length
        /// detection is WinForms-only there → the block is recorded as untraceable and
        /// skipped, byte-identical to the original walk). The rebuild-producer EA arm
        /// (#1261 s2pf-14) passes a handler backed by
        /// <c>RebuildProducerCore.CalcProcsLengthAndCheck</c> so a real PROCS is EMITTED
        /// (skipping a live PROCS in the producer would silently corrupt the rebuild free
        /// list). Receives the ROM, the PROCS <see cref="EAUtilCore.Data"/>, and the
        /// <c>lastMatchAddr</c> baseline ALREADY advanced by <c>Append</c> + Padding4
        /// (exactly as WF does at the TOP of its PROCS branch, before the GREP/length).
        /// </summary>
        internal delegate ProcsEmitResult ProcsEmitHandler(ROM rom, EAUtilCore.Data data, uint advancedLastMatchAddr);

        /// <summary>
        /// The byte-identical EA DataList walk shared by the uninstall trace
        /// (<see cref="TraceEAFile"/>) and the rebuild-producer EA arm (#1261 s2pf-14).
        /// Walks the parsed <paramref name="ea"/> entries (ORG / ASM / MIX / LYN /
        /// LYNHOOK / POINTER_ARRAY / PROCS / BIN), building the
        /// <see cref="BinMapping"/>s it can reconstruct into <paramref name="binMappings"/>
        /// by GREP-matching the expanded bytes against <paramref name="rom"/> from a
        /// monotonically advancing <c>lastMatchAddr</c> baseline (seeded at
        /// <c>RomInfo.compress_image_borderline_address</c>), and recording any block it
        /// could NOT trace (GREP miss, guarded PROCS) into <paramref name="untraceable"/>.
        ///
        /// <para>Exposed (internal static, ROM-explicit) for the rebuild-producer EA arm,
        /// s2pf-14; this is the ONE walker both the uninstall path and the producer arm
        /// call, so they stay byte-identical. Behaviour is unchanged from the original
        /// inline <see cref="TraceEAFile"/> walk — the only difference is that the ROM it
        /// reads is passed explicitly rather than re-read from <see cref="CoreState.ROM"/>
        /// (the uninstall caller passes <c>CoreState.ROM</c>, so the result is identical).</para>
        ///
        /// <para>The ONLY divergence between the two callers is <paramref name="procsHandler"/>:
        /// when <c>null</c> (uninstall, the default) a PROCS block is recorded as
        /// untraceable and skipped (WinForms-only length detection); when supplied
        /// (producer) the handler reproduces WF's PROCS emit (GREP + CalcProcsLengthAndCheck,
        /// skip ONLY on NOT_FOUND). Every other branch is identical for both callers, so the
        /// uninstall behaviour is NOT regressed.</para>
        /// </summary>
        /// <param name="rom">The ROM the expanded blocks are GREP-matched against (the
        /// current/patched ROM for the uninstall path). Must be non-null with a non-null
        /// <c>RomInfo</c> — the callers guard this before calling.</param>
        /// <param name="ea">The parsed EA file whose <see cref="EAUtilCore.DataList"/> is walked.</param>
        /// <param name="binMappings">Accumulator for the reconstructed ranges (appended to).</param>
        /// <param name="untraceable">Accumulator for blocks that could not be traced (appended to).</param>
        /// <param name="procsHandler">Producer-only PROCS emit hook (see
        /// <see cref="ProcsEmitHandler"/>); <c>null</c> for the uninstall path, which skips
        /// PROCS as residue exactly as before.</param>
        /// <param name="initialLastMatchAddr">The starting GREP/POINTER_ARRAY/PROCS baseline.
        /// <c>null</c> (the uninstall caller's default) seeds it from
        /// <c>rom.RomInfo.compress_image_borderline_address</c> — byte-identical to the
        /// original single-file walk. The PRODUCER's multi-event-file loop passes the value
        /// RETURNED by the previous file's call, so the baseline is CARRIED ACROSS files
        /// exactly as WF <c>PatchForm.TraceEAPatchedMapping</c> does (it seeds
        /// <c>lastMatchAddr</c> ONCE at :5266, BEFORE the <c>foreach (files)</c> loop at
        /// :5268). Without this, file 2+ would re-seed at the borderline and a duplicate
        /// pattern earlier in free space could be mis-selected (Copilot PR #1329 finding).</param>
        /// <returns>The advanced <c>lastMatchAddr</c> after walking this file — the producer
        /// threads it into the next file's call to preserve the cross-file baseline.</returns>
        internal static uint EmitEaDataList(ROM rom, EAUtilCore ea, List<BinMapping> binMappings, List<string> untraceable,
            ProcsEmitHandler procsHandler = null, uint? initialLastMatchAddr = null)
        {
            uint lastMatchAddr = initialLastMatchAddr ?? rom.RomInfo.compress_image_borderline_address;

            for (int n = 0; n < ea.DataList.Count; n++)
            {
                EAUtilCore.Data data = ea.DataList[n];
                if (data.DataType == EAUtilCore.DataEnum.ORG)
                {
                    uint addr = data.ORGAddr;

                    var b = new BinMapping
                    {
                        key = "ORG",
                        filename = "",
                        addr = addr,
                        length = 0, //不明
                        type = Address.DataTypeEnum.MIX,
                    };

                    if (U.isSafetyOffset(addr + 64, rom))
                    {//長さが不明なので比較するとき困るので適当に64バイトほど取得します.
                        b.bin = rom.getBinaryData(addr, 64);
                        b.mask = MakeMaskAddress(b.bin, addr, rom);
                    }
                    else
                    {
                        b.bin = new byte[0] { };
                        b.mask = new bool[0] { };
                    }

                    binMappings.Add(b);
                    lastMatchAddr = addr;
                }
                else if (data.DataType == EAUtilCore.DataEnum.ASM
                    || data.DataType == EAUtilCore.DataEnum.MIX)
                {
                    if (data.BINData == null || data.BINData.Length == 0)
                    {//empty data
                        continue;
                    }
                    //展開されるものを生成して、GREP検索する必要があります.
                    bool[] isSkip;
                    byte[] mod = ReadMod(data.BINData, out isSkip, rom);

                    //可変なので、maskパターンを作って検索します.
                    uint addr = U.GrepPatternMatch(rom.Data, mod, isSkip, lastMatchAddr, 0, 4);
                    if (addr == U.NOT_FOUND)
                    {//パッチが見つからなかった — 復元できない残骸として記録する.
                        untraceable.Add(R._("{0} block not found in ROM: {1}",
                            data.DataType.ToString(), BlockName(data.Name)));
                        continue;
                    }

                    uint length = (uint)mod.Length;

                    var b = new BinMapping
                    {
                        key = data.DataType.ToString(),
                        filename = data.Name,
                        addr = addr,
                        length = length,
                        bin = rom.getBinaryData(addr, length),
                        mask = isSkip,
                        type = data.DataType == EAUtilCore.DataEnum.ASM
                            ? Address.DataTypeEnum.PATCH_ASM
                            : Address.DataTypeEnum.MIX,
                    };

                    EraseORG(binMappings, b);
                    binMappings.Add(b);

                    lastMatchAddr = addr + length;
                }
                else if (data.DataType == EAUtilCore.DataEnum.LYN)
                {
                    if (data.BINData == null || data.BINData.Length == 0)
                    {//empty data
                        continue;
                    }
                    //展開されるものを生成して、GREP検索する必要があります.
                    bool[] isSkip = MakeLynMaskPattern(data.BINData);

                    //可変なので、maskパターンを作って検索します.
                    uint addr = U.GrepPatternMatch(rom.Data, data.BINData, isSkip, lastMatchAddr, 0, 4);
                    if (addr == U.NOT_FOUND)
                    {//LYN ELFが見つからなかった — 復元できない残骸として記録する.
                        untraceable.Add(R._("LYN block not found in ROM: {0}", BlockName(data.Name)));
                        continue;
                    }
                    uint length = (uint)data.BINData.Length;

                    var b = new BinMapping
                    {
                        key = data.DataType.ToString(),
                        filename = data.Name,
                        addr = addr,
                        length = length,
                        bin = rom.getBinaryData(addr, length),
                        mask = isSkip,
                        type = Address.DataTypeEnum.PATCH_ASM,
                    };

                    EraseORG(binMappings, b);
                    binMappings.Add(b);

                    lastMatchAddr = addr + length;
                }
                else if (data.DataType == EAUtilCore.DataEnum.LYNHOOK)
                {
                    uint addr = data.ORGAddr;
                    // 20 bytes (NOT the 16-byte hook stub): matches WF
                    // PatchForm.TraceEAPatchedMapping, which reverts the 16-byte lyn hook
                    // stub plus the 4-byte aligned slot lyn may also overwrite. See the
                    // EAUtilCore.DataEnum.LYNHOOK comment.
                    uint length = (uint)20;

                    var b = new BinMapping
                    {
                        key = "ORG",
                        filename = "",
                        addr = data.ORGAddr,
                        length = length,
                        bin = rom.getBinaryData(addr, length),
                        type = Address.DataTypeEnum.PATCH_ASM,
                    };
                    b.mask = MakeMaskAddress(b.bin, addr, rom);

                    binMappings.Add(b);

                    lastMatchAddr = addr + length;
                }
                else if (data.DataType == EAUtilCore.DataEnum.POINTER_ARRAY)
                {
                    //最後に書き込んだ部分から、ポインタと思われる部分を連続して検出する.
                    lastMatchAddr += data.Append;
                    lastMatchAddr = U.Padding4(lastMatchAddr);

                    uint addr = lastMatchAddr;
                    for (; addr + 3 < rom.Data.Length; addr += 4)
                    {
                        uint a = rom.u32(addr);
                        if (!U.isSafetyPointer(a, rom))
                        {
                            break;
                        }
                    }
                    uint length = addr - lastMatchAddr;
                    if (length <= 0)
                    {
                        continue;
                    }
                    addr = lastMatchAddr;

                    var b = new BinMapping
                    {
                        key = data.DataType.ToString(),
                        filename = data.Name,
                        addr = addr,
                        length = length,
                        bin = rom.getBinaryData(addr, length),
                        mask = MakeFullMask(length),
                        type = Address.DataTypeEnum.POINTER_ARRAY,
                    };

                    EraseORG(binMappings, b);
                    binMappings.Add(b);

                    lastMatchAddr = addr + length;
                }
                else if (data.DataType == EAUtilCore.DataEnum.PROCS)
                {
                    // Advance the GREP baseline BEFORE skipping — exactly as WF
                    // PatchForm.TraceEAPatchedMapping does (lastMatchAddr += Append;
                    // Padding4) at the TOP of its PROCS branch, before the
                    // CalcLengthAndCheck==NOT_FOUND `continue`. Omitting this would leave
                    // lastMatchAddr pointing BEFORE the PROCS region, so the next block's
                    // GREP could match an earlier address and revert the WRONG bytes.
                    lastMatchAddr += data.Append;
                    lastMatchAddr = U.Padding4(lastMatchAddr);

                    if (procsHandler == null)
                    {
                        // UNINSTALL path (byte-identical to the original walk): PROCS length
                        // detection (ProcsScriptForm.CalcLengthAndCheck) is WinForms-only;
                        // without it the length is unknown, so we skip the range itself —
                        // identical to the WinForms tracer's `continue` when CalcLengthAndCheck
                        // returns NOT_FOUND. Record it as residue so the caller can warn.
                        // (See class doc scope note.)
                        untraceable.Add(R._("PROCS table (length detection is WinForms-only): {0}",
                            string.IsNullOrEmpty(data.Name) ? R._("(label)") : data.Name));
                        continue;
                    }

                    // PRODUCER path (#1261 s2pf-14): the handler reproduces WF's PROCS emit
                    // (PatchForm.cs:5437-5471) — GREP the BINData from the borderline, then
                    // CalcProcsLengthAndCheck the resolved address; on NOT_FOUND it returns a
                    // null Mapping (verbatim WF `continue` at 5453-5455), and we record the
                    // skip as residue (honest omission — NEVER a guessed length). The walker
                    // keeps the handler's advanced lastMatchAddr either way.
                    ProcsEmitResult pr = procsHandler(rom, data, lastMatchAddr);
                    lastMatchAddr = pr.NewLastMatchAddr;
                    if (pr.Mapping == null)
                    {
                        untraceable.Add(R._("PROCS table (length detection failed): {0}",
                            string.IsNullOrEmpty(data.Name) ? R._("(label)") : data.Name));
                        continue;
                    }
                    EraseORG(binMappings, pr.Mapping);
                    binMappings.Add(pr.Mapping);
                    continue;
                }
                else
                {
                    //展開されるものを生成して、GREP検索する必要があります.
                    if (data.BINData == null || data.BINData.Length == 0)
                    {//EAは何も書き込んでいない — 残骸ではない.
                        continue;
                    }
                    uint addr = U.Grep(rom.Data, data.BINData, lastMatchAddr);
                    if (addr == U.NOT_FOUND)
                    {//BINが見つからなかった — 復元できない残骸として記録する.
                        untraceable.Add(R._("BIN block not found in ROM: {0}", BlockName(data.Name)));
                        continue;
                    }
                    uint length = (uint)data.BINData.Length;

                    var b = new BinMapping
                    {
                        key = data.DataType.ToString(),
                        filename = data.Name,
                        addr = addr,
                        length = length,
                        bin = data.BINData,
                        type = Address.DataTypeEnum.BIN,
                    };
                    if (data.DataType == EAUtilCore.DataEnum.BIN)
                    {
                        b.mask = new bool[length];
                    }
                    else
                    {
                        b.mask = MakeMaskAddress(b.bin, addr, rom);
                    }

                    EraseORG(binMappings, b);
                    binMappings.Add(b);

                    lastMatchAddr = addr + length;
                }
            }

            // Return the advanced baseline so the producer's multi-event-file loop can carry
            // it into the next file (WF seeds lastMatchAddr ONCE before its foreach). The
            // uninstall caller walks a single file and ignores this.
            return lastMatchAddr;
        }

        /// <summary>
        /// Trace <paramref name="eaFilePath"/> against the live ROM and revert every
        /// traced range to the bytes in <paramref name="cleanOriginalRom"/> (the ROM as
        /// it was before the patch), recording all writes into <paramref name="undo"/>.
        ///
        /// Validation (mirrors the WinForms uninstall dialog's checks — never throws):
        ///  - a ROM must be loaded;
        ///  - the EA file must exist and trace at least one range;
        ///  - the clean ROM must be non-empty and at least as large as the highest
        ///    traced offset, so a byte-for-byte restore is well-defined.
        /// On any failure the live ROM is left untouched and a localized error is
        /// returned; the caller rolls back <paramref name="undo"/>.
        /// </summary>
        public static UninstallResult Uninstall(string eaFilePath, byte[] cleanOriginalRom, Undo.UndoData undo)
        {
            var result = new UninstallResult();

            ROM rom = CoreState.ROM;
            if (rom == null)
            {
                result.ErrorMessage = R._("No ROM is loaded.");
                return result;
            }
            if (string.IsNullOrEmpty(eaFilePath) || !File.Exists(eaFilePath))
            {
                result.ErrorMessage = R._("Event file not found: {0}", eaFilePath ?? "");
                return result;
            }
            if (cleanOriginalRom == null || cleanOriginalRom.Length <= 0)
            {
                result.ErrorMessage = R._("The selected clean ROM is empty.");
                return result;
            }
            if (undo == null)
            {
                result.ErrorMessage = R._("Undo manager unavailable.");
                return result;
            }

            TraceResult trace = TraceEAFile(eaFilePath);
            List<BinMapping> binmap = trace.Mappings;

            // Surface untraceable blocks on the result so the View can warn the user
            // (a "success" that leaves patch residue is the outcome to avoid).
            result.FullyTraced = trace.FullyTraced;
            result.UntraceableBlocks = trace.Untraceable;
            result.UntracedCount = trace.UntracedCount;

            if (binmap.Count == 0)
            {
                // Nothing reconstructible. If the trace recorded untraceable blocks,
                // say so specifically (residue we can't revert) rather than the generic
                // "not a patch" message.
                result.ErrorMessage = trace.Untraceable.Count > 0
                    ? R._("This event file's patched ranges cannot be traced for in-place uninstall ({0} block(s)). Uninstall it via the WinForms patch manager, or revert with a backup ROM.",
                        trace.Untraceable.Count.ToString())
                    : R._("Could not trace any patched ranges from this event file. It may not be an applied EA patch, or it uses constructs this in-place uninstall does not support.");
                return result;
            }

            // The clean ROM must cover the highest traced offset, otherwise we cannot
            // restore those bytes — reject up front (mirrors the WF size sanity check).
            uint highest = 0;
            foreach (BinMapping map in binmap)
            {
                uint end = map.addr + (map.length == 0 ? 1u : map.length);
                if (end > highest) highest = end;
            }
            if (highest > cleanOriginalRom.Length)
            {
                result.ErrorMessage = R._("The selected clean ROM is too small ({0} bytes) for this patch (needs at least {1} bytes). Did you pick the wrong file?",
                    cleanOriginalRom.Length.ToString(), highest.ToString());
                return result;
            }

            string error = UninstallPatchInner(binmap, cleanOriginalRom, undo, result);
            if (error != "")
            {
                result.ErrorMessage = error;
                return result;
            }

            // Reverted the ranges we COULD trace. Success is true, but FullyTraced may be
            // false (residue remains) — the View MUST warn the user in that case.
            result.Success = true;
            result.RangeCount = binmap.Count;
            return result;
        }

        // Faithful port of PatchForm.UninstallPatchInner (EA path): overwrite each
        // traced byte with the clean-original byte, under undo; auto-length unknown
        // ranges; then strip a zero-filled extended ROM tail.
        static string UninstallPatchInner(List<BinMapping> binmap, byte[] orignalROM, Undo.UndoData undodata, UninstallResult result)
        {
            ROM rom = CoreState.ROM;
            uint current_rom_length = (uint)rom.Data.Length;
            uint reverted = 0;

            for (int n = 0; n < binmap.Count; n++)
            {
                BinMapping map = binmap[n];

                if (map.length == 0)
                {//サイズがわからないので、自動的に求めます.
                    map.length = CalcAutoLength(map.addr, orignalROM);
                    map.bin = rom.getBinaryData(map.addr, map.length);
                }

                for (int i = 0; i < map.length; i++)
                {
                    uint addr = map.addr + (uint)i;
                    CoreState.CommentCache?.Remove(addr);
                    if (addr >= current_rom_length)
                    {
                        continue;
                    }

                    uint o = AtByte(orignalROM, addr); //パッチを含んでいないROMの内容
                    rom.write_u8(addr, o, undodata);
                    reverted++;
                }
                // (The WinForms MENU key fixup via MenuCommandForm is BIN-patch only and
                // does not arise for an instant-EA patch — omitted, see class doc.)
            }

            StripROM(binmap, undodata);

            result.BytesReverted = reverted;
            return "";
        }

        // Port of PatchForm.StripROM: if reverting leaves the extended ROM tail all
        // zero, shrink the ROM back down (recorded in undo as a resize position).
        static void StripROM(List<BinMapping> binmap, Undo.UndoData undodata)
        {
            ROM rom = CoreState.ROM;
            uint extendsAddr = U.toOffset(rom.RomInfo.extends_address);
            int length = rom.Data.Length;

            //終端が0x00で埋まるならROMサイズを小さくする.
            uint stripSize = U.NOT_FOUND;
            for (int n = 0; n < binmap.Count; n++)
            {
                BinMapping map = binmap[n];
                uint addr = map.addr;
                if (addr < extendsAddr)
                {//拡張領域ではないのでstripできない.
                    continue;
                }

                for (int i = (int)addr; i < length; i++, addr++)
                {
                    if (rom.Data[i] != 0x00)
                    {
                        break;
                    }
                }
                if (addr == length)
                {
                    if (stripSize > map.addr)
                    {
                        stripSize = map.addr;
                    }
                }
            }
            if (rom.Data.Length > stripSize
                && stripSize >= extendsAddr)
            {
                undodata.list.Add(new Undo.UndoPostion(stripSize, (uint)rom.Data.Length - stripSize));
                rom.write_resize_data(stripSize);
            }
        }

        // Port of PatchForm.CalcAutoLength: how far the live ROM diverges from the
        // clean ROM starting at addr, tolerating up to RecoverMissMatch matching bytes.
        static uint CalcAutoLength(uint addr, byte[] other, int RecoverMissMatch = 10)
        {
            ROM rom = CoreState.ROM;
            int length = Math.Min(rom.Data.Length, other.Length);
            int i;
            for (i = (int)addr; i < length; i++)
            {
                if (rom.Data[i] != other[i])
                {
                    continue;
                }

                i++;
                int missCount = 0;
                for (; i < length; i++)
                {
                    if (rom.Data[i] != other[i])
                    {
                        i -= missCount;
                        break;
                    }

                    if (missCount >= RecoverMissMatch)
                    {
                        i -= missCount;

                        return ((uint)i) - addr;
                    }

                    missCount++;
                }
            }

            return ((uint)i) - addr;
        }

        // ---- Trace helpers (ports of the PatchForm private statics) ---------------
        //
        // These are exposed internal static for the rebuild-producer EA arm (#1261
        // s2pf-14), which calls them — together with <see cref="EmitEaDataList"/> — to
        // stay byte-identical to this verified #1242 uninstall trace. MakeMaskAddress /
        // ReadMod take an explicit ROM (matching the ROM-explicit walker — they only used
        // CoreState.ROM for the isSafetyOffset length bound, threaded through now);
        // MakeLynMaskPattern / MakeFullMask / EraseORG are pure (no ROM read). Behaviour
        // is unchanged: the uninstall caller passes CoreState.ROM, and the
        // U.isSafetyOffset(uint, ROM) overload is the same formula as U.isSafetyOffset(uint).

        // Port of PatchForm.MakeMaskAddress: mask the LDR-pointer bytes inside a code
        // block so the address-dependent words don't break a GREP pattern match.
        /// <summary>
        /// Mask the LDR-pointer bytes inside a code block so the address-dependent words
        /// don't break a GREP pattern match. ROM-explicit (only reads
        /// <paramref name="rom"/> for the <see cref="U.isSafetyOffset(uint, ROM)"/> length
        /// bound). Exposed for the rebuild-producer EA arm, s2pf-14; byte-identical to the
        /// uninstall walk (which passes <see cref="CoreState.ROM"/>).
        /// </summary>
        internal static bool[] MakeMaskAddress(byte[] original, uint base_address, ROM rom)
        {
            bool[] isSkip = new bool[original.Length];

            base_address = U.toOffset(base_address);
            if (!U.isSafetyOffset(base_address, rom))
            {
                return isSkip;
            }

            List<DisassemblerTrumb.LDRPointer> ldr = DisassemblerTrumb.MakeLDRMap(original, 0);

            uint base_pointer = U.toPointer(base_address);
            for (int i = 0; i < ldr.Count; i++)
            {
                if (ldr[i].ldr_data >= base_pointer
                    && ldr[i].ldr_data <= base_pointer + original.Length)
                {
                    isSkip[ldr[i].ldr_data_address + 0] = true;
                    isSkip[ldr[i].ldr_data_address + 1] = true;
                    isSkip[ldr[i].ldr_data_address + 2] = true;
                    isSkip[ldr[i].ldr_data_address + 3] = true;
                }
            }
            return isSkip;
        }

        // Port of PatchForm.ReadMod(string[], byte[], out bool[]) — the instant-EA path
        // has no per-key address-move args, so chaddr is 0 and the mask is built off
        // base 0 (matching ReadMod with an empty sp[]).
        /// <summary>
        /// Build the GREP mask for an ASM/MIX block off base 0 (instant-EA path has no
        /// per-key address-move args). ROM-explicit (forwards <paramref name="rom"/> to
        /// <see cref="MakeMaskAddress"/>). Exposed for the rebuild-producer EA arm,
        /// s2pf-14; byte-identical to the uninstall walk (which passes <see cref="CoreState.ROM"/>).
        /// </summary>
        internal static byte[] ReadMod(byte[] b, out bool[] isSkip, ROM rom)
        {
            uint chaddr = 0; // U.atoi0x(U.at(sp, 2)) with an empty sp == 0
            isSkip = MakeMaskAddress(b, chaddr, rom);
            return b;
        }

        // Port of PatchForm.ReadMod(string[], string filename, out bool[]) — the
        // FILE-reading overload (WinForms PatchForm.cs:4309). Reads a BIN file from disk
        // and builds the GREP mask off the per-key address-move arg (sp[2]) instead of the
        // instant-EA hardcoded base 0. Exposed for the rebuild-producer BIN arm (#1261
        // s2pf-15) — byte-identical to WF: missing file -> empty bin + empty mask; else
        // read all bytes and mask via MakeMaskAddress with chaddr = atoi0x(at(sp,2)).
        /// <summary>
        /// Read the installed BIN file from <paramref name="filename"/> and build its GREP
        /// mask off the per-key address-move arg <c>sp[2]</c> (WinForms
        /// <c>PatchForm.ReadMod(string[], string, out bool[])</c>, :4309). When the file is
        /// absent this returns an empty byte[] + empty mask (verbatim WF). ROM-explicit
        /// (forwards <paramref name="rom"/> to <see cref="MakeMaskAddress"/>). Exposed for
        /// the rebuild-producer TYPE=BIN arm, s2pf-15.
        /// </summary>
        internal static byte[] ReadMod(string[] sp, string filename, out bool[] isSkip, ROM rom)
            => ReadModCore(sp, filename, out isSkip, rom, System.IO.File.Exists, System.IO.File.ReadAllBytes);

        internal static byte[] ReadModWithFileReadsForTest(string[] sp, string filename, out bool[] isSkip, ROM rom,
            Func<string, bool> fileExists, Func<string, byte[]> readBytes)
            => ReadModCore(sp, filename, out isSkip, rom, fileExists, readBytes);

        static byte[] ReadModCore(string[] sp, string filename, out bool[] isSkip, ROM rom,
            Func<string, bool> fileExists, Func<string, byte[]> readBytes)
        {
            if (string.IsNullOrEmpty(filename) || !fileExists(filename))
            {//WF :4311-4315 — missing file: empty bin + empty mask.
                isSkip = new bool[0];
                return new byte[0];
            }
            // WF :4317 — read the whole installed BIN file. Byte-faithful to WF: WF guards
            // ONLY File.Exists, then calls File.ReadAllBytes directly (NO try/catch). We do
            // NOT swallow a read failure into empty output — that would widen WF behaviour
            // and silently hide a corrupt/locked installed BIN file as a zero-length mapping
            // (Copilot plan-review #1261 s2pf-15). A genuine read error propagates exactly as
            // it does in WF.
            byte[] b = readBytes(filename);

            // WF :4320 — chaddr = U.atoi0x(U.at(sp, 2)); the address the block was relocated
            // to, so MakeMaskAddress can mask the LDR-pointer words that depend on it.
            uint chaddr = U.atoi0x(U.at(sp, 2));
            isSkip = MakeMaskAddress(b, chaddr, rom);
            return b;
        }

        //lynによってインポートされるelfのマスクパターンを作ります。
        // Port of PatchForm.MakeLynMaskPattern.
        /// <summary>
        /// Build the GREP mask for a lyn-imported ELF (mask the all-zero 4-byte words
        /// lyn relocates). Pure (no ROM read). Exposed for the rebuild-producer EA arm,
        /// s2pf-14; byte-identical to the uninstall walk.
        /// </summary>
        internal static bool[] MakeLynMaskPattern(byte[] bin)
        {
            bool[] isSkip = new bool[bin.Length];
            for (int i = 0; i + 3 < bin.Length; i += 4)
            {
                if (bin[i] == 0 && bin[i + 1] == 0 && bin[i + 2] == 0 && bin[i + 3] == 0)
                {
                    isSkip[i + 0] = true;
                    isSkip[i + 1] = true;
                    isSkip[i + 2] = true;
                    isSkip[i + 3] = true;
                }
            }
            return isSkip;
        }

        // Port of PatchForm.MakeFullMask.
        /// <summary>
        /// Build an all-true mask of <paramref name="length"/> bytes (POINTER_ARRAY,
        /// where every byte is address-dependent). Pure (no ROM read). Exposed for the
        /// rebuild-producer EA arm, s2pf-14; byte-identical to the uninstall walk.
        /// </summary>
        internal static bool[] MakeFullMask(uint length)
        {
            bool[] r = new bool[length];
            for (int i = 0; i < length; i++)
            {
                r[i] = true;
            }
            return r;
        }

        // Port of PatchForm.EraseORG: a later concrete mapping at the same address
        // supersedes a provisional ORG mapping there.
        /// <summary>
        /// Drop a provisional ORG mapping superseded by a later concrete mapping at the
        /// same address. Pure (no ROM read). Exposed for the rebuild-producer EA arm,
        /// s2pf-14; byte-identical to the uninstall walk.
        /// </summary>
        internal static void EraseORG(List<BinMapping> binMappings, BinMapping b)
        {
            for (int i = 0; i < binMappings.Count; i++)
            {
                BinMapping a = binMappings[i];
                if (a.addr != b.addr)
                {
                    continue;
                }
                if (a.key == "ORG")
                {
                    binMappings.RemoveAt(i);
                    i--;
                    continue;
                }
            }
        }

        // Safe byte read from a byte[] (port of U.at(byte[], addr) — 0 when OOB).
        static uint AtByte(byte[] data, uint addr)
        {
            if (data == null || addr >= data.Length)
            {
                return 0;
            }
            return data[addr];
        }

        // The display name for an untraceable block: the file/label name, or a
        // localized "(inline)" placeholder for an unnamed (inline) block.
        static string BlockName(string name)
        {
            return string.IsNullOrEmpty(name) ? R._("(inline)") : name;
        }
    }
}
