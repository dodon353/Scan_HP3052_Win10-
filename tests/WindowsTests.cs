using HP3052Scanner;
using System.Drawing.Imaging;
using System.Text;

internal static class WindowsTests
{
    static int passed;
    static void Check(bool value, string text) { if (!value) throw new Exception("FAIL: " + text); Console.WriteLine("PASS: " + text); passed++; }
    [STAThread]
    static int Main(string[] args)
    {
        string root = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(root);
        try
        {
            using var image = new Bitmap(256, 128, PixelFormat.Format24bppRgb);
            image.SetResolution(300, 300);
            using (var g = Graphics.FromImage(image)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 0, 0, 128, 128); }
            foreach (string mode in new[] { "Color", "Gray", "Lineart" })
            {
                string path = Path.Combine(root, mode + ".png");
                ScanImages.SavePng(image, path, mode);
                using var read = new Bitmap(path);
                Check(read.Width == 256 && read.Height == 128, "PNG dimensions: " + mode);
                Check(Image.GetPixelFormatSize(read.PixelFormat) == (mode == "Lineart" ? 1 : mode == "Gray" ? 8 : 24), "Actual PNG bit depth: " + mode);
                Check(read.GetPixel(30, 30).R == 0 && read.GetPixel(200, 30).R == 255, "Black/white pixel polarity: " + mode);
                var page = new PageRecord("1", "Test", Path.GetFileName(path), 256, 128, 300, 300, Mode: mode);
                string pdf = Path.Combine(root, mode + ".pdf");
                PdfWriter.Write(pdf, new[] { page }, _ => path, true, 90, false, CancellationToken.None);
                string data = Encoding.ASCII.GetString(File.ReadAllBytes(pdf));
                Check(data.Contains(mode == "Color" ? "/DeviceRGB" : "/DeviceGray") && data.Contains("/Predictor 15"), "Windows PNG to lossless PDF channels: " + mode);
                string copy = Path.Combine(root, mode + "-copy.png");
                ScanImages.AtomicImage(path, copy, "PNG", 90, false);
                Check(File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(copy)), "PNG export preserves indexed pixels: " + mode);
                string tiff = Path.Combine(root, mode + ".tiff");
                ScanImages.AtomicImage(path, tiff, "TIFF", 90, false);
                using var tiffRead = new Bitmap(tiff);
                Check(Image.GetPixelFormatSize(tiffRead.PixelFormat) == Image.GetPixelFormatSize(read.PixelFormat) && tiffRead.GetPixel(30,30).R == 0 && tiffRead.GetPixel(200,30).R == 255, "TIFF keeps bit depth and pixel polarity: " + mode);
            }
            using (var thumb = ScanImages.Thumbnail(image, new Size(202, 256))) Check(thumb.Size == new Size(202, 256), "Thumbnail obeys high-DPI target dimensions");
            using (var viewer = new ScanViewer { Size = new Size(500, 500) })
            {
                viewer.SetImage(ScanImages.Load(Path.Combine(root, "Gray.png")));
                viewer.ActualSize(); Check(viewer.Zoom == 1, "Viewer 100% uses original pixels");
                viewer.ChangeZoom(2); Check(viewer.Zoom == 2, "Viewer zoom changes without decoding the PNG again");
                viewer.FitPage(); Check(viewer.Zoom > 0, "Fit image computes a valid scale");
            }
            ProcessingChecks(root);
            Console.WriteLine("TOTAL PASSED: " + passed);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    static void ProcessingChecks(string root)
    {
        using var original = new Bitmap(400, 500, PixelFormat.Format24bppRgb);
        original.SetResolution(200, 200);
        using (var g = Graphics.FromImage(original)) { g.Clear(Color.FromArgb(232,232,232)); g.FillRectangle(Brushes.Black, 100, 100, 170, 200); g.FillRectangle(Brushes.Red, 280, 320, 10, 10); }
        var none = new ProcessingOptions(false, false, false);
        var identity = PageProcessing.Analyze(original, none, CancellationToken.None);
        using (var copy = PageProcessing.Apply(original, identity, none, CancellationToken.None))
        {
            bool exact = true;
            for (int y=0;y<500;y++) for(int x=0;x<400;x++) exact &= copy.GetPixel(x,y).ToArgb()==original.GetPixel(x,y).ToArgb();
            Check(exact, "Disabled processing preserves every RGB pixel exactly");
            Check(copy.HorizontalResolution == 200 && copy.VerticalResolution == 200, "Processing preserves physical DPI");
        }
        var options = new ProcessingOptions(false, true, true);
        var plan = PageProcessing.Analyze(original, options, CancellationToken.None);
        using (var processed = PageProcessing.Apply(original, plan, options, CancellationToken.None))
        {
            var box=plan.Bounds(400,500);
            Check(processed.Width==box.Width&&processed.Height==box.Height, "Full bitmap matches planned crop bounds");
            Check(processed.Width<400&&processed.Height<500, "Bitmap processing removes blank margins");
            Check(processed.GetPixel(0,0).R>232, "Bitmap readability lightens the gray paper");
            var stamp=processed.GetPixel(284-box.X,324-box.Y);
            Check(stamp.R>stamp.G&&stamp.R>stamp.B, "Bitmap processing retains a red stamp");
            string file=Path.Combine(root,"processed.png"); ScanImages.SavePng(processed,file,"Color");
            using var read=ScanImages.Load(file);
            Check(read.Size==processed.Size&&Math.Abs(read.HorizontalResolution-200)<1, "Processed PNG roundtrip keeps dimensions and DPI");
        }
        Check(original.GetPixel(0,0).R==232&&original.GetPixel(110,110).R==0, "Applying processing does not mutate the original bitmap");
        var rotatedPlan=new ProcessingPlan(-2,0,0,1,1,true,"");
        using(var rotated=PageProcessing.Apply(original,rotatedPlan,none,CancellationToken.None))
        {
            Check(rotated.Width>original.Width&&rotated.Height>original.Height, "Rotation expands the canvas instead of clipping its corners");
            Check(rotated.GetPixel(0,0).ToArgb()==Color.White.ToArgb(), "Rotation fills newly exposed corners with white");
        }
        Check(PageProcessing.ResultMode("Lineart",rotatedPlan)=="Gray", "Rotated monochrome pages retain antialiased edges as grayscale");
        Check(PageProcessing.ResultMode("Lineart",identity)=="Lineart"&&PageProcessing.ResultMode("Color",rotatedPlan)=="Color", "Unrotated monochrome and rotated color retain their modes");
        using var noise=new Bitmap(40,40,PixelFormat.Format24bppRgb); noise.SetResolution(200,200);
        using(var g=Graphics.FromImage(noise))g.Clear(Color.White);
        noise.SetPixel(10,10,Color.Black); noise.SetPixel(20,20,Color.Black); noise.SetPixel(21,20,Color.Black);
        var noisePlan=new ProcessingPlan(0,0,0,1,1,true,"");
        using(var cleaned=PageProcessing.Apply(noise,noisePlan,new(false,false,false,RemoveSpecks:true),CancellationToken.None))
            Check(cleaned.GetPixel(10,10).R==255&&cleaned.GetPixel(20,20).R==0&&cleaned.GetPixel(21,20).R==0, "Optional speck filter removes isolated pixels while retaining adjacent details");
        using(var untouched=PageProcessing.Apply(noise,noisePlan,none,CancellationToken.None))
            Check(untouched.GetPixel(10,10).R==0, "Isolated punctuation is preserved when speck filtering is off");
    }
}
