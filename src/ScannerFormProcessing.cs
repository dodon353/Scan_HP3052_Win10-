namespace HP3052Scanner;

internal sealed partial class ScannerForm
{
    async void AutoProcessPage()
    {
        if (busy || initializing) return;
        int index = OnePage();
        if (index < 0) return;
        var page = session.Pages[index];
        await RunOperation(async token =>
        {
            imageVersion++; thumbnailVersion++; displayedFile = null; viewer.SetImage(null);
            SetStatus("Подготовка страницы к обработке…");
            await imageLoading.WaitAsync(token);
            Bitmap small;
            try { small = await Task.Run(() => PageProcessing.PreviewSource(session.ImagePath(page))); }
            finally { imageLoading.Release(); }
            using (small)
            using (var dialog = new ProcessingDialog(small))
            {
                token.ThrowIfCancellationRequested();
                if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Plan == null)
                { SetStatus("Обработка отменена. Оригинал сохранён."); return; }
                var plan = dialog.Plan;
                var options = dialog.Options;
                token.ThrowIfCancellationRequested();
                string path = session.NewImagePath();
                bool committed = false;
                try
                {
                    SetStatus("Обработка полного скана…");
                    var result = await Task.Run(() =>
                    {
                        token.ThrowIfCancellationRequested();
                        using var original = ScanImages.Load(session.ImagePath(page));
                        using var processed = PageProcessing.Apply(original, plan, options, token);
                        token.ThrowIfCancellationRequested();
                        string mode = PageProcessing.ResultMode(page.Mode, plan);
                        ScanImages.SavePng(processed, path, mode);
                        return (processed.Width, processed.Height, processed.HorizontalResolution, processed.VerticalResolution, Mode: mode);
                    });
                    token.ThrowIfCancellationRequested();
                    session.Replace(index, path, result.Width, result.Height, result.HorizontalResolution, result.VerticalResolution, result.Mode);
                    committed = true;
                    AppFiles.Log($"Page processing: angle={plan.Angle}; crop={options.Crop}; readability={options.Readability}; strong={options.Strong}; specks={options.RemoveSpecks}; margin={options.MarginMm}");
                    SetStatus("Страница обработана. Ctrl+Z — вернуть оригинал, Ctrl+Y — повторить.");
                }
                finally { if (!committed) TryDelete(path); }
            }
        });
        if (!IsDisposed) RefreshPages(index);
    }
}
