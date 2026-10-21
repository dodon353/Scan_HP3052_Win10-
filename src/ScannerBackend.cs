using System.Diagnostics;
using System.Text;

namespace HP3052Scanner;
internal sealed record LinuxResult(int Code, string Output, string Error)
{
    public void RequireSuccess()
    {
        if (Code != 0)
        {
            AppFiles.Log($"SANE exit {Code}\n{Error}\n{Output}");
            throw new InvalidOperationException(ScanMessages.DescribeCommandFailure(Code, Error, Output));
        }
    }
}

internal sealed class ScannerBackend
{
    readonly string executable;
    readonly Dictionary<string, ScannerCapabilities> cache = new();
    public ScannerBackend(string? program = null)
    {
        executable = program ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "wsl.exe");
    }

    ProcessStartInfo StartInfo(string distro, string shell)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[]
        {
            "-d",
            distro,
            "-u",
            "root",
            "--exec",
            "/bin/bash",
            "-c",
            shell
        }

        )
            info.ArgumentList.Add(a);
        return info;
    }

    public static string ToWslPath(string value)
    {
        string full = Path.GetFullPath(value);
        if (full.Length < 3 || full[1] != ':' || full[2] != '\\')
            throw new IOException("Для сканирования временная папка должна находиться на локальном диске Windows.");
        return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
    }

    public async Task<LinuxResult> RunAsync(string distro, string command, CancellationToken cancellation, int timeout = 180000, Action<int>? pageCompleted = null)
    {
        cancellation.ThrowIfCancellationRequested();
        string pid = "/tmp/hp3052_" + Guid.NewGuid().ToString("N") + ".pid";
        string stopped = pid + ".cancel";
        string cleanup = "rm -f " + ScanRequest.Quote(pid) + " " + ScanRequest.Quote(stopped);
        string body = "export LC_ALL=C; trap " + ScanRequest.Quote(cleanup) + " EXIT; if [ -f " + ScanRequest.Quote(stopped) + " ]; then exit 130; fi; /bin/bash -c " + ScanRequest.Quote("exec " + command) + " & job=$!; printf '%s\\n' \"$job\" > " + ScanRequest.Quote(pid) + "; if [ -f " + ScanRequest.Quote(stopped) + " ]; then kill -TERM \"$job\" 2>/dev/null || true; fi; wait \"$job\"";
        using var process = Process.Start(StartInfo(distro, body)) ?? throw new IOException("Не удалось запустить WSL. Запустите INSTALL-UBUNTU.cmd.");
        var output = process.StandardOutput.ReadToEndAsync();
        async Task<string> ReadErrors()
        {
            var buffer = new StringBuilder();
            string? line;
            while ((line = await process.StandardError.ReadLineAsync()) != null)
            {
                buffer.AppendLine(line);
                var match = System.Text.RegularExpressions.Regex.Match(line, @"^Scanned page (\d+)\.\s*\(scanner status = (?:0|5)\)");
                if (match.Success && pageCompleted != null)
                    try
                    {
                        pageCompleted(int.Parse(match.Groups[1].Value));
                    }
                    catch (Exception ex)
                    {
                        AppFiles.Log("Batch completion marker: " + ex);
                    }
            }

            return buffer.ToString();
        }

        var errors = ReadErrors();
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // Terminate only this job's recorded Linux PID, never every user's scanimage.
            string stop = "touch " + ScanRequest.Quote(stopped) + "; if [ -f " + ScanRequest.Quote(pid) + " ]; then read -r job < " + ScanRequest.Quote(pid) + "; case \"$job\" in ''|*[!0-9]*) ;; *) if [ \"$job\" -gt 1 ]; then kill -TERM \"$job\" 2>/dev/null || true; fi ;; esac; fi";
            try
            {
                using var killer = Process.Start(StartInfo(distro, stop));
                if (killer != null)
                {
                    var drainOut = killer.StandardOutput.ReadToEndAsync();
                    var drainErr = killer.StandardError.ReadToEndAsync();
                    try
                    {
                        await killer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                        await Task.WhenAll(drainOut, drainErr).WaitAsync(TimeSpan.FromSeconds(1));
                    }
                    catch
                    {
                        try
                        {
                            killer.Kill(true);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            AppFiles.Log("Scan job cancelled or timed out: " + pid);
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new TimeoutException("Истекло время ожидания сканера. Уже полученные страницы сохранены в проекте.");
        }

        string stdout = (await output).Replace("\0", "");
        string stderr = (await errors).Replace("\0", "");
        if (process.ExitCode != 0)
            AppFiles.Log($"WSL/SANE exit {process.ExitCode}\n{stderr}\n{stdout}");
        return new(process.ExitCode, stdout, stderr);
    }

    public async Task<(ScanRequest Request, ScannerCapabilities Capabilities)> Prepare(ScanRequest request, CancellationToken token, bool refresh = false, bool validate = true)
    {
        request = request with { Ip = await ScannerAddress.Resolve(request.Ip, token), Address = request.Address ?? request.Ip };
        string key = request.Ip + "|" + request.Distro + "|" + request.Source;
        if (refresh)
            cache.Remove(key);
        if (!cache.TryGetValue(key, out var capabilities))
        {
            var listing = await RunAsync(request.Distro, "scanimage -d " + ScanRequest.Quote(request.Device) + " -A", token, 45000);
            listing.RequireSuccess();
            AppFiles.Log("Scanner options (general):\n" + listing.Output + "\n" + listing.Error);
            if (!listing.Output.Contains("--resolution", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Сканер не подтвердил доступность. Проверьте IP и питание аппарата.");
            var general = ScannerCapabilities.Parse(listing.Output);
            string source = general.ResolveSource(request.Source);
            var details = await RunAsync(request.Distro, "scanimage -d " + ScanRequest.Quote(request.Device) + " --source " + ScanRequest.Quote(source) + " -A", token, 45000);
            details.RequireSuccess();
            capabilities = ScannerCapabilities.Parse(details.Output);
            cache[key] = capabilities;
            AppFiles.Log("Scanner options (" + source + "):\n" + details.Output);
        }

        request = request with
        {
            DriverSource = capabilities.ResolveSource(request.Source)
        };
        if (validate)
            capabilities.Validate(request);
        return (request, capabilities);
    }
}
