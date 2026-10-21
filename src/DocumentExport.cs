using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace HP3052Scanner;
internal static class ScanImages
{
    public static Bitmap Load(string path, float? dpi = null)
    {
        using var source = Image.FromFile(path);
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        float dx = dpi ?? source.HorizontalResolution, dy = dpi ?? source.VerticalResolution;
        result.SetResolution(float.IsFinite(dx) && dx > 0 ? dx : 300, float.IsFinite(dy) && dy > 0 ? dy : 300);
        using (var g = Graphics.FromImage(result))
        {
            g.Clear(Color.White);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }

        return result;
    }

    public static void SavePng(Bitmap image, string path, string mode = "Color")
    {
        string temp = path + ".tmp";
        try
        {
            if (mode is "Gray" or "Lineart")
            {
                using var indexed = Indexed(image, mode);
                indexed.Save(temp, ImageFormat.Png);
            }
            else image.Save(temp, ImageFormat.Png);
            File.Move(temp, path, false);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
            }
        }
    }

    static Bitmap Indexed(Bitmap image, string mode)
    {
        var format = mode == "Lineart" ? PixelFormat.Format1bppIndexed : PixelFormat.Format8bppIndexed;
        var result = new Bitmap(image.Width, image.Height, format);
        try
        {
            result.SetResolution(image.HorizontalResolution, image.VerticalResolution);
            var palette = result.Palette;
            for (int i = 0; i < palette.Entries.Length; i++)
            {
                int gray = mode == "Lineart" ? i * 255 : i;
                palette.Entries[i] = Color.FromArgb(gray, gray, gray);
            }
            result.Palette = palette;
            var input = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var output = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, format);
                try
                {
                    var bgr = new byte[image.Width * 3];
                    for (int y = 0; y < image.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(input.Scan0, y * input.Stride), bgr, 0, bgr.Length);
                        byte[] row = RasterPacking.Row(bgr, image.Width, mode);
                        Marshal.Copy(row, 0, IntPtr.Add(output.Scan0, y * output.Stride), row.Length);
                    }
                }
                finally { result.UnlockBits(output); }
            }
            finally { image.UnlockBits(input); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public static Bitmap Thumbnail(Bitmap image, Size size)
    {
        var result = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.WhiteSmoke);
        double ratio = Math.Min((size.Width - 10.0) / image.Width, (size.Height - 10.0) / image.Height);
        int w = Math.Max(1, (int)(image.Width * ratio)), h = Math.Max(1, (int)(image.Height * ratio));
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        return result;
    }
    public static Bitmap? Reduced(Bitmap image)
    {
        if (Math.Max(image.Width, image.Height) <= 2048) return null;
        double ratio = 2048.0 / Math.Max(image.Width, image.Height);
        var result = new Bitmap(Math.Max(1, (int)(image.Width * ratio)), Math.Max(1, (int)(image.Height * ratio)));
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, 0, 0, result.Width, result.Height);
        return result;
    }

    public static void SaveJpeg(Bitmap image, string path, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        image.Save(path, codec, p);
    }

    public static void AtomicImage(string source, string dest, string format, int quality, bool overwrite)
    {
        string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (format == "PNG") { File.Copy(source, temp); File.Move(temp, dest, overwrite); return; }
            using (var b = format == "JPG" ? Load(source) : new Bitmap(source))
            {
                if (format == "JPG")
                    SaveJpeg(b, temp, quality);
                else if (format == "TIFF")
                {
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid);
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression,
                        (long)(b.PixelFormat == PixelFormat.Format1bppIndexed ? EncoderValue.CompressionCCITT4 : EncoderValue.CompressionLZW));
                    b.Save(temp, codec, parameters);
                }
                else
                    b.Save(temp, format switch
                    {
                        "PNG" => ImageFormat.Png,
                        "TIFF" => ImageFormat.Tiff,
                        _ => ImageFormat.Bmp
                    });
            }

            File.Move(temp, dest, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch
            {
            }
        }
    }
}

internal static class PdfWriter
{
    public static void Write(string dest, IReadOnlyList<PageRecord> pages, Func<PageRecord, string> source, bool lossless, int quality, bool overwrite, CancellationToken token)
    {
        PdfStreamWriter.Write(dest, pages.Count, i =>
        {
            var page = pages[i];
            using var b = ScanImages.Load(source(page));
            string packed = dest + "." + Guid.NewGuid().ToString("N") + ".image-tmp";
            try
            {
                if (lossless)
                {
                    using var compressed = new FileStream(packed, FileMode.CreateNew, FileAccess.Write);
                    var bits = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        var row = new byte[b.Width * 3];
                        RasterPacking.Write(compressed, b.Width, b.Height, page.Mode, y =>
                        {
                            Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                            return row;
                        }, token);
                    }
                    finally
                    {
                        b.UnlockBits(bits);
                    }
                }
                else
                    ScanImages.SaveJpeg(b, packed, quality);
                return new EncodedPdfPage(packed, b.Width, b.Height, page.DpiX, page.DpiY, lossless ? "FlateDecode" : "DCTDecode", true,
                    lossless && RasterPacking.Colors(page.Mode) == 1 ? "DeviceGray" : "DeviceRGB", lossless ? RasterPacking.Bits(page.Mode) : 8, lossless);
            }
            catch
            {
                try
                {
                    File.Delete(packed);
                }
                catch
                {
                }

                throw;
            }
        }, overwrite, token);
    }
}
