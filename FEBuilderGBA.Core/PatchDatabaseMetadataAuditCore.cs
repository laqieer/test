using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace FEBuilderGBA
{
    internal static class PatchDatabaseMetadataAuditCore
    {
        static readonly Regex FgrepToken = new Regex(@"\$FGREP", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        static readonly Regex FgrepGrammar = new Regex(@"^\$FGREP([0-9]+)(ENDA|END)?\+?([0-9]+)? ",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        static readonly HashSet<string> FileKeys = new HashSet<string>(
            new[] { "EA", "SYMBOL", "EDIT_PATCH", "BIN", "BINP", "BINAP", "BINF" },
            StringComparer.OrdinalIgnoreCase);

        internal sealed record Limits
        {
            public int MaxTextFiles { get; init; } = 10_000;
            public long MaxFileBytes { get; init; } = PatchMetadataCore.MaxPatchDefinitionBytes;
            public long MaxTextBytes { get; init; } = PatchMetadataCore.MaxMetadataAggregateBytes;
            public int MaxEdges { get; init; } = 30_000;
            public int MaxDepth { get; init; } = 32;
            public long MaxReferencedBytes { get; init; } = 64L << 20;

            internal void Validate()
            {
                if (MaxTextFiles < 0 || MaxTextFiles > 10_000 ||
                    MaxFileBytes < 0 || MaxFileBytes > PatchMetadataCore.MaxPatchDefinitionBytes ||
                    MaxTextBytes < 0 || MaxTextBytes > PatchMetadataCore.MaxMetadataAggregateBytes ||
                    MaxEdges < 0 || MaxEdges > 30_000 || MaxDepth < 0 || MaxDepth > 32 ||
                    MaxReferencedBytes < 0 || MaxReferencedBytes > (64L << 20))
                    throw new ArgumentOutOfRangeException(nameof(Limits));
            }
        }

        internal sealed record Reference(string Source, string Target, bool Text, bool Optional);
        internal sealed record Result(int DescriptorCount, long TextBytes, IReadOnlyList<Reference> References);

        internal static Result Audit(string versionRoot, IReadOnlyDictionary<string, long> files,
            Limits? limits = null, CancellationToken cancellationToken = default, Action<string>? readObserver = null)
        {
            limits ??= new Limits();
            limits.Validate();
            return new AuditState(versionRoot, files, limits, cancellationToken, readObserver).Run();
        }

        internal static bool HasNonOrdinalIncludeCandidateForTest(string line)
            => AuditState.HasNonOrdinalIncludeCandidate(line, default);

        sealed class AuditState
        {
            readonly string root;
            readonly Dictionary<string, (string Name, long Length)> files;
            readonly Limits limits;
            readonly CancellationToken cancellation;
            readonly Action<string>? readObserver;
            readonly Dictionary<string, int> visitedDepths = new Dictionary<string, int>(StringComparer.Ordinal);
            readonly HashSet<string> active = new HashSet<string>(StringComparer.Ordinal);
            readonly List<Reference> references = new List<Reference>();
            long textBytes;
            long referencedBytes;

            internal AuditState(string root, IReadOnlyDictionary<string, long> files, Limits limits,
                CancellationToken cancellation, Action<string>? readObserver)
            {
                ArgumentNullException.ThrowIfNull(files);
                this.root = Path.GetFullPath(root);
                this.files = new Dictionary<string, (string Name, long Length)>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                this.limits = limits;
                this.cancellation = cancellation;
                this.readObserver = readObserver;
                foreach (var file in files)
                {
                    string canonical = PatchDatabaseZipReaderCore.NormalizeEntryPath(file.Key,
                        new PatchDatabaseZipReaderCore.Limits(), out bool directory);
                    if (directory || canonical != file.Key || file.Value < 0 ||
                        !this.files.TryAdd(file.Key, (file.Key, file.Value)))
                        throw Invalid("Invalid or ambiguous metadata manifest.");
                }
            }

            internal Result Run()
            {
                string[] descriptors = files.Keys.Where(IsDescriptor).ToArray();
                if (descriptors.Length == 0) throw Invalid("The archive contains no PATCH_*.txt descriptors.");
                foreach (string name in descriptors) Visit(name, 0);
                foreach (string name in files.Keys.Where(n => n.EndsWith(".event", StringComparison.OrdinalIgnoreCase)))
                    Visit(name, 0);
                return new Result(descriptors.Length, textBytes, references.AsReadOnly());
            }

            int Visit(string name, int depth)
            {
                cancellation.ThrowIfCancellationRequested();
                if (active.Contains(name)) throw Invalid("Cyclic metadata references are not supported.");
                if (visitedDepths.TryGetValue(name, out int cachedDepth))
                {
                    if (cachedDepth > limits.MaxDepth - depth)
                        throw Invalid("Metadata graph depth limit exceeded.");
                    return cachedDepth;
                }
                if (depth > limits.MaxDepth || visitedDepths.Count + active.Count >= limits.MaxTextFiles)
                    throw Invalid("Metadata graph depth/file limit exceeded.");
                var file = files[name];
                long remaining = limits.MaxTextBytes - textBytes;
                long cap = Math.Min(limits.MaxFileBytes, remaining);
                if (file.Length > cap) throw Invalid("Metadata text byte limit exceeded.");
                string path = Path.Combine(root, file.Name.Replace('/', Path.DirectorySeparatorChar));
                readObserver?.Invoke(file.Name);
                if (!PatchMetadataCore.TryReadBoundedFileLines(path, cap, PatchMetadataCore.MaxRawParamLines,
                        out List<string> lines, out long readBytes, out var failure))
                    throw Invalid("Metadata read rejected: " + failure);
                textBytes = checked(textBytes + readBytes);
                if (readBytes != file.Length) throw Invalid("Metadata changed after extraction.");
                active.Add(name);
                var next = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    foreach (string raw in lines)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var variants = new HashSet<string>(StringComparer.Ordinal)
                        {
                            raw.Trim(),
                            U.ClipComment(raw).Trim(),
                            raw.Replace("{J}", "").Replace("{U}", "").Trim(),
                            U.ClipComment(raw.Replace("{J}", "").Replace("{U}", "")).Trim(),
                        };
                        // CleanupKey strips a two-character locale from the entire key,
                        // which can include a file-backed macro's filename.
                        foreach (string variant in variants.ToArray())
                        {
                            int separator = variant.IndexOf('=');
                            if (separator >= 3 && variant[separator - 3] == '.')
                                variants.Add(variant.Substring(0, separator - 3) + variant.Substring(separator));
                        }
                        // Deduplication is per physical line, not across duplicate directives.
                        var lineEdges = new Dictionary<string, (string Target, bool Text, bool Optional)>(StringComparer.Ordinal);
                        foreach (string line in variants)
                            AuditLine(name, line, lineEdges);
                        foreach (var edge in lineEdges.Values)
                        {
                            AddEdge(name, edge.Target, edge.Text, edge.Optional);
                            if (edge.Text && files.ContainsKey(edge.Target)) next.Add(edge.Target);
                        }
                    }
                    int descendantDepth = 0;
                    foreach (string target in next)
                        descendantDepth = Math.Max(descendantDepth, 1 + Visit(target, depth + 1));
                    visitedDepths.Add(name, descendantDepth);
                    return descendantDepth;
                }
                finally { active.Remove(name); }
            }

            void AuditLine(string source, string line, Dictionary<string, (string Target, bool Text, bool Optional)> edges)
            {
                int macroIndex = 0;
                int equals = line.IndexOf('=');
                foreach (Match token in FgrepToken.Matches(line))
                {
                    string expression = line.Substring(token.Index);
                    Match grammar = FgrepGrammar.Match(expression);
                    if (!grammar.Success ||
                        !uint.TryParse(grammar.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint align) ||
                        align == 0 ||
                        (grammar.Groups[3].Success && !uint.TryParse(grammar.Groups[3].Value,
                            NumberStyles.None, CultureInfo.InvariantCulture, out _)))
                        throw Invalid("Malformed or ambiguous file-backed macro.");
                    int end = equals > token.Index ? equals - token.Index : -1;
                    string operand = end < 0
                        ? expression.Substring(grammar.Length)
                        : expression.Substring(grammar.Length, end - grammar.Length);
                    Record(edges, "macro:" + macroIndex++, Resolve(source, operand, false), false, false);
                }

                if (equals >= 0)
                {
                    string key = line.Substring(0, equals).Trim();
                    string keyword = key.Split(':')[0].Split('.')[0];
                    if (FileKeys.Contains(keyword))
                    {
                        string operand = line.Substring(equals + 1);
                        if (operand.Length != 0)
                        {
                            bool text = keyword.Equals("EDIT_PATCH", StringComparison.OrdinalIgnoreCase) ||
                                keyword.Equals("EA", StringComparison.OrdinalIgnoreCase) ||
                                keyword.Equals("SYMBOL", StringComparison.OrdinalIgnoreCase);
                            Record(edges, "value:" + keyword.ToUpperInvariant(), Resolve(source, operand, false), text, false);
                        }
                    }
                }
                AuditEventLine(source, line, edges);
            }

            void AuditEventLine(string source, string line, Dictionary<string, (string Target, bool Text, bool Optional)> edges)
            {
                string? incbin = DirectiveOperand(line, "#incbin");
                string? lynText = DirectiveOperand(line, "#inctext lyn");
                string? lynEvent = DirectiveOperand(line, "#inctevent lyn");
                string? png = DirectiveOperand(line, "#incext Png2Dmp");
                bool include = line.Contains("#include", StringComparison.OrdinalIgnoreCase) &&
                    line.Contains("lyn.event", StringComparison.OrdinalIgnoreCase);
                if (!include && HasNonOrdinalIncludeCandidate(line, cancellation))
                    throw Invalid("Collation-dependent event include spelling is ambiguous.");
                int directiveCount = (incbin != null ? 1 : 0) + (lynText != null ? 1 : 0) +
                    (lynEvent != null ? 1 : 0) + (png != null ? 1 : 0) + (include ? 1 : 0);
                if (directiveCount == 0) return;
                if (directiveCount != 1) throw Invalid("Multiple file directives on one event line are ambiguous.");
                string[] quoted = QuotedOperands(incbin ?? lynText ?? lynEvent ?? png ?? line);
                if (quoted.Length == 0) throw Invalid("Ambiguous quoted event-file operand.");
                int expected = lynText != null ? 2 : 1;
                if (quoted.Length > expected) throw Invalid("Unexpected event-file operands.");
                for (int i = 0; i < quoted.Length; i++)
                {
                    string target = Resolve(source, quoted[i], false);
                    Record(edges, "event:" + i, target, include, false);
                    if (incbin != null)
                    {
                        Record(edges, "source-s:" + i, Derived(target, ".s", false), false, true);
                        Record(edges, "source-asm:" + i, Derived(target, ".asm", false), false, true);
                    }
                    if (png != null) Record(edges, "png-hint:" + i, Derived(target, ".dmp", true), false, true);
                }
            }

            static string? DirectiveOperand(string line, string directive)
            {
                // EAUtilCore.Keyword searches anywhere, preferring a space match over a tab.
                int position = line.IndexOf(directive + " ", StringComparison.OrdinalIgnoreCase);
                if (position < 0) position = line.IndexOf(directive + "\t", StringComparison.OrdinalIgnoreCase);
                return position < 0 ? null : line.Substring(position + directive.Length + 1).Trim();
            }

            internal static bool HasNonOrdinalIncludeCandidate(string line, CancellationToken cancellation)
            {
                if (!line.Any(c => c > 127 || char.IsControl(c))) return false;
                const string includeToken = "#include";
                const string filenameToken = "lyn.event";
                int includeMatch = 0, filenameMatch = 0;
                bool foundInclude = false;
                // Legacy linguistic IndexOf can ignore characters that ordinal matching
                // retains. Reject compatible spellings without depending on the current
                // culture. Small chunks bound compatibility-normalization expansion.
                for (int start = 0; start < line.Length;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int length = Math.Min(1024, line.Length - start);
                    if (start + length < line.Length && char.IsHighSurrogate(line[start + length - 1]))
                        length--;
                    string normalized = line.Substring(start, length).Normalize(NormalizationForm.FormKD);
                    start += length;
                    foreach (char character in normalized)
                    {
                        if (character > 127 || char.IsControl(character)) continue;
                        char value = char.ToLowerInvariant(character);
                        if (!foundInclude)
                        {
                            includeMatch = value == includeToken[includeMatch] ? includeMatch + 1 :
                                value == includeToken[0] ? 1 : 0;
                            foundInclude = includeMatch == includeToken.Length;
                        }
                        else
                        {
                            filenameMatch = value == filenameToken[filenameMatch] ? filenameMatch + 1 :
                                value == filenameToken[0] ? 1 : 0;
                            if (filenameMatch == filenameToken.Length) return true;
                        }
                    }
                }
                return false;
            }

            static void Record(Dictionary<string, (string Target, bool Text, bool Optional)> edges,
                string slot, string target, bool text, bool optional)
            {
                var value = (target, text, optional);
                if (edges.TryGetValue(slot, out var previous) && previous != value)
                    throw Invalid("Metadata consumer interpretations disagree about a file operand.");
                edges[slot] = value;
            }

            static string[] QuotedOperands(string line)
            {
                var result = new List<string>();
                int position = 0;
                while (position < line.Length)
                {
                    int start = line.IndexOf('"', position);
                    if (start < 0) break;
                    int end = line.IndexOf('"', start + 1);
                    if (end < 0) throw Invalid("Unclosed event filename quote.");
                    result.Add(line.Substring(start + 1, end - start - 1));
                    position = end + 1;
                }
                return result.ToArray();
            }

            string Resolve(string source, string operand, bool optional)
            {
                if (operand.Length == 0 || operand != operand.Trim() ||
                    operand[0] == '/' || operand[0] == '\\' ||
                    operand.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
                    throw Invalid("Rooted, network, malformed or ambiguous metadata file operand.");
                // A backslash is a literal filename character on Unix; accepting its Windows
                // interpretation there would audit a different file than the legacy consumer.
                if (!OperatingSystem.IsWindows() && operand.Contains('\\'))
                    throw Invalid("Metadata filename separator is not portable to this platform.");
                string portable = operand.Replace('\\', '/');
                var components = source.Split('/').SkipLast(1).ToList();
                foreach (string part in portable.Split('/'))
                {
                    if (part.Length == 0) throw Invalid("Ambiguous metadata path separator.");
                    if (part == ".") continue;
                    if (part == "..")
                    {
                        if (components.Count == 0) throw Invalid("Metadata operand escapes its version subtree.");
                        components.RemoveAt(components.Count - 1);
                    }
                    else components.Add(part);
                }
                string target = string.Join("/", components);
                string canonical = PatchDatabaseZipReaderCore.NormalizeEntryPath(target,
                    new PatchDatabaseZipReaderCore.Limits(), out bool directory);
                if (canonical != target || directory) throw Invalid("Ambiguous metadata path normalization.");
                if (files.TryGetValue(target, out var entry)) return entry.Name;
                if (optional) return target;
                throw Invalid("Metadata references a file absent from the selected version subtree.");
            }

            string Derived(string target, string extension, bool append)
            {
                string candidate = append ? target + extension : Path.ChangeExtension(target, extension);
                return Resolve("__root__", candidate, true);
            }

            void AddEdge(string source, string target, bool text, bool optional)
            {
                if (references.Count >= limits.MaxEdges) throw Invalid("Metadata reference-count limit exceeded.");
                if (files.TryGetValue(target, out var entry))
                {
                    if (entry.Length > limits.MaxReferencedBytes - referencedBytes)
                        throw Invalid("Metadata referenced-data byte limit exceeded.");
                    referencedBytes += entry.Length;
                }
                else if (!optional) throw Invalid("Missing metadata dependency.");
                references.Add(new Reference(source, target, text, optional));
            }

            static bool IsDescriptor(string name)
            {
                string file = name.Split('/').Last();
                return file.StartsWith("PATCH_", StringComparison.OrdinalIgnoreCase) &&
                    file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            }
        }

        static InvalidDataException Invalid(string message) => new InvalidDataException(message);
    }
}
