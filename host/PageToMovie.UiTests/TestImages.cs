namespace PageToMovie.UiTests;

/// <summary>
/// Image fixtures built in memory, so an upload test exercises the real image path without
/// carrying a binary file around. Shared: character and location look uploads both need one.
/// </summary>
public static class TestImages
{
    /// <summary>Minimal valid RGBA PNG filled with one colour.</summary>
    public static byte[] TinyPng(int w, int h, byte r = 200, byte g = 150, byte b = 120)
    {
        using var ms = new MemoryStream();
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length); if (BitConverter.IsLittleEndian) Array.Reverse(len);
            ms.Write(len);
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes); ms.Write(data);
            var crc = Crc32(typeBytes.Concat(data).ToArray());
            var crcBytes = BitConverter.GetBytes(crc); if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
            ms.Write(crcBytes);
        }
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BitConverter.GetBytes(w).Reverse().ToArray().CopyTo(ihdr, 0);
        BitConverter.GetBytes(h).Reverse().ToArray().CopyTo(ihdr, 4);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk("IHDR", ihdr);
        var raw = new byte[h * (1 + w * 4)];
        for (var y = 0; y < h; y++)
        {
            var o = y * (1 + w * 4);
            for (var x = 0; x < w; x++)
            {
                raw[o + 1 + (x * 4)] = r;
                raw[o + 2 + (x * 4)] = g;
                raw[o + 3 + (x * 4)] = b;
                raw[o + 4 + (x * 4)] = 255;
            }
        }
        using var z = new MemoryStream();
        using (var zs = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zs.Write(raw);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }
}
