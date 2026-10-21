using System.IO.Compression;
using System.Text.RegularExpressions;

namespace HP3052Scanner;

internal static class ScannerDiscovery
{
    public static string[] Parse(string text) => Regex.Matches(text, @"hpaio:/net/HP_LaserJet_3052\?ip=(\d{1,3}(?:\.\d{1,3}){3})", RegexOptions.IgnoreCase)
        .Select(m => m.Groups[1].Value).Where(IsValid).Distinct().ToArray();
    static bool IsValid(string value) { try { AppFiles.ValidateIp(value); return true; } catch { return false; } }
}

internal static class RasterPacking
{
    public static int Bits(string mode) => mode == "Lineart" ? 1 : 8;
    public static int Colors(string mode) => mode is "Gray" or "Lineart" ? 1 : 3;
    public static byte[] Row(byte[] bgr, int width, string mode)
    {
        if (width < 1 || bgr.Length < width * 3) throw new ArgumentException("Некорректная строка изображения.");
        byte[] row = new byte[mode == "Lineart" ? (width + 7) / 8 : width * Colors(mode)];
        for (int x = 0; x < width; x++)
        {
            int i = x * 3;
            int gray = (299 * bgr[i + 2] + 587 * bgr[i + 1] + 114 * bgr[i] + 500) / 1000;
            if (mode == "Lineart") { if (gray >= 128) row[x / 8] |= (byte)(128 >> (x % 8)); }
            else if (mode == "Gray") row[x] = (byte)gray;
            else { row[i] = bgr[i + 2]; row[i + 1] = bgr[i + 1]; row[i + 2] = bgr[i]; }
        }
        return row;
    }

    // PNG Up predictor, one filter byte per row; works on packed monochrome too.
    public static void Write(Stream destination, int width, int height, string mode, Func<int, byte[]> bgrRow, CancellationToken token)
    {
        using var z = new ZLibStream(destination, CompressionLevel.Optimal, true);
        byte[] previous = new byte[mode == "Lineart" ? (width + 7) / 8 : width * Colors(mode)];
        byte[] filtered = new byte[previous.Length + 1];
        filtered[0] = 2;
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            byte[] row = Row(bgrRow(y), width, mode);
            for (int i = 0; i < row.Length; i++) filtered[i + 1] = unchecked((byte)(row[i] - previous[i]));
            z.Write(filtered);
            previous = row;
        }
    }
}
