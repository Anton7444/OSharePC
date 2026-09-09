using System.Buffers;
using System.Text;

namespace CatShareSender;

/// <summary>
/// Streaming ZIP writer matching the OnePlus Share iOS FileChunkedInput data path:
/// each file is CRC32-prepassed, emitted as ZIP method STORED (0), and copied in
/// 1 MiB chunks. Sizes/CRC are present in the local header, so Java ZipInputStream
/// can consume STORED entries without a data descriptor. ZIP64 is emitted when
/// size/offset/count limits require it.
/// </summary>
internal static class OfficialStoredZipWriter
{
    private const int CrcBufferSize = 512 * 1024;
    private const int TransferBufferSize = 1024 * 1024;
    private const ushort Utf8Flag = 0x0800;
    private const ushort StoredMethod = 0;

    private sealed class EntryInfo
    {
        public required string Path { get; init; }
        public required byte[] NameBytes { get; init; }
        public required long Size { get; init; }
        public required uint Crc32 { get; init; }
        public required ushort DosTime { get; init; }
        public required ushort DosDate { get; init; }
        public long LocalHeaderOffset { get; set; }
        public bool SizeNeedsZip64 => Size >= uint.MaxValue;
        public bool OffsetNeedsZip64 => LocalHeaderOffset >= uint.MaxValue;
        public bool NeedsZip64 => SizeNeedsZip64 || OffsetNeedsZip64;
    }

    public static async Task WriteAsync(
        Stream output,
        IReadOnlyList<string> files,
        long totalBytes,
        Action<long, long>? progress,
        CancellationToken cancellationToken)
    {
        var writer = new Writer(output);
        var entries = new List<EntryInfo>(files.Count);
        long sent = 0;

        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
                throw new FileNotFoundException("Transfer source disappeared before streaming.", path);

            var name = SanitizeEntryName(Path.GetFileName(path));
            var nameBytes = Encoding.UTF8.GetBytes(name);
            if (nameBytes.Length > ushort.MaxValue)
                throw new InvalidOperationException($"ZIP entry name is too long: {name}");

            var crcStarted = System.Diagnostics.Stopwatch.StartNew();
            var crc = await ComputeCrc32Async(path, cancellationToken);
            crcStarted.Stop();

            // Re-read metadata after the CRC pass. If a producer changed the file while
            // calculating CRC, refuse to emit a corrupt STORED entry.
            fileInfo.Refresh();
            var size = fileInfo.Length;
            var (dosTime, dosDate) = ToDosTime(fileInfo.LastWriteTime);

            var entry = new EntryInfo
            {
                Path = path,
                NameBytes = nameBytes,
                Size = size,
                Crc32 = crc,
                DosTime = dosTime,
                DosDate = dosDate,
                LocalHeaderOffset = writer.Offset,
            };

            Log.Info($"HTTP: STORED ZIP prepass '{name}' size={size} crc=0x{crc:X8} in {crcStarted.Elapsed.TotalMilliseconds:F0}ms");
            await writer.WriteLocalHeaderAsync(entry, cancellationToken);

            var transferStarted = System.Diagnostics.Stopwatch.StartNew();
            var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
            long fileSent = 0;
            try
            {
                await using var fs = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    TransferBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                while (true)
                {
                    var read = await fs.ReadAsync(buffer.AsMemory(0, TransferBufferSize), cancellationToken);
                    if (read <= 0) break;
                    await writer.WriteRawAsync(buffer.AsMemory(0, read), cancellationToken);
                    fileSent += read;
                    sent += read;
                    try { progress?.Invoke(sent, totalBytes); } catch { }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (fileSent != size)
                throw new IOException($"Transfer source changed while streaming '{path}' (expected {size}, read {fileSent}).");

            transferStarted.Stop();
            var mibPerSec = transferStarted.Elapsed.TotalSeconds > 0
                ? fileSent / 1048576d / transferStarted.Elapsed.TotalSeconds
                : 0;
            Log.Info($"HTTP: STORED ZIP streamed '{name}' {fileSent} bytes at {mibPerSec:F2} MiB/s");
            entries.Add(entry);
        }

        var centralDirectoryOffset = writer.Offset;
        foreach (var entry in entries)
            await writer.WriteCentralDirectoryEntryAsync(entry, cancellationToken);
        var centralDirectorySize = writer.Offset - centralDirectoryOffset;

        var needsZip64 = entries.Count >= ushort.MaxValue ||
                         centralDirectoryOffset >= uint.MaxValue ||
                         centralDirectorySize >= uint.MaxValue ||
                         entries.Any(e => e.NeedsZip64);

        if (needsZip64)
            await writer.WriteZip64EndAsync(entries.Count, centralDirectorySize, centralDirectoryOffset, cancellationToken);

        await writer.WriteEndAsync(entries.Count, centralDirectorySize, centralDirectoryOffset, cancellationToken);
        Log.Info($"HTTP: official STORED ZIP complete, payload={sent}/{totalBytes} bytes, entries={entries.Count}, wire={writer.Offset} bytes, zip64={needsZip64}");
    }

    private static async Task<uint> ComputeCrc32Async(string path, CancellationToken cancellationToken)
    {
        uint crc = 0xFFFFFFFFu;
        var buffer = ArrayPool<byte>.Shared.Rent(CrcBufferSize);
        try
        {
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CrcBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                var read = await fs.ReadAsync(buffer.AsMemory(0, CrcBufferSize), cancellationToken);
                if (read <= 0) break;
                for (var i = 0; i < read; i++)
                    crc = CrcTable[(crc ^ buffer[i]) & 0xFF] ^ (crc >> 8);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return ~crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }

    private static (ushort time, ushort date) ToDosTime(DateTime dateTime)
    {
        if (dateTime.Year < 1980 || dateTime.Year > 2107)
            dateTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local);

        var time = (ushort)((dateTime.Hour << 11) | (dateTime.Minute << 5) | (dateTime.Second / 2));
        var date = (ushort)(((dateTime.Year - 1980) << 9) | (dateTime.Month << 5) | dateTime.Day);
        return (time, date);
    }

    private static string SanitizeEntryName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }

    private sealed class Writer(Stream output)
    {
        public long Offset { get; private set; }

        public async Task WriteRawAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            await output.WriteAsync(data, cancellationToken);
            Offset += data.Length;
        }

        public Task WriteLocalHeaderAsync(EntryInfo entry, CancellationToken cancellationToken)
        {
            var extra = entry.SizeNeedsZip64 ? BuildZip64Extra(entry.Size, entry.Size, null) : Array.Empty<byte>();
            var header = Build(binary =>
            {
                binary.Write(0x04034B50u);
                binary.Write((ushort)(entry.SizeNeedsZip64 ? 45 : 20));
                binary.Write(Utf8Flag);
                binary.Write(StoredMethod);
                binary.Write(entry.DosTime);
                binary.Write(entry.DosDate);
                binary.Write(entry.Crc32);
                binary.Write(entry.SizeNeedsZip64 ? uint.MaxValue : (uint)entry.Size);
                binary.Write(entry.SizeNeedsZip64 ? uint.MaxValue : (uint)entry.Size);
                binary.Write((ushort)entry.NameBytes.Length);
                binary.Write((ushort)extra.Length);
                binary.Write(entry.NameBytes);
                binary.Write(extra);
            });
            return WriteRawAsync(header, cancellationToken);
        }

        public Task WriteCentralDirectoryEntryAsync(EntryInfo entry, CancellationToken cancellationToken)
        {
            var extra = entry.NeedsZip64
                ? BuildZip64Extra(
                    entry.SizeNeedsZip64 ? entry.Size : null,
                    entry.SizeNeedsZip64 ? entry.Size : null,
                    entry.OffsetNeedsZip64 ? entry.LocalHeaderOffset : null)
                : Array.Empty<byte>();

            var header = Build(binary =>
            {
                binary.Write(0x02014B50u);
                binary.Write((ushort)45); // version made by
                binary.Write((ushort)(entry.NeedsZip64 ? 45 : 20));
                binary.Write(Utf8Flag);
                binary.Write(StoredMethod);
                binary.Write(entry.DosTime);
                binary.Write(entry.DosDate);
                binary.Write(entry.Crc32);
                binary.Write(entry.SizeNeedsZip64 ? uint.MaxValue : (uint)entry.Size);
                binary.Write(entry.SizeNeedsZip64 ? uint.MaxValue : (uint)entry.Size);
                binary.Write((ushort)entry.NameBytes.Length);
                binary.Write((ushort)extra.Length);
                binary.Write((ushort)0); // comment length
                binary.Write((ushort)0); // disk start
                binary.Write((ushort)0); // internal attrs
                binary.Write(0u);        // external attrs
                binary.Write(entry.OffsetNeedsZip64 ? uint.MaxValue : (uint)entry.LocalHeaderOffset);
                binary.Write(entry.NameBytes);
                binary.Write(extra);
            });
            return WriteRawAsync(header, cancellationToken);
        }

        public async Task WriteZip64EndAsync(int count, long centralSize, long centralOffset, CancellationToken cancellationToken)
        {
            var zip64Offset = Offset;
            var end = Build(binary =>
            {
                binary.Write(0x06064B50u);
                binary.Write(44UL); // remaining record size
                binary.Write((ushort)45);
                binary.Write((ushort)45);
                binary.Write(0u);
                binary.Write(0u);
                binary.Write((ulong)count);
                binary.Write((ulong)count);
                binary.Write((ulong)centralSize);
                binary.Write((ulong)centralOffset);
            });
            await WriteRawAsync(end, cancellationToken);

            var locator = Build(binary =>
            {
                binary.Write(0x07064B50u);
                binary.Write(0u);
                binary.Write((ulong)zip64Offset);
                binary.Write(1u);
            });
            await WriteRawAsync(locator, cancellationToken);
        }

        public Task WriteEndAsync(int count, long centralSize, long centralOffset, CancellationToken cancellationToken)
        {
            var end = Build(binary =>
            {
                binary.Write(0x06054B50u);
                binary.Write((ushort)0);
                binary.Write((ushort)0);
                binary.Write((ushort)Math.Min(count, ushort.MaxValue));
                binary.Write((ushort)Math.Min(count, ushort.MaxValue));
                binary.Write(centralSize >= uint.MaxValue ? uint.MaxValue : (uint)centralSize);
                binary.Write(centralOffset >= uint.MaxValue ? uint.MaxValue : (uint)centralOffset);
                binary.Write((ushort)0);
            });
            return WriteRawAsync(end, cancellationToken);
        }

        private static byte[] Build(Action<BinaryWriter> write)
        {
            using var ms = new MemoryStream();
            using (var binary = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
                write(binary);
            return ms.ToArray();
        }

        private static byte[] BuildZip64Extra(long? uncompressed, long? compressed, long? localOffset)
        {
            var payload = Build(binary =>
            {
                if (uncompressed.HasValue) binary.Write((ulong)uncompressed.Value);
                if (compressed.HasValue) binary.Write((ulong)compressed.Value);
                if (localOffset.HasValue) binary.Write((ulong)localOffset.Value);
            });

            return Build(binary =>
            {
                binary.Write((ushort)0x0001);
                binary.Write((ushort)payload.Length);
                binary.Write(payload);
            });
        }
    }
}
