// SPDX-License-Identifier: GPL-3.0-or-later
// Core-side patch-scan hardcode detector (#1035).
//
// Ports ONLY the patch-scan part of WinForms PatchForm.MakeHardCodeWarning
// (FEBuilderGBA/PatchForm.cs ~:9594):
//   - ScanPatchs(path)          (~:107)  — enumerate PATCH_*.txt recursively.
//   - LoadPatch                 (~:247)  — parse into a Param dict (language
//                                          resolved CleanupKey).
//   - isCanonicalSkip(patch)    (~:6889) — CANONICAL_SKIP=true short-circuit.
//   - CheckIFFast/CheckIF       (~:4385) — IF/IFNOT/PATCHED_IF/PATCHED_IFNOT/
//                                          CONFLICT_IF tri-state gate. A patch is
//                                          skipped only when the gate returns "E".
//   - GetAddrPatchAddressToValue8Fast (~:9560) — read the ADDRESS-param 8-bit id.
//
// This is NOT the full ASM/MAP symbol pipeline (GetAsmMapFile / SearchNear),
// which is tracked separately by #1026. Hardcode warnings sourced from
// disassembled ASM references are out of scope here.
//
// Every ROM read is bounds/EOF guarded; the scanner never throws. No CoreState
// reads for the ROM/safety paths — the ROM is always passed explicitly so it
// works in headless tests where CoreState.ROM may be a different instance.
using System;
using System.Collections.Generic;
using System.IO;
// Unify the scanner's patch record onto the public WinForms-mirror type so the
// #1261 producer can consume ScanPatchs/LoadPatch results and iterate patch.Param
// without a private nested type. The scanner only sets PatchFileName + Param and
// reads only those two fields, so the existing ScanHardCodes behaviour is unchanged.
using PatchSt = FEBuilderGBA.PatchInstallCore.PatchSt;

namespace FEBuilderGBA
{
    public static class PatchHardCodeScanner
    {

        /// <summary>
        /// Scan all PATCH_*.txt files for the loaded ROM version and populate the
        /// three hardcode lookup arrays (each must be length 256, keyed by id).
        /// Ports WinForms PatchForm.MakeHardCodeWarning faithfully:
        ///   skip if isCanonicalSkip, skip if CheckIF == "E", then for
        ///   ADDRESS_TYPE in {UNIT,CLASS,ITEM} read the 8-bit id and (if != 0 and
        ///   in range) set the matching array slot.
        /// Read-only: never mutates the ROM. Never throws.
        /// </summary>
        public static void ScanHardCodes(ROM rom, bool[] unit, bool[] cls, bool[] item)
        {
            if (rom == null || rom.Data == null) return;
            if (unit == null || cls == null || item == null) return;

            string version = SafeVersionFolder(rom);
            if (version == "") return;

            string patchDir = ResolvePatchDirectory(version);
            string lang = CoreState.Language ?? "en";

            List<PatchSt> patchs = ScanPatchs(rom, patchDir, lang);
            foreach (PatchSt patch in patchs)
            {
                if (isCanonicalSkip(patch))
                {
                    continue;
                }
                string checkIF = CheckIF(rom, patch);
                if (checkIF == "E")
                {
                    continue;
                }

                string addressType = U.at(patch.Param, "ADDRESS_TYPE");
                if (addressType == "UNIT")
                {
                    uint id = GetAddrPatchAddressToValue8Fast(rom, patch);
                    if (id == 0) continue;
                    if (id < unit.Length) unit[id] = true;
                }
                else if (addressType == "CLASS")
                {
                    uint id = GetAddrPatchAddressToValue8Fast(rom, patch);
                    if (id == 0) continue;
                    if (id < cls.Length) cls[id] = true;
                }
                else if (addressType == "ITEM")
                {
                    uint id = GetAddrPatchAddressToValue8Fast(rom, patch);
                    if (id == 0) continue;
                    if (id < item.Length) item[id] = true;
                }
            }
        }

        /// <summary>
        /// Resolve the config/patch2/{version} directory: CoreState.BaseDirectory,
        /// then the current dir, then walk up to a repo root (.git) for dev runs.
        /// Mirrors the Avalonia PatchManagerViewModel.ResolvePatchDirectory walk so
        /// headless tests find the submodule checkout.
        /// </summary>
        public static string ResolvePatchDirectory(string version)
        {
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(CoreState.BaseDirectory)) roots.Add(CoreState.BaseDirectory);
            roots.Add(AppContext.BaseDirectory);
            try { roots.Add(Directory.GetCurrentDirectory()); } catch { }

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string path = Path.Combine(root, "config", "patch2", version);
                if (Directory.Exists(path)) return path;
            }

            // Development: walk up from each root to a repo root and look there.
            foreach (string root in roots)
            {
                string dir = root;
                for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
                {
                    try
                    {
                        if (Directory.Exists(Path.Combine(dir, ".git")) ||
                            File.Exists(Path.Combine(dir, "FEBuilderGBA.sln")))
                        {
                            string path = Path.Combine(dir, "config", "patch2", version);
                            if (Directory.Exists(path)) return path;
                            break;
                        }
                    }
                    catch { }
                    string parent = Path.GetDirectoryName(dir) ?? "";
                    if (parent == dir) break;
                    dir = parent;
                }
            }

            string fallbackRoot = !string.IsNullOrEmpty(CoreState.BaseDirectory)
                ? CoreState.BaseDirectory : AppContext.BaseDirectory;
            return Path.Combine(fallbackRoot ?? "", "config", "patch2", version);
        }

        static string SafeVersionFolder(ROM rom)
        {
            try
            {
                if (rom.RomInfo == null) return "";
                if (rom.RomInfo.version == 0) return ""; // WF ScanPatchs returns empty for version 0.
                return rom.RomInfo.VersionToFilename ?? "";
            }
            catch { return ""; }
        }

        // ---- ScanPatchs (~:107) -------------------------------------------------
        // PUBLIC for the #1261 ROM-rebuild producer (s2pf-1): the producer's
        // MakePatchStructDataListCore orchestrator calls this with the rom + the
        // ResolvePatchDirectory(version) path + language. Body unchanged.
        public static List<PatchSt> ScanPatchs(ROM rom, string path, string lang)
        {
            var patchs = new List<PatchSt>();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return patchs;

            string[] files;
            try
            {
                files = Directory.GetFiles(path, "PATCH_*.txt", SearchOption.AllDirectories);
            }
            catch
            {
                return patchs;
            }

            foreach (string fullfilename in files)
            {
                PatchSt patch = LoadPatch(rom, fullfilename, lang);
                if (patch == null) continue;
                patchs.Add(patch);
            }
            return patchs;
        }

        // ---- LoadPatch (~:247) --------------------------------------------------
        // PUBLIC for the #1261 producer (s2pf-1). Body unchanged.
        public static PatchSt LoadPatch(ROM rom, string fullfilename, string lang)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullfilename);
            }
            catch
            {
                return null;
            }

            bool canSecondLanguageEnglish = U.CanSecondLanguageEnglish(lang);

            // PatchInstallCore.PatchSt does not default-init Param (the former private
            // nested record did), so initialize it explicitly to preserve behaviour.
            var p = new PatchSt { PatchFileName = fullfilename, Param = new Dictionary<string, string>() };

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // Use the rom-aware OtherLangLine overload (U.cs:851) so the
                // {J}/{U} language-line filter reads the EXPLICIT rom passed to
                // ScanHardCodes — NOT the ambient CoreState.ROM (which may be a
                // different instance in headless tests). Honors this file's
                // "no CoreState ROM reads" header guarantee.
                if (U.IsComment(line) || U.OtherLangLine(line, rom))
                {
                    continue;
                }
                line = U.ClipComment(line);
                line = line.Trim();

                int sep = line.IndexOf('=');
                if (sep < 0)
                {
                    continue;
                }
                string key = line.Substring(0, sep);
                string value = line.Substring(sep + 1);

                key = CleanupKey(key, lang, canSecondLanguageEnglish, p);
                if (key == "")
                {
                    continue;
                }

                p.Param[key] = value;
            }

            // WF returns null when there is no TYPE (not a real patch).
            string type = U.at(p.Param, "TYPE");
            if (type == "")
            {
                return null;
            }
            return p;
        }

        // ---- CleanupKey (~:211) -------------------------------------------------
        static string CleanupKey(string key, string lang, bool canSecondLanguageEnglish, PatchSt patch)
        {
            if (key.Length < 3)
            {
                return key;
            }
            if (key[key.Length - 3] != '.')
            {
                return key;
            }
            string k = key.Substring(key.Length - 2);
            if (k == lang)
            {
                return key.Substring(0, key.Length - 3);
            }

            if (canSecondLanguageEnglish)
            {
                if (k == "en")
                {
                    string ret_key = key.Substring(0, key.Length - 3);
                    if (!patch.Param.ContainsKey(ret_key))
                    {
                        return ret_key;
                    }
                }
            }

            return "";
        }

        // ---- isCanonicalSkip (~:6889) ------------------------------------------
        // PUBLIC for the #1261 producer (s2pf-1). Body unchanged.
        public static bool isCanonicalSkip(PatchSt patch)
        {
            string v = U.at(patch.Param, "CANONICAL_SKIP", "0");
            return U.stringbool(v);
        }

        // ---- CheckIF (~:4395) ---------------------------------------------------
        // Faithful port of the tri-state gate. Returns "E" (excluded / prereq
        // missing), "I" (installed), or "" (passes). MakeHardCodeWarning only
        // skips on "E". The WinForms CacheCheckIF side-table is irrelevant to the
        // gate result, so it is intentionally omitted here.
        // PUBLIC for the #1261 producer (s2pf-1): the orchestrator gates patches with
        // this (the WF MakePatchStructDataList path uses CheckIFFast, whose only
        // difference is the CacheCheckIF side-table — irrelevant to the gate result,
        // already documented as intentionally omitted above). Body unchanged.
        public static string CheckIF(ROM rom, PatchSt patch)
            => CheckIFCore(rom, patch, File.Exists, File.ReadAllBytes);

        internal static string CheckIFWithFileReadsForTest(ROM rom, PatchSt patch,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
            => CheckIFCore(rom, patch, fileExists, readFile);

        static string CheckIFCore(ROM rom, PatchSt patch, Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            foreach (var pair in patch.Param)
            {
                string[] sp = pair.Key.Split(':');
                string key = sp[0];
                string addrstring = U.at(sp, 1);
                string value = pair.Value;

                bool isnot = false;
                if (key != "IF" && key != "PATCHED_IFNOT")
                {
                    if (key != "IFNOT" && key != "PATCHED_IF" && key != "CONFLICT_IF")
                    {
                        continue;
                    }
                    isnot = true;
                }

                string basedir = Path.GetDirectoryName(patch.PatchFileName) ?? "";
                uint address = convertBinAddressString(rom, addrstring, basedir, fileExists, readFile);
                if (!U.isSafetyOffset(address, rom))
                {
                    if (!isnot)
                    {
                        return "E";
                    }
                    continue;
                }

                string[] args = value.Split(' ');
                if (args.Length <= 1)
                {
                    return "E";
                }

                uint[] data = new uint[args.Length];
                bool readOk = true;
                for (int i = 0; i < args.Length; i++)
                {
                    uint a = address + (uint)i;
                    if (!U.isSafetyOffset(a, rom)) { readOk = false; break; }
                    data[i] = rom.u8(a);
                }
                if (!readOk)
                {
                    // Reading the comparison window ran off the ROM. For a positive
                    // condition this means the prereq cannot be satisfied (= "E");
                    // for an inverted one it is treated as "not matching" (continue).
                    if (!isnot) return "E";
                    continue;
                }

                uint[] need = new uint[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    need[i] = U.atoi0x(args[i]);
                }

                bool notFound = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (data[i] != need[i])
                    {
                        notFound = true;
                        break;
                    }
                }

                if (isnot)
                {
                    if (notFound == false)
                    {
                        if (key == "PATCHED_IF")
                        {
                            return "I";
                        }
                        return "E";
                    }
                }
                else
                {
                    if (notFound == true)
                    {
                        if (key == "PATCHED_IF")
                        {
                            return "I";
                        }
                        return "E";
                    }
                }
            }

            return "";
        }

        /// <summary>
        /// Tri-state install status used by the #1261 PatchForm EA/BIN safe-reject gate (s2pf-12).
        /// </summary>
        public enum InstallStatusEnum
        {
            /// <summary>No install marker matched (and every marker was resolvable) — the patch's bytes
            /// are NOT in the ROM, so a rebuild can safely omit it.</summary>
            NotInstalled = 0,
            /// <summary>An install marker matched the ROM — the patch's bytes ARE present.</summary>
            Installed = 1,
            /// <summary>The install status CANNOT be determined: at least one install marker uses a
            /// signature this Core build does NOT resolve byte-faithfully to WinForms (e.g. an
            /// <c>$XGREP</c> masked GREP, <c>$FREEAREA</c>, or another un-ported macro) and the resolver
            /// returned an unsafe offset, OR a resolvable marker's comparison window pointed off the ROM /
            /// failed its byte read. (As of s2pf-18 #1261 the byte-faithful <c>$GREP</c>/<c>$FGREP</c>
            /// family is NOT a source of Unknown: a NOT_FOUND grep — including a MISSING/empty <c>$FGREP</c>
            /// file, which yields an empty pattern — is treated as NOT-MATCHING (-&gt; NotInstalled when no
            /// other marker matches), mirroring WF's CheckIF exactly.) Treated as possibly-installed by the
            /// safe-reject gate (conservative: never claim "safe" when the patch might be present).</summary>
            Unknown = 2,
        }

        /// <summary>
        /// Resolve the INSTALL STATUS of <paramref name="patch"/> against <paramref name="rom"/> for the
        /// #1261 PatchForm EA/BIN safe-reject gate (s2pf-12) — a SOUND over-approximation of the WinForms
        /// <c>PatchForm.IsInstalled</c> (FEBuilderGBA/PatchForm.cs:199), which deems a patch installed iff
        /// its detail-mode <c>CheckIF</c> result contains the substring <c>"PATCHED_IF"</c> (i.e. some
        /// <c>PATCHED_IF</c> match OR some <c>PATCHED_IFNOT</c> match).
        /// <para>
        /// Only the install-marker keys (<c>PATCHED_IF</c> / <c>PATCHED_IFNOT</c>) are inspected — plain
        /// <c>IF</c>/<c>IFNOT</c>/<c>CONFLICT_IF</c> are prerequisites, not install markers, and never make
        /// WF's <c>IsInstalled</c> true. Per marker:
        /// <list type="bullet">
        ///   <item>BYTE-FAITHFUL <c>$GREP</c>/<c>$FGREP</c> family whose grep returns NOT_FOUND (the
        ///   post-install signature is absent — INCLUDING a MISSING/empty <c>$FGREP</c> file, whose empty
        ///   pattern grep's to NOT_FOUND) -&gt; NOT-MATCHING (does NOT make the patch Unknown), exactly
        ///   mirroring WF's CheckIF unsafe-address <c>continue</c> (PatchForm.cs:4416-4433). If no marker
        ///   matches, the patch is NotInstalled (s2pf-18 #1261 — the gate-usefulness narrowing).</item>
        ///   <item>address UNRESOLVABLE via a NON-faithful form (<c>convertBinAddressString</c> -&gt;
        ///   unsafe/NOT_FOUND for an <c>$XGREP</c>/<c>$FREEAREA</c> or another un-ported macro this Core
        ///   build does not resolve byte-faithfully) -&gt; <see cref="InstallStatusEnum.Unknown"/> (the gate
        ///   refuses conservatively).</item>
        ///   <item><c>PATCHED_IF</c> whose bytes MATCH the ROM, or <c>PATCHED_IFNOT</c> whose bytes MISMATCH
        ///   -&gt; <see cref="InstallStatusEnum.Installed"/> (mirrors the WF detail-string "PATCHED_IF" rule).</item>
        /// </list>
        /// If every marker is resolvable (or a faithful-grep not-matching) and none indicates installed -&gt;
        /// <see cref="InstallStatusEnum.NotInstalled"/>. A patch with NO install markers (an empty but
        /// non-null Param) is <see cref="InstallStatusEnum.NotInstalled"/> — inheriting WF's own blind spot
        /// (WF's <c>IsInstalled</c> is likewise false for a marker-less patch). A <c>null</c> Param (which
        /// cannot be inspected at all) is <see cref="InstallStatusEnum.Unknown"/> — uninspectable proves
        /// nothing, so the conservative answer is "possibly installed".
        /// </para>
        /// <para>
        /// <b>SOUNDNESS.</b> This is a deliberate OVER-approximation: it returns NotInstalled only when it
        /// can PROVE the patch absent — either by a resolvable byte mismatch, or by a BYTE-FAITHFUL
        /// <c>$GREP</c>/<c>$FGREP</c> NOT_FOUND (whose result is identical to WF's, so Core says NotInstalled
        /// only where WF does — a false-negative is IMPOSSIBLE). Any other doubt (an un-ported macro
        /// resolving unsafe) yields Unknown, so the safe-reject gate never says "safe" while the patch could
        /// be present. The only residual gap is the WF-inherited marker-less case, documented and identical
        /// to WF.
        /// </para>
        /// The ROM is passed EXPLICITLY (no CoreState.ROM read), honoring this file's header guarantee.
        /// </summary>
        public static InstallStatusEnum EaBinInstallStatus(ROM rom, PatchSt patch)
            => EaBinInstallStatusCore(rom, patch, File.Exists, File.ReadAllBytes);

        internal static InstallStatusEnum EaBinInstallStatusWithFileReadsForTest(ROM rom, PatchSt patch,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
            => EaBinInstallStatusCore(rom, patch, fileExists, readFile);

        static InstallStatusEnum EaBinInstallStatusCore(ROM rom, PatchSt patch,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            if (rom == null) throw new ArgumentNullException(nameof(rom));
            if (patch == null) throw new ArgumentNullException(nameof(patch));
            // A null Param means NO marker can be inspected. The contract is "NotInstalled only when
            // absence is PROVEN (every resolvable marker evaluated, none matched)". An uninspectable
            // patch proves nothing, so the only SOUND answer is Unknown — the safe-reject gate must not
            // treat it as safe (Copilot PR #1326 review). (LoadPatch never produces a null Param, so this
            // guards only a direct/synthetic caller.)
            if (patch.Param == null) return InstallStatusEnum.Unknown;

            bool sawUnknown = false;

            foreach (var pair in patch.Param)
            {
                string[] sp = pair.Key.Split(':');
                string key = sp[0];
                string addrstring = U.at(sp, 1);
                string value = pair.Value;

                // Only the two INSTALL markers count toward installed-status (WF IsInstalled looks for
                // "PATCHED_IF" in the detail string, which only PATCHED_IF / PATCHED_IFNOT produce).
                bool isPatchedIfNot;
                if (key == "PATCHED_IF") isPatchedIfNot = false;
                else if (key == "PATCHED_IFNOT") isPatchedIfNot = true;
                else continue;

                // SOUNDNESS — detect UNFAITHFULLY-resolvable signatures BEFORE trusting the resolved
                // address. Some macro forms convertBinAddressString cannot resolve faithfully AND cannot
                // reject cleanly (it would return a WRONG-but-safe offset, NOT NOT_FOUND): reading bytes at
                // that garbage offset could spuriously report Installed or NotInstalled — neither sound. So
                // any marker whose addrstring is such a form ($XGREP / $FREEAREA) is Unknown OUTRIGHT,
                // regardless of what the resolver returns.
                //   $FGREP <file> (file-inclusion GREP) is NO LONGER in that set as of s2pf-18 (#1261):
                //   convertBinAddressString now reads the referenced file's bytes VERBATIM as the search
                //   pattern (MakeFGrepData — a faithful port of WF MakeGrepData(value, basedir)). Together
                //   with inline $GREP it is the FAITHFUL GREP family — byte-identical to WF's resolution —
                //   so its NOT_FOUND is handled below (not-matching, mirroring WF), NOT Unknown-outright.
                //   This is what makes the gate USEFUL on vanilla FE8U: all 159 of FE8U's blocking EA/BIN
                //   markers are absent-signature $GREP/$FGREP rows (125 $FGREP + 34 inline $GREP) that now
                //   resolve to NotInstalled instead of forcing Unknown.
                if (!IsFaithfullyResolvableInstallAddress(addrstring))
                {
                    sawUnknown = true;
                    continue;
                }

                string basedir = Path.GetDirectoryName(patch.PatchFileName) ?? "";
                uint address = convertBinAddressString(rom, addrstring, basedir, fileExists, readFile);
                if (!U.isSafetyOffset(address, rom))
                {
                    // The resolved address is NOT_FOUND / unsafe. What that MEANS depends on the marker form:
                    //
                    //   GREP / FGREP family (faithfully ported — MakeGrepData / MakeFGrepData + U.Grep are
                    //   VERBATIM ports of WF): a NOT_FOUND here means the post-install byte SIGNATURE is
                    //   ABSENT from the ROM. WF's CheckIF treats exactly this case as NOT-MATCHING: a
                    //   PATCHED_IF/PATCHED_IFNOT whose address is unsafe is in the `isnot` branch and hits
                    //   `continue` (PatchForm.cs:4416-4433), contributing nothing — so WF's IsInstalled is
                    //   FALSE for a patch whose only markers are absent-signature GREPs. Because Core's grep
                    //   result is BYTE-IDENTICAL to WF's for this family, Core can faithfully treat it as
                    //   not-matching too (skip WITHOUT setting sawUnknown). This is the s2pf-18 narrowing
                    //   (#1261) that makes the gate USEFUL on vanilla FE8U: all 159 of FE8U's blocking EA/BIN
                    //   markers are absent-signature $GREP/$FGREP rows -> now NotInstalled, matching WF.
                    //   FALSE-NEGATIVE IMPOSSIBLE: Core NOT_FOUND <=> WF NOT_FOUND for this family, so Core
                    //   can only say NotInstalled exactly where WF does.
                    //
                    //   Any OTHER form (an unsupported macro, $XGREP/$FREEAREA already filtered above, or a
                    //   $0x/$P32 pointer into freespace) is NOT byte-faithfully resolved by this Core build,
                    //   so a NOT_FOUND there does NOT prove the signature absent -> stay Unknown.
                    if (IsFaithfulGrepFamilyInstallAddress(addrstring))
                    {
                        continue; // faithful absent-signature GREP/FGREP -> not-matching (mirrors WF)
                    }
                    sawUnknown = true;
                    continue;
                }

                string[] args = value.Split(' ');
                if (args.Length <= 1)
                {
                    // A malformed marker (no comparison window) cannot be evaluated -> Unknown.
                    sawUnknown = true;
                    continue;
                }

                bool readOk = true;
                uint[] data = new uint[args.Length];
                for (int i = 0; i < args.Length; i++)
                {
                    uint a = address + (uint)i;
                    if (!U.isSafetyOffset(a, rom)) { readOk = false; break; }
                    data[i] = rom.u8(a);
                }
                if (!readOk)
                {
                    sawUnknown = true;
                    continue;
                }

                bool notFound = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (data[i] != U.atoi0x(args[i]))
                    {
                        notFound = true;
                        break;
                    }
                }

                // WF detail-string "PATCHED_IF" => installed:
                //   PATCHED_IF    installed when bytes MATCH    (notFound == false)
                //   PATCHED_IFNOT installed when bytes MISMATCH (notFound == true)
                bool markerSaysInstalled = isPatchedIfNot ? notFound : !notFound;
                if (markerSaysInstalled)
                {
                    return InstallStatusEnum.Installed;
                }
            }

            return sawUnknown ? InstallStatusEnum.Unknown : InstallStatusEnum.NotInstalled;
        }

        /// <summary>
        /// Can <paramref name="addrstring"/> (an install-marker address) be resolved FAITHFULLY by this
        /// Core build's <see cref="convertBinAddressString"/>? Returns <c>false</c> ONLY for the macro forms
        /// the resolver does NOT port faithfully AND cannot reject cleanly (it would return a WRONG-but-safe
        /// offset instead of NOT_FOUND), so the caller must treat them as Unknown OUTRIGHT:
        /// <list type="bullet">
        ///   <item><c>$XGREP...</c> — masked/wildcard GREP, not ported (the install-path
        ///   <see cref="convertBinAddressString"/> returns NOT_FOUND for it; listed here belt-and-suspenders
        ///   so it is Unknown even if a future resolver change made it return a non-NOT_FOUND offset).</item>
        ///   <item><c>$FREEAREA</c> — the install path returns 0 (an unsafe offset); not a real byte marker.</item>
        /// </list>
        /// <para>
        /// <c>$FGREP &lt;file&gt;</c> (file-inclusion GREP) is FAITHFULLY resolvable as of s2pf-18 (#1261):
        /// <see cref="MakeFGrepData"/> reads the basedir-relative file's bytes VERBATIM as the search pattern
        /// (a port of WF <c>MakeGrepData(value, basedir)</c>). It resolves to a real ROM offset, or — on a
        /// missing/unreadable/empty file — to the empty pattern, which <see cref="U.Grep"/> turns into
        /// NOT_FOUND. Neither path is a wrong-but-safe offset, so <c>$FGREP</c> is NO LONGER Unknown-outright;
        /// its NOT_FOUND is then handled by the caller as a byte-faithful GREP/FGREP NOT_FOUND — treated as
        /// NOT-MATCHING (-&gt; NotInstalled when nothing else matches), via
        /// <see cref="IsFaithfulGrepFamilyInstallAddress"/>, NOT Unknown. This is what makes the gate USEFUL
        /// on vanilla FE8U. (Soundness preserved: Core's grep is byte-identical to WF's, so a false-NEGATIVE
        /// is impossible.)
        /// </para>
        /// Every other form (plain numeric, <c>$0x</c> pointer, inline <c>$GREP</c>/<c>$GREP_ENABLE_POINTER</c>,
        /// <c>$P32</c>) either resolves faithfully or maps to NOT_FOUND. A NOT_FOUND from the byte-faithful
        /// <c>$GREP</c> family is not-matching (see <see cref="IsFaithfulGrepFamilyInstallAddress"/>); a
        /// NOT_FOUND from a non-faithful pointer/macro form stays Unknown via the safety-offset check. This
        /// is deliberately conservative — when in doubt, Unknown.
        /// </summary>
        static bool IsFaithfullyResolvableInstallAddress(string addrstring)
        {
            if (string.IsNullOrEmpty(addrstring)) return false; // empty/null -> resolver NOT_FOUND anyway
            if (addrstring[0] != '$') return true;              // plain numeric address — always faithful

            string value = addrstring.Substring(1);
            if (value.Length == 0) return false;

            // $XGREP — masked GREP, not ported. $FREEAREA — write-time allocator (install path -> 0).
            // $FGREP is NOT here: MakeFGrepData now reads the file VERBATIM, so it resolves faithfully or
            // cleanly maps a missing file to NOT_FOUND (the caller's safety check -> Unknown). s2pf-18 #1261.
            if (value.StartsWith("XGREP", StringComparison.Ordinal)) return false;
            if (value.StartsWith("FREEAREA", StringComparison.Ordinal)) return false;

            return true;
        }

        /// <summary>
        /// Is <paramref name="addrstring"/> a <c>$GREP</c> or <c>$FGREP</c> install-marker address — the
        /// byte-signature GREP family this Core build resolves BYTE-IDENTICALLY to WinForms
        /// (<see cref="MakeGrepData"/> / <see cref="MakeFGrepData"/> + <see cref="U.Grep"/>/<see cref="U.GrepEnd"/>
        /// are all verbatim ports)? For this family ONLY, a NOT_FOUND grep result faithfully means "the
        /// post-install signature is ABSENT" — exactly the case WF's <c>CheckIF</c> treats as NOT-MATCHING
        /// (the unsafe-address <c>continue</c> at PatchForm.cs:4416-4433) — so the caller may classify it as
        /// not-installed for that marker WITHOUT going Unknown. (s2pf-18 #1261, the gate-usefulness narrowing.)
        /// <para>
        /// EXCLUDES <c>$XGREP</c> (masked GREP — not ported; its NOT_FOUND is NOT byte-faithful, so a NOT_FOUND
        /// there must stay Unknown to keep the false-negative impossible). Also excludes every non-GREP form;
        /// a NOT_FOUND from a pointer/macro form does NOT prove a signature absent.
        /// </para>
        /// </summary>
        static bool IsFaithfulGrepFamilyInstallAddress(string addrstring)
        {
            if (string.IsNullOrEmpty(addrstring) || addrstring[0] != '$') return false;
            string value = addrstring.Substring(1);
            // $GREP... or $FGREP... (NOT $XGREP — masked GREP is not byte-faithfully ported).
            if (value.StartsWith("GREP", StringComparison.Ordinal)) return true;   // inline hex GREP
            if (value.StartsWith("FGREP", StringComparison.Ordinal)) return true;  // file-inclusion GREP
            return false;
        }

        // ---- convertBinAddressString (~:3000) ----------------------------------
        // Ports the address-resolution forms used by CheckIF (appnedSize == 0,
        // start_offset == 0x100). Plain addresses + $pointer + the GREP family +
        // P32. Macro forms that need WinForms-only helpers ($FREEAREA, TEXTID,
        // EndWeaponDebuffTable*, XGREP) resolve to NOT_FOUND, which the safety
        // check converts to the "E" skip for a positive condition — matching the
        // WinForms outcome that those patches are not applicable.
        static uint convertBinAddressString(ROM rom, string addrstring, string basedir,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            const uint start_offset = 0x100;

            if (string.IsNullOrEmpty(addrstring))
            {
                return U.NOT_FOUND;
            }

            if (addrstring[0] != '$')
            {
                // Plain numeric address.
                return U.toOffset(U.atoi0x(addrstring));
            }

            string value = addrstring.Substring(1);
            if (value.Length == 0) return U.NOT_FOUND;

            if (U.isnum(value[0]))
            {
                // $0x123 -> pointer dereference.
                uint addr = U.toOffset(U.atoi0x(value));
                if (!U.isSafetyOffset(addr, rom))
                {
                    return U.NOT_FOUND;
                }
                return rom.p32(addr);
            }

            if (value == "FREEAREA")
            {
                // CheckIF passes appnedSize == 0 -> WF returns 0 (unsafe offset).
                return 0;
            }

            // GREP family. $GREP = inline hex pattern; $FGREP = file-inclusion (the
            // pattern bytes come from the basedir-relative file — MakeFGrepData, ported
            // VERBATIM from WF in s2pf-18 #1261). $XGREP (masked) still needs a mask
            // builder not ported here -> NOT_FOUND.
            var m = RegexCache.Match(value, @"^(F|X)?GREP([0-9]+)(ENDA|END)?\+?([0-9]+)? ");
            if (m.Groups.Count >= 5 && m.Success)
            {
                if (m.Groups[1].Value == "X")
                {
                    // XGREP unsupported here -> NOT_FOUND.
                    return U.NOT_FOUND;
                }

                uint align = U.atoi(m.Groups[2].Value);
                uint skip = U.atoi(m.Groups[4].Value);
                byte[] need = MakeGrepData(value, basedir, m.Groups[1].Value == "F", fileExists, readFile);
                if (need == null || need.Length == 0) return U.NOT_FOUND;

                if (m.Groups[3].Value == "ENDA")
                {
                    return U.GrepEnd(rom.Data, need, start_offset, 0, align, skip, false);
                }
                else if (m.Groups[3].Value == "END")
                {
                    return U.GrepEnd(rom.Data, need, start_offset, 0, align, skip, true);
                }
                else
                {
                    return U.Grep(rom.Data, need, start_offset, 0, align);
                }
            }

            if (value.IndexOf("GREP_ENABLE_POINTER ") == 0)
            {
                return U.GrepEnablePointer(rom.Data, start_offset, 0);
            }

            if (value.IndexOf("P32 ") == 0)
            {
                return ReadPointer(rom, value, 0);
            }
            if (value.IndexOf("P32+4 ") == 0)
            {
                return ReadPointer(rom, value, 4);
            }

            // TEXTID / EndWeaponDebuffTable* depend on WinForms-only helpers.
            return U.NOT_FOUND;
        }

        static byte[] MakeGrepData(string value, string basedir, bool isFile,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            // $FGREP <file> — file-inclusion GREP. VERBATIM port of the WinForms
            // MakeGrepData(value, basedir) overload (FEBuilderGBA/PatchForm.cs:3222):
            // everything AFTER the first space is the basedir-relative filename, and
            // the file's raw bytes ARE the search pattern. A missing/unreadable file
            // yields an empty pattern (WF returns new byte[0]) -> U.Grep returns
            // NOT_FOUND. For the byte-faithful GREP/FGREP family, EaBinInstallStatus
            // treats that NOT_FOUND as NOT-MATCHING (-> NotInstalled when nothing else
            // matches), exactly as WF's CheckIF does — NOT Unknown (s2pf-18 #1261).
            // Never a wrong-but-safe offset. Headless-safe: a bad path/IO error degrades
            // to the empty pattern, never throws/ShowError (the inline-safe-parse
            // discipline this file guarantees).
            if (isFile)
            {
                return MakeFGrepData(value, basedir, fileExists, readFile);
            }

            // $GREP <bytes...> — inline hex byte pattern (unchanged).
            string[] sp = value.Split(' ');
            var grepdata = new List<byte>();
            for (int i = 1; i < sp.Length; i++)
            {
                if (sp[i].Length == 0) continue;
                if (sp[i][0] == '$')
                {
                    // Macro tokens inside an INLINE GREP (not the $FGREP file form,
                    // which never reaches here) are not ported; treat as not-found so
                    // the gate excludes safely.
                    return null;
                }
                grepdata.Add((byte)U.atoi0x(sp[i]));
            }
            return grepdata.ToArray();
        }

        // VERBATIM port of WinForms PatchForm.MakeGrepData(value, basedir)
        // (FEBuilderGBA/PatchForm.cs:3222) — the $FGREP file-inclusion pattern source.
        // WF: firstSpace<0 -> new byte[0]; filename = everything after the first space;
        // fullpath = Path.Combine(basedir, filename); !File.Exists -> new byte[0];
        // else File.ReadAllBytes(fullpath).
        //
        // FAITHFULNESS (Copilot PR #1336 review): an EMPTY basedir is NOT short-circuited
        // here — WF still does Path.Combine("", filename) == filename and would read the
        // file from the CURRENT directory. We mirror that exactly (the install-detection
        // caller always passes the patch dir, so empty basedir only arises for a synthetic
        // PatchFileName with no directory). Only `null` is normalized to "" so Path.Combine
        // cannot throw. Headless-safe: any IO failure / bad path degrades to the empty
        // pattern (try/catch), never throws — preserving WF's missing-file outcome (empty
        // pattern -> U.Grep NOT_FOUND).
        static byte[] MakeFGrepData(string value, string basedir,
            Func<string, bool> fileExists, Func<string, byte[]> readFile)
        {
            int firstSp = value.IndexOf(' ');
            if (firstSp < 0)
            {
                return new byte[0];
            }
            string filename = value.Substring(firstSp + 1);
            // WF passes `filename` straight to Path.Combine — even "" (Path.Combine(dir, "")
            // returns dir, which File.Exists rejects) — so do NOT special-case an empty
            // filename; only the null guard keeps Path.Combine from throwing.
            try
            {
                string fullpath = Path.Combine(basedir ?? "", filename ?? "");
                if (!fileExists(fullpath))
                {
                    return new byte[0];
                }
                return readFile(fullpath);
            }
            catch
            {
                return new byte[0];
            }
        }

        static uint ReadPointer(ROM rom, string value, uint plus)
        {
            string[] sp = value.Split(' ');
            if (sp.Length < 2) return U.NOT_FOUND;
            uint p = U.atoi0x(sp[1]);
            if (!U.isSafetyOffset(p, rom)) return U.NOT_FOUND;

            uint a = p + plus;
            if (!U.isSafetyOffset(a + 3, rom)) return U.NOT_FOUND;
            uint pp = rom.u32(a);
            if (!U.isSafetyPointer(pp, rom)) return U.NOT_FOUND;
            if (U.IsValueOdd(pp)) pp--;
            return U.toOffset(pp);
        }

        /// <summary>
        /// Mirror of WinForms <c>PatchForm.isFilterHardCoding</c> (FEBuilderGBA/PatchForm.cs:9569)
        /// — the per-patch predicate behind the Patch Manager <c>hardcoding_{type}=NN</c> filter
        /// token. Returns true iff this patch hard-codes the given 8-bit <paramref name="value"/>
        /// for the given ADDRESS_TYPE <paramref name="typeNameUpper"/> (one of <c>UNIT</c>/<c>CLASS</c>/
        /// <c>ITEM</c>, already upper-cased). Gated EXACTLY as WinForms:
        /// <list type="bullet">
        ///   <item><paramref name="value"/> must be &gt; 0 (WF: <c>value &lt;= 0 -&gt; false</c>);</item>
        ///   <item>not <see cref="isCanonicalSkip"/>;</item>
        ///   <item><see cref="CheckIF"/> must not return <c>"E"</c>;</item>
        ///   <item><c>ADDRESS_TYPE</c> must equal <paramref name="typeNameUpper"/>;</item>
        ///   <item>the resolved 8-bit id (<see cref="GetAddrPatchAddressToValue8Fast"/>) must equal
        ///   <paramref name="value"/>.</item>
        /// </list>
        /// The ROM is passed EXPLICITLY (no CoreState.ROM read), honoring this file's header guarantee.
        /// Read-only; never throws (a null patch / null Param yields false).
        /// </summary>
        public static bool IsHardCodingPatch(ROM rom, PatchSt patch, uint value, string typeNameUpper)
        {
            if (rom == null || patch == null || patch.Param == null) return false;
            if (value <= 0) return false;
            if (isCanonicalSkip(patch)) return false;
            if (CheckIF(rom, patch) == "E") return false;

            string addressType = U.at(patch.Param, "ADDRESS_TYPE");
            if (addressType != typeNameUpper) return false;

            uint id = GetAddrPatchAddressToValue8Fast(rom, patch);
            return id == value;
        }

        // ---- GetAddrPatchAddressToValue8Fast (~:9560) --------------------------
        // WinForms GetAddrPatchAddressFast reads the ADDRESS param with U.atoi0x,
        // which parses the FIRST token of a possibly multi-token ADDRESS (e.g.
        // "0x037B86 0x33270" -> 0x037B86). The real UNIT/CLASS/ITEM patches use
        // plain ROM offsets, where toOffset is a no-op. Per the #1035 brief we
        // additionally toOffset() the result so a pointer-style 0x08...... ADDRESS
        // resolves to its ROM offset (WinForms would reject it as out of range
        // and yield id 0); for every real patch the behavior is identical.
        static uint GetAddrPatchAddressToValue8Fast(ROM rom, PatchSt patch)
        {
            string address_string = U.at(patch.Param, "ADDRESS");
            uint addr = U.toOffset(U.atoi0x(address_string));
            if (!U.isSafetyOffset(addr, rom))
            {
                return 0;
            }
            return rom.u8(addr);
        }
    }
}
