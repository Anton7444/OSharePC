using System.Buffers;
using System.Buffers.Binary;

namespace CatShareSender;

/// <summary>
/// Prepares the CRC32 required by STORED ZIP entries as soon as files are staged.
/// The official OShare/iOS path needs CRC and size in the local header before any
/// payload bytes can be sent, so doing this work during BLE/user-confirmation time
/// keeps /download from sitting at 0% on large files.
/// </summary>
internal static class PreparedCrcCache
{
    private const int ReadBufferSize = 4 * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, State> States = new(StringComparer.OrdinalIgnoreCase);
    private static CancellationTokenSource? _prepareCts;
    private static readonly uint[][] Tables = BuildTables();

    private sealed class State
    {
        public required long Size { get; init; }
        public required long LastWriteTicks { get; init; }
        public required Task<PreparedFile> Work { get; init; }
    }

    private readonly record struct PreparedFile(uint Crc32, long Size, long LastWriteTicks);

    public static void Begin(IEnumerable<string> files)
    {
        var paths = files
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        CancellationToken token;
        lock (Gate)
        {
            _prepareCts?.Cancel();
            _prepareCts?.Dispose();
            States.Clear();
            _prepareCts = paths.Length == 0 ? null : new CancellationTokenSource();
            if (_prepareCts is null) return;
            token = _prepareCts.Token;
        }

        _ = Task.Run(async () =>
        {
            foreach (var path in paths)
            {
                try
                {
                    await GetCrc32Async(path, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Log.Warn($"CRC prepare: '{Path.GetFileName(path)}' deferred ({ex.Message})");
                }
            }
        });
    }

    public static async Task<uint> GetCrc32Async(string path, CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);

        while (true)
        {
            var state = GetOrStart(path);
            var prepared = await state.Work.WaitAsync(cancellationToken);

            var now = new FileInfo(path);
            if (now.Exists && now.Length == prepared.Size && now.LastWriteTimeUtc.Ticks == prepared.LastWriteTicks)
                return prepared.Crc32;

            lock (Gate)
            {
                if (States.TryGetValue(path, out var current) && ReferenceEquals(current, state))
                    States.Remove(path);
            }
            Log.Warn($"CRC prepare: '{Path.GetFileName(path)}' changed after preparation; recomputing");
        }
    }

    private static State GetOrStart(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Transfer source disappeared before CRC preparation.", path);

        var size = info.Length;
        var ticks = info.LastWriteTimeUtc.Ticks;

        lock (Gate)
        {
            if (States.TryGetValue(path, out var existing) &&
                existing.Size == size && existing.LastWriteTicks == ticks &&
                !existing.Work.IsCanceled && !existing.Work.IsFaulted)
                return existing;

            Log.Info($"CRC prepare: starting '{Path.GetFileName(path)}' ({size} bytes)");
            var state = new State
            {
                Size = size,
                LastWriteTicks = ticks,
                Work = Task.Run(() => ComputeAsync(path, size, ticks, _prepareCts?.Token ?? CancellationToken.None))
            };
            States[path] = state;
            return state;
        }
    }

    private static async Task<PreparedFile> ComputeAsync(string path, long expectedSize, long expectedTicks, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        uint crc = 0xFFFFFFFFu;
        long readTotal = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);

        try
        {
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                ReadBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                var read = await fs.ReadAsync(buffer.AsMemory(0, ReadBufferSize), cancellationToken);
                if (read <= 0) break;
                crc = UpdateSlicingBy8(crc, buffer.AsSpan(0, read));
                readTotal += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedSize || info.LastWriteTimeUtc.Ticks != expectedTicks || readTotal != expectedSize)
            throw new IOException($"Transfer source changed while preparing CRC for '{path}'.");

        crc = ~crc;
        started.Stop();
        var mibPerSec = started.Elapsed.TotalSeconds > 0
            ? expectedSize / 1048576d / started.Elapsed.TotalSeconds
            : 0;
        Log.Info($"CRC prepare: ready '{Path.GetFileName(path)}' crc=0x{crc:X8} in {started.Elapsed.TotalMilliseconds:F0}ms ({mibPerSec:F1} MiB/s)");
        return new PreparedFile(crc, expectedSize, expectedTicks);
    }

    // ZIP uses IEEE CRC-32 (polynomial 0xEDB88320). Processing eight bytes per
    // iteration removes the old per-byte managed-loop bottleneck while preserving
    // the exact PKZIP checksum expected by Android's ZipInputStream.
    private static uint UpdateSlicingBy8(uint crc, ReadOnlySpan<byte> data)
    {
        var i = 0;
        var length = data.Length;
        while (i + 8 <= length)
        {
            var first = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i, 4)) ^ crc;
            var second = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(i + 4, 4));
            crc = Tables[7][first & 0xFF] ^
                  Tables[6][(first >> 8) & 0xFF] ^
                  Tables[5][(first >> 16) & 0xFF] ^
                  Tables[4][first >> 24] ^
                  Tables[3][second & 0xFF] ^
                  Tables[2][(second >> 8) & 0xFF] ^
                  Tables[1][(second >> 16) & 0xFF] ^
                  Tables[0][second >> 24];
            i += 8;
        }

        while (i < length)
        {
            crc = Tables[0][(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
            i++;
        }
        return crc;
    }

    private static uint[][] BuildTables()
    {
        var tables = new uint[8][];
        for (var t = 0; t < tables.Length; t++) tables[t] = new uint[256];

        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            tables[0][i] = value;
        }

        for (var t = 1; t < tables.Length; t++)
        {
            for (var i = 0; i < 256; i++)
            {
                var value = tables[t - 1][i];
                tables[t][i] = tables[0][value & 0xFF] ^ (value >> 8);
            }
        }
        return tables;
    }
}
