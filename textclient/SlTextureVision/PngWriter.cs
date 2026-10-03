// SL Texture Vision - part of galatea (https://github.com/davidabrooks/galatea). Same licence terms as the rest of the galatea repository.
using System.IO.Compression;
using LibreMetaverse.Imaging;

namespace SlTextureVision;

/// <summary>Dependency-free PNG encoder (8-bit gray, gray+alpha, RGB or RGBA; filter 0; zlib via System.IO.Compression).</summary>
public static class PngWriter
{
    static readonly uint[] Crc = BuildCrc();
    static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; }
        return t;
    }

    /// <summary>Encode a LibreMetaverse <see cref="ManagedImage"/> (rows top-down, as decoded from JPEG 2000) to PNG bytes.
    /// An alpha channel is kept only when some pixel is not fully opaque.</summary>
    public static byte[] Encode(ManagedImage img)
    {
        bool color = (img.Channels & ManagedImage.ImageChannels.Color) != 0;
        bool alpha = (img.Channels & ManagedImage.ImageChannels.Alpha) != 0 && img.Alpha.Length == img.Width * img.Height && img.Alpha.Any(a => a != 255);
        int bpp = (color ? 3 : 1) + (alpha ? 1 : 0);
        byte colorType = (byte)(color ? (alpha ? 6 : 2) : (alpha ? 4 : 0));
        int w = img.Width, h = img.Height, stride = w * bpp + 1;
        var raw = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int o = y * stride; raw[o++] = 0;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                raw[o++] = img.Red[i];
                if (color) { raw[o++] = img.Green[i]; raw[o++] = img.Blue[i]; }
                if (alpha) raw[o++] = img.Alpha[i];
            }
        }
        return Encode(w, h, colorType, raw);
    }

    /// <summary>Encode already-filtered scanlines (each row prefixed with filter byte 0).</summary>
    public static byte[] Encode(int width, int height, byte colorType, byte[] filteredRows)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)width); BE(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = colorType; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(filteredRows);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type); s.Write(t); s.Write(data);
        uint c = 0xFFFFFFFFu;
        foreach (var b in t) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4]; BE(crc, 0, c ^ 0xFFFFFFFFu); s.Write(crc);
    }

    static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
}
