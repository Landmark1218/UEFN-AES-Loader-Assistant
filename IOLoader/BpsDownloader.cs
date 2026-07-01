using System.IO.Compression;
using System.Security.Cryptography;

namespace UEFNMapInstaller;

/// <summary>
/// Parses a BPS (BuildPatchServices) manifest,
/// downloads chunks, and reconstructs files.
/// </summary>
internal static class BpsDownloader
{
    private const uint ManifestMagic = 0x44BEC00C;
    private const uint ChunkMagic    = 0xB1FE3AA2;

    public static async Task<List<string>> ReconstructFilesAsync(
        string manifestPath,
        string baseUrl,
        string outputDir,
        string channel = "alt",
        CancellationToken ct = default)
    {
        var raw      = await File.ReadAllBytesAsync(manifestPath, ct);
        var manifest = ParseManifest(raw);

        var cacheDir = Path.Combine(outputDir, ".chunks");
        Directory.CreateDirectory(cacheDir);

        var written = new List<string>();
        foreach (var file in manifest.Files)
        {
            var filename = Path.GetFileName(file.Filename);
            var target   = Path.Combine(outputDir, filename);

            Console.WriteLine($"  [{file.FileSize:N0} B] {filename}");

            using var sha1 = SHA1.Create();
            await using var fs = File.Create(target);

            foreach (var part in file.ChunkParts)
            {
                var chunkData = await FetchChunkAsync(baseUrl, part.Chunk, cacheDir, channel, ct);
                var piece = new byte[part.Size];
                Array.Copy(chunkData, (int)part.Offset, piece, 0, (int)part.Size);
                await fs.WriteAsync(piece, 0, piece.Length, ct);
                sha1.TransformBlock(piece, 0, piece.Length, null, 0);
            }

            sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var digest   = sha1.Hash!.ToHexString();
            if (!string.Equals(digest, file.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(target);
                throw new InvalidDataException(
                    $"SHA1 mismatch: {filename} (got={digest}, expected={file.Sha1})");
            }

            written.Add(target);
        }

        try { Directory.Delete(cacheDir, recursive: true); } catch { /* ignore */ }
        return written;
    }

    // ── Manifest parsing (without SpanReader) ──────────────────────

    private static BpsManifest ParseManifest(byte[] raw)
    {
        var r = new ByteReader(raw);

        uint magic = r.ReadUInt32();
        if (magic != ManifestMagic)
            throw new InvalidDataException($"Invalid BPS manifest magic: 0x{magic:X8}");

        int  headerSize       = r.ReadInt32();
        int  uncompressedSize = r.ReadInt32();
        int  compressedSize   = r.ReadInt32();
        r.Skip(20);
        byte storedAs = r.ReadByte();
        if (headerSize > 37) r.ReadInt32(); // featureLevel
        r.Seek(headerSize);

        byte[] bodyRaw = r.ReadBytes((storedAs & 1) != 0 ? compressedSize : uncompressedSize);
        byte[] body;
        if ((storedAs & 1) != 0)
        {
            using var ms      = new MemoryStream(bodyRaw);
            ms.Seek(2, SeekOrigin.Begin);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs   = new MemoryStream();
            deflate.CopyTo(outMs);
            body = outMs.ToArray();
        }
        else
        {
            body = bodyRaw;
        }

        if (body.Length != uncompressedSize)
            throw new InvalidDataException("BPS manifest decompressed size mismatch");

        var br = new ByteReader(body);

        // Skip meta section
        int metaStart = br.Position;
        int metaSize  = br.ReadInt32();
        br.Seek(metaStart + metaSize);

        // Chunk section
        int chunkStart    = br.Position;
        int chunkDataSize = br.ReadInt32();
        br.ReadByte(); // version
        int chunkCount = br.ReadInt32();

        var chunks = new BpsChunk[chunkCount];
        for (int i = 0; i < chunkCount; i++) chunks[i] = new BpsChunk { Guid = br.ReadGuid() };
        for (int i = 0; i < chunkCount; i++) chunks[i].Hash = br.ReadUInt64();
        for (int i = 0; i < chunkCount; i++) chunks[i].Sha1 = br.ReadBytes(20).ToHexString();
        for (int i = 0; i < chunkCount; i++) chunks[i].Group = br.ReadByte();
        for (int i = 0; i < chunkCount; i++) chunks[i].WindowSize = br.ReadUInt32();
        for (int i = 0; i < chunkCount; i++) chunks[i].FileSize = br.ReadUInt64();
        br.Seek(chunkStart + chunkDataSize);

        var chunkLookup = chunks.ToDictionary(c => c.Guid);

        // File section
        int filesStart    = br.Position;
        int filesDataSize = br.ReadInt32();
        br.ReadByte(); // filesVersion
        int fileCount = br.ReadInt32();

        var files = new BpsFileRecord[fileCount];
        for (int i = 0; i < fileCount; i++) files[i] = new BpsFileRecord { Filename = br.ReadFString() };
        for (int i = 0; i < fileCount; i++) files[i].SymlinkTarget = br.ReadFString();
        for (int i = 0; i < fileCount; i++) files[i].Sha1 = br.ReadBytes(20).ToHexString();
        for (int i = 0; i < fileCount; i++) files[i].Flags = br.ReadByte();
        for (int i = 0; i < fileCount; i++)
        {
            int tagCount = br.ReadInt32();
            for (int j = 0; j < tagCount; j++) br.ReadFString();
        }
        for (int i = 0; i < fileCount; i++)
        {
            int  partCount = br.ReadInt32();
            long fileSize  = 0;
            for (int j = 0; j < partCount; j++)
            {
                br.ReadUInt32();
                var guid   = br.ReadGuid();
                var offset = br.ReadUInt32();
                var size   = br.ReadUInt32();
                if (!chunkLookup.TryGetValue(guid, out var chunk))
                    throw new InvalidDataException($"Chunk GUID not found: {guid}");
                files[i].ChunkParts.Add(new BpsChunkPart { Chunk = chunk, Offset = offset, Size = size });
                fileSize += size;
            }
            files[i].FileSize = fileSize;
        }

        return new BpsManifest { Files = files };
    }

    // ── Chunk download ─────────────────────────────────────────────

    private static async Task<byte[]> FetchChunkAsync(
        string baseUrl, BpsChunk chunk, string cacheDir, string channel, CancellationToken ct)
    {
        var cachePath = Path.Combine(cacheDir, $"{chunk.Group:D2}_{chunk.Hash:X16}_{chunk.GuidHex}.chunk");

        if (!File.Exists(cachePath) || new FileInfo(cachePath).Length != (long)chunk.FileSize)
        {
            var url = $"{baseUrl.TrimEnd('/')}/{Uri.EscapeDataString(channel)}/ChunksV4/{chunk.Group:D2}/{chunk.Hash:X16}_{chunk.GuidHex}.chunk";
            Console.Write($"    chunk: {chunk.GuidHex[..8]}... ");
            await EpicHttp.DownloadFileAsync(url, cachePath, null, null, ct);
            Console.WriteLine("OK");
        }

        return ParseChunk(await File.ReadAllBytesAsync(cachePath, ct));
    }

    private static byte[] ParseChunk(byte[] raw)
    {
        var r     = new ByteReader(raw);
        uint magic = r.ReadUInt32();
        if (magic != ChunkMagic)
            throw new InvalidDataException($"Invalid BPS chunk magic: 0x{magic:X8}");

        r.ReadUInt32();                          // version
        int headerSize      = r.ReadInt32();
        int compressedSize  = r.ReadInt32();
        r.Skip(16);                              // guid
        r.Skip(8);                               // hash
        byte storedAs = r.ReadByte();
        r.Seek(headerSize);                      // Seek to end of header

        byte[] payload = r.ReadBytes(compressedSize);

        if ((storedAs & 1) != 0)
        {
            using var ms      = new MemoryStream(payload);
            ms.Seek(2, SeekOrigin.Begin);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs   = new MemoryStream();
            deflate.CopyTo(outMs);
            return outMs.ToArray();
        }
        return payload;
    }

    // ── Data models ────────────────────────────────────────────────

    private sealed class BpsManifest  { public BpsFileRecord[] Files { get; set; } = []; }

    private sealed class BpsFileRecord
    {
        public string Filename       { get; set; } = "";
        public string SymlinkTarget  { get; set; } = "";
        public string Sha1           { get; set; } = "";
        public byte   Flags          { get; set; }
        public long   FileSize       { get; set; }
        public List<BpsChunkPart> ChunkParts { get; } = [];
    }

    private sealed class BpsChunk
    {
        public string Guid       { get; set; } = "";
        public string GuidHex    => Guid.Replace("-", "").ToUpperInvariant();
        public ulong  Hash       { get; set; }
        public string Sha1       { get; set; } = "";
        public byte   Group      { get; set; }
        public uint   WindowSize { get; set; }
        public ulong  FileSize   { get; set; }
    }

    private sealed class BpsChunkPart
    {
        public BpsChunk Chunk  { get; set; } = null!;
        public uint     Offset { get; set; }
        public uint     Size   { get; set; }
    }

    // ── ByteReader (non-ref struct, usable in async) ──────────────────

    private sealed class ByteReader
    {
        private readonly byte[] _data;
        private int _pos;
        public int Position => _pos;

        public ByteReader(byte[] data) { _data = data; _pos = 0; }

        public void Seek(int offset) => _pos = offset;
        public void Skip(int n) => _pos += n;

        public byte   ReadByte()  => _data[_pos++];
        public int    ReadInt32() { var v = BitConverter.ToInt32(_data, _pos); _pos += 4; return v; }
        public uint   ReadUInt32(){ var v = BitConverter.ToUInt32(_data, _pos); _pos += 4; return v; }
        public ulong  ReadUInt64(){ var v = BitConverter.ToUInt64(_data, _pos); _pos += 8; return v; }

        public byte[] ReadBytes(int n)
        {
            var buf = new byte[n];
            Array.Copy(_data, _pos, buf, 0, n);
            _pos += n;
            return buf;
        }

        public string ReadFString()
        {
            int size = ReadInt32();
            if (size == 0) return "";
            if (size > 0)
            {
                var bytes = ReadBytes(size);
                return System.Text.Encoding.UTF8.GetString(
                    bytes[^1] == 0 ? bytes[..^1] : bytes);
            }
            var u16 = ReadBytes(-size * 2);
            return System.Text.Encoding.Unicode.GetString(
                u16.Length >= 2 && u16[^1] == 0 && u16[^2] == 0 ? u16[..^2] : u16);
        }

        public string ReadGuid()
        {
            var raw       = ReadBytes(16);
            var converted = new byte[16];
            for (int i = 0; i < 4; i++)
            {
                uint be = (uint)(raw[i*4]<<24 | raw[i*4+1]<<16 | raw[i*4+2]<<8 | raw[i*4+3]);
                converted[i*4]   = (byte)(be & 0xFF);
                converted[i*4+1] = (byte)((be >>  8) & 0xFF);
                converted[i*4+2] = (byte)((be >> 16) & 0xFF);
                converted[i*4+3] = (byte)((be >> 24) & 0xFF);
            }
            return new Guid(converted).ToString("D");
        }
    }
}
