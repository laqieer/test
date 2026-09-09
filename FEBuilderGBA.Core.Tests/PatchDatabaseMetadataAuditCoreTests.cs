using System.Text;

namespace FEBuilderGBA.Core.Tests;

public class PatchDatabaseMetadataAuditCoreTests
{
    [Theory]
    [InlineData("PATCHED_IF:$FGREP4 {0}=01 02")]
    [InlineData("IF:$FGREP4 {0}=01 02")]
    [InlineData("IFNOT:$FGREP4END+1 {0}=01 02")]
    [InlineData("CONFLICT_IF:$FGREP4ENDA {0}=01 02")]
    [InlineData("ADDRESS=$FGREP4 {0}")]
    [InlineData("POINTER=$FGREP4 {0}")]
    [InlineData("DATACOUNT=$FGREP4 {0}")]
    [InlineData("WIDTH=$FGREP4 {0}")]
    [InlineData("SYMBOL={0}")]
    [InlineData("EDIT_PATCH={0}")]
    [InlineData("EA={0}")]
    [InlineData("BIN:0x100={0}")]
    public void EveryOperandPosition_RejectsNetworkWithoutExternalIo(string format)
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt", "NAME=Test\nTYPE=BIN\n" +
            string.Format(format, @"\\never-contact.invalid\share\pattern.bin"));
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
        Assert.All(fixture.Reads, name => Assert.True(fixture.Files.ContainsKey(name)));
        Assert.DoesNotContain(fixture.Reads, name => name.Contains("never-contact", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../../outside.bin")]
    [InlineData("C:\\outside.bin")]
    [InlineData("C:outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("https://never-contact.invalid/file")]
    [InlineData("pattern.bin:stream")]
    public void EscapingOrRootedOperands_Reject(string operand)
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt", "TYPE=BIN\nPATCHED_IF:$FGREP4 " + operand + "=01");
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
        Assert.Single(fixture.Reads);
    }

    [Fact]
    public void InternalSiblingReferences_ArePreserved()
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt", "NAME=Test\nTYPE=BIN\nPATCHED_IF:$FGREP4 ../shared/pattern.bin=01");
        fixture.PutBytes("shared/pattern.bin", new byte[] { 1, 2, 3 });
        var result = fixture.Audit();
        Assert.Equal(1, result.DescriptorCount);
        Assert.Contains(result.References, r => r.Target == "shared/pattern.bin");
    }

    [Fact]
    public void ShadowedAndOtherLanguageOperands_AreStillAudited()
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt",
            "TYPE=BIN\nADDRESS.ja=$FGREP4 ../../outside.bin\nADDRESS=0x100\nTYPE=STRUCT\n");
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
    }

    [Theory]
    [InlineData("../...en", "...en")]
    [InlineData("../shared/pattern.en", "shared/pattern.en")]
    public void WholeKeyLanguageSuffixCannotHideADifferentOperand(string operand, string manifestName)
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=BIN\nIF:$FGREP4 " + operand + "=0xAA 0xBB");
        fixture.PutBytes(manifestName, new byte[] { 0xAA, 0xBB });
        int consumers = 0, exists = 0, reads = 0;
        Assert.Throws<InvalidDataException>(() =>
        {
            fixture.Audit();
            consumers++;
            var rom = MakeRom();
            var patch = PatchHardCodeScanner.LoadPatch(rom, Path.Combine(fixture.Root, "nested", "PATCH_test.txt"), "en");
            _ = PatchHardCodeScanner.CheckIFWithFileReadsForTest(rom, patch,
                _ => { exists++; return false; }, _ => { reads++; return Array.Empty<byte>(); });
        });
        Assert.Equal(0, consumers);
        Assert.Equal(0, exists);
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData("ADDRESS")]
    [InlineData("POINTER")]
    [InlineData("DATACOUNT")]
    public void ValueOperandEqualsCharacterCannotTruncateTheAuditedFilename(string key)
    {
        using var fixture = new Fixture();
        const string operand = "bait=../../../outside.bin";
        fixture.Put("nested/PATCH_test.txt", "TYPE=STRUCT\n" + key + "=$FGREP4 " + operand);
        fixture.PutBytes("nested/bait", new byte[] { 0 });
        int consumers = 0, reads = 0;
        Assert.Throws<InvalidDataException>(() =>
        {
            fixture.Audit();
            consumers++;
            _ = PatchMacroAddressResolverCore.ResolveWithFileReadsForTest(MakeRom(), "$FGREP4 " + operand,
                Path.Combine(fixture.Root, "nested"), 0x100,
                _ => { reads++; return false; }, _ => { reads++; return Array.Empty<byte>(); });
        });
        Assert.Equal(0, consumers);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ValueOperandPreservesALegitimateInternalFilenameContainingEquals()
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=ADDR\nADDRESS=$FGREP4 pattern=data.bin");
        fixture.PutBytes("nested/pattern=data.bin", new byte[] { 0xAA, 0xBB });
        Assert.Equal("nested/pattern=data.bin", Assert.Single(fixture.Audit().References).Target);
        var rom = MakeRom();
        rom.Data[0x1000] = 0xAA;
        rom.Data[0x1001] = 0xBB;
        Assert.Equal(0x1000u, PatchMacroAddressResolverCore.Resolve(rom, "$FGREP4 pattern=data.bin",
            Path.Combine(fixture.Root, "nested")));
    }

    public static IEnumerable<object[]> TextEncodingCases()
    {
        foreach (string encoding in new[] { "utf8", "utf16", "utf16be", "utf32", "utf32be" })
            foreach (string newline in new[] { "\n", "\r", "\r\n" })
                yield return new object[] { encoding, newline };
    }

    [Theory]
    [MemberData(nameof(TextEncodingCases))]
    public void AuditAndActualConsumersAgreeAcrossBomAndNewlineDecoding(string kind, string newline)
    {
        using var fixture = new Fixture();
        Encoding encoding = EncodingFor(kind);
        string text = string.Join(newline, "TYPE=BIN", "NAME=Default", "NAME.en=English",
            "NAME.ja=Japanese", "PATCHED_IF:$FGREP4 pattern.bin=0xAA 0xBB");
        fixture.PutBytes("nested/PATCH_test.txt", encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        fixture.PutBytes("nested/pattern.bin", new byte[] { 0xAA, 0xBB });
        var audit = fixture.Audit();
        Assert.Equal("nested/pattern.bin", Assert.Single(audit.References).Target);
        var rom = MakeRom();
        rom.Data[0x1000] = 0xAA;
        rom.Data[0x1001] = 0xBB;
        string definition = Path.Combine(fixture.Root, "nested", "PATCH_test.txt");
        var metadata = PatchMetadataCore.ParsePatchFile(definition, "nested", rom, "en");
        var independent = PatchHardCodeScanner.LoadPatch(rom, definition, "en");
        Assert.Equal(PatchMetadataCore.PatchStatus.Installed, metadata.Status);
        Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.Installed,
            PatchHardCodeScanner.EaBinInstallStatus(rom, independent));
        Assert.Equal("English", independent.Param["NAME"]);
    }

    [Fact]
    public void InvalidUtf8ReplacementHasTheSameManifestTargetAsLegacyReaders()
    {
        using var fixture = new Fixture();
        byte[] prefix = Encoding.UTF8.GetBytes("TYPE=BIN\nPATCHED_IF:$FGREP4 ");
        byte[] suffix = Encoding.UTF8.GetBytes(".bin=0xAA 0xBB");
        fixture.PutBytes("nested/PATCH_test.txt", prefix.Concat(new byte[] { 0xFF }).Concat(suffix).ToArray());
        fixture.PutBytes("nested/\uFFFD.bin", new byte[] { 0xAA, 0xBB });
        var audit = fixture.Audit();
        Assert.Equal("nested/\uFFFD.bin", Assert.Single(audit.References).Target);
        var rom = MakeRom();
        rom.Data[0x1000] = 0xAA;
        rom.Data[0x1001] = 0xBB;
        string definition = Path.Combine(fixture.Root, "nested", "PATCH_test.txt");
        Assert.Equal(PatchMetadataCore.PatchStatus.Installed,
            PatchMetadataCore.ParsePatchFile(definition, "nested", rom, "en").Status);
        Assert.Equal("I", PatchHardCodeScanner.CheckIF(rom, PatchHardCodeScanner.LoadPatch(rom, definition, "en")));
    }

    [Fact]
    public void DuplicateDirectivesRemainAuditedWithoutChangingLastValueConsumerSemantics()
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=BIN\nPATCHED_IF:$FGREP4 pattern.bin=0xAA 0xBB\n" +
            "PATCHED_IF:$FGREP4 pattern.bin=0xCC 0xDD");
        fixture.PutBytes("nested/pattern.bin", new byte[] { 0xAA, 0xBB });
        Assert.Equal(2, fixture.Audit().References.Count);
        var rom = MakeRom();
        rom.Data[0x1000] = 0xAA;
        rom.Data[0x1001] = 0xBB;
        string definition = Path.Combine(fixture.Root, "nested", "PATCH_test.txt");
        Assert.Equal(PatchMetadataCore.PatchStatus.NotInstalled,
            PatchMetadataCore.ParsePatchFile(definition, "nested", rom, "en").Status);
        Assert.Equal(PatchHardCodeScanner.InstallStatusEnum.NotInstalled,
            PatchHardCodeScanner.EaBinInstallStatus(rom, PatchHardCodeScanner.LoadPatch(rom, definition, "en")));
    }

    [Theory]
    [InlineData("{J}EA={0}")]
    [InlineData("{U}EA={0}")]
    [InlineData("EA.fr={0}")]
    [InlineData("EDIT_PATCH.en={0}")]
    [InlineData("SYMBOL.ja={0}")]
    [InlineData("// IF:$FGREP4 {0}=0xAA 0xBB")]
    [InlineData("PATCHED_IF:$FGREP4 {0}=0xAA 0xBB // trailing comment")]
    public void InactiveLocaleRegionAndCommentFormsCannotHideAnExternalReference(string field)
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=BIN\n" +
            field.Replace("{0}", @"\\unused.invalid\share\file"));
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
        Assert.Equal(new[] { "nested/PATCH_test.txt" }, fixture.Reads);
    }

    [Theory]
    [InlineData("en", false, "English", "english.event")]
    [InlineData("ja", false, "Japanese", "japanese.event")]
    [InlineData("fr", false, "English", "english.event")]
    [InlineData("fr", true, "Default", "english.event")]
    public void AllLocaleFileValuesAreAuditedWhileActualFallbackRemainsUnchanged(
        string language, bool hasDefault, string expectedName, string expectedEvent)
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=EA\n" + (hasDefault ? "NAME=Default\n" : "") +
            "NAME.en=English\nNAME.ja=Japanese\nEA.en=english.event\nEA.ja=japanese.event");
        fixture.Put("nested/english.event", "");
        fixture.Put("nested/japanese.event", "");
        var audit = fixture.Audit();
        Assert.Equal(2, audit.References.Count);
        var patch = PatchHardCodeScanner.LoadPatch(MakeRom(),
            Path.Combine(fixture.Root, "nested", "PATCH_test.txt"), language);
        Assert.Equal(expectedName, patch.Param["NAME"]);
        Assert.Equal(expectedEvent, patch.Param["EA"]);
    }

    static ROM MakeRom()
    {
        var rom = new ROM();
        Assert.True(rom.LoadLow("synthetic.gba", new byte[0x1000000], "BE8E01"));
        return rom;
    }

    [Theory]
    [InlineData("#incbin \"{0}\"")]
    [InlineData("Label: #incbin \"{0}\"")]
    [InlineData("#inctext lyn \"safe.o\" \"{0}\"")]
    [InlineData("#inctevent lyn \"{0}\"")]
    [InlineData("#include \"{0}/lyn.event\"")]
    [InlineData("#incext Png2Dmp \"{0}\"")]
    public void NestedEventOperands_AreAuditedBeforeParserIo(string format)
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt", "TYPE=EA\nEA=main.event");
        fixture.Put("patch/main.event", string.Format(format, "../../outside"));
        fixture.PutBytes("patch/safe.o", new byte[] { 1 });
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
        Assert.All(fixture.Reads, name => Assert.True(fixture.Files.ContainsKey(name)));
    }

    [Fact]
    public void EditPatchWithoutPatchFilename_IsInTheClosure()
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt", "TYPE=BIN\nEDIT_PATCH=nested.data");
        fixture.Put("patch/nested.data", "TYPE=BIN\nSYMBOL=../../outside");
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
        Assert.Contains("patch/nested.data", fixture.Reads);
    }

    [Fact]
    public void RecursiveEditPatchCycle_Rejects()
    {
        using var fixture = new Fixture();
        fixture.Put("patch/PATCH_test.txt", "TYPE=BIN\nEDIT_PATCH=nested.txt");
        fixture.Put("patch/nested.txt", "TYPE=BIN\nEDIT_PATCH=PATCH_test.txt");
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16")]
    [InlineData("utf16be")]
    [InlineData("utf32")]
    [InlineData("utf32be")]
    public void BomDecoding_CannotHideAnOperand(string kind)
    {
        Encoding encoding = EncodingFor(kind);
        using var fixture = new Fixture();
        string text = "TYPE=BIN\r\nPATCHED_IF:$FGREP4 ../../outside=01\r";
        fixture.PutBytes("patch/PATCH_test.txt", encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray());
        Assert.Throws<InvalidDataException>(() => fixture.Audit());
    }

    static Encoding EncodingFor(string kind) => kind switch
        {
            "utf16" => Encoding.Unicode,
            "utf16be" => Encoding.BigEndianUnicode,
            "utf32" => Encoding.UTF32,
            "utf32be" => new UTF32Encoding(true, true),
            _ => new UTF8Encoding(true),
        };

    [Fact]
    public void ReadBudget_IsExact()
    {
        using var fixture = new Fixture();
        const string text = "TYPE=BIN";
        fixture.Put("PATCH_test.txt", text);
        Assert.Equal(1, fixture.Audit(new PatchDatabaseMetadataAuditCore.Limits { MaxTextBytes = text.Length }).DescriptorCount);
        Assert.Throws<InvalidDataException>(() =>
            fixture.Audit(new PatchDatabaseMetadataAuditCore.Limits { MaxTextBytes = text.Length - 1 }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void PreviouslyVisitedDescriptorsCannotHideTheActualReferenceDepth(int depthLimit)
    {
        using var fixture = new Fixture();
        for (int i = depthLimit; i >= 0; i--)
            fixture.Put("PATCH_" + i + ".txt", "TYPE=BIN" +
                (i == depthLimit ? "" : "\nEDIT_PATCH=PATCH_" + (i + 1) + ".txt"));
        var limits = new PatchDatabaseMetadataAuditCore.Limits { MaxDepth = depthLimit };
        Assert.Equal(depthLimit + 1, fixture.Audit(limits).DescriptorCount);
        fixture.Put("PATCH_parent.txt", "TYPE=BIN\nEDIT_PATCH=PATCH_0.txt");
        Assert.Throws<InvalidDataException>(() => fixture.Audit(limits));
    }

    [Fact]
    public void TextFileEdgeAndRepeatedReferenceByteBoundariesAreExact()
    {
        using var fixture = new Fixture();
        fixture.Put("PATCH_test.txt", "TYPE=BIN\nEDIT_PATCH=child.data\nBIN:0x100=pattern.bin\nBIN:0x200=pattern.bin");
        fixture.Put("child.data", "TYPE=BIN");
        fixture.PutBytes("pattern.bin", new byte[] { 1, 2, 3 });
        long bytes = fixture.Files["child.data"] + 6;
        var exact = new PatchDatabaseMetadataAuditCore.Limits { MaxTextFiles = 2, MaxEdges = 3, MaxReferencedBytes = bytes };
        Assert.Equal(3, fixture.Audit(exact).References.Count);
        Assert.Throws<InvalidDataException>(() => fixture.Audit(exact with { MaxTextFiles = 1 }));
        Assert.Throws<InvalidDataException>(() => fixture.Audit(exact with { MaxEdges = 2 }));
        Assert.Throws<InvalidDataException>(() => fixture.Audit(exact with { MaxReferencedBytes = bytes - 1 }));
    }

    [Fact]
    public void TwoLynReadOperands_CountTwice()
    {
        using var fixture = new Fixture();
        fixture.Put("PATCH_test.txt", "TYPE=EA\nEA=main.event");
        fixture.Put("main.event", "#inctext lyn \"shared.o\" \"shared.o\"");
        fixture.PutBytes("shared.o", new byte[] { 1, 2, 3 });
        var result = fixture.Audit();
        Assert.Equal(2, result.References.Count(r => r.Source == "main.event" && r.Target == "shared.o"));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("s")]
    [InlineData("asm")]
    public void AuditedEventClosureContainsEveryActualImplicitProbeAndRead(string hintKind)
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=EA\nEA=main.event");
        fixture.Put("nested/main.event",
            "#incbin \"../shared/payload.bin\"\n" +
            "#inctext lyn \"../shared/code.o\" \"../shared/hook.o\"\n" +
            "#inctevent lyn \"../shared/code.o\"\n" +
            "#include \"../shared/lyn.event\"\n" +
            "#incext Png2Dmp \"../shared/image.png\"");
        fixture.PutBytes("shared/payload.bin", new byte[] { 0x11, 0x22 });
        fixture.PutBytes("shared/code.o", new byte[] { 0 });
        fixture.PutBytes("shared/hook.o", new byte[] { 0 });
        fixture.Put("shared/lyn.event", "BYTE 0x33 0x44");
        fixture.PutBytes("shared/image.png", new byte[] { 0 });
        if (hintKind != "none")
        {
            fixture.Put("shared/payload." + hintKind, "");
            fixture.PutBytes("shared/image.png.dmp", new byte[] { 0x55, 0x66 });
        }
        var audit = fixture.Audit();
        var allowed = audit.References.Select(r => r.Target).Append("nested/main.event").ToHashSet(StringComparer.Ordinal);
        var calls = new List<(string Kind, string Name)>();
        void Check(string kind, string path)
        {
            string name = Path.GetRelativePath(fixture.Root, Path.GetFullPath(path)).Replace(Path.DirectorySeparatorChar, '/');
            Assert.Contains(name, allowed);
            calls.Add((kind, name));
        }
        string main = Path.Combine(fixture.Root, "nested", "main.event");
        var spied = new EAUtilCore(main,
            path => { Check("exists", path); return File.Exists(path); },
            path => { Check("bytes", path); return File.ReadAllBytes(path); },
            path => { Check("lines", path); return File.ReadAllLines(path); },
            (path, hook) => { Check("elf", path); return new Elf(path, hook); });
        var actual = new EAUtilCore(main);
        Assert.Equal(actual.DataList.Select(d => (d.Name, d.DataType)), spied.DataList.Select(d => (d.Name, d.DataType)));
        Assert.Equal(actual.UntraceableNotes, spied.UntraceableNotes);
        Assert.Equal(3, calls.Count(c => c.Kind == "elf"));
        Assert.Contains(("exists", "shared/payload.s"), calls);
        if (hintKind != "s") Assert.Contains(("exists", "shared/payload.asm"), calls);
        Assert.Contains(("exists", "shared/image.png.dmp"), calls);
        if (hintKind == "none")
        {
            Assert.Single(spied.UntraceableNotes);
            Assert.DoesNotContain(calls, c => c.Kind == "bytes" && c.Name == "shared/image.png.dmp");
        }
        else Assert.Contains(("bytes", "shared/image.png.dmp"), calls);
    }

    [Theory]
    [InlineData("#incbin \"{0}\"")]
    [InlineData("#inctext lyn \"code.o\" \"{0}\"")]
    [InlineData("#inctevent lyn \"{0}\"")]
    [InlineData("#include \"{0}/lyn.event\"")]
    [InlineData("#incext Png2Dmp \"{0}\"")]
    public void RejectedEventClosureNeverCallsActualParserFileOrElfReaders(string directive)
    {
        using var fixture = new Fixture();
        fixture.Put("nested/PATCH_test.txt", "TYPE=EA\nEA=main.event");
        fixture.Put("nested/main.event", string.Format(directive, @"\\unused.invalid\share\file"));
        fixture.PutBytes("nested/code.o", new byte[] { 0 });
        int exists = 0, bytes = 0, lines = 0, elves = 0, parsers = 0;
        Assert.Throws<InvalidDataException>(() =>
        {
            fixture.Audit();
            parsers++;
            _ = new EAUtilCore(Path.Combine(fixture.Root, "nested", "main.event"),
                _ => { exists++; return false; },
                _ => { bytes++; return Array.Empty<byte>(); },
                _ => { lines++; return Array.Empty<string>(); },
                (_, _) => { elves++; throw new InvalidOperationException("ELF reader must not run."); });
        });
        Assert.Equal(0, parsers);
        Assert.Equal(0, exists);
        Assert.Equal(0, bytes);
        Assert.Equal(0, lines);
        Assert.Equal(0, elves);
    }

    public static IEnumerable<object[]> CollationDirectiveCases()
    {
        foreach (string culture in new[] { "", "en-US", "ja-JP", "tr-TR" })
            foreach (string ignorable in new[] { "\0", "\u00AD", "\u034F", "\u200B", "\u200D", "\uFE0F", "\U000E0100" })
                foreach (bool inFilename in new[] { false, true })
                    yield return new object[] { culture, ignorable, inFilename };
    }

    [Theory]
    [MemberData(nameof(CollationDirectiveCases))]
    public void CollationDependentIncludeIsRejectedBeforeAnyConsumerIo(string cultureName, string ignorable, bool inFilename)
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            using var fixture = new Fixture();
            string directive = inFilename ? "#include" : "#inc" + ignorable + "lude";
            string basename = inFilename ? "ly" + ignorable + "n.event" : "lyn.event";
            fixture.Put("nested/PATCH_test.txt", "TYPE=EA\nEA=main.event");
            string line = directive + " \"\\\\unused.invalid\\share\\" + basename + "\"";
            fixture.Put("nested/main.event", line);
            int consumers = 0, reads = 0;
            Assert.Throws<InvalidDataException>(() =>
            {
                fixture.Audit();
                consumers++;
                _ = new EAUtilCore(Path.Combine(fixture.Root, "nested", "main.event"),
                    _ => { reads++; return false; },
                    _ => { reads++; return Array.Empty<byte>(); },
                    _ => new[] { line },
                    (_, _) => { reads++; throw new InvalidOperationException("ELF reader must not run."); });
            });
            Assert.Equal(0, consumers);
            Assert.Equal(0, reads);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void LegacyLinguisticIncludeRecognitionIsDemonstratedWithSpiesOnly()
    {
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            const string line = "#inc\u00ADlude \"\\\\unused.invalid\\share\\lyn.event\"";
            int probes = 0;
            _ = new EAUtilCore("owned-main.event",
                _ => { probes++; return false; },
                _ => throw new InvalidOperationException("No binary read is allowed."),
                _ => new[] { line },
                (_, _) => throw new InvalidOperationException("No ELF read is allowed."));
            Assert.Equal(1, probes);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = originalCulture; }
    }

    [Fact]
    public void OrdinalIncludePreservesLegitimateUnicodeInternalDirectories()
    {
        using var fixture = new Fixture();
        fixture.Put("PATCH_test.txt", "TYPE=EA\nEA=main.event");
        fixture.Put("main.event", "#include \"日本語/lyn.event\"");
        fixture.Put("日本語/lyn.event", "BYTE 0x11");
        Assert.Contains(fixture.Audit().References, r => r.Source == "main.event" && r.Target == "日本語/lyn.event");
    }

    [Theory]
    [InlineData("")]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("tr-TR")]
    public void BmpLinguisticIgnorablesCannotBypassTheBoundedCandidateRecognition(string cultureName)
    {
        var comparison = System.Globalization.CultureInfo.GetCultureInfo(cultureName).CompareInfo;
        for (int value = 0; value <= char.MaxValue; value++)
        {
            char character = (char)value;
            if (char.IsSurrogate(character) || character is '\r' or '\n') continue;
            string directive = "#inc" + character + "lude \"lyn.event\"";
            if (comparison.IndexOf(directive, "#include", System.Globalization.CompareOptions.None) >= 0)
                Assert.True(PatchDatabaseMetadataAuditCore.HasNonOrdinalIncludeCandidateForTest(directive),
                    "Uncovered directive character U+" + value.ToString("X4"));
            string filename = "#include \"ly" + character + "n.event\"";
            if (comparison.IndexOf(filename, "lyn.event", System.Globalization.CompareOptions.None) >= 0)
                Assert.True(PatchDatabaseMetadataAuditCore.HasNonOrdinalIncludeCandidateForTest(filename),
                    "Uncovered filename character U+" + value.ToString("X4"));
        }
    }

    [Fact]
    public void CollationRecognitionSpansBoundedNormalizationChunksAndSurrogatePairs()
    {
        string prefix = new string('x', 1023) + "\U000E0100";
        Assert.True(PatchDatabaseMetadataAuditCore.HasNonOrdinalIncludeCandidateForTest(
            prefix + "#inc\u00ADlude \"ly\u034Fn.event\""));
        Assert.True(PatchDatabaseMetadataAuditCore.HasNonOrdinalIncludeCandidateForTest(
            new string('x', 1020) + "#inc\u00ADlude \"lyn.event\""));
    }

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "TestResults",
            "metadata-audit-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, long> Files { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = new();

        public void Put(string name, string content) => PutBytes(name, new UTF8Encoding(false).GetBytes(content));

        public void PutBytes(string name, byte[] content)
        {
            string path = Path.Combine(Root, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            Files[name] = content.Length;
        }

        public PatchDatabaseMetadataAuditCore.Result Audit(PatchDatabaseMetadataAuditCore.Limits? limits = null)
            => PatchDatabaseMetadataAuditCore.Audit(Root, Files, limits, readObserver: Reads.Add);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
