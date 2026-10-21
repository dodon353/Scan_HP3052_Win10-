namespace HP3052Scanner;

internal sealed record ProcessingOptions(bool Deskew = true, bool Crop = true, bool Readability = true, bool Strong = false, bool RemoveSpecks = false, double MarginMm = 3)
{
    public void Validate()
    {
        if (!double.IsFinite(MarginMm) || MarginMm < 0 || MarginMm > 20) throw new ArgumentException("Поле должно быть от 0 до 20 мм.");
    }
}
internal readonly record struct PixelBox(int X, int Y, int Width, int Height);
internal sealed record ProcessingPlan(double Angle, double Left, double Top, double Right, double Bottom, bool HasContent, string Note)
{
    public PixelBox Bounds(int width, int height)
    {
        var size = PageProcessingCore.RotatedSize(width, height, Angle);
        int left = Math.Clamp((int)Math.Floor(Left * size.Width), 0, size.Width - 1);
        int top = Math.Clamp((int)Math.Floor(Top * size.Height), 0, size.Height - 1);
        int right = Math.Clamp((int)Math.Ceiling(Right * size.Width), left + 1, size.Width);
        int bottom = Math.Clamp((int)Math.Ceiling(Bottom * size.Height), top + 1, size.Height);
        return new(left, top, right - left, bottom - top);
    }
}
internal sealed class GrayFrame
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public GrayFrame(int width, int height, byte[] pixels)
    {
        if (width < 1 || height < 1 || pixels.Length != checked(width * height)) throw new ArgumentException("Некорректное изображение анализа.");
        Width = width; Height = height; Pixels = pixels;
    }
}

internal sealed class PaperBackground
{
    readonly double[] values;
    readonly int columns, rows, width, height;
    public int Black { get; }
    public int Paper { get; }
    public PaperBackground(GrayFrame frame, CancellationToken token)
    {
        width = frame.Width; height = frame.Height;
        columns = Math.Max(1, (width + 63) / 64); rows = Math.Max(1, (height + 63) / 64);
        values = new double[columns * rows];
        var global = new int[256];
        foreach (byte p in frame.Pixels) global[p]++;
        Black = Math.Min(100, Percentile(global, frame.Pixels.Length, .005));
        Paper = Percentile(global, frame.Pixels.Length, .9);
        for (int gy = 0; gy < rows; gy++)
            for (int gx = 0; gx < columns; gx++)
            {
                token.ThrowIfCancellationRequested();
                var histogram = new int[256]; int count = 0;
                for (int y = gy * height / rows; y < (gy + 1) * height / rows; y++)
                    for (int x = gx * width / columns; x < (gx + 1) * width / columns; x++)
                    { histogram[frame.Pixels[y * width + x]]++; count++; }
                values[gy * columns + gx] = Math.Clamp(Percentile(histogram, count, .9), 96, 255);
            }
    }
    static int Percentile(int[] histogram, int count, double fraction)
    {
        int limit = Math.Max(1, (int)Math.Ceiling(count * fraction)), sum = 0;
        for (int i = 0; i < 256; i++) { sum += histogram[i]; if (sum >= limit) return i; }
        return 255;
    }
    public double At(double normalizedX, double normalizedY)
    {
        double x = Math.Clamp(normalizedX * columns - .5, 0, columns - 1), y = Math.Clamp(normalizedY * rows - .5, 0, rows - 1);
        int l = (int)x, t = (int)y, r = Math.Min(l + 1, columns - 1), b = Math.Min(t + 1, rows - 1);
        double fx = x - l, fy = y - t;
        return (values[t * columns + l] * (1 - fx) + values[t * columns + r] * fx) * (1 - fy)
            + (values[b * columns + l] * (1 - fx) + values[b * columns + r] * fx) * fy;
    }
}

internal static class PageProcessingCore
{
    public static byte Luma(byte r, byte g, byte b) => (byte)((299 * r + 587 * g + 114 * b + 500) / 1000);
    public static (int Width, int Height) RotatedSize(int width, int height, double angle)
    {
        if (width < 1 || height < 1 || !double.IsFinite(angle) || Math.Abs(angle) > 5.01) throw new ArgumentException("Некорректная геометрия обработки.");
        if (Math.Abs(angle) < .001) return (width, height);
        double a = angle * Math.PI / 180, c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        return (checked((int)Math.Ceiling(width * c + height * s)), checked((int)Math.Ceiling(height * c + width * s)));
    }

    public static ProcessingPlan Analyze(GrayFrame input, ProcessingOptions options, double dpiX, double dpiY, CancellationToken token)
    {
        options.Validate();
        if (!double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX <= 0 || dpiY <= 0) throw new ArgumentException("Некорректное разрешение обработки.");
        token.ThrowIfCancellationRequested();
        double angle = options.Deskew ? DetectCorrection(input, token) : 0;
        // Classify ink before rotation: white corner fill must not make gray paper
        // look like foreground near the rotated sheet's boundary.
        var background = new PaperBackground(input, token);
        var mask = new byte[input.Pixels.Length];
        for (int y = 0; y < input.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < input.Width; x++)
                mask[y * input.Width + x] = input.Pixels[y * input.Width + x] < Math.Min(242, Math.Min(background.Paper, background.At((x + .5) / input.Width, (y + .5) / input.Height)) - 18) ? (byte)0 : (byte)255;
        }
        var unrotatedMask = new GrayFrame(input.Width, input.Height, mask);
        var frame = Math.Abs(angle) > .001 ? Rotate(unrotatedMask, angle, token) : unrotatedMask;
        int left = frame.Width, top = frame.Height, right = -1, bottom = -1, ink = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < frame.Width; x++)
                if (frame.Pixels[y * frame.Width + x] < 224)
                { ink++; left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        }
        bool content = ink >= Math.Max(24, frame.Pixels.Length / 20000);
        string note = !content ? "Содержимое не найдено: границы сохранены." : options.Deskew && angle == 0 ? "Уверенный наклон не найден: поворот пропущен." : "";
        if (!options.Crop || !content) return new(angle, 0, 0, 1, 1, content, note);
        int mx = (int)Math.Ceiling(options.MarginMm * dpiX / 25.4), my = (int)Math.Ceiling(options.MarginMm * dpiY / 25.4);
        left = Math.Max(0, left - mx); top = Math.Max(0, top - my);
        right = Math.Min(frame.Width, right + 1 + mx); bottom = Math.Min(frame.Height, bottom + 1 + my);
        return new(angle, (double)left / frame.Width, (double)top / frame.Height, (double)right / frame.Width, (double)bottom / frame.Height, true, note);
    }

    public static double DetectCorrection(GrayFrame frame, CancellationToken token)
    {
        var background = new PaperBackground(frame, token);
        var points = new List<(int X, int Y)>();
        // Use every row; a bounded subset of ink points keeps angle search inexpensive.
        for (int y = 0; y < frame.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < frame.Width; x++)
                if (frame.Pixels[y * frame.Width + x] < Math.Min(210, background.At((x + .5) / frame.Width, (y + .5) / frame.Height) - 40)) points.Add((x, y));
        }
        if (points.Count < 150) return 0;
        int stride = Math.Max(1, (points.Count + 39999) / 40000);
        double Score(double degrees)
        {
            token.ThrowIfCancellationRequested();
            double slope = Math.Tan(degrees * Math.PI / 180);
            var bins = new int[frame.Height + frame.Width / 4 + 8];
            int offset = frame.Width / 8 + 4;
            for (int i = 0; i < points.Count; i += stride)
            {
                var p = points[i]; int row = (int)Math.Round(p.Y - slope * (p.X - frame.Width / 2.0)) + offset;
                if ((uint)row < (uint)bins.Length) bins[row]++;
            }
            double sum = 0; foreach (int n in bins) sum += (double)n * n;
            return sum;
        }
        double baseline = Score(0), best = baseline, angle = 0;
        for (int i = -10; i <= 10; i++) { double a = i * .5, score = Score(a); if (score > best) { best = score; angle = a; } }
        double coarse = angle;
        for (int i = -5; i <= 5; i++)
        {
            double a = coarse + i * .1;
            if (Math.Abs(a) > 5) continue;
            double score = Score(a); if (score > best) { best = score; angle = a; }
        }
        // Reject weak evidence, near-zero corrections and peaks at the search boundary.
        if (Math.Abs(angle) < .15 || Math.Abs(angle) >= 4.9 || best < baseline * 1.12) return 0;
        double side = Math.Max(Score(Math.Max(-5, angle - .8)), Score(Math.Min(5, angle + .8)));
        return best < side * 1.035 ? 0 : -Math.Round(angle, 2);
    }

    public static GrayFrame Rotate(GrayFrame input, double angle, CancellationToken token)
    {
        var size = RotatedSize(input.Width, input.Height, angle);
        var output = new byte[checked(size.Width * size.Height)];
        double a = angle * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        byte Pixel(int x, int y) => x < 0 || y < 0 || x >= input.Width || y >= input.Height ? (byte)255 : input.Pixels[y * input.Width + x];
        for (int y = 0; y < size.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < size.Width; x++)
            {
                double dx = x + .5 - size.Width / 2.0, dy = y + .5 - size.Height / 2.0;
                double sx = c * dx + s * dy + input.Width / 2.0 - .5, sy = -s * dx + c * dy + input.Height / 2.0 - .5;
                int ix = (int)Math.Floor(sx), iy = (int)Math.Floor(sy); double fx = sx - ix, fy = sy - iy;
                output[y * size.Width + x] = (byte)Math.Clamp((int)Math.Round((Pixel(ix, iy) * (1 - fx) + Pixel(ix + 1, iy) * fx) * (1 - fy) + (Pixel(ix, iy + 1) * (1 - fx) + Pixel(ix + 1, iy + 1) * fx) * fy), 0, 255);
            }
        }
        return new(size.Width, size.Height, output);
    }

    public static (byte R, byte G, byte B) Readable(byte r, byte g, byte b, double background, int black, bool strong)
    {
        double luma = Luma(r, g, b), amount = strong ? 1 : .65;
        black = Math.Clamp(black, 0, 100);
        background = Math.Clamp(background, black + 40, 255);
        double target = Math.Clamp((luma - black) * 255 / (background - black), 0, 255);
        double factor = luma > 0 ? target / luma : 0;
        byte Map(byte value) => (byte)Math.Clamp((int)Math.Round(value * (1 - amount) + Math.Min(255, value * factor) * amount), 0, 255);
        int chroma = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        if (luma >= background - 8 && chroma < 24)
        {
            byte White(byte value) => (byte)Math.Clamp((int)Math.Round(value * (1 - amount) + 255 * amount), 0, 255);
            return (White(r), White(g), White(b));
        }
        return (Map(r), Map(g), Map(b));
    }
}
