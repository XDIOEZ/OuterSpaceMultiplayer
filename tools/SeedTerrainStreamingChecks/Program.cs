using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FreeWorldMultiplayer;

internal static class Program
{
    private static int checks;
    private static readonly string[] Grounds = {
        "ground", "water", "water:shallow", "主地形", "雪", "🌍", "e\u0301",
        "\u00e9", "Mixed CASE", "value\\escaped", " ", "a\uD800", "a\uDC00",
        "0", "NUL\0inside"
    };
    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("FAILED: " + reason);
        checks++;
    }
    private static string OldHash(byte[] encoded)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(encoded)).Replace("-", "").ToLowerInvariant();
    }
    private static byte[] OldEncode(string[,] tiles)
    {
        // Frozen production NativeSeedMapCodec.EncodeTiles before this
        // change. Keep this independent: both encoded cache bytes AND
        // checksums must stay compatible with already-installed clients.
        const int maxCells = 1024 * 1024;
        if (tiles == null || tiles.GetLength(0) <= 0 || tiles.GetLength(1) <= 0 ||
            tiles.Length > maxCells || tiles.GetLowerBound(0) != 0 ||
            tiles.GetLowerBound(1) != 0)
            throw new InvalidDataException("Invalid bounded terrain matrix.");
        var paletteSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (string value in tiles)
        {
            if (String.IsNullOrEmpty(value) || value.Length > 256)
                throw new InvalidDataException("Native terrain contains a missing or oversized ground ID.");
            paletteSet.Add(value);
        }
        if (paletteSet.Count > 4096)
            throw new InvalidDataException("Seed terrain has too many ground definitions.");
        var palette = paletteSet.ToList();
        palette.Sort(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(0x31544D46);
                writer.Write(tiles.GetLength(1));
                writer.Write(tiles.GetLength(0));
                writer.Write(palette.Count);
                foreach (string value in palette)
                {
                    byte[] text = Encoding.UTF8.GetBytes(value);
                    writer.Write(text.Length);
                    writer.Write(text);
                    indices.Add(value, indices.Count);
                }
                foreach (string value in tiles) writer.Write(indices[value]);
            }
            return stream.ToArray();
        }
    }
    private static string Outcome(Func<object> call, out object value)
    {
        value = null;
        try { value = call(); return "accepted"; }
        catch (Exception error) { return error.GetType().Name + ": " + error.Message; }
    }
    private static void Compare(string[,] tiles, string label)
    {
        object oldData, newData, streamedHash;
        string oldResult = Outcome(() => OldEncode(tiles), out oldData);
        string newResult = Outcome(() => SeedTerrainStreamingCodec.EncodeTiles(tiles), out newData);
        string hashResult = Outcome(() => SeedTerrainStreamingCodec.HashTiles(tiles), out streamedHash);
        Check(oldResult == newResult && oldResult == hashResult,
            label + " identical input validation and exception text, old=" +
            oldResult + " encoded=" + newResult + " hashed=" + hashResult);
        if (oldResult != "accepted") return;
        var legacyBytes = (byte[])oldData;
        var encodedBytes = (byte[])newData;
        Check(legacyBytes.SequenceEqual(encodedBytes),
            label + " identical FM T1 palette/index encoding bytes");
        string expectedHash = OldHash(legacyBytes);
        Check(String.Equals(expectedHash, (string)streamedHash, StringComparison.Ordinal),
            label + " same existing SHA256 content identity");
        Check(((string)streamedHash).Length == 64 &&
            ((string)streamedHash).All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f'),
            label + " exact lowercase 64-character legacy hash format");
    }
    private static long Allocated(Action action, int count)
    {
        for (int i = 0; i < 5; i++) action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < count; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static string[,] Make(int width, int height, Func<int, int, string> item)
    {
        var result = new string[height, width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++) result[y, x] = item(x, y);
        return result;
    }
    private static void VerifyRawWireFormat()
    {
        var tiles = new string[,] { { "snow", "soil" }, { "soil", "snow" } };
        byte[] data = SeedTerrainStreamingCodec.EncodeTiles(tiles);
        using (var ms = new MemoryStream(data))
        using (var reader = new BinaryReader(ms, Encoding.UTF8))
        {
            Check(reader.ReadInt32() == 0x31544D46,
                "existing cache magic is unchanged");
            Check(reader.ReadInt32() == 2 && reader.ReadInt32() == 2,
                "existing cache width/height order unchanged");
            Check(reader.ReadInt32() == 2, "legacy sorted palette count");
            int n = reader.ReadInt32();
            Check(Encoding.UTF8.GetString(reader.ReadBytes(n)) == "snow",
                "palette sorted by StringComparer.Ordinal instead of first seen");
            n = reader.ReadInt32();
            Check(Encoding.UTF8.GetString(reader.ReadBytes(n)) == "soil",
                "second palette element encodes UTF8 explicitly");
            int[] expected = { 0, 1, 1, 0 };
            foreach (int index in expected)
                Check(reader.ReadInt32() == index,
                    "row-major terrain index remains little-endian int32");
            Check(ms.Position == ms.Length, "no cache header or trailing bytes added");
        }
        Compare(tiles, "independently decoded cache layout");
    }

    private static int Main()
    {
        try
        {
            Compare(null, "null terrain");
            Compare(new string[0, 2], "zero height");
            Compare(new string[1, 0], "zero width");
            Compare(new string[1025, 1024], "over 1048576 bounded cells");
            Compare(new string[1, 1], "unfilled terrain contains null");
            Compare(new string[,] { { "" } }, "empty ground ID");
            Compare(new string[,] { { new string('x', 257) } },
                "oversized UTF16 ground identifier");
            Compare(new string[,] { { new string('x', 256) } },
                "maximum supported identifier");
            Compare(new string[,] { { "\0" } },
                "embedded NUL codepoint encodes deterministically");
            Compare(new string[,] { { "water" } }, "single-cell cache");
            Compare(new string[,] { { "A", "a", "a", "A" } },
                "ordinal case-sensitive palette and repeat references");
            Compare(new string[,] { { "e\u0301", "\u00e9", "e\u0301" } },
                "Unicode normalization forms retain different palette items");
            Compare(new string[,] { { "🌍", "中文", "e\u0301", "a\uD800" } },
                "UTF8 multilingual, surrogate fallback and emoji bytes");
            VerifyRawWireFormat();

            var manyIds = Make(65, 65, (x, y) => "tile-" + (y * 65 + x));
            Compare(manyIds, "4225 distinct palette values exceed the native limit");
            var paletteBoundary = Make(64, 64, (x, y) => "tile-" + (y * 64 + x));
            Compare(paletteBoundary, "4096 distinct palette entries accepted");
            var baseline = Make(1024, 1024, (x, y) =>
                ((x + y * 19) % 127) == 0 ? "sea" : "rock");
            Compare(baseline, "maximum allowed 1MiB-cell terrain with two tile IDs");

            var rnd = new Random(20261018);
            for (int iteration = 0; iteration < 2700; iteration++)
            {
                int width = rnd.Next(1, 40), height = rnd.Next(1, 34);
                var values = Make(width, height,
                    (x, y) => Grounds[rnd.Next(Grounds.Length)]);
                if (rnd.Next(0, 29) == 0)
                    values[rnd.Next(height), rnd.Next(width)] = null;
                if (rnd.Next(0, 29) == 0)
                    values[rnd.Next(height), rnd.Next(width)] = new string('x', 257);
                Compare(values, "deterministic random terrain and invalid cell " + iteration);
            }

            // Content hashes are used as both the baseline cache key and the
            // final after-override checksum. Changing one tile must change
            // the digest; restoring it must recover the original digest.
            var normal = Make(128, 96, (x, y) => (x + y) % 5 == 0 ? "sea" : "grass");
            string before = SeedTerrainStreamingCodec.HashTiles(normal);
            normal[90, 110] = "sand";
            string after = SeedTerrainStreamingCodec.HashTiles(normal);
            Check(before != after, "one ground overwrite changes cached checksum");
            Compare(normal, "override hash still matches legacy serialized terrain");
            normal[90, 110] = "sea";
            Check(before == SeedTerrainStreamingCodec.HashTiles(normal),
                "reverting an override restores original checksum identity");

            // Measure the old SHA256(EncodeTiles()) path against the linked
            // new production streaming writer, excluding native map factory
            // and game loading. The old path materializes a ~1MiB buffer for
            // each 512x512 map; the new writer hashes bounded streaming data.
            const int frames = 70, benchmarkWidth = 512, benchmarkHeight = 512;
            var medium = Make(benchmarkWidth, benchmarkHeight,
                (x, y) => Grounds[(x * 17 + y * 13) % Grounds.Length]);
            Action oldHash = () => GC.KeepAlive(OldHash(OldEncode(medium)));
            Action newHash = () => GC.KeepAlive(SeedTerrainStreamingCodec.HashTiles(medium));
            string reference = OldHash(OldEncode(medium));
            string actual = SeedTerrainStreamingCodec.HashTiles(medium);
            Check(reference == actual, "large map hash identity must remain exactly unchanged");
            long originalBytes = Allocated(oldHash, frames);
            long streamingBytes = Allocated(newHash, frames);
            Check(originalBytes > streamingBytes &&
                originalBytes - streamingBytes > (long)frames * 512 * 1024,
                "streaming SHA256 eliminates the large intermediate terrain byte array");
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < 15; i++) oldHash();
            long legacyTicks = clock.ElapsedTicks;
            clock.Restart();
            for (int i = 0; i < 15; i++) newHash();
            long streamedTicks = clock.ElapsedTicks;
            Console.WriteLine("INFO: " + frames + " hash checks of " +
                benchmarkWidth + "x" + benchmarkHeight + " SEED terrain: old encode-then-SHA256 " +
                originalBytes + " B, linked production streaming SHA256 " +
                streamingBytes + " B (managed bytes, warmed .NET8)");
            Console.WriteLine("INFO: 15 synthetic hashes measured old " +
                legacyTicks + " vs streaming " + streamedTicks +
                " stopwatch ticks (not a game CPU/FPS benchmark)");
            Console.WriteLine("PASS: " + checks +
                " linked SEED terrain cache bytes/hash compatibility checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
