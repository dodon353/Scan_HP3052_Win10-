using HP3052Scanner;

internal static class ProcessingTests
{
    static readonly CancellationToken None = CancellationToken.None;
    internal static GrayFrame TextPage()
    {
        int w = 700, h = 900;
        var data = Enumerable.Repeat((byte)232, w * h).ToArray();
        for (int row = 0; row < 18; row++)
            for (int word = 0; word < 9; word++)
                for (int letter = 0; letter < 5; letter++)
                {
                    int left = 90 + word * 55 + letter * 8, top = 125 + row * 31;
                    for (int y = 0; y < 9; y++)
                        for (int x = 0; x < 5; x++)
                            if (x == 0 || y == 0 || y == 4 || (letter % 2 == 0 && x == 4)) data[(top + y) * w + left + x] = 55;
                }
        return new(w, h, data);
    }
    public static void Run(string root, Action<bool, string> check)
    {
        void Throws(Action a, string name) { bool caught = false; try { a(); } catch (ArgumentException) { caught = true; } check(caught, name); }
        var blank = new GrayFrame(700, 900, Enumerable.Repeat((byte)240, 630000).ToArray());
        var blankPlan = PageProcessingCore.Analyze(blank, new(), 100, 100, None);
        check(!blankPlan.HasContent && blankPlan.Angle == 0 && blankPlan.Bounds(700, 900) == new PixelBox(0, 0, 700, 900), "Blank sheet retains its complete boundaries");
        var text = TextPage();
        check(PageProcessingCore.DetectCorrection(text, None) == 0, "Straight text is not rotated");
        foreach (double angle in new[] { -3.0, -1.4, 1.7, 3.2 })
        {
            var tilted = PageProcessingCore.Rotate(text, angle, None);
            double correction = PageProcessingCore.DetectCorrection(tilted, None);
            check(Math.Abs(correction + angle) <= .25, $"Skew {angle} degrees detected with correct sign: {correction}");
            var corrected = PageProcessingCore.Rotate(tilted, correction, None);
            check(Math.Abs(PageProcessingCore.DetectCorrection(corrected, None)) <= .2, $"Correction leaves horizontal text at {angle} degrees");
        }
        var plan = PageProcessingCore.Analyze(text, new(Deskew: false), 100, 100, None);
        var bounds = plan.Bounds(700, 900);
        check(bounds.X == 78 && bounds.Y == 113 && bounds.Width < 600 && bounds.Height < 650, "Crop uses the content union and three millimetres of padding");
        var noCrop = PageProcessingCore.Analyze(text, new(Deskew: false, Crop: false), 100, 100, None);
        check(noCrop.Bounds(700, 900) == new PixelBox(0, 0, 700, 900), "Disabling crop retains canvas dimensions");
        var largeMargin = PageProcessingCore.Analyze(text, new(Deskew: false, MarginMm: 10), 100, 100, None).Bounds(700, 900);
        check(largeMargin.X < bounds.X && largeMargin.Y < bounds.Y && largeMargin.Width > bounds.Width, "Larger physical padding expands the crop");
        var twice = plan.Bounds(1400, 1800);
        check(Math.Abs(twice.X - bounds.X * 2) <= 1 && Math.Abs(twice.Width - bounds.Width * 2) <= 2, "Preview crop maps proportionally onto the full resolution");
        var marked = new GrayFrame(text.Width, text.Height, (byte[])text.Pixels.Clone());
        for (int y = 835; y < 842; y++) for (int x = 640; x < 646; x++) marked.Pixels[y * 700 + x] = 205;
        var markedBounds = PageProcessingCore.Analyze(marked, new(Deskew: false), 100, 100, None).Bounds(700, 900);
        check(markedBounds.X + markedBounds.Width > 645 && markedBounds.Y + markedBounds.Height > 841, "Faint peripheral page number is included in the crop");
        var spot = new GrayFrame(700, 900, (byte[])blank.Pixels.Clone()); spot.Pixels[350] = 0;
        check(!PageProcessingCore.Analyze(spot, new(), 100, 100, None).HasContent, "A single noise pixel does not trigger destructive cropping");
        Throws(() => PageProcessingCore.Analyze(text, new(MarginMm: double.NaN), 100, 100, None), "Invalid physical margin is rejected");
        Throws(() => PageProcessingCore.Analyze(text, new(), 0, 100, None), "Invalid image DPI is rejected");
        Throws(() => PageProcessingCore.RotatedSize(700, 900, 90), "Rotation outside the supported small-angle range is rejected");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool stopped = false; try { PageProcessingCore.Analyze(text, new(), 100, 100, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
        check(stopped, "Canceled analysis stops before editing pixels");
        var paper = PageProcessingCore.Readable(232, 232, 232, 232, 55, false);
        var dark = PageProcessingCore.Readable(55, 55, 55, 232, 55, false);
        var strong = PageProcessingCore.Readable(55, 55, 55, 232, 55, true);
        check(paper.R > 232 && paper.R == paper.G && paper.G == paper.B, "Readability lightens neutral paper without tinting");
        check(dark.R < 55 && strong.R < dark.R, "Text contrast increases with selectable strength");
        check(PageProcessingCore.Readable(255, 255, 255, 255, 0, false) == ((byte)255, (byte)255, (byte)255), "White paper remains white");
        var stamp = PageProcessingCore.Readable(190, 45, 60, 232, 55, false);
        check(stamp.R > stamp.B && stamp.B > stamp.G && stamp.R != stamp.G, "Colored stamp keeps its color channel ordering");
        var background = new PaperBackground(blank, None);
        check(Math.Abs(background.At(0, 0) - 240) < .01 && background.Black <= 100, "Background estimate handles a uniform gray sheet");
        var session = new DocumentSession(Path.Combine(root, "processing-session"));
        string original = session.NewImagePath(); File.WriteAllText(original, "unchanged original");
        session.Add(original, 700, 900, 300, 300, mode: "Lineart");
        string replacement = session.NewImagePath(); File.WriteAllText(replacement, "processed revision");
        session.Replace(0, replacement, bounds.Width, bounds.Height, 300, 300, "Gray");
        check(session.Pages[0].Mode == "Gray" && File.ReadAllText(original) == "unchanged original", "Processing commits a separate revision and updates its pixel mode");
        session.Undo();
        check(session.ImagePath(session.Pages[0]) == original && session.Pages[0].Mode == "Lineart" && session.Pages.Count == 1, "Undo restores original image and mode without deleting the scan");
        session.Redo();
        check(session.ImagePath(session.Pages[0]) == replacement && session.Pages[0].Mode == "Gray", "Redo restores the processed revision");
        // Reproducible visual fixture, using the same portable analysis and tonal transform.
        var example = PageProcessingCore.Rotate(text, 2.4, None);
        var examplePlan = PageProcessingCore.Analyze(example, new(), 100, 100, None);
        var exampleBox = examplePlan.Bounds(example.Width, example.Height);
        check(exampleBox.Width < 600 && exampleBox.Height < 650, "Deskewed gray paper edges are not mistaken for content during cropping");
        var rotated = PageProcessingCore.Rotate(example, examplePlan.Angle, None);
        var box = examplePlan.Bounds(example.Width, example.Height);
        var bg = new PaperBackground(rotated, None);
        var output = new byte[box.Width * box.Height];
        for (int y = 0; y < box.Height; y++) for (int x = 0; x < box.Width; x++)
        {
            int px = x + box.X, py = y + box.Y; byte p = rotated.Pixels[py * rotated.Width + px];
            output[y * box.Width + x] = PageProcessingCore.Readable(p, p, p, bg.At((px + .5) / rotated.Width, (py + .5) / rotated.Height), bg.Black, false).R;
        }
        SavePgm(Path.Combine(root, "processing-before.pgm"), example);
        SavePgm(Path.Combine(root, "processing-after.pgm"), new(box.Width, box.Height, output));
    }
    static void SavePgm(string path, GrayFrame frame)
    {
        using var file = File.Create(path); file.Write(System.Text.Encoding.ASCII.GetBytes($"P5\n{frame.Width} {frame.Height}\n255\n")); file.Write(frame.Pixels);
    }
}
