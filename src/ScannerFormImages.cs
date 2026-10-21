using System.Drawing.Imaging;

namespace HP3052Scanner;

internal sealed partial class ScannerForm
{
    void ConfigureThumbnails()
    {
        thumbnailVersion++;
        double scale = Math.Min(DeviceDpi / 96.0, 256.0 / 165);
        thumbnails.Images.Clear();
        thumbnails.ImageSize = new Size((int)(130 * scale), (int)(165 * scale));
    }

    void RefreshPages(int select = -2)
    {
        var selected = pages.SelectedIndices.Cast<int>().Where(i => i < session.Pages.Count).Select(i => pages.Items[i].Tag as string).ToHashSet();
        updatingPages = true;
        pages.BeginUpdate();
        try
        {
            pages.Items.Clear();
            foreach (var p in session.Pages)
                pages.Items.Add(new ListViewItem(p.Name, p.File) { Tag = p.Id, ToolTipText = $"{p.Width}×{p.Height} px, {p.DpiX:0} DPI" });
            if (select == -2)
            {
                foreach (ListViewItem item in pages.Items) item.Selected = selected.Contains((string)item.Tag!);
                if (pages.SelectedIndices.Count == 0 && pages.Items.Count > 0) pages.Items[0].Selected = true;
            }
            else if (select >= 0 && pages.Items.Count > 0)
            {
                var item = pages.Items[Math.Min(select, pages.Items.Count - 1)];
                item.Selected = true;
                item.EnsureVisible();
            }
        }
        finally { pages.EndUpdate(); updatingPages = false; }
        ShowSelectedPage();
        _ = LoadThumbnails(++thumbnailVersion, session.Pages.ToArray(), thumbnails.ImageSize);
        UpdateTitle();
    }

    async Task LoadThumbnails(int version, PageRecord[] snapshot, Size size)
    {
        foreach (var p in snapshot)
        {
            if (version != thumbnailVersion || IsDisposed) return;
            if (thumbnails.Images.ContainsKey(p.File)) continue;
            Bitmap? thumb = null;
            try
            {
                await imageLoading.WaitAsync();
                try
                {
                    if (version != thumbnailVersion || IsDisposed) return;
                    string path = session.ImagePath(p);
                    string cache = Path.Combine(session.Root, "Thumbnails", size.Width + "x" + size.Height, p.File);
                    thumb = await Task.Run(() =>
                    {
                        if (File.Exists(cache))
                            try { return new Bitmap(cache); } catch { File.Delete(cache); }
                        using var image = ScanImages.Load(path);
                        var result = ScanImages.Thumbnail(image, size);
                        try { Directory.CreateDirectory(Path.GetDirectoryName(cache)!); ScanImages.SavePng(result, cache); return result; }
                        catch { result.Dispose(); throw; }
                    });
                }
                finally { imageLoading.Release(); }
                if (version != thumbnailVersion || IsDisposed) return;
                thumbnails.Images.Add(p.File, thumb);
                pages.Invalidate();
            }
            catch (Exception e) { AppFiles.Log("Thumbnail: " + p.Name + " " + e); }
            finally { thumb?.Dispose(); }
        }
    }

    void ShowSelectedPage()
    {
        if (pages.SelectedIndices.Count == 0)
        {
            if (session.Pages.Count == 0) { imageVersion++; displayedFile = null; viewer.SetImage(null); showingPreview = false; }
            return;
        }
        var p = session.Pages[pages.SelectedIndices[0]];
        string path = session.ImagePath(p);
        if (!showingPreview && displayedFile == path) return;
        _ = LoadViewerImage(path, false);
    }

    async Task LoadViewerImage(string path, bool preview)
    {
        int version = ++imageVersion;
        displayedFile = null;
        showingPreview = false;
        viewer.SetImage(null);
        Bitmap? full = null, reduced = null;
        try
        {
            await imageLoading.WaitAsync();
            try
            {
                if (version != imageVersion || IsDisposed) return;
                (full, reduced) = await Task.Run(() =>
                {
                    var image = ScanImages.Load(path);
                    try { return (image, ScanImages.Reduced(image)); }
                    catch { image.Dispose(); throw; }
                });
            }
            finally { imageLoading.Release(); }
            if (version != imageVersion || IsDisposed) return;
            viewer.SetImage(full, reduced);
            full = reduced = null;
            displayedFile = path;
            showingPreview = preview;
        }
        catch (Exception e) { if (version == imageVersion && !IsDisposed) { AppFiles.Log(e.ToString()); Warn("Не удалось открыть страницу: " + e.Message); } }
        finally { full?.Dispose(); reduced?.Dispose(); }
    }

    void ShowPreview()
    {
        if (previewPath == null || !File.Exists(previewPath)) { Warn("Сначала выполните предпросмотр со стекла."); return; }
        updatingPages = true;
        foreach (ListViewItem item in pages.SelectedItems) item.Selected = false;
        updatingPages = false;
        _ = LoadViewerImage(previewPath, true);
    }

    async void RotatePage(RotateFlipType turn, bool swapDpi)
    {
        if (busy) return;
        int i = OnePage();
        if (i < 0) return;
        var p = session.Pages[i];
        await TransformPage(i, p, bitmap =>
        {
            bitmap.RotateFlip(turn);
            bitmap.SetResolution(swapDpi ? p.DpiY : p.DpiX, swapDpi ? p.DpiX : p.DpiY);
            return bitmap;
        });
    }

    async void Crop()
    {
        if (busy) return;
        int i = OnePage();
        if (i < 0) return;
        var rect = viewer.Selection;
        if (rect.Width < 1 || rect.Height < 1) { Warn("Выделите область на странице левой кнопкой мыши."); return; }
        var p = session.Pages[i];
        await TransformPage(i, p, bitmap =>
        {
            var cropped = bitmap.Clone(Rectangle.Intersect(rect, new Rectangle(0, 0, bitmap.Width, bitmap.Height)), PixelFormat.Format24bppRgb);
            cropped.SetResolution(p.DpiX, p.DpiY);
            return cropped;
        });
    }

    async Task TransformPage(int i, PageRecord page, Func<Bitmap, Bitmap> transform)
    {
        await RunOperation(async token =>
        {
            string path = session.NewImagePath();
            imageVersion++;
            thumbnailVersion++;
            displayedFile = null;
            viewer.SetImage(null);
            await imageLoading.WaitAsync(token);
            (int Width, int Height, float HorizontalResolution, float VerticalResolution) result;
            try { result = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                using var original = ScanImages.Load(session.ImagePath(page));
                var edited = transform(original);
                try
                {
                    token.ThrowIfCancellationRequested();
                    ScanImages.SavePng(edited, path, page.Mode);
                    return (edited.Width, edited.Height, edited.HorizontalResolution, edited.VerticalResolution);
                }
                finally { if (!ReferenceEquals(edited, original)) edited.Dispose(); }
            }); }
            finally { imageLoading.Release(); }
            if (token.IsCancellationRequested) { TryDelete(path); token.ThrowIfCancellationRequested(); }
            session.Replace(i, path, result.Width, result.Height, result.HorizontalResolution, result.VerticalResolution);
            RefreshPages(i);
            SetStatus("Изменение сохранено в проекте. Ctrl+Z — отменить, Ctrl+Y — повторить.");
        });
        if (!IsDisposed) RefreshPages(i);
    }

    bool TextInputFocused()
    {
        Control? c = ActiveControl;
        while (c != null)
        {
            if (c is TextBoxBase or NumericUpDown or ComboBox) return true;
            c = c is ContainerControl container ? container.ActiveControl : null;
        }
        return false;
    }

    void NavigateHistory(bool repeat)
    {
        if (busy || initializing) return;
        if (repeat ? !session.CanRedo : !session.CanUndo) { SetStatus("Нет действий для " + (repeat ? "повтора." : "отмены.")); return; }
        Edit(() => { if (repeat) session.Redo(); else session.Undo(); });
    }

    async Task DiscoverScanner()
    {
        await RunOperation(async token =>
        {
            SetStatus("Поиск доступных HP 3052…");
            var result = await backend.RunAsync(config.distro, "scanimage -L", token, 45000);
            result.RequireSuccess();
            var addresses = ScannerDiscovery.Parse(result.Output);
            if (addresses.Length == 0) { Warn("HP 3052 не найден автоматически. Укажите адрес или сетевое имя вручную. Поиск драйвера может не обнаружить аппарат в другой подсети."); return; }
            string? address = addresses.Length == 1 ? addresses[0] : AskText("Найдены сканеры", string.Join(", ", addresses) + "\nВведите адрес нужного аппарата", addresses[0]);
            if (address != null) { ScannerAddress.Validate(address); ip.Text = address; SaveConfiguration(); SetStatus("Найден сканер: " + address + ". Нажмите «Проверить связь»."); }
        });
    }

    async Task CleanStorage()
    {
        if (busy || initializing) return;
        var recoveries = Directory.GetDirectories(AppFiles.DataRoot, "CurrentDocument_recovery_*")
            .Where(p => Directory.GetLastWriteTimeUtc(p) < DateTime.UtcNow.AddDays(-30)).ToArray();
        if (MessageBox.Show(this, "Будут удалены неиспользуемые версии изображений и кэш миниатюр. Текущие страницы и история отмены сохранятся.\n\nРезервных папок повреждённых проектов старше 30 дней: " + recoveries.Length + ". Эти старые папки также будут удалены.\n\nПродолжить очистку?", "Очистка файлов", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await RunOperation(async token =>
        {
            thumbnailVersion++;
            await imageLoading.WaitAsync(token);
            try
            {
                await Task.Run(() =>
                {
                    session.PruneImages(previewPath);
                    string cache = Path.Combine(session.Root, "Thumbnails");
                    if (Directory.Exists(cache)) Directory.Delete(cache, true);
                    foreach (string directory in recoveries) { token.ThrowIfCancellationRequested(); Directory.Delete(directory, true); }
                });
            }
            finally { imageLoading.Release(); ConfigureThumbnails(); RefreshPages(); }
            SetStatus("Неиспользуемые файлы очищены.");
        });
    }
}
