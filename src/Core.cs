using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HP3052Scanner;
internal sealed class ScanProfile
{
    public string Name { get; set; } = "Документ";
    public string Mode { get; set; } = "Gray";
    public int Dpi { get; set; } = 300;
    public string Source { get; set; } = "Flatbed";
    public string Paper { get; set; } = "A4";
    public double Width { get; set; } = 210;
    public double Height { get; set; } = 297;
    public string Format { get; set; } = "PDF";
    public bool Merged { get; set; } = true;
    public bool LosslessPdf { get; set; } = true;
    public int JpegQuality { get; set; } = 90;

    public ScanProfile Copy() => (ScanProfile)MemberwiseClone();
    public static List<ScanProfile> Defaults() => new()
    {
        new(),
        new()
        {
            Name = "Цветной документ",
            Mode = "Color"
        },
        new()
        {
            Name = "Фото 10×15",
            Mode = "Color",
            Dpi = 600,
            Paper = "Фото 10×15",
            Width = 100,
            Height = 150,
            Format = "JPG",
            Merged = false,
            LosslessPdf = false,
            JpegQuality = 95
        }
    };
}

internal sealed class Configuration
{
    public string ip { get; set; } = "";
    public string distro { get; set; } = "Ubuntu-22.04";
    public string LastFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public string BaseName { get; set; } = "Документ";
    public bool AddDate { get; set; }
    public bool ShowAutoSourceHint { get; set; } = true;
    public ScanProfile Current { get; set; } = new();
    public List<ScanProfile> Profiles { get; set; } = ScanProfile.Defaults();
}

internal static class AppFiles
{
    public static string DataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HP3052Scanner");
    public static string ConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HP3052Scanner", "settings.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true
    };
    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(fs, value, Json);
                fs.Flush(true);
            }

            File.Move(temp, path, true);
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

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            string path = Path.Combine(DataRoot, "application.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                for (int i = 2; i >= 1; i--)
                    if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                File.Move(path, path + ".1", true);
            }
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}\n");
        }
        catch
        {
        }
    }

    public static void ValidateIp(string value)
    {
        if (!IPAddress.TryParse(value, out var p) || p.AddressFamily != AddressFamily.InterNetwork || p.ToString() != value || IPAddress.IsLoopback(p) || p.Equals(IPAddress.Any) || p.Equals(IPAddress.Broadcast) || p.GetAddressBytes()[0] == 0 || p.GetAddressBytes()[0] >= 224)
            throw new InvalidOperationException("Введите корректный IPv4-адрес принтера.");
    }
}

internal sealed record ScanRequest(string Ip, string Distro, string Mode, int Dpi, string Source, double Left, double Top, double Width, double Height)
{
    // Keep the user's physical source separate from HPLIP's command value.
    public string? DriverSource { get; init; }
    public string? Address { get; init; }
    public bool DriverDefaultArea { get; init; }
    public bool RequiresEmptyFeeder => Source.Equals("Flatbed", StringComparison.OrdinalIgnoreCase) && string.Equals(DriverSource, "Auto", StringComparison.OrdinalIgnoreCase);
    public string Device => "hpaio:/net/HP_LaserJet_3052?ip=" + Ip;

    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
    public string Arguments()
    {
        AppFiles.ValidateIp(Ip);
        if (Dpi < 50 || Dpi > 1200 || Width < 1 || Height < 1 || Left < 0 || Top < 0 || !double.IsFinite(Width + Height + Left + Top))
            throw new InvalidOperationException("Недопустимые параметры области сканирования.");
        if (Mode is not ("Color" or "Gray" or "Lineart"))
            throw new InvalidOperationException("Недопустимый цветовой режим.");
        string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        string geometry = DriverDefaultArea ? "" : " -l " + N(Left) + " -t " + N(Top) + " -x " + N(Width) + " -y " + N(Height);
        return "scanimage -d " + Quote(Device) + " --source " + Quote(DriverSource ?? Source) + " --mode " + Quote(Mode) + " --resolution " + Dpi + " --format=tiff" + geometry;
    }
}

internal sealed class ScannerCapabilities
{
    public List<string> Sources { get; } = new();
    public List<string> Modes { get; } = new();
    public List<int> Resolutions { get; } = new();
    public int MinDpi { get; private set; } = 50;
    public int MaxDpi { get; private set; } = 1200;
    public double? MaxWidth { get; private set; }
    public double? MaxHeight { get; private set; }
    public double? DefaultLeft { get; private set; }
    public double? DefaultTop { get; private set; }

    public static ScannerCapabilities Parse(string text)
    {
        var c = new ScannerCapabilities();
        string Options(string name)
        {
            var m = Regex.Match(text, @"(?m)^\s*--" + name + @"\s+([^\r\n]+)");
            return m.Success ? Regex.Replace(m.Groups[1].Value, @"\s*\[[^\]]*\].*$", "").Trim() : "";
        }

        c.Sources.AddRange(Options("source").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        c.Modes.AddRange(Options("mode").Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        string resolution = Options("resolution");
        var range = Regex.Match(resolution, @"(\d+)\.\.(\d+)");
        if (range.Success)
        {
            c.MinDpi = int.Parse(range.Groups[1].Value);
            c.MaxDpi = int.Parse(range.Groups[2].Value);
        }
        else
            foreach (Match m in Regex.Matches(resolution, @"\d+"))
            {
                int n = int.Parse(m.Value);
                if (n >= 50 && n <= 1200 && !c.Resolutions.Contains(n))
                    c.Resolutions.Add(n);
            }

        if (c.Resolutions.Count > 0)
        {
            c.MinDpi = c.Resolutions.Min();
            c.MaxDpi = c.Resolutions.Max();
        }

        double? Geometry(string name)
        {
            var m = Regex.Match(text, @"(?m)^\s*(?:-" + name + @"|--" + (name == "x" ? "br-x" : "br-y") + @")\s+([\d.]+)\.\.([\d.]+)mm");
            return m.Success && double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        c.MaxWidth = Geometry("x");
        c.MaxHeight = Geometry("y");
        double? Position(string shortName, string fullName)
        {
            var m = Regex.Match(text, @"(?m)^[ \t]*(?:-" + shortName + "|--" + fullName + @")[ \t]+[^\r\n]*\[([\d.]+)\]");
            return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        c.DefaultLeft = Position("l", "tl-x");
        c.DefaultTop = Position("t", "tl-y");
        return c;
    }

    public string ResolveSource(string desired)
    {
        if (Sources.Count == 0)
            return desired;
        var source = Sources.FirstOrDefault(x => x.Equals(desired, StringComparison.OrdinalIgnoreCase));
        // Legacy HP PML scanners expose Auto|ADF: Auto uses glass with an empty feeder.
        if (source == null && desired.Equals("Flatbed", StringComparison.OrdinalIgnoreCase))
            source = Sources.FirstOrDefault(x => x.Equals("Auto", StringComparison.OrdinalIgnoreCase));
        return source ?? throw new InvalidOperationException((desired.Equals("Flatbed", StringComparison.OrdinalIgnoreCase) ? "Драйвер не предоставляет источник «Стекло» или Auto." : "Драйвер не предоставляет автоподатчик.") + " Доступные источники: " + string.Join(", ", Sources) + ". Подробности — в журнале.");
    }

    public void Validate(ScanRequest r)
    {
        if (r.Dpi < MinDpi || r.Dpi > MaxDpi || (Resolutions.Count > 0 && !Resolutions.Contains(r.Dpi)))
            throw new InvalidOperationException($"Для выбранного источника драйвер не поддерживает {r.Dpi} DPI. Доступно: " + (Resolutions.Count > 0 ? string.Join(", ", Resolutions) : $"{MinDpi}–{MaxDpi}"));
        if (Modes.Count > 0 && !Modes.Contains(r.Mode, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выбранный цветовой режим не поддерживается этим источником.");
        if (!r.DriverDefaultArea && (MaxWidth.HasValue && r.Left + r.Width > MaxWidth + .2 || MaxHeight.HasValue && r.Top + r.Height > MaxHeight + .2))
            throw new InvalidOperationException("Выбранная область больше доступной области сканера. Уменьшите размер оригинала.");
    }
}

internal sealed record FeederCompletion(string Status, string? Information, bool Error)
{
    public static FeederCompletion From(int code, int received)
    {
        if (code != 0 && code != 7)
            return new($"Автоподатчик остановлен. Получено страниц: {received}.", ScanMessages.Describe(code), true);
        if (received == 0)
            return new("Автоподатчик не вернул страниц.", ScanMessages.Describe(7), false);
        return new($"Сканирование завершено. Добавлено страниц: {received}.", null, false);
    }
}

internal static class ScanMessages
{
    public static string DescribeCommandFailure(int code, string error, string output = "")
    {
        string detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
        string message = Regex.IsMatch(detail, @"setting of option --(?:br-[xy]|tl-[xy]) failed", RegexOptions.IgnoreCase)
            ? "Драйвер отклонил границы области сканирования."
            : code == 1 ? "Команда сканирования завершилась с ошибкой (код 1)." : Describe(code);
        if (detail.Length > 1600) detail = detail[..1600] + "\nПолный ответ — в журнале.";
        return message + (detail.Length > 0 ? "\n\nОтвет драйвера:\n" + detail : "\nПодробности — в журнале.");
    }
    public static bool IsFeederEmpty(int code) => code == 7;
    public static string Describe(int code) => code switch
    {
        1 => "Драйвер не поддерживает запрошенную функцию.",
        2 => "Сканирование отменено на устройстве.",
        4 => "Сканер не принял параметры. Проверьте источник, размер и разрешение оригинала.",
        3 => "Сканер занят. Дождитесь окончания другой операции.",
        6 => "Бумага замялась в автоподатчике. Уберите замятый лист и повторите сканирование.",
        7 => "В автоподатчике нет бумаги или он её не захватил. Загрузите листы лицевой стороной вверх, выровняйте направляющие и повторите сканирование.",
        8 => "Открыта крышка сканера или автоподатчика. Закройте её и повторите операцию.",
        9 => "Ошибка обмена со сканером. Проверьте питание и сетевое соединение.",
        10 => "Недостаточно памяти для сканирования. Уменьшите разрешение.",
        11 => "Драйвер запретил доступ к сканеру.",
        _ => $"Не удалось выполнить сканирование (код {code}). Подробности записаны в журнал приложения."};
}

internal static class ExportSelection
{
    public static List<PageRecord> Choose(IReadOnlyList<PageRecord> pages, IEnumerable<int> selected, bool onlySelected, bool forceAll) => !forceAll && onlySelected ? selected.Distinct().OrderBy(x => x).Select(i => pages[i]).ToList() : pages.ToList();
}

internal static class ExportNames
{
    public static string Safe(string value)
    {
        foreach (char c in "<>:\"/\\|?*".Concat(Enumerable.Range(0, 32).Select(n => (char)n)))
            value = value.Replace(c, '_');
        value = value.Trim().TrimEnd('.');
        if (value.Length == 0)
            value = "Документ";
        if (Regex.IsMatch(value.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            value = "_" + value;
        return value.Length > 110 ? value[..110] : value;
    }

    public static string[] Plan(string folder, string baseName, string extension, IReadOnlyList<PageRecord> pages, bool merged, bool date, DateTime now)
    {
        if (baseName.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
            baseName = baseName[..^(extension.Length + 1)];
        string stem = Safe(baseName) + (date ? "_" + now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture) : "");
        var result = merged ? new[]
        {
            Path.Combine(folder, stem + "." + extension)
        }

        : pages.Select((p, i) => Path.Combine(folder, stem + "_" + (Regex.IsMatch(p.Name, @"^Страница_\d+$") ? (i + 1).ToString("000") : Safe(p.Name)) + "." + extension)).ToArray();
        if (result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
            throw new IOException("Имена файлов повторяются. Переименуйте одинаковые страницы.");
        return result;
    }
}

internal static class ExportSavePlan
{
    static string Extension(string format) => format switch
    {
        "PDF" => "pdf", "JPG" => "jpg", "PNG" => "png", "TIFF" => "tiff", "BMP" => "bmp",
        _ => throw new ArgumentException("Неизвестный формат сохранения.")
    };
    public static string Filter(string format) => format switch
    {
        "PDF" => "Документ PDF|*.pdf", "JPG" => "Изображение JPEG|*.jpg;*.jpeg",
        "PNG" => "Изображение PNG|*.png", "TIFF" => "Изображение TIFF|*.tiff;*.tif",
        "BMP" => "Изображение BMP|*.bmp", _ => throw new ArgumentException("Неизвестный формат сохранения.")
    };
    public static string[] FromChoice(string chosenPath, string format, int count, bool mergedPdf)
    {
        if (count < 1) throw new ArgumentException("Нет страниц для сохранения.");
        string extension = Extension(format);
        string folder = Path.GetDirectoryName(Path.GetFullPath(chosenPath))!;
        string name = Path.GetFileName(chosenPath);
        string typedExtension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        // Keep a supported alias (jpeg/tif), replace another image/document extension.
        bool alias = format == "JPG" && typedExtension == "jpeg" || format == "TIFF" && typedExtension == "tif";
        if (alias) extension = typedExtension;
        if (new[] { "pdf", "jpg", "jpeg", "png", "tif", "tiff", "bmp" }.Contains(typedExtension))
            name = Path.GetFileNameWithoutExtension(name);
        string stem = ExportNames.Safe(name);
        if (count == 1 || format == "PDF" && mergedPdf)
            return new[] { Path.Combine(folder, stem + "." + extension) };
        return Enumerable.Range(1, count).Select(i => Path.Combine(folder, stem + "_" + i.ToString("000", CultureInfo.InvariantCulture) + "." + extension)).ToArray();
    }
    public static string Suggest(string folder, string format, int count, bool mergedPdf, DateTime now)
    {
        string stem = "Скан_" + now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        for (int suffix = 1; ; suffix++)
        {
            string path = Path.Combine(folder, stem + (suffix == 1 ? "" : "_" + suffix) + "." + Extension(format));
            if (!File.Exists(path) && !Directory.Exists(path) && FromChoice(path, format, count, mergedPdf).All(p => !File.Exists(p) && !Directory.Exists(p)))
                return path;
        }
    }
}
