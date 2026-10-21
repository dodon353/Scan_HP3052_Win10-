using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace HP3052Scanner;

internal static class PageProcessing
{
    public static Bitmap PreviewSource(string file, int maxSide = 1400)
    {
        using var full = ScanImages.Load(file);
        double ratio = Math.Min(1, (double)maxSide / Math.Max(full.Width, full.Height));
        var image = new Bitmap(Math.Max(1, (int)Math.Round(full.Width * ratio)), Math.Max(1, (int)Math.Round(full.Height * ratio)), PixelFormat.Format24bppRgb);
        try
        {
            image.SetResolution(full.HorizontalResolution * image.Width / full.Width, full.VerticalResolution * image.Height / full.Height);
            using var g = Graphics.FromImage(image);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, image.Width, image.Height);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public static GrayFrame Sample(Bitmap image, CancellationToken token)
    {
        double ratio = Math.Min(1, 1000.0 / Math.Max(image.Width, image.Height));
        using var small = new Bitmap(Math.Max(1, (int)Math.Round(image.Width * ratio)), Math.Max(1, (int)Math.Round(image.Height * ratio)), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, 0, 0, small.Width, small.Height);
        }
        var gray = new byte[small.Width * small.Height];
        var bits = small.LockBits(new Rectangle(0, 0, small.Width, small.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[small.Width * 3];
            for (int y = 0; y < small.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), row, 0, row.Length);
                for (int x = 0; x < small.Width; x++) gray[y * small.Width + x] = PageProcessingCore.Luma(row[x * 3 + 2], row[x * 3 + 1], row[x * 3]);
            }
        }
        finally { small.UnlockBits(bits); }
        return new(small.Width, small.Height, gray);
    }

    public static ProcessingPlan Analyze(Bitmap image, ProcessingOptions options, CancellationToken token)
    {
        var sample = Sample(image, token);
        return PageProcessingCore.Analyze(sample, options, image.HorizontalResolution * sample.Width / image.Width, image.VerticalResolution * sample.Height / image.Height, token);
    }

    public static Bitmap Apply(Bitmap original, ProcessingPlan plan, ProcessingOptions options, CancellationToken token)
    {
        options.Validate(); token.ThrowIfCancellationRequested();
        var size = PageProcessingCore.RotatedSize(original.Width, original.Height, plan.Angle);
        var crop = plan.Bounds(original.Width, original.Height);
        var result = Math.Abs(plan.Angle) < .001
            ? original.Clone(new Rectangle(crop.X, crop.Y, crop.Width, crop.Height), PixelFormat.Format24bppRgb)
            : new Bitmap(crop.Width, crop.Height, PixelFormat.Format24bppRgb);
        try
        {
            result.SetResolution(original.HorizontalResolution, original.VerticalResolution);
            if (Math.Abs(plan.Angle) >= .001)
            {
                using var g = Graphics.FromImage(result);
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TranslateTransform(size.Width / 2f - crop.X, size.Height / 2f - crop.Y);
                g.RotateTransform((float)plan.Angle);
                g.TranslateTransform(-original.Width / 2f, -original.Height / 2f);
                g.DrawImage(original, new Rectangle(0, 0, original.Width, original.Height), 0, 0, original.Width, original.Height, GraphicsUnit.Pixel);
            }
            token.ThrowIfCancellationRequested();
            if (options.Readability || options.RemoveSpecks) Enhance(result, options, token);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    static void Enhance(Bitmap image, ProcessingOptions options, CancellationToken token)
    {
        var background = new PaperBackground(Sample(image, token), token);
        var bits = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            int length = image.Width * 3;
            byte[] previous = new byte[length], current = new byte[length], next = new byte[length], output = new byte[length];
            void Read(int y, byte[] row) => Marshal.Copy(IntPtr.Add(bits.Scan0, Math.Clamp(y, 0, image.Height - 1) * bits.Stride), row, 0, length);
            Read(0, previous); Read(0, current); Read(1, next);
            for (int y = 0; y < image.Height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < image.Width; x++)
                {
                    int i = x * 3;
                    double paper = background.At((x + .5) / image.Width, (y + .5) / image.Height);
                    bool speck = options.RemoveSpecks && x > 0 && x < image.Width - 1 && y > 0 && y < image.Height - 1
                        && PageProcessingCore.Luma(current[i + 2], current[i + 1], current[i]) < paper - 30;
                    if (speck)
                        for (int dy = -1; dy <= 1 && speck; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                var row = dy < 0 ? previous : dy > 0 ? next : current;
                                int n = i + dx * 3;
                                if (PageProcessingCore.Luma(row[n + 2], row[n + 1], row[n]) < paper - 10) { speck = false; break; }
                            }
                    (byte R, byte G, byte B) rgb = speck ? ((byte)255, (byte)255, (byte)255)
                        : options.Readability ? PageProcessingCore.Readable(current[i + 2], current[i + 1], current[i], paper, background.Black, options.Strong)
                        : (current[i + 2], current[i + 1], current[i]);
                    output[i] = rgb.B; output[i + 1] = rgb.G; output[i + 2] = rgb.R;
                }
                Marshal.Copy(output, 0, IntPtr.Add(bits.Scan0, y * bits.Stride), length);
                var reusable = previous; previous = current; current = next; next = reusable; Read(y + 2, next);
            }
        }
        finally { image.UnlockBits(bits); }
    }

    public static string ResultMode(string originalMode, ProcessingPlan plan) => originalMode == "Lineart" && Math.Abs(plan.Angle) > .001 ? "Gray" : originalMode;
}
