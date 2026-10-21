using System.Globalization;
using System.Text;

namespace HP3052Scanner;
internal sealed record EncodedPdfPage(string File, int Width, int Height, float DpiX, float DpiY, string Filter, bool DeleteAfterUse = false, string ColorSpace = "DeviceRGB", int Bits = 8, bool Predictor = false);
internal static class PdfStreamWriter
{
    public static void Write(string dest, int pageCount, Func<int, EncodedPdfPage> pageProvider, bool overwrite, CancellationToken token)
    {
        if (pageCount < 1)
            throw new InvalidOperationException("Нет страниц для сохранения.");
        string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                using var bw = new BinaryWriter(fs, Encoding.ASCII, true);
                void W(string s) => bw.Write(Encoding.ASCII.GetBytes(s));
                var offsets = new List<long>
                {
                    0
                };
                int count = 2 + pageCount * 3;
                void Obj(int id, string text)
                {
                    offsets.Add(fs.Position);
                    W($"{id} 0 obj\n{text}\nendobj\n");
                }

                W("%PDF-1.4\n");
                Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
                Obj(2, $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 3} 0 R"))}] /Count {pageCount} >>");
                for (int i = 0; i < pageCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var page = pageProvider(i);
                    try
                    {
                        if (page.Width < 1 || page.Height < 1 || !float.IsFinite(page.DpiX) || !float.IsFinite(page.DpiY) || page.DpiX <= 0 || page.DpiY <= 0 || page.Filter is not ("FlateDecode" or "DCTDecode") || page.ColorSpace is not ("DeviceRGB" or "DeviceGray") || page.Bits is not (1 or 8) || page.Bits == 1 && page.ColorSpace != "DeviceGray")
                            throw new IOException("Некорректные параметры страницы PDF.");
                        int n = 3 + i * 3;
                        double pw = page.Width * 72.0 / page.DpiX, ph = page.Height * 72.0 / page.DpiY;
                        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
                        Obj(n, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(pw)} {N(ph)}] /Resources << /XObject << /Im{i} {n + 1} 0 R >> >> /Contents {n + 2} 0 R >>");
                        long length = new FileInfo(page.File).Length;
                        offsets.Add(fs.Position);
                        string predictor = page.Predictor ? $" /DecodeParms << /Predictor 15 /Colors {(page.ColorSpace == "DeviceGray" ? 1 : 3)} /BitsPerComponent {page.Bits} /Columns {page.Width} >>" : "";
                        W($"{n + 1} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /{page.ColorSpace} /BitsPerComponent {page.Bits} /Filter /{page.Filter}{predictor} /Length {length} >>\nstream\n");
                        using (var input = File.OpenRead(page.File))
                            input.CopyTo(fs);
                        W("\nendstream\nendobj\n");
                        string content = $"q\n{N(pw)} 0 0 {N(ph)} 0 0 cm\n/Im{i} Do\nQ\n";
                        Obj(n + 2, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
                    }
                    finally
                    {
                        if (page.DeleteAfterUse)
                            try
                            {
                                File.Delete(page.File);
                            }
                            catch
                            {
                            }
                    }
                }

                long xref = fs.Position;
                W($"xref\n0 {count + 1}\n0000000000 65535 f \n");
                foreach (long offset in offsets.Skip(1))
                    W($"{offset:0000000000} 00000 n \n");
                W($"trailer\n<< /Size {count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
                bw.Flush();
                fs.Flush(true);
            }

            token.ThrowIfCancellationRequested();
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
