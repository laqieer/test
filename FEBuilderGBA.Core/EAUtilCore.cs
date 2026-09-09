using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FEBuilderGBA
{
    /// <summary>
    /// GUI-free port of the Event Assembler simple parser
    /// (WinForms <c>EAUtil</c>, FEBuilderGBA/EAUtil.cs), scoped to the data kinds the
    /// in-place <b>Uninstall</b> trace consumes (#1242, follow-up to #1170). Shared by
    /// <see cref="EventAssemblerUninstallCore"/> and any future headless EA tracer.
    ///
    /// Faithfully reproduces the WinForms <c>Parse()</c> ordering and the
    /// <c>ORG / #incbin (ASM/BIN/MIX) / #inctevent|#inctext lyn / LYN_HOOK /
    /// #include lyn.event / String(...) / HINT=POINTER_ARRAY / HINT=PROCS</c> data
    /// emission, producing the same <see cref="Data"/>/<see cref="DataEnum"/> list the
    /// WinForms tracer walks. Uses only Core-resident types (<see cref="U"/>,
    /// <see cref="Elf"/>, <see cref="DisassemblerTrumb"/>, <see cref="EAUtilLynDumpMode"/>,
    /// <see cref="CoreState.ROM"/>, <see cref="CoreState.SystemTextEncoder"/>).
    ///
    /// Scope boundary (intentional, documented — mirrors #1170's auto-def boundary,
    /// NOT a silent truncation): the WinForms parser's <c>#incext Png2Dmp</c> path
    /// rasterizes a PNG inline via <c>ImageUtil.OpenBitmap</c> (System.Drawing,
    /// WinForms-only). This Core parser keeps only the <c>.png.dmp</c> HINT-file
    /// fallback (the same byte source the WinForms path uses when a sibling
    /// <c>*.png.dmp</c> exists, EAUtil.cs <c>Png2DmpLZ77</c>) and otherwise skips an
    /// inline PNG-raster block. The uninstall trace GREP-matches the produced BIN in
    /// ROM either way, so a hinted PNG is traced; an un-hinted inline raster is the one
    /// EA data kind this Core parser does not reconstruct. Such scripts must run through
    /// the WinForms patch uninstall.
    /// </summary>
    public class EAUtilCore
    {
        public enum DataEnum
        {
            ORG
            , MIX //incbinされたデータ 判別不能
            , ASM //incbinされたデータ ASM
            , BIN //incbinされたデータ BIN
            , LYN //lynによってインポートされるelfファイル
            // lyn hook. The hook STUB that lyn writes at the hooked address is 16 bytes
            // (the WF source's "16バイト" refers to that stub). The UNINSTALL TRACER
            // reverts a 20-byte range at the hook address, matching WF
            // PatchForm.TraceEAPatchedMapping (length = 20): it covers the 16-byte stub
            // plus the 4-byte aligned slot lyn may also overwrite, so the revert
            // restores the whole patched region rather than only the stub.
            , LYNHOOK
            , POINTER_ARRAY
            , PROCS
        }

        public class Data
        {
            public string Name { get; private set; }
            public string Dir { get; private set; }
            public uint ORGAddr { get; private set; }
            public byte[] BINData { get; private set; }
            public DataEnum DataType { get; private set; }
            public uint Append { get; private set; }

            public Data(uint orgaddr, DataEnum dataType, uint append = 0)
            {
                this.ORGAddr = orgaddr;
                this.DataType = dataType;
                this.Append = append;
                this.Name = "";
                this.Dir = "";
            }
            public Data(string name, byte[] data, DataEnum dataType, uint append = 0)
            {
                this.Name = name;
                this.Dir = "";
                this.BINData = data;
                this.DataType = dataType;
                this.Append = append;
            }
            public Data(string filename, string dir, byte[] data, DataEnum dataType, uint append = 0)
            {
                this.Name = filename;
                this.Dir = dir;
                this.BINData = data;
                this.DataType = dataType;
                this.Append = append;
            }
        }

        public List<Data> DataList { get; private set; }
        public List<string> IfNDefList { get; private set; }
        /// <summary>
        /// Human-readable notes about EA blocks this Core parser could NOT reconstruct
        /// (currently: un-hinted inline <c>#incext Png2Dmp</c> rasters — see the class
        /// doc scope boundary). Callers (e.g. the uninstall tracer) surface these so a
        /// revert is never SILENTLY incomplete. Empty == the parser reconstructed every
        /// emitting block it saw.
        /// </summary>
        public List<string> UntraceableNotes { get; private set; }
        public string Filename { get; private set; }
        public string Dir { get; private set; }
        EAUtilLynDumpMode LynDump;
        readonly Func<string, bool> fileExists;
        readonly Func<string, byte[]> readBytes;
        readonly Func<string, string[]> readLines;
        readonly Func<string, bool, Elf> readElf;

        public EAUtilCore(string filename)
            : this(filename, File.Exists, File.ReadAllBytes, File.ReadAllLines,
                (path, hook) => new Elf(path, useHookMode: hook))
        {
        }

        internal EAUtilCore(string filename, Func<string, bool> fileExists, Func<string, byte[]> readBytes,
            Func<string, string[]> readLines, Func<string, bool, Elf> readElf)
        {
            this.fileExists = fileExists;
            this.readBytes = readBytes;
            this.readLines = readLines;
            this.readElf = readElf;
            Parse(filename);
        }

        void Parse(string filename)
        {
            this.DataList = new List<Data>();
            this.Filename = filename;
            this.Dir = Path.GetDirectoryName(filename);
            this.IfNDefList = new List<string>();
            this.UntraceableNotes = new List<string>();

            this.CurrentLabel = "";
            string[] lines = readLines(filename);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = U.ClipComment(lines[i]);
                if (line == "")
                {
                    continue;
                }

                ParseJumpToHack(line);
                ParseIfNDef(line);
            }
            //本格的なパース.
            for (int i = 0; i < lines.Length; i++)
            {
                string line = U.ClipComment(lines[i]);
                if (ParseLynDump(line, lines[i]))
                {
                    continue;
                }
                if (line == "")
                {
                    continue;
                }

                ParseORG(line);
                ParseIncBIN(line, lines[i]);
                ParseLynELF(line, lines[i]);
                ParseLynHook(line, lines[i]);
                ParseLynInclude(line, lines[i]);
                ParsePng2Dmp(line, lines[i]);
                ParseString(line, lines[i]);
                ParseLabel(line, lines[i]);
            }
            ParseLynDump("", "");
        }

        bool ParseLynDump(string line, string orignalIine)
        {
            if (this.LynDump == null)
            {
                if (line == "")
                {
                    return false;
                }
                if (orignalIine.IndexOf("// lyn output of ", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    this.LynDump = new EAUtilLynDumpMode();
                    return true;
                }
                else
                {
                    return false;
                }
            }

            if (this.LynDump == null)
            {
                return false;
            }

            bool r = this.LynDump.ParseLine(line);
            if (r)
            {//LYNDUMPの処理を行うので、続きのパースは不要.
                return true;
            }

            //LYNDUMPの終了
            AddLynDump(this.LynDump, "");

            this.LynDump = null;
            return false;
        }

        void AddLynDump(EAUtilLynDumpMode lyn, string filename)
        {
            if (lyn.GetCount() == 0)
            {//ORG指定がない場合
                if (filename == "")
                {
                    filename = "LYNDUMP";
                }
                Data data = new Data(filename, this.Dir, lyn.GetDataAll(), DataEnum.LYN, 0);
                this.DataList.Add(data);
                return;
            }

            int count = lyn.GetCount();
            for (int i = 0; i < count; i++)
            {
                Data data = new Data("LYNDUMP_" + lyn.GetName(i), lyn.GetData(i), DataEnum.LYN, 0);
                this.DataList.Add(data);
            }
        }

        void ParseLabel(string line, string orignalIine)
        {
            string a = line.Trim();
            int pos = a.IndexOf(':');
            if (pos < 0)
            {
                return;
            }
            this.CurrentLabel = a.Substring(0, pos);


            if (orignalIine.IndexOf("HINT=POINTER_ARRAY", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                uint append = ParseAdd(orignalIine);
                Data data = new Data(this.CurrentLabel, new byte[] { }, DataEnum.POINTER_ARRAY, append);
                this.DataList.Add(data);
            }
            else if (orignalIine.IndexOf("HINT=PROCS", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                uint append = ParseAdd(orignalIine);
                byte[] procsTopBIN = ParseProcs(orignalIine);
                Data data = new Data(this.CurrentLabel, procsTopBIN, DataEnum.PROCS, append);
                this.DataList.Add(data);
            }
        }

        byte[] ParseProcs(string orignalIine)
        {
            int pos = orignalIine.IndexOf("HINT=PROCS");
            if (pos < 0)
            {
                return new byte[] { };
            }
            string str = orignalIine.Substring(pos);
            str = U.cut(str, "\"", "\"");
            if (str == "")
            {
                return new byte[] { };
            }
            // CoreState.SystemTextEncoder / CoreState.ROM mirror the WinForms
            // Program.SystemTextEncoder / Program.ROM used by the original.
            if (CoreState.SystemTextEncoder == null || CoreState.ROM == null)
            {
                return new byte[] { };
            }
            byte[] needString = CoreState.SystemTextEncoder.Encode(str);
            uint hintStringAddr = U.Grep(CoreState.ROM.Data, needString, CoreState.ROM.RomInfo.compress_image_borderline_address, 0, 4);
            if (hintStringAddr == U.NOT_FOUND)
            {
                return new byte[] { };
            }
            byte[] needProcString = new byte[8];
            U.write_u8(needProcString, 0, 0x01);
            U.write_p32(needProcString, 4, hintStringAddr);

            return needProcString;
        }

        int FindJumpToHack(string line)
        {
            int pos = line.IndexOf("jumpToHack", StringComparison.OrdinalIgnoreCase);
            if (pos >= 0)
            {
                return pos;
            }
            pos = line.IndexOf("callHack", StringComparison.OrdinalIgnoreCase);
            if (pos >= 0)
            {
                return pos;
            }
            pos = line.IndexOf("replaceWithHack", StringComparison.OrdinalIgnoreCase);
            if (pos >= 0)
            {
                return pos;
            }

            return -1;
        }

        void ParseIfNDef(string line)
        {
            int pos = line.IndexOf("#ifndef", StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                return;
            }

            string ifdef_keyword = line.Substring(pos + 7 + 1).Trim();
            if (ifdef_keyword == "")
            {
                return;
            }
            IfNDefList.Add(ifdef_keyword);
        }

        void ParseJumpToHack(string line)
        {
            int pos = FindJumpToHack(line);
            if (pos < 0)
            {
                return;
            }
            string label = U.cut(line, "(", ")");
            JumpToHackLabeles.Add(label);
        }

        bool isCode(string fullPath)
        {
            //ソースコードがあればASMだろう.
            string srcFilename = U.ChangeExtFilename(fullPath, ".s");
            if (fileExists(srcFilename))
            {//ソースコードがあったのでASMです
                return true;
            }
            srcFilename = U.ChangeExtFilename(fullPath, ".asm");
            if (fileExists(srcFilename))
            {//ソースコードがあったのでASMです
                return true;
            }

            if (this.JumpToHackLabeles.IndexOf(this.CurrentLabel) >= 0)
            {//JumpToHackで呼び出されているのでASMです
                return true;
            }
            return false;
        }

        string CurrentLabel;
        List<string> JumpToHackLabeles = new List<string>();

        bool ParseIncBIN(string line, string orignalIine)
        {
            string a = Keyword(line, "#incbin");
            if (a == "")
            {
                return false;
            }
            string filename = U.cut(a, "\"", "\"");
            string fullbinname = Path.Combine(this.Dir, filename);

            DataEnum dataType;
            if (orignalIine.IndexOf("HINT=BIN", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dataType = DataEnum.BIN;
            }
            else if (orignalIine.IndexOf("HINT=ASM", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dataType = DataEnum.ASM;
            }
            else if (filename.IndexOf(".png.") >= 0)
            {//png
                dataType = DataEnum.BIN;
            }
            else
            {//ヒントがない場合は、 JumpToHackの有無で判断します.
                if (isCode(fullbinname))
                {
                    dataType = DataEnum.ASM;
                }
                else
                {
                    dataType = DataEnum.MIX;
                }
            }
            if (!fileExists(fullbinname))
            {
                Data emptydata = new Data(filename, this.Dir, new byte[0], dataType);
                this.DataList.Add(emptydata);
                return false;
            }

            Data data = new Data(filename, this.Dir, readBytes(fullbinname), dataType);
            this.DataList.Add(data);
            return true;
        }

        bool ParseLynHook(string line, string orignalIine)
        {
            string keyword = "HINT=LYN_HOOK=";
            int pos = orignalIine.IndexOf(keyword);
            if (pos < 0)
            {
                return false;
            }
            uint orgaddr = U.atoi0x(orignalIine.Substring(pos + keyword.Length));

            Data data = new Data(orgaddr, DataEnum.ORG);
            this.DataList.Add(data);
            return true;
        }

        bool ParseLynELF(string line, string orignalIine)
        {
            bool inctevent_lyn = false;
            string a = Keyword(line, "#inctevent lyn");
            if (a == "")
            {
                a = Keyword(line, "#inctext lyn");
                if (a == "")
                {
                    return false;
                }
            }
            else
            {
                inctevent_lyn = true;
            }
            string filename = U.cut(a, "\"", "\"");
            string fullbinname = Path.Combine(this.Dir, filename);

            DataEnum dataType = DataEnum.LYN;
            if (!fileExists(fullbinname))
            {
                Data emptydata = new Data(filename, this.Dir, new byte[0], dataType);
                this.DataList.Add(emptydata);
                return false;
            }

            Elf elf = readElf(fullbinname, false);
            Data data = new Data(filename, this.Dir, elf.ProgramBIN, dataType);
            this.DataList.Add(data);

            if (inctevent_lyn == false)
            {
                ParseLynSecondArgs(a);
            }
            return true;
        }

        bool ParseLynSecondArgs(string a)
        {
            //次の引数へ
            a = U.skip(a, "\"");
            a = U.skip(a, "\"");
            string filename = U.cut(a, "\"", "\"");
            string fullbinname = Path.Combine(this.Dir, filename);

            if (!fileExists(fullbinname))
            {
                return false;
            }

            Elf elf = readElf(fullbinname, true);
            foreach (Elf.Sym sym in elf.SymList)
            {
                if (!U.isPointer(sym.addr))
                {
                    continue;
                }
                uint addr = U.toOffset(sym.addr);
                addr = DisassemblerTrumb.ProgramAddrToPlain(addr);
                Data data = new Data(addr, DataEnum.LYNHOOK);
                this.DataList.Add(data);
            }

            return true;
        }

        bool ParseLynInclude(string line, string orignalIine)
        {
            int start = line.IndexOf("#include");
            if (start < 0)
            {
                return false;
            }

            int lyn_event_pos = line.IndexOf("lyn.event", start);
            if (lyn_event_pos < 0)
            {
                return false;
            }
            string filename = U.cut(line, "\"", "\"");
            string fullfilename = Path.Combine(this.Dir, filename);

            if (!fileExists(fullfilename))
            {
                return false;
            }

            string[] lines = readLines(fullfilename);
            EAUtilLynDumpMode lyndmp = new EAUtilLynDumpMode();
            foreach (string l in lines)
            {
                bool r = lyndmp.ParseLine(l);
                if (!r)
                {
                    break;
                }
            }

            //LYNDUMPの終了
            AddLynDump(lyndmp, filename);
            return true;
        }

        bool ParseString(string line, string orignalIine)
        {
            int start = line.IndexOf("String(");
            if (start < 0)
            {
                return false;
            }

            start += 7;
            int term = line.IndexOf(')', start);
            if (term <= 0)
            {
                return false;
            }
            string str = line.Substring(start, term - start);
            str = str.Trim('"');
            byte[] lowbin = System.Text.Encoding.GetEncoding("Shift_JIS").GetBytes(str);

            uint size = (uint)lowbin.Length + 1;
            byte[] bin = new byte[size];
            Array.Copy(lowbin, bin, lowbin.Length);

            Data data = new Data("String(" + str + ")", bin, DataEnum.BIN);
            this.DataList.Add(data);

            return true;
        }

        // Scope boundary (see class doc): only the .png.dmp HINT-file fallback is
        // honoured here — the inline PNG rasterization (ImageUtil.OpenBitmap) is
        // WinForms-only and cannot move to Core. An un-hinted inline raster block is
        // skipped; a hinted one is emitted as BIN and traced like any other BIN.
        bool ParsePng2Dmp(string line, string orignalIine)
        {
            string a = Keyword(line, "#incext Png2Dmp");
            if (a == "")
            {
                return false;
            }
            string filename = U.cut(a, "\"", "\"");
            string fullbinname = Path.Combine(this.Dir, filename);

            if (!fileExists(fullbinname))
            {
                return false;
            }

            DataEnum dataType = DataEnum.BIN;

            // Both the --lz77 and plain inline-raster paths need ImageUtil.OpenBitmap
            // (WinForms-only) to rasterize; the sibling .png.dmp hint, when present,
            // gives the same bytes without it. If the hint is missing we CANNOT
            // reconstruct this block — record an untraceable note so the uninstall
            // tracer never silently omits the range (it stays as patch residue).
            byte[] hint = Png2DmpHint(fullbinname);
            if (hint.Length > 0)
            {
                Data data = new Data(filename, this.Dir, hint, dataType);
                this.DataList.Add(data);
                return true;
            }

            this.UntraceableNotes.Add(R._("Png2Dmp image (no .dmp hint): {0}", filename));
            return false;
        }

        byte[] Png2DmpHint(string filename)
        {
            string hint = filename + ".dmp";
            if (fileExists(hint))
            {
                return readBytes(hint);
            }
            return new byte[] { };
        }

        bool ParseORG(string line)
        {
            string a = Keyword(line, "ORG");
            if (a == "")
            {
                return false;
            }
            uint addr = U.atoi0x(a);
            if (addr <= 0)
            {
                return false;
            }
            addr = U.toOffset(addr);
            if (!U.isSafetyOffset(addr))
            {
                return false;
            }

            Data data = new Data(addr, DataEnum.ORG);
            this.DataList.Add(data);
            return true;
        }

        string Keyword(string line, string keyword)
        {
            int pos = line.IndexOf(keyword + " ", StringComparison.OrdinalIgnoreCase);
            if (pos < 0)
            {
                pos = line.IndexOf(keyword + "\t", StringComparison.OrdinalIgnoreCase);
                if (pos < 0)
                {
                    return "";
                }
            }
            string r = line.Substring(pos + keyword.Length + 1).Trim();
            return r;
        }

        public static bool IsFBGTemp(string filename)
        {
            string file = Path.GetFileName(filename);
            return (file.IndexOf("_FBG_Temp_") == 0);
        }

        uint ParseAdd(string orignalIine)
        {
            int hint_pos = orignalIine.IndexOf("HINT=");
            if (hint_pos < 0)
            {
                return 0;
            }
            int add_pos = orignalIine.IndexOf("ADD=", hint_pos + 5);
            if (add_pos < 0)
            {
                return 0;
            }
            return U.atoi(orignalIine.Substring(add_pos + 4));
        }
    }
}
