using System.IO.Compression;

namespace Zr3i.Imagery;

/// <summary>
/// The smallest PNG writer that does the job: 8-bit RGB, no interlacing, no filtering. The BCL has a zlib stream but no PNG encoder, and a
/// picture library would be a new package for forty lines of format.
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildTable();

    /// <param name="rgb">Width x height x 3 bytes, rows from the top.</param>
    public static byte[] Encode(int width, int height, byte[] rgb)
    {
        if (width <= 0 || height <= 0 || rgb.Length != width * height * 3) throw new ArgumentException("The pixel data does not match the size.", nameof(rgb));

        using var png = new MemoryStream();
        png.Write(Signature);

        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8;    // bit depth
        header[9] = 2;    // colour type: RGB
        Chunk(png, "IHDR", header);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];     // filter type 0 (none) then the row
            for (var y = 0; y < height; y++)
            {
                Buffer.BlockCopy(rgb, y * width * 3, row, 1, width * 3);
                zlib.Write(row, 0, row.Length);
            }
        }

        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        s.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = Crc32(typeBytes, data);
        var tail = new byte[4];
        WriteInt(tail, 0, unchecked((int)crc));
        s.Write(tail);
    }

    private static void WriteInt(byte[] b, int at, int value)
    {
        b[at] = (byte)(value >> 24);
        b[at + 1] = (byte)(value >> 16);
        b[at + 2] = (byte)(value >> 8);
        b[at + 3] = (byte)value;
    }

    public static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }

        return table;
    }
}
