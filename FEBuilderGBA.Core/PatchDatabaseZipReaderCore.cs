using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FEBuilderGBA
{
    internal static class PatchDatabaseZipReaderCore
    {
        internal const string OwnershipFileName = ".febuilder-patch-import.json";
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly uint[] CrcTable = CreateCrcTable();

        internal sealed record Limits
        {
            public long MaxInputBytes { get; init; } = 1L << 30;
            public long MaxCentralBytes { get; init; } = 32L << 20;
            public int MaxEntries { get; init; } = 100_000;
            public int MaxFiles { get; init; } = 50_000;
            public int MaxMaterializedNodes { get; init; } = 100_000;
            public long MaxFileBytes { get; init; } = 128L << 20;
            public long MaxExpandedBytes { get; init; } = 2L << 30;
            public int MaxPathBytes { get; init; } = 1024;
            public int MaxComponentBytes { get; init; } = 255;
            public int MaxDepth { get; init; } = 32;

            internal void Validate()
            {
                Check(MaxInputBytes, 1L << 30);
                Check(MaxCentralBytes, 32L << 20);
                Check(MaxEntries, 100_000);
                Check(MaxFiles, 50_000);
                Check(MaxMaterializedNodes, 100_000);
                Check(MaxFileBytes, 128L << 20);
                Check(MaxExpandedBytes, 2L << 30);
                Check(MaxPathBytes, 1024);
                Check(MaxComponentBytes, 255);
                Check(MaxDepth, 32);
            }

            static void Check(long value, long maximum)
            {
                if (value < 0 || value > maximum)
                    throw new ArgumentOutOfRangeException(nameof(value), "A test limit cannot exceed its production ceiling.");
            }
        }

        internal sealed class Entry
        {
            public string Name { get; init; } = "";
            public string RelativePath { get; internal set; } = "";
            public bool IsDirectory { get; init; }
            public long Length { get; init; }
            public long CompressedLength { get; init; }
            public uint Crc { get; init; }
            public ushort Method { get; init; }
            internal ushort Flags { get; init; }
            internal byte[] RawName { get; init; } = Array.Empty<byte>();
            internal long LocalOffset { get; init; }
            internal bool CentralZip64Sizes { get; init; }
            internal long DataOffset { get; set; }
        }

        internal sealed class Archive
        {
            public string Version { get; init; } = "";
            public IReadOnlyList<Entry> Files { get; init; } = Array.Empty<Entry>();
            public IReadOnlyList<Entry> Directories { get; init; } = Array.Empty<Entry>();
            public int MaterializedNodes { get; init; }
            public long ExpandedBytes { get; init; }
            internal long InputBytes { get; init; }
        }

        internal static bool IsSupportedVersion(string version)
            => version is "FE6" or "FE7J" or "FE7U" or "FE8J" or "FE8U";

        internal static Archive Inspect(Stream source, string version, Limits? limits = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(source);
            limits ??= new Limits();
            limits.Validate();
            if (!source.CanRead || !source.CanSeek)
                throw Invalid("ZIP preflight requires the importer's seekable spool.");
            if (!IsSupportedVersion(version))
                throw Invalid("Unsupported patch database version.");
            if (source.Length > limits.MaxInputBytes)
                throw Invalid("Compressed ZIP exceeds the input limit.");
            try
            {
                var directory = ReadDirectoryLocation(source, limits);
                var entries = ReadCentralEntries(source, directory.Offset, directory.Length,
                    directory.Count, limits, cancellationToken);
                CheckLocalEntries(source, entries, directory.Offset, cancellationToken);
                return SelectVersion(entries, version, source.Length, limits, cancellationToken);
            }
            catch (OverflowException ex) { throw new InvalidDataException("ZIP offset or size overflow.", ex); }
            catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated ZIP structure.", ex); }
            catch (DecoderFallbackException ex) { throw new InvalidDataException("ZIP names must be valid UTF-8.", ex); }
        }

        static (long Offset, long Length, int Count) ReadDirectoryLocation(Stream stream, Limits limits)
        {
            long length = stream.Length;
            if (length < 22) throw Invalid("Missing ZIP end record.");
            int tailLength = (int)Math.Min(length, 22 + ushort.MaxValue);
            byte[] tail = ReadAt(stream, length - tailLength, tailLength, length);
            int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (U32(tail, i) != 0x06054b50 || i + 22 + U16(tail, i + 20) != tail.Length)
                    continue;
                if (end >= 0) throw Invalid("Ambiguous ZIP end records.");
                end = i;
            }
            if (end < 0) throw Invalid("Missing or truncated ZIP end record.");
            long endOffset = length - tailLength + end;
            if (U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0)
                throw Invalid("Multi-disk ZIP is not supported.");
            ulong diskCount = U16(tail, end + 8), count = U16(tail, end + 10);
            ulong centralLength = U32(tail, end + 12), centralOffset = U32(tail, end + 16);
            bool sentinel = count == ushort.MaxValue || diskCount == ushort.MaxValue ||
                centralLength == uint.MaxValue || centralOffset == uint.MaxValue;
            bool hasLocator = endOffset >= 20 && U32(ReadAt(stream, endOffset - 20, 4, length), 0) == 0x07064b50;
            long centralEnd = endOffset;
            if (sentinel || hasLocator)
            {
                if (!hasLocator) throw Invalid("Missing ZIP64 locator.");
                byte[] locator = ReadAt(stream, endOffset - 20, 20, length);
                if (U32(locator, 4) != 0 || U32(locator, 16) != 1)
                    throw Invalid("Multi-disk ZIP64 is not supported.");
                long zip64Offset = CheckedLong(U64(locator, 8));
                byte[] z = ReadAt(stream, zip64Offset, 56, endOffset - 20);
                if (U32(z, 0) != 0x06064b50 || U64(z, 4) != 44 ||
                    checked(zip64Offset + 56) != endOffset - 20)
                    throw Invalid("Unsupported ZIP64 end record.");
                if (U32(z, 16) != 0 || U32(z, 20) != 0)
                    throw Invalid("Multi-disk ZIP64 is not supported.");
                AgreeUnlessSentinel(diskCount, U64(z, 24), ushort.MaxValue);
                AgreeUnlessSentinel(count, U64(z, 32), ushort.MaxValue);
                AgreeUnlessSentinel(centralLength, U64(z, 40), uint.MaxValue);
                AgreeUnlessSentinel(centralOffset, U64(z, 48), uint.MaxValue);
                diskCount = U64(z, 24);
                count = U64(z, 32);
                centralLength = U64(z, 40);
                centralOffset = U64(z, 48);
                centralEnd = zip64Offset;
            }
            if (diskCount != count || count > (ulong)limits.MaxEntries ||
                centralLength > (ulong)limits.MaxCentralBytes)
                throw Invalid("ZIP central-directory count or byte limit exceeded.");
            long offset = CheckedLong(centralOffset), size = CheckedLong(centralLength);
            if (checked(offset + size) != centralEnd || size < checked((long)count * 46))
                throw Invalid("Invalid central-directory extent.");
            return (offset, size, (int)count);
        }

        static void AgreeUnlessSentinel(ulong small, ulong large, ulong sentinel)
        {
            if (small != sentinel && small != large)
                throw Invalid("ZIP32/ZIP64 end records disagree.");
        }

        static List<Entry> ReadCentralEntries(Stream stream, long offset, long length,
            int count, Limits limits, CancellationToken cancellationToken)
        {
            long end = checked(offset + length), position = offset;
            var entries = new List<Entry>(count);
            var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] h = ReadAt(stream, position, 46, end);
                if (U32(h, 0) != 0x02014b50) throw Invalid("Invalid central-directory record.");
                ushort flags = U16(h, 8), method = U16(h, 10);
                CheckFlags(flags, method);
                int nameLength = U16(h, 28), extraLength = U16(h, 30), commentLength = U16(h, 32);
                if (nameLength == 0 || nameLength > limits.MaxPathBytes)
                    throw Invalid("ZIP entry name limit exceeded.");
                long body = checked(position + 46);
                byte[] rawName = ReadAt(stream, body, nameLength, end);
                string decoded = StrictUtf8.GetString(rawName);
                string name = NormalizeEntryPath(decoded, limits, out bool isDirectory);
                byte[] extra = ReadAt(stream, checked(body + nameLength), extraLength, end);
                var extras = ReadExtras(extra, rawName, decoded);
                ulong expanded = U32(h, 24), compressed = U32(h, 20), local = U32(h, 42);
                ulong disk = U16(h, 34);
                bool zip64Sizes = expanded == uint.MaxValue || compressed == uint.MaxValue;
                int z = 0;
                if (expanded == uint.MaxValue) expanded = Extra64(extras, ref z);
                if (compressed == uint.MaxValue) compressed = Extra64(extras, ref z);
                if (local == uint.MaxValue) local = Extra64(extras, ref z);
                if (disk == ushort.MaxValue)
                {
                    if (extras == null || z + 4 > extras.Length) throw Invalid("Missing ZIP64 disk value.");
                    disk = U32(extras, z);
                    z += 4;
                }
                if (z != 0 && z != extras!.Length) throw Invalid("Ambiguous ZIP64 size extra.");
                if (disk != 0 || expanded > (ulong)limits.MaxFileBytes ||
                    compressed > (ulong)limits.MaxInputBytes)
                    throw Invalid("ZIP entry disk or size limit exceeded.");
                uint attributes = U32(h, 38);
                uint unixType = (attributes >> 16) & 0xf000;
                if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
                    throw Invalid("ZIP links and special files are not supported.");
                if ((attributes & 0x448) != 0)
                    throw Invalid("ZIP reparse/device/volume attributes are not supported.");
                bool directoryAttribute = (attributes & 0x10) != 0 || unixType == 0x4000;
                if (directoryAttribute != isDirectory && (directoryAttribute || unixType == 0x8000))
                    throw Invalid("ZIP directory attributes disagree with the name.");
                if (isDirectory && (expanded != 0 || compressed != 0 || U32(h, 16) != 0))
                    throw Invalid("ZIP directory entries cannot carry data.");
                if (method == 0 && compressed != expanded)
                    throw Invalid("Stored ZIP entry sizes disagree.");
                if (!paths.TryAdd(name, isDirectory)) throw Invalid("Colliding ZIP entries.");
                entries.Add(new Entry
                {
                    Name = name,
                    RawName = rawName,
                    IsDirectory = isDirectory,
                    Length = CheckedLong(expanded),
                    CompressedLength = CheckedLong(compressed),
                    LocalOffset = CheckedLong(local),
                    Crc = U32(h, 16),
                    Flags = flags,
                    Method = method,
                    CentralZip64Sizes = zip64Sizes,
                });
                position = checked(body + nameLength + extraLength + commentLength);
                if (position > end) throw Invalid("Truncated central-directory entry.");
            }
            if (position != end) throw Invalid("Central-directory count does not consume its extent.");
            CheckPathNamespace(entries, paths);
            return entries;
        }

        static void CheckFlags(ushort flags, ushort method)
        {
            if (method != 0 && method != 8) throw Invalid("Only stored/deflate ZIP entries are supported.");
            if ((flags & ~0x080e) != 0 || (method == 0 && (flags & 6) != 0))
                throw Invalid("Encrypted or unsupported ZIP flags.");
        }

        static byte[]? ReadExtras(byte[] extra, byte[] rawName, string decodedName)
        {
            byte[]? zip64 = null;
            bool unicodeSeen = false;
            int p = 0;
            while (p < extra.Length)
            {
                if (extra.Length - p < 4) throw Invalid("Truncated ZIP extra header.");
                int kind = U16(extra, p), length = U16(extra, p + 2);
                p += 4;
                if (length > extra.Length - p) throw Invalid("Truncated ZIP extra body.");
                if (kind == 1)
                {
                    if (zip64 != null) throw Invalid("Duplicate ZIP64 extra.");
                    zip64 = extra.AsSpan(p, length).ToArray();
                }
                else if (kind == 0x7075)
                {
                    if (unicodeSeen || length < 5 || extra[p] != 1 ||
                        U32(extra, p + 1) != Crc32(rawName) ||
                        StrictUtf8.GetString(extra, p + 5, length - 5) != decodedName)
                        throw Invalid("Conflicting Unicode path extra.");
                    unicodeSeen = true;
                }
                p += length;
            }
            return zip64;
        }

        static ulong Extra64(byte[]? extra, ref int position)
        {
            if (extra == null || extra.Length - position < 8) throw Invalid("Missing ZIP64 size/offset.");
            ulong value = U64(extra, position);
            position += 8;
            return value;
        }

        static void CheckLocalEntries(Stream stream, List<Entry> entries, long centralOffset,
            CancellationToken cancellationToken)
        {
            var sorted = entries.OrderBy(e => e.LocalOffset).ToArray();
            long expectedOffset = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Entry e = sorted[i];
                if (e.LocalOffset != expectedOffset)
                    throw Invalid("Overlapping local entries, prefix or inter-record padding.");
                long next = i + 1 < sorted.Length ? sorted[i + 1].LocalOffset : centralOffset;
                byte[] h = ReadAt(stream, e.LocalOffset, 30, next);
                if (U32(h, 0) != 0x04034b50 || U16(h, 6) != e.Flags || U16(h, 8) != e.Method)
                    throw Invalid("Local header disagrees with the central directory.");
                int nameLength = U16(h, 26), extraLength = U16(h, 28);
                if (nameLength != e.RawName.Length) throw Invalid("Local ZIP name differs.");
                long nameOffset = checked(e.LocalOffset + 30);
                byte[] name = ReadAt(stream, nameOffset, nameLength, next);
                if (!name.AsSpan().SequenceEqual(e.RawName)) throw Invalid("Local ZIP name differs.");
                byte[] extra = ReadAt(stream, checked(nameOffset + nameLength), extraLength, next);
                byte[]? zip64 = ReadExtras(extra, name, StrictUtf8.GetString(name));
                ulong expanded = U32(h, 22), compressed = U32(h, 18);
                bool local64 = expanded == uint.MaxValue || compressed == uint.MaxValue;
                int z = 0;
                if (expanded == uint.MaxValue) expanded = Extra64(zip64, ref z);
                if (compressed == uint.MaxValue) compressed = Extra64(zip64, ref z);
                if (z != 0 && z != zip64!.Length) throw Invalid("Ambiguous local ZIP64 sizes.");
                bool descriptor = (e.Flags & 8) != 0;
                uint crc = U32(h, 14);
                if (descriptor)
                {
                    if ((expanded != 0 && expanded != (ulong)e.Length) ||
                        (compressed != 0 && compressed != (ulong)e.CompressedLength) ||
                        (crc != 0 && crc != e.Crc))
                        throw Invalid("Local streaming placeholders disagree with central metadata.");
                }
                else if (expanded != (ulong)e.Length || compressed != (ulong)e.CompressedLength || crc != e.Crc)
                    throw Invalid("Local ZIP sizes/CRC disagree with central metadata.");
                e.DataOffset = checked(nameOffset + nameLength + extraLength);
                long dataEnd = checked(e.DataOffset + e.CompressedLength);
                if (dataEnd > next) throw Invalid("Compressed ZIP extent overlaps another structure.");
                if (descriptor)
                    CheckDescriptor(stream, e, dataEnd, next, local64 || e.CentralZip64Sizes);
                else if (dataEnd != next)
                    throw Invalid("Unexpected data descriptor or padding.");
                expectedOffset = next;
            }
            if (expectedOffset != centralOffset) throw Invalid("Unowned bytes before central directory.");
        }

        static void CheckDescriptor(Stream stream, Entry entry, long offset, long end, bool zip64)
        {
            long remaining = end - offset;
            int unsignedLength = zip64 ? 20 : 12;
            if (remaining != unsignedLength && remaining != unsignedLength + 4)
                throw Invalid("Missing, truncated or unsupported-width ZIP data descriptor.");
            byte[] descriptor = ReadAt(stream, offset, (int)remaining, end);
            int matches = 0;
            for (int prefix = 0; prefix <= 4; prefix += 4)
            {
                if (descriptor.Length != unsignedLength + prefix) continue;
                if (prefix == 4 && U32(descriptor, 0) != 0x08074b50) continue;
                if (U32(descriptor, prefix) != entry.Crc) continue;
                ulong compressed = zip64 ? U64(descriptor, prefix + 4) : U32(descriptor, prefix + 4);
                ulong expanded = zip64 ? U64(descriptor, prefix + 12) : U32(descriptor, prefix + 8);
                if (compressed == (ulong)entry.CompressedLength && expanded == (ulong)entry.Length)
                    matches++;
            }
            if (matches != 1) throw Invalid("ZIP data descriptor disagrees with central metadata.");
        }

        internal static string NormalizeEntryPath(string raw, Limits limits, out bool directory)
        {
            if (string.IsNullOrEmpty(raw) || raw[0] == '/' || raw[0] == '\\')
                throw Invalid("Rooted or empty ZIP entry name.");
            string path = raw.Replace('\\', '/').Normalize(NormalizationForm.FormC);
            directory = path.EndsWith("/", StringComparison.Ordinal);
            if (directory) path = path.Substring(0, path.Length - 1);
            string[] parts = path.Split('/');
            if (parts.Length > limits.MaxDepth || StrictUtf8.GetByteCount(path) > limits.MaxPathBytes)
                throw Invalid("ZIP path depth/length limit exceeded.");
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == "." || part == ".." ||
                    part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    StrictUtf8.GetByteCount(part) > limits.MaxComponentBytes ||
                    part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)) ||
                    part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals(OwnershipFileName, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("Unsafe or reserved ZIP path component.");
                string stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
                if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                    ((stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                    stem.Length == 4 && "123456789¹²³".Contains(stem[3])))
                    throw Invalid("Reserved device name in ZIP.");
            }
            return path;
        }

        static void CheckPathNamespace(List<Entry> entries, Dictionary<string, bool> paths)
        {
            // Index only explicit entries: materializing every implicit directory would multiply
            // attacker-controlled metadata allocations by the maximum path depth.
            foreach (Entry entry in entries)
            {
                int separator = entry.Name.IndexOf('/');
                while (separator >= 0)
                {
                    if (paths.TryGetValue(entry.Name.Substring(0, separator), out bool directory) && !directory)
                        throw Invalid("ZIP file/directory collision.");
                    separator = entry.Name.IndexOf('/', separator + 1);
                }
            }
            string[]? previous = null, previousSpelling = null;
            foreach (Entry entry in entries.OrderBy(e => e.Name.Replace('/', '\0'), StringComparer.OrdinalIgnoreCase))
            {
                string[] components = entry.Name.Split('/');
                string[] spelling = StrictUtf8.GetString(entry.RawName).Replace('\\', '/').TrimEnd('/').Split('/');
                if (previous != null)
                    for (int i = 0; i < Math.Min(previous.Length, components.Length); i++)
                    {
                        if (!components[i].Equals(previous[i], StringComparison.OrdinalIgnoreCase)) break;
                        if (!spelling[i].Equals(previousSpelling![i], StringComparison.Ordinal))
                            throw Invalid("Case/Unicode aliases for a ZIP directory.");
                    }
                previous = components;
                previousSpelling = spelling;
            }
        }

        static Archive SelectVersion(List<Entry> entries, string version, long inputBytes, Limits limits,
            CancellationToken cancellationToken)
        {
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (Entry entry in entries)
            {
                string[] parts = entry.Name.Split('/');
                if (parts[0] == version) roots.Add(version + "/");
                if (parts.Length >= 2 && parts[1] == version) roots.Add(parts[0] + "/" + version + "/");
            }
            if (roots.Count != 1) throw Invalid("Missing or ambiguous canonical version subtree.");
            string root = roots.Single();
            var files = new List<Entry>();
            var directories = new List<Entry>();
            long total = 0;
            foreach (Entry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.Name.StartsWith(root, StringComparison.Ordinal)) continue;
                entry.RelativePath = entry.Name.Substring(root.Length);
                if (entry.IsDirectory)
                {
                    directories.Add(entry);
                    continue;
                }
                total = checked(total + entry.Length);
                if (files.Count >= limits.MaxFiles || total > limits.MaxExpandedBytes)
                    throw Invalid("Selected database file/byte limit exceeded.");
                files.Add(entry);
            }
            if (files.Count == 0) throw Invalid("The selected database subtree contains no files.");
            int nodes = 0;
            string[] previous = Array.Empty<string>();
            foreach (Entry entry in files.Concat(directories).OrderBy(e => e.RelativePath.Replace('/', '\0'),
                StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] components = entry.RelativePath.Split('/');
                int common = 0;
                while (common < Math.Min(previous.Length, components.Length) && previous[common] == components[common])
                    common++;
                nodes = checked(nodes + components.Length - common);
                if (nodes > limits.MaxMaterializedNodes)
                    throw Invalid("Selected database materialized-node limit exceeded.");
                previous = components;
            }
            return new Archive
            {
                Version = version, Files = files.AsReadOnly(), Directories = directories.AsReadOnly(),
                MaterializedNodes = nodes, ExpandedBytes = total, InputBytes = inputBytes,
            };
        }

        internal static async Task CopyEntryAsync(Stream source, Archive archive, Entry entry,
            Stream destination, CancellationToken cancellationToken = default)
        {
            if (source.Length != archive.InputBytes || !archive.Files.Contains(entry))
                throw Invalid("ZIP spool or entry identity changed after validation.");
            source.Position = entry.DataOffset;
            using var slice = new SliceStream(source, entry.CompressedLength);
            using Stream expanded = entry.Method == 8
                ? new DeflateStream(slice, CompressionMode.Decompress, true)
                : slice;
            byte[] buffer = new byte[64 * 1024];
            long total = 0;
            uint crc = uint.MaxValue;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await expanded.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (read > entry.Length - total) throw Invalid("Expanded ZIP bytes exceed the declared limit.");
                crc = UpdateCrc(crc, buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
            if (total != entry.Length || (crc ^ uint.MaxValue) != entry.Crc || slice.Remaining != 0)
                throw Invalid("Expanded ZIP size/CRC does not match validated metadata.");
        }

        internal static uint Crc32(ReadOnlySpan<byte> data)
            => UpdateCrc(uint.MaxValue, data) ^ uint.MaxValue;

        static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
            return crc;
        }

        static uint[] CreateCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value >> 1) ^ ((value & 1) != 0 ? 0xedb88320U : 0);
                table[i] = value;
            }
            return table;
        }

        static byte[] ReadAt(Stream stream, long offset, int length, long boundary)
        {
            if (offset < 0 || length < 0 || offset > boundary || length > boundary - offset ||
                boundary > stream.Length)
                throw Invalid("ZIP structure lies outside its bounded extent.");
            stream.Position = offset;
            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
        static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
        static ulong U64(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        static long CheckedLong(ulong value) => checked((long)value);
        static InvalidDataException Invalid(string message) => new InvalidDataException(message);

        sealed class SliceStream(Stream source, long remaining) : Stream
        {
            public long Remaining { get; private set; } = remaining;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = source.Read(buffer, offset, (int)Math.Min(count, Remaining));
                Remaining -= read;
                return read;
            }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                int count = (int)Math.Min(buffer.Length, Remaining);
                int read = await source.ReadAsync(buffer.Slice(0, count), cancellationToken).ConfigureAwait(false);
                Remaining -= read;
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
