namespace HP3052Scanner;
internal static class Program
{
    [STAThread]
    static void Main()
    {
        FileStream? documentLock = null;
        try
        {
            ApplicationConfiguration.Initialize();
            Directory.CreateDirectory(AppFiles.DataRoot);
            documentLock = new FileStream(Path.Combine(AppFiles.DataRoot, "application.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33)
        {
            MessageBox.Show("Приложение уже открыто. Используйте существующее окно.", "HP 3052", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        catch (Exception e)
        {
            AppFiles.Log(e.ToString());
            MessageBox.Show("Не удалось подготовить папку приложения.\n" + e.Message, "HP 3052", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        using (documentLock)
        {
            Application.ThreadException += (_, e) =>
            {
                AppFiles.Log(e.Exception.ToString());
                MessageBox.Show("Операция не завершена. Проект сохраняется автоматически.\n" + e.Exception.Message, "HP 3052", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            try
            {
                using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                using var window = new ScannerForm { Icon = icon };
                Application.Run(window);
            }
            catch (Exception e)
            {
                AppFiles.Log(e.ToString());
                MessageBox.Show("Не удалось запустить приложение.\n" + e.Message, "HP 3052", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
