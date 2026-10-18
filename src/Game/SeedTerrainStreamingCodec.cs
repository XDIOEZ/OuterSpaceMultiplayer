using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FreeWorldMultiplayer
{
    // Use one canonical FM T1 palette/index writer for both the stored cache
    // representation and its SHA-256 identity. A large ground map can contain
    // 1,048,576 cells: HashTiles must not materialize an extra 4MiB byte[]
    // solely to compute the checksum of the already-known terrain.
    internal static class SeedTerrainStreamingCodec
    {
        private const int MaxCells = 1024 * 1024;
        private const int MaxPalette = 4096;
        private const int Magic = 0x31544D46;
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private static List<string> Palette(string[,] tiles)
        {
            if (tiles == null || tiles.GetLength(0) <= 0 || tiles.GetLength(1) <= 0
                || tiles.Length > MaxCells || tiles.GetLowerBound(0) != 0
                || tiles.GetLowerBound(1) != 0)
                throw new InvalidDataException("Invalid bounded terrain matrix.");

            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in tiles)
            {
                if (String.IsNullOrEmpty(value) || value.Length > 256)
                    throw new InvalidDataException("Native terrain contains a missing or oversized ground ID.");
                unique.Add(value);
            }
            if (unique.Count > MaxPalette)
                throw new InvalidDataException("Seed terrain has too many ground definitions.");

            var palette = new List<string>(unique);
            palette.Sort(StringComparer.Ordinal);
            return palette;
        }

        private static void WriteTiles(BinaryWriter writer, string[,] tiles, List<string> palette)
        {
            var indices = new Dictionary<string, int>(StringComparer.Ordinal);
            writer.Write(Magic);
            writer.Write(tiles.GetLength(1));
            writer.Write(tiles.GetLength(0));
            writer.Write(palette.Count);
            foreach (string value in palette)
            {
                byte[] text = Utf8.GetBytes(value);
                writer.Write(text.Length);
                writer.Write(text);
                indices.Add(value, indices.Count);
            }
            foreach (string value in tiles) writer.Write(indices[value]);
        }

        internal static byte[] EncodeTiles(string[,] tiles)
        {
            List<string> palette = Palette(tiles);
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Utf8, true))
                    WriteTiles(writer, tiles, palette);
                return stream.ToArray();
            }
        }

        internal static string HashTiles(string[,] tiles)
        {
            List<string> palette = Palette(tiles);
            using (var sha = SHA256.Create())
            using (var crypto = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            using (var buffered = new BufferedStream(crypto, 32768))
            {
                using (var writer = new BinaryWriter(buffered, Utf8, true))
                {
                    WriteTiles(writer, tiles, palette);
                    writer.Flush();
                }
                buffered.Flush();
                crypto.FlushFinalBlock();
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
