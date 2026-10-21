using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HP3052Scanner;

internal static class SettingsStore
{
    public static Configuration Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path)) return new();
        try
        {
            var c = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path)) ?? throw new JsonException("Пустые настройки.");
            c.Profiles = (c.Profiles ?? ScanProfile.Defaults()).Where(p => p != null && !string.IsNullOrWhiteSpace(p.Name)).ToList();
            c.Current ??= new();
            if (string.IsNullOrWhiteSpace(c.distro)) c.distro = "Ubuntu-22.04";
            return c;
        }
        catch (JsonException e)
        {
            string backup = path + ".corrupt_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6] + ".bak";
            // Failure to preserve the original must stop startup, never allow silent overwrite.
            File.Copy(path, backup, false);
            warning = "Файл настроек повреждён. Его точная копия сохранена:\n" + backup + "\n\nВведите адрес сканера заново. Прежние профили можно восстановить из резервной копии settings.json.bak.\n" + e.Message;
            return new();
        }
    }
    public static void Save(string path, Configuration settings)
    {
        if (File.Exists(path))
        {
            bool valid;
            try { valid = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path)) != null; }
            catch (JsonException) { valid = false; }
            if (valid) File.Copy(path, path + ".bak", true);
        }
        AppFiles.AtomicJson(path, settings);
    }
}

internal static class ScannerAddress
{
    public static void Validate(string address)
    {
        if (IPAddress.TryParse(address, out _)) { AppFiles.ValidateIp(address); return; }
        if (address.Length > 253 || !Regex.IsMatch(address, @"^(?=.{1,253}$)[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?$") || Uri.CheckHostName(address) != UriHostNameType.Dns || address.Split('.').Any(s => s.Length == 0 || s.Length > 63 || s.StartsWith('-') || s.EndsWith('-')) || address.All(c => char.IsDigit(c) || c == '.'))
            throw new InvalidOperationException("Введите IPv4-адрес или имя сканера в локальной сети.");
    }
    public static async Task<string> Resolve(string address, CancellationToken token)
    {
        Validate(address);
        if (IPAddress.TryParse(address, out _)) return address;
        var addresses = await Dns.GetHostAddressesAsync(address, token);
        foreach (var a in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            try { AppFiles.ValidateIp(a.ToString()); return a.ToString(); } catch (InvalidOperationException) { }
        throw new IOException("Имя сканера не разрешилось в доступный IPv4-адрес: " + address);
    }
}

internal static class ScanPolicy
{
    public static int Timeout(ScanRequest r)
    {
        double area = Math.Max(.05, r.Width * r.Height / (210 * 297.0));
        double load = area * Math.Pow(r.Dpi / 300.0, 2) * (r.Mode == "Color" ? 2 : 1);
        return (int)Math.Clamp(180000 * load, 180000, 3600000);
    }
    public static ScanRequest Preview(ScanRequest r, ScannerCapabilities c) => r with
    {
        Dpi = c.MinDpi,
        DriverDefaultArea = true,
        Left = c.DefaultLeft ?? throw new IOException("Драйвер не сообщил начало области стекла по горизонтали."),
        Top = c.DefaultTop ?? throw new IOException("Драйвер не сообщил начало области стекла по вертикали."),
        Width = c.MaxWidth ?? r.Width,
        Height = c.MaxHeight ?? r.Height
    };
    public static ScanRequest PreviewResult(ScanRequest request, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth < 1 || pixelHeight < 1 || request.Dpi < 1) throw new IOException("Некорректный размер предпросмотра.");
        return request with { Width = pixelWidth * 25.4 / request.Dpi, Height = pixelHeight * 25.4 / request.Dpi };
    }
}
