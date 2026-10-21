using System.Drawing.Imaging;
using System.Text.Json;

namespace HP3052Scanner;
internal sealed partial class ScannerForm : Form
{
    readonly TextBox ip = new()
    {
        Dock = DockStyle.Fill
    };
    readonly ComboBox source = Choice(), mode = Choice(), dpi = Choice(), paper = Choice(), profiles = Choice(), format = Choice(), pdfCompression = Choice();
    readonly NumericUpDown width = Number(10, 216, 210), height = Number(10, 356, 297), quality = Number(40, 100, 90);
    readonly CheckBox merged = new()
    {
        Text = "Один PDF",
        AutoSize = true,
        Checked = true
    }, selectedOnly = new()
    {
        Text = "Только выбранные",
        AutoSize = true
    };
    readonly Button scan = Button("Сканировать со стекла"), previewScan = Button("Предпросмотр со стекла"), areaScan = Button("Сканировать выделенную область"), cancel = Button("Отмена"), check = Button("Проверить связь"), save = Button("Сохранить документ…");
    readonly Button undo = Button("Отменить изменение"), redo = Button("Повторить");
    readonly CheckBox autoHint = new() { Text = "Напоминать о пустом лотке для стекла", AutoSize = true, Checked = true };
    readonly SemaphoreSlim imageLoading = new(1, 1);
    int imageVersion, thumbnailVersion;
    string? displayedFile;
    int[] dragIndexes = Array.Empty<int>();
    readonly HashSet<string> autoHintsShown = new();
    readonly Label capabilities = new()
    {
        AutoSize = true,
        MaximumSize = new Size(240, 0),
        ForeColor = Color.DimGray,
        Text = "Выберите источник и размер оригинала."
    };
    readonly ScanViewer viewer = new();
    readonly ListView pages = new()
    {
        Dock = DockStyle.Fill,
        View = View.LargeIcon,
        MultiSelect = true,
        HideSelection = false,
        AllowDrop = true,
        ShowItemToolTips = true,
        BackColor = Color.White
    };
    readonly ImageList thumbnails = new()
    {
        ImageSize = new Size(130, 165),
        ColorDepth = ColorDepth.Depth32Bit
    };
    readonly ToolStripStatusLabel status = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft,
        Text = "Готово"
    }, viewInfo = new()
    {
        Text = ""
    };
    readonly ToolStrip viewerTools = new()
    {
        GripStyle = ToolStripGripStyle.Hidden
    };
    readonly ProgressBar progress = new()
    {
        Dock = DockStyle.Bottom,
        Height = 4,
        Visible = false,
        Style = ProgressBarStyle.Marquee
    };
    readonly List<Control> lockWhileBusy = new();
    readonly ScannerBackend backend = new();
    Configuration config;
    DocumentSession session;
    CancellationTokenSource? operation;
    bool busy, loading, updatingPages, showingPreview, closeRequested, closingConfirmed, initializing = true;
    string? previewPath, lastExport;
    ScanRequest? previewRequest;
    static ComboBox Choice() => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Dock = DockStyle.Fill,
        DropDownWidth = 250
    };
    static NumericUpDown Number(decimal min, decimal max, decimal value) => new()
    {
        Minimum = min,
        Maximum = max,
        Value = value,
        DecimalPlaces = 0,
        Dock = DockStyle.Fill
    };
    static Button Button(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(0, 30),
        Padding = new Padding(5, 2, 5, 2)
    };
    public ScannerForm()
    {
        Text = "HP 3052 — сканирование документов";
        Font = new Font("Segoe UI", 9);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(1280, 820);
        MinimumSize = new Size(980, 650);
        StartPosition = FormStartPosition.CenterScreen;
        config = LoadConfiguration();
        string sessionRoot = Path.Combine(AppFiles.DataRoot, "CurrentDocument");
        try
        {
            session = new DocumentSession(sessionRoot);
        }
        catch (Exception ex)
        {
            AppFiles.Log("Project recovery: " + ex);
            string backup = sessionRoot + "_recovery_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N")[..6];
            try { Directory.Move(sessionRoot, backup); }
            catch (Exception moveError) { throw new IOException("Не удалось переместить повреждённый проект в резервную папку. Исходные файлы оставлены в " + sessionRoot + ". Закройте программы, использующие эту папку, и повторите запуск.", moveError); }
            session = new DocumentSession(sessionRoot);
            MessageBox.Show("Не удалось открыть предыдущий проект. Его файлы сохранены в:\n" + backup + "\n\nМожно начать новый документ.", "Восстановление документа", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        source.Items.AddRange(new object[] { "Стекло", "Автоподатчик (пачка листов)" });
        source.SelectedIndex = 0;
        mode.Items.AddRange(new object[] { "Цветной", "Оттенки серого", "Чёрно-белый" });
        mode.SelectedIndex = 1;
        dpi.Items.AddRange(new object[] { "75", "100", "150", "200", "300", "600", "1200" });
        dpi.SelectedItem = "300";
        paper.Items.AddRange(new object[] { "A4", "A5", "Фото 10×15", "Вся доступная область", "Свой размер" });
        paper.SelectedIndex = 0;
        format.Items.AddRange(new object[] { "PDF", "JPG", "PNG", "TIFF", "BMP" });
        format.SelectedIndex = 0;
        pdfCompression.Items.AddRange(new object[] { "Без потерь — текст", "Компактный — JPEG" });
        pdfCompression.SelectedIndex = 0;
        quality.Increment = 5;
        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 6, 10, 6),
            BackColor = Color.FromArgb(242, 246, 250)
        };
        var title = new Label
        {
            Text = "HP LaserJet 3052",
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(2, 7, 22, 0)
        };
        Button newDocument = Button("Новый документ"), openFolder = Button("Открыть папку"), openResult = Button("Открыть результат"), log = Button("Журнал");
        header.Controls.AddRange(new Control[] { title, newDocument, undo, redo, openFolder, openResult, log });
        lockWhileBusy.AddRange(new Control[] { newDocument, undo, redo });
        newDocument.Click += async (_, _) => await NewDocument();
        undo.Click += (_, _) => NavigateHistory(false);
        redo.Click += (_, _) => NavigateHistory(true);
        openFolder.Click += (_, _) => OpenLocation(lastExport ?? config.LastFolder);
        openResult.Click += (_, _) => OpenResult();
        log.Click += (_, _) => OpenLog();
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(8),
            BackColor = Color.FromArgb(242, 246, 250)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        var settingsScroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(10)
        };
        var settings = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Padding = new Padding(0, 0, 4, 10)
        };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 54));
        void Row(string label, Control c)
        {
            int r = settings.RowCount++;
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settings.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 4, 6) }, 0, r);
            settings.Controls.Add(c, 1, r);
            c.Margin = new Padding(2, 4, 2, 4);
            lockWhileBusy.Add(c);
        }

        void Wide(Control c)
        {
            int r = settings.RowCount++;
            settings.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            settings.Controls.Add(c, 0, r);
            settings.SetColumnSpan(c, 2);
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(0, 4, 0, 5);
        }

        Wide(new Label { Text = "Параметры сканирования", Font = new Font(Font, FontStyle.Bold), AutoSize = true });
        Row("Адрес / имя", ip);
        Wide(check);
        var discover = Button("Найти HP 3052 в сети");
        Wide(discover);
        lockWhileBusy.Add(discover);
        discover.Click += async (_, _) => await DiscoverScanner();
        lockWhileBusy.Add(check);
        Row("Профиль", profiles);
        var profileButtons = new FlowLayoutPanel
        {
            AutoSize = true
        };
        Button saveProfile = Button("Сохранить…"), deleteProfile = Button("Удалить");
        profileButtons.Controls.AddRange(new Control[] { saveProfile, deleteProfile });
        Wide(profileButtons);
        lockWhileBusy.Add(profileButtons);
        Row("Источник", source);
        Row("Режим", mode);
        Row("Разрешение DPI", dpi);
        Row("Оригинал", paper);
        Row("Ширина, мм", width);
        Row("Высота, мм", height);
        Wide(capabilities);
        autoHint.Checked = config.ShowAutoSourceHint;
        Wide(autoHint);
        lockWhileBusy.Add(autoHint);
        var cleanup = Button("Очистить временные файлы…");
        Wide(cleanup);
        lockWhileBusy.Add(cleanup);
        cleanup.Click += async (_, _) => await CleanStorage();
        cancel.Enabled = false;
        lockWhileBusy.AddRange(new Control[] { scan, previewScan, areaScan });
        Wide(new Label { Text = "Автоподатчик: загрузите листы лицевой стороной вверх и подведите направляющие.", AutoSize = true, MaximumSize = new Size(235, 0), ForeColor = Color.DimGray, Padding = new Padding(0, 8, 0, 0) });
        settingsScroll.Controls.Add(settings);
        layout.Controls.Add(settingsScroll, 0, 0);
        var center = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 0, 6, 0)
        };
        ToolStripButton Tool(string text, Action action)
        {
            var b = new ToolStripButton(text);
            b.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    Warn(e.Message);
                }
            };
            viewerTools.Items.Add(b);
            return b;
        }

        Tool("−", () => viewer.ChangeZoom(1 / 1.25));
        Tool("+", () => viewer.ChangeZoom(1.25));
        Tool("Вписать", viewer.FitPage);
        Tool("По ширине", viewer.FitWidth);
        Tool("100%", viewer.ActualSize);
        viewerTools.Items.Add(new ToolStripSeparator());
        Tool("↶ 90°", () => Rotate(false));
        Tool("↷ 90°", () => Rotate(true));
        Tool("180°", () => Rotate180());
        Tool("Обрезать", Crop);
        Tool("Автообработка…", AutoProcessPage);
        Tool("Предпросмотр", ShowPreview);
        center.Controls.Add(viewer);
        center.Controls.Add(viewerTools);
        viewerTools.Dock = DockStyle.Top;
        layout.Controls.Add(center, 1, 0);
        pages.LargeImageList = thumbnails;
        var pagePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White
        };
        var pageLabel = new Label
        {
            Text = "Страницы документа",
            Dock = DockStyle.Top,
            Height = 29,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font, FontStyle.Bold)
        };
        var pageTools = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(3)
        };
        Button rename = Button("Имя"), remove = Button("Удалить"), up = Button("↑"), down = Button("↓");
        pageTools.Controls.AddRange(new Control[] { rename, remove, up, down });
        lockWhileBusy.Add(pageTools);
        pagePanel.Controls.Add(pages);
        pagePanel.Controls.Add(pageTools);
        pagePanel.Controls.Add(pageLabel);
        layout.Controls.Add(pagePanel, 2, 0);
        var exports = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(10),
            BackColor = Color.White
        };
        void ExportControl(string label, Control c)
        {
            exports.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(5, 8, 3, 3) });
            c.Dock = DockStyle.None;
            exports.Controls.Add(c);
            lockWhileBusy.Add(c);
        }

        format.Width = 78;
        ExportControl("Формат", format);
        exports.Controls.Add(merged);
        pdfCompression.Width = 170;
        exports.Controls.Add(pdfCompression);
        quality.Width = 55;
        ExportControl("Качество JPEG", quality);
        exports.Controls.AddRange(new Control[] { selectedOnly, save });
        lockWhileBusy.AddRange(new Control[] { merged, pdfCompression, selectedOnly, save });
        var statusBar = new StatusStrip();
        statusBar.Items.AddRange(new ToolStripItem[] { status, viewInfo });
        var scanBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 2, 10, 6),
            BackColor = Color.FromArgb(242, 246, 250)
        };
        scanBar.Controls.AddRange(new Control[] { scan, previewScan, areaScan, cancel });
        Controls.Add(layout);
        Controls.Add(exports);
        Controls.Add(progress);
        Controls.Add(statusBar);
        Controls.Add(scanBar);
        Controls.Add(header);
        ip.Text = config.ip;
        ReloadProfiles();
        ApplyProfile(config.Current ?? new());
        profiles.SelectedIndexChanged += (_, _) =>
        {
            if (!loading && profiles.SelectedIndex >= 0)
                ApplyProfile(config.Profiles[profiles.SelectedIndex]);
        };
        saveProfile.Click += (_, _) => SaveProfile();
        deleteProfile.Click += (_, _) => DeleteProfile();
        paper.SelectedIndexChanged += (_, _) => PaperChanged();
        source.SelectedIndexChanged += (_, _) =>
        {
            scan.Text = source.SelectedIndex == 1 ? "Сканировать из автоподатчика" : "Сканировать со стекла";
            ResetResolutions();
        };
        format.SelectedIndexChanged += (_, _) => ExportOptions();
        merged.CheckedChanged += (_, _) => ExportOptions();
        pdfCompression.SelectedIndexChanged += (_, _) => ExportOptions();
        scan.Click += async (_, _) => await ScanDocument();
        previewScan.Click += async (_, _) => await ScanPreview();
        areaScan.Click += async (_, _) => await ScanArea();
        check.Click += async (_, _) => await CheckConnection();
        cancel.Click += (_, _) => operation?.Cancel();
        save.Click += async (_, _) => await ExportDocument();
        pages.SelectedIndexChanged += (_, _) =>
        {
            if (!updatingPages && !busy)
                ShowSelectedPage();
        };
        rename.Click += (_, _) => RenamePage();
        remove.Click += (_, _) => RemovePages();
        up.Click += (_, _) => MovePage(-1);
        down.Click += (_, _) => MovePage(1);
        pages.ItemDrag += (_, e) =>
        {
            if (!busy && e.Item is ListViewItem item)
            {
                dragIndexes = pages.SelectedIndices.Cast<int>().OrderBy(i => i).ToArray();
                pages.DoDragDrop(dragIndexes, DragDropEffects.Move);
            }
        };
        pages.DragOver += (_, e) => e.Effect = !busy && e.Data?.GetDataPresent(typeof(int[])) == true ? DragDropEffects.Move : DragDropEffects.None;
        pages.DragDrop += (_, e) =>
        {
            if (busy || dragIndexes.Length == 0)
                return;
            var item = pages.GetItemAt(pages.PointToClient(new Point(e.X, e.Y)).X, pages.PointToClient(new Point(e.X, e.Y)).Y);
            int target = item?.Index ?? session.Pages.Count;
            var movedIds = dragIndexes.Select(i => session.Pages[i].Id).ToHashSet();
            Edit(() => session.MoveMany(dragIndexes, target), -1);
            foreach (ListViewItem row in pages.Items) row.Selected = movedIds.Contains(session.Pages[row.Index].Id);
            dragIndexes = Array.Empty<int>();
        };
        viewer.ViewChanged += (_, _) =>
        {
            var r = viewer.Selection;
            viewInfo.Text = $"Масштаб {viewer.Zoom * 100:0}%" + (r.Width > 0 ? $"  Область {r.Width}×{r.Height} px" : "");
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && (e.KeyCode == Keys.Z || e.KeyCode == Keys.Y) && !busy && !TextInputFocused())
            {
                NavigateHistory(e.KeyCode == Keys.Y || e.Shift);
                e.Handled = e.SuppressKeyPress = true;
            }

            if (e.Control && e.KeyCode == Keys.S && !busy)
            {
                save.PerformClick();
                e.Handled = true;
            }
        };
        FormClosing += HandleClosing;
        FormClosed += (_, _) =>
        {
            imageVersion++;
            thumbnailVersion++;
            try { session.ClearHistory(); session.PruneImages(); } catch (Exception e) { AppFiles.Log("Project cleanup: " + e.Message); }
            TryDelete(previewPath);
            thumbnails.Dispose();
        };
        Shown += async (_, _) =>
        {
            var wa = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
            Size = new Size(Math.Min(Width, wa.Width), Math.Min(Height, wa.Height));
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
            await RestoreDocument();
        };
        ConfigureThumbnails();
        DpiChanged += (_, _) => { ConfigureThumbnails(); RefreshPages(); };
        PaperChanged();
        ExportOptions();
        UpdateTitle();
    }

    Configuration LoadConfiguration()
    {
        var c = SettingsStore.Load(AppFiles.ConfigPath, out string? warning);
        if (warning != null) MessageBox.Show(warning, "Восстановление настроек", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return c;
    }

    ScanProfile CurrentProfile() => new()
    {
        Name = string.IsNullOrWhiteSpace(profiles.Text) ? "Пользовательские" : profiles.Text,
        Mode = mode.SelectedIndex switch
        {
            0 => "Color",
            2 => "Lineart",
            _ => "Gray"
        },
        Dpi = int.Parse(dpi.Text),
        Source = source.SelectedIndex == 1 ? "ADF" : "Flatbed",
        Paper = paper.Text,
        Width = (double)width.Value,
        Height = (double)height.Value,
        Format = format.Text,
        Merged = merged.Checked,
        LosslessPdf = pdfCompression.SelectedIndex == 0,
        JpegQuality = (int)quality.Value
    };
    void SaveConfiguration(bool requireIp = false)
    {
        var value = ip.Text.Trim();
        try
        {
            ScannerAddress.Validate(value);
            config.ip = value;
        }
        catch
        {
            if (requireIp)
                throw;
        }

        config.Current = CurrentProfile();
        config.ShowAutoSourceHint = autoHint.Checked;
        SettingsStore.Save(AppFiles.ConfigPath, config);
    }

    void ReloadProfiles()
    {
        loading = true;
        profiles.Items.Clear();
        profiles.Items.AddRange(config.Profiles.Select(x => (object)x.Name).ToArray());
        profiles.SelectedIndex = -1;
        loading = false;
    }

    void ApplyProfile(ScanProfile p)
    {
        loading = true;
        ResetResolutions();
        source.SelectedIndex = p.Source == "ADF" ? 1 : 0;
        mode.SelectedIndex = p.Mode switch
        {
            "Color" => 0,
            "Lineart" => 2,
            _ => 1
        };
        dpi.SelectedItem = p.Dpi.ToString();
        if (dpi.SelectedIndex < 0)
            dpi.SelectedItem = "300";
        paper.SelectedItem = p.Paper;
        if (paper.SelectedIndex < 0)
            paper.SelectedIndex = 0;
        width.Value = (decimal)(double.IsFinite(p.Width) ? Math.Clamp(p.Width, (double)width.Minimum, (double)width.Maximum) : 210);
        height.Value = (decimal)(double.IsFinite(p.Height) ? Math.Clamp(p.Height, (double)height.Minimum, (double)height.Maximum) : 297);
        format.SelectedItem = p.Format;
        if (format.SelectedIndex < 0)
            format.SelectedIndex = 0;
        merged.Checked = p.Merged;
        pdfCompression.SelectedIndex = p.LosslessPdf ? 0 : 1;
        quality.Value = Math.Clamp(p.JpegQuality, 40, 100);
        profiles.SelectedIndex = config.Profiles.FindIndex(x => x.Name == p.Name);
        loading = false;
        PaperChanged();
        ExportOptions();
        scan.Text = source.SelectedIndex == 1 ? "Сканировать из автоподатчика" : "Сканировать со стекла";
    }

    void PaperChanged()
    {
        if (loading)
            return;
        switch (paper.SelectedIndex)
        {
            case 0:
                width.Value = 210;
                height.Value = 297;
                break;
            case 1:
                width.Value = 148;
                height.Value = 210;
                break;
            case 2:
                width.Value = 100;
                height.Value = 150;
                break;
        }

        width.Enabled = height.Enabled = paper.SelectedIndex == 4 && !busy;
    }

    void ResetResolutions()
    {
        string prior = dpi.Text;
        dpi.Items.Clear();
        dpi.Items.AddRange(new object[] { "75", "100", "150", "200", "300", "600", "1200" });
        dpi.SelectedItem = prior;
        if (dpi.SelectedIndex < 0)
            dpi.SelectedItem = "300";
        capabilities.Text = "Возможности источника уточняются при проверке связи.";
    }

    void ExportOptions()
    {
        bool pdf = format.Text == "PDF";
        merged.Enabled = pdf && !busy;
        pdfCompression.Enabled = pdf && !busy;
        quality.Enabled = !busy && (format.Text == "JPG" || pdf && pdfCompression.SelectedIndex == 1);
    }

    void SaveProfile()
    {
        if (busy)
            return;
        string? name = AskText("Сохранить профиль", "Название профиля", profiles.Text);
        if (string.IsNullOrWhiteSpace(name))
            return;
        try
        {
            var p = CurrentProfile();
            p.Name = name.Trim();
            int i = config.Profiles.FindIndex(x => x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && MessageBox.Show(this, "Заменить существующий профиль «" + p.Name + "»?", "Профиль", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            if (i >= 0)
                config.Profiles[i] = p;
            else
                config.Profiles.Add(p);
            SaveConfiguration();
            ReloadProfiles();
            loading = true;
            profiles.SelectedIndex = config.Profiles.FindIndex(x => x.Name == p.Name);
            loading = false;
            SetStatus("Профиль сохранён.");
        }
        catch (Exception e)
        {
            Warn(e.Message);
        }
    }

    void DeleteProfile()
    {
        if (busy || profiles.SelectedIndex < 0)
            return;
        if (MessageBox.Show(this, "Удалить профиль «" + profiles.Text + "»?", "Профиль", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        try
        {
            config.Profiles.RemoveAt(profiles.SelectedIndex);
            SaveConfiguration();
            ReloadProfiles();
        }
        catch (Exception e)
        {
            Warn(e.Message);
        }
    }

    ScanRequest CaptureRequest(bool preview = false)
    {
        SaveConfiguration(true);
        var p = CurrentProfile();
        return new(config.ip, config.distro, preview ? "Color" : p.Mode, preview ? 75 : p.Dpi, preview ? "Flatbed" : p.Source, 0, 0, p.Width, p.Height);
    }

    async Task<ScanRequest> PrepareRequest(ScanRequest request, CancellationToken token, bool refresh = false, bool expandPaper = true, bool probeOnly = false, bool preview = false)
    {
        var prepared = await backend.Prepare(request, token, refresh, false);
        var c = prepared.Capabilities;
        capabilities.Text = "Драйвер: " + (request.Source == "ADF" ? "автоподатчик" : "стекло") + "; DPI " + (c.Resolutions.Count > 0 ? string.Join(", ", c.Resolutions) : $"{c.MinDpi}–{c.MaxDpi}");
        if (expandPaper && paper.SelectedIndex == 3)
            request = prepared.Request with
            {
                Width = c.MaxWidth ?? request.Width,
                Height = c.MaxHeight ?? request.Height,
                DriverDefaultArea = true
            };
        else
            request = prepared.Request;
        if (request.RequiresEmptyFeeder)
            capabilities.Text += ". Для стекла лоток автоподатчика должен быть пуст (HPLIP Auto).";
        if (preview) request = ScanPolicy.Preview(request, c);
        if (!loading && !preview && request.Source == (source.SelectedIndex == 1 ? "ADF" : "Flatbed"))
        {
            var choices = (c.Resolutions.Count > 0 ? c.Resolutions : new[]
            {
                75,
                100,
                150,
                200,
                300,
                600,
                1200
            }.Append(c.MinDpi).Append(c.MaxDpi)).Where(n => n >= c.MinDpi && n <= c.MaxDpi).Distinct().OrderBy(n => n).ToArray();
            if (choices.Length > 0)
            {
                dpi.Items.Clear();
                dpi.Items.AddRange(choices.Select(n => (object)n.ToString()).ToArray());
                dpi.SelectedItem = request.Dpi.ToString();
                if (dpi.SelectedIndex < 0)
                    dpi.SelectedIndex = Array.IndexOf(choices, 300) >= 0 ? Array.IndexOf(choices, 300) : 0;
            }
        }

        if (!probeOnly)
        {
            c.Validate(request);
            string hintKey = request.Ip + "|" + request.Distro;
            if (request.RequiresEmptyFeeder && autoHint.Checked && !autoHintsShown.Contains(hintKey) && MessageBox.Show(this,
                "Драйвер этого сканера использует режим Auto для сканирования со стекла.\n\nУберите все листы из верхнего лотка автоподатчика и положите оригинал на стекло. Иначе аппарат может взять лист из лотка.\n\nПродолжить сканирование?",
                "Сканирование со стекла", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                throw new OperationCanceledException(token);
            if (request.RequiresEmptyFeeder) autoHintsShown.Add(hintKey);
        }
        return request;
    }

    async Task<bool> RunOperation(Func<CancellationToken, Task> action, bool saving = false)
    {
        if (busy || initializing)
            return false;
        busy = true;
        operation = new();
        foreach (var c in lockWhileBusy)
            c.Enabled = false;
        pages.Enabled = false;
        viewerTools.Enabled = false;
        viewer.Enabled = false;
        cancel.Enabled = true;
        progress.Visible = true;
        bool ok = false;
        try
        {
            await action(operation.Token);
            ok = true;
        }
        catch (OperationCanceledException)
        {
            SetStatus(saving ? "Сохранение отменено. Страницы остаются в проекте." : "Операция отменена. Полученные страницы остаются в проекте.");
        }
        catch (Exception e)
        {
            AppFiles.Log(e.ToString());
            SetStatus("Операция не завершена. Страницы сохранены в проекте.");
            Warn(e.Message);
        }
        finally
        {
            busy = false;
            operation.Dispose();
            operation = null;
            foreach (var c in lockWhileBusy)
                c.Enabled = true;
            pages.Enabled = true;
            viewer.Enabled = true;
            viewerTools.Enabled = true;
            cancel.Enabled = false;
            progress.Visible = false;
            PaperChanged();
            ExportOptions();
            UpdateTitle();
            if (closeRequested)
            {
                closeRequested = false;
                BeginInvoke(new Action(Close));
            }
        }

        return ok;
    }

    async Task CheckConnection()
    {
        ScanRequest r;
        try
        {
            r = CaptureRequest();
        }
        catch (Exception e)
        {
            Warn(e.Message);
            return;
        }

        await RunOperation(async token =>
        {
            SetStatus("Проверка сканера без подачи бумаги…");
            await PrepareRequest(r, token, true, true, true);
            SetStatus("Сканер отвечает. " + capabilities.Text);
        });
    }

    async Task ScanDocument()
    {
        ScanRequest r;
        try
        {
            r = CaptureRequest();
        }
        catch (Exception e)
        {
            Warn(e.Message);
            return;
        }

        await RunOperation(async token =>
        {
            SetStatus("Проверка параметров сканера…");
            r = await PrepareRequest(r, token);
            if (r.Source.Equals("Flatbed", StringComparison.OrdinalIgnoreCase))
            {
                using var b = await Acquire(r, token);
                await StoreBitmap(b, pixelMode: r.Mode);
                RefreshPages(session.Pages.Count - 1);
                SetStatus("Страница добавлена в документ.");
            }
            else
                await ScanFeeder(r, token);
        });
    }

    async Task<Bitmap> Acquire(ScanRequest request, CancellationToken token)
    {
        string temp = Path.Combine(Path.GetTempPath(), "hp3052_" + Guid.NewGuid().ToString("N") + ".tiff");
        try
        {
            SetStatus("Сканирование со стекла…");
            AppFiles.Log("Glass scan request:\n" + JsonSerializer.Serialize(request, AppFiles.Json));
            var result = await backend.RunAsync(request.Distro, request.Arguments() + " > " + ScanRequest.Quote(ScannerBackend.ToWslPath(temp)), token, ScanPolicy.Timeout(request));
            result.RequireSuccess();
            return await Task.Run(() => ScanImages.Load(temp, request.Dpi));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    async Task ScanPreview()
    {
        ScanRequest r;
        try
        {
            r = CaptureRequest(true);
        }
        catch (Exception e)
        {
            Warn(e.Message);
            return;
        }

        await RunOperation(async token =>
        {
            r = await PrepareRequest(r, token, expandPaper: false, preview: true);
            using var b = await Acquire(r, token);
            string path = session.NewImagePath();
            await Task.Run(() => ScanImages.SavePng(b, path), token);
            TryDelete(previewPath);
            previewPath = path;
            previewRequest = ScanPolicy.PreviewResult(r, b.Width, b.Height);
            ShowPreview();
            SetStatus("Предпросмотр готов. Выделите область левой кнопкой мыши.");
        });
    }

    async Task ScanArea()
    {
        if (previewPath == null || previewRequest == null || !showingPreview || viewer.Image == null || viewer.Selection.Width < 1 || viewer.Selection.Height < 1)
        {
            Warn("Откройте предпросмотр со стекла и выделите область мышью.");
            return;
        }

        ScanRequest current;
        try
        {
            current = CaptureRequest();
        }
        catch (Exception e)
        {
            Warn(e.Message);
            return;
        }

        var rect = viewer.Selection;
        var reference = previewRequest;
        if (current.Ip != (reference.Address ?? reference.Ip) || current.Distro != reference.Distro)
        {
            Warn("Адрес сканера изменён. Выполните новый предпросмотр перед сканированием области.");
            return;
        }

        var r = current with
        {
            Ip = reference.Ip,
            Distro = reference.Distro,
            Address = reference.Address,
            Source = "Flatbed",
            DriverDefaultArea = false,
            Left = reference.Left + rect.X * 25.4 / reference.Dpi,
            Top = reference.Top + rect.Y * 25.4 / reference.Dpi,
            Width = rect.Width * 25.4 / reference.Dpi,
            Height = rect.Height * 25.4 / reference.Dpi
        };
        await RunOperation(async token =>
        {
            r = await PrepareRequest(r, token, false, false);
            using var b = await Acquire(r, token);
            await StoreBitmap(b, pixelMode: r.Mode);
            RefreshPages(session.Pages.Count - 1);
            SetStatus("Выделенная область добавлена в документ.");
        });
    }

    async Task StoreBitmap(Bitmap bitmap, string origin = "", string pixelMode = "Color")
    {
        string path = session.NewImagePath();
        await Task.Run(() => ScanImages.SavePng(bitmap, path, pixelMode));
        session.Add(path, bitmap.Width, bitmap.Height, bitmap.HorizontalResolution, bitmap.VerticalResolution, origin, pixelMode);
        UpdateTitle();
    }

    async Task ScanFeeder(ScanRequest r, CancellationToken token)
    {
        string dir = Path.Combine(session.Root, "batch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        AppFiles.AtomicJson(Path.Combine(dir, "request.json"), r);
        string pattern = ScannerBackend.ToWslPath(Path.Combine(dir, "page_%05d.tiff"));
        var reports = new Progress<int>(n => SetStatus($"Автоподатчик: получено страниц {n}. Сканирование продолжается…"));
        LinuxResult? result = null;
        Exception? failure = null;
        try
        {
            SetStatus("Сканирование из автоподатчика…");
            result = await backend.RunAsync(r.Distro, r.Arguments() + " --batch=" + ScanRequest.Quote(pattern) + " --batch-start=1 --batch-increment=1", token, 1800000, n =>
            {
                File.WriteAllText(Path.Combine(dir, $"page_{n:00000}.complete"), "");
                ((IProgress<int>)reports).Report(n);
            });
        }
        catch (Exception e)
        {
            failure = e;
        }

        int count;
        try { count = await ImportBatch(dir, r.Dpi, result != null && (result.Code == 0 || result.Code == 7), r.Mode); }
        finally { RefreshPages(session.Pages.Count - 1); }
        if (failure is OperationCanceledException)
            throw new OperationCanceledException(token);
        if (failure != null)
            throw failure;
        if (result == null)
            throw new IOException("Не получен результат сканирования.");
        var conclusion = FeederCompletion.From(result.Code, count);
        SetStatus(conclusion.Status);
        if (conclusion.Error)
            throw new IOException(ScanMessages.DescribeCommandFailure(result.Code, result.Error, result.Output));
        if (conclusion.Information != null)
            MessageBox.Show(this, conclusion.Information, "Загрузите бумагу в автоподатчик", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    async Task<int> ImportBatch(string dir, int resolution, bool allowUnmarked, string pixelMode = "Color")
    {
        int count = 0;
        var files = Directory.GetFiles(dir, "page_*.tiff").OrderBy(x => x, StringComparer.Ordinal).ToArray();
        bool failed = false;
        foreach (string file in files)
        {
            string origin = Path.GetFileName(dir) + "/" + Path.GetFileName(file);
            if (session.WasImported(origin))
                continue;
            if (!allowUnmarked && !File.Exists(Path.ChangeExtension(file, ".complete")))
                continue;
            try
            {
                using var b = await Task.Run(() => ScanImages.Load(file, resolution));
                await StoreBitmap(b, origin, pixelMode);
                count++;
            }
            catch (Exception e)
            {
                failed = true;
                AppFiles.Log("Batch page recovery: " + file + " " + e);
            }
        }

        if (!failed)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
            }
        }
        else
            throw new IOException("Часть страниц не удалось прочитать. Готовые страницы добавлены; исходные файлы сохранены в проекте для восстановления.");
        return count;
    }

    async Task RestoreDocument()
    {
        try
        {
            foreach (string dir in Directory.GetDirectories(session.Root, "batch_*"))
            {
                if (session.IsBatchIgnored(Path.GetFileName(dir)))
                    continue;
                try
                {
                    var r = JsonSerializer.Deserialize<ScanRequest>(File.ReadAllText(Path.Combine(dir, "request.json")));
                    if (r != null)
                        await ImportBatch(dir, r.Dpi, false, r.Mode);
                }
                catch (Exception e)
                {
                    AppFiles.Log("Batch restore: " + e);
                }
            }

            if (session.Pages.Count > 0)
            {
                var answer = MessageBox.Show(this, $"Найден предыдущий документ: {session.Pages.Count} стр.\n\nВосстановить его?\n«Нет» — очистить проект и начать новый документ.", "Восстановление документа", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);
                if (answer == DialogResult.No)
                    session.Clear();
            }

            RefreshPages(session.Pages.Count > 0 ? 0 : -1);
            SetStatus(session.Pages.Count > 0 ? "Документ восстановлен. Можно продолжить работу." : "Готово к сканированию.");
        }
        catch (Exception e)
        {
            AppFiles.Log(e.ToString());
            Warn("Не удалось полностью восстановить документ. " + e.Message);
            RefreshPages();
        }
        finally
        {
            initializing = false;
            UpdateTitle();
        }
    }

    int OnePage()
    {
        if (showingPreview || pages.SelectedIndices.Count != 1)
        {
            Warn("Выберите одну страницу документа справа.");
            return -1;
        }

        int selected = pages.SelectedIndices[0];
        if (displayedFile != session.ImagePath(session.Pages[selected])) { Warn("Дождитесь загрузки выбранной страницы."); return -1; }
        return selected;
    }

    void Edit(Action action, int select = -2)
    {
        if (busy || initializing)
            return;
        try
        {
            var before = session.Pages.ToArray();
            action();
            if (before.SequenceEqual(session.Pages)) { SetStatus("Нет изменений для отмены или применения."); UpdateTitle(); return; }
            RefreshPages(select);
            SetStatus("Изменения сохранены в проекте. Для сохранения в PDF или изображения нажмите «Сохранить документ».");
        }
        catch (Exception e)
        {
            AppFiles.Log(e.ToString());
            Warn(e.Message);
        }
    }

    void RenamePage()
    {
        int i = OnePage();
        if (i < 0)
            return;
        string? name = AskText("Имя страницы", "Название", session.Pages[i].Name);
        if (!string.IsNullOrWhiteSpace(name))
            Edit(() => session.Rename(i, ExportNames.Safe(name)), i);
    }

    void RemovePages()
    {
        if (busy || pages.SelectedIndices.Count == 0)
            return;
        var ids = pages.SelectedIndices.Cast<int>().ToArray();
        Edit(() => session.Remove(ids), Math.Max(0, ids.Min() - 1));
    }

    void MovePage(int delta)
    {
        int i = OnePage();
        if (i >= 0)
            Edit(() => session.Move(i, i + delta), Math.Clamp(i + delta, 0, session.Pages.Count - 1));
    }

    void Rotate(bool clockwise) => RotatePage(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone, true);
    void Rotate180() => RotatePage(RotateFlipType.Rotate180FlipNone, false);
    async Task<bool> ExportDocument(bool forceAll = false)
    {
        if (busy || initializing)
            return false;
        if (session.Pages.Count == 0)
        {
            Warn("Нет отсканированных страниц.");
            return false;
        }

        try
        {
            SaveConfiguration();
        }
        catch (Exception e)
        {
            Warn(e.Message);
            return false;
        }

        var chosen = ExportSelection.Choose(session.Pages, pages.SelectedIndices.Cast<int>(), selectedOnly.Checked, forceAll);
        if (chosen.Count == 0)
        {
            Warn("Выберите страницы для сохранения или снимите флажок «Только выбранные».");
            return false;
        }

        string f = format.Text, extension = f.ToLowerInvariant();
        bool single = f == "PDF" && merged.Checked;
        string folder = config.LastFolder;
        if (!Directory.Exists(folder))
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string[] planned;
        try
        {
            var suggested = ExportSavePlan.Suggest(folder, f, chosen.Count, single, DateTime.Now);
            using var dialog = new SaveFileDialog
            {
                Title = single || chosen.Count == 1 ? "Сохранить документ " + f : $"Сохранить {chosen.Count} страниц {f}: имя — основа файлов",
                Filter = ExportSavePlan.Filter(f),
                FileName = Path.GetFileName(suggested),
                InitialDirectory = folder,
                OverwritePrompt = false,
                AddExtension = true,
                DefaultExt = extension,
                CheckPathExists = true,
                ValidateNames = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return false;
            planned = ExportSavePlan.FromChoice(dialog.FileName, f, chosen.Count, single);
            folder = Path.GetDirectoryName(planned[0])!;
        }
        catch (Exception e)
        {
            Warn(e.Message);
            return false;
        }

        bool overwrite = false;
        int existing = planned.Count(File.Exists);
        if (existing > 0)
        {
            if (MessageBox.Show(this, $"Уже существуют файлы: {existing}.\nЗаменить их?", "Сохранение документа", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return false;
            overwrite = true;
        }

        bool lossless = pdfCompression.SelectedIndex == 0;
        int jpeg = (int)quality.Value;
        int written = 0;
        string destination = folder;
        bool ok = await RunOperation(async token =>
        {
            await Task.Run(() =>
            {
                if (single)
                {
                    PdfWriter.Write(planned[0], chosen, session.ImagePath, lossless, jpeg, overwrite, token);
                    written = 1;
                }
                else
                    for (int i = 0; i < chosen.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (f == "PDF")
                            PdfWriter.Write(planned[i], new[] { chosen[i] }, session.ImagePath, lossless, jpeg, overwrite, token);
                        else
                            ScanImages.AtomicImage(session.ImagePath(chosen[i]), planned[i], f, jpeg, overwrite);
                        written++;
                    }
            }, token);
            if (chosen.Count == session.Pages.Count)
                session.MarkSaved();
            config.LastFolder = destination;
            SaveConfiguration();
            lastExport = planned.Length == 1 ? planned[0] : destination;
            SetStatus($"Сохранено файлов: {written}. {destination}");
        }, true);
        if (!ok && written > 0)
            MessageBox.Show(this, $"До остановки сохранено файлов: {written}.\nПапка: {destination}\nВсе страницы остаются в проекте.", "Частичное сохранение", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return ok;
    }

    async Task NewDocument()
    {
        if (busy)
            return;
        if (session.Dirty)
        {
            var answer = MessageBox.Show(this, "Сохранить текущий документ в файлы перед очисткой проекта?", "Новый документ", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes && !await ExportDocument(true))
                return;
        }
        else if (session.Pages.Count > 0 && MessageBox.Show(this, "Начать новый документ и очистить список страниц?", "Новый документ", MessageBoxButtons.YesNo) != DialogResult.Yes)
            return;
        try
        {
            session.Clear();
            TryDelete(previewPath);
            previewPath = null;
            previewRequest = null;
            thumbnails.Images.Clear();
            RefreshPages(-1);
            SetStatus("Новый документ. Готово к сканированию.");
        }
        catch (Exception e)
        {
            Warn(e.Message);
        }
    }

    void HandleClosing(object? sender, FormClosingEventArgs e)
    {
        if (closingConfirmed)
            return;
        if (busy)
        {
            e.Cancel = true;
            closeRequested = true;
            operation?.Cancel();
            SetStatus("Остановка операции перед закрытием…");
            return;
        }

        if (initializing)
        {
            e.Cancel = true;
            return;
        }

        try
        {
            SaveConfiguration();
        }
        catch (Exception error)
        {
            AppFiles.Log(error.ToString());
        }

        if (!session.Dirty)
            return;
        var answer = MessageBox.Show(this, "Документ ещё не сохранён в файлы. Сохранить перед выходом?\n\nПроект останется доступен для восстановления при следующем запуске.", "Несохранённый документ", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == DialogResult.Yes)
        {
            e.Cancel = true;
            BeginInvoke(new Action(async () =>
            {
                if (await ExportDocument(true))
                {
                    closingConfirmed = true;
                    Close();
                }
            }));
        }
    }

    void UpdateTitle()
    {
        undo.Enabled = !busy && !initializing && session.CanUndo;
        redo.Enabled = !busy && !initializing && session.CanRedo;
        Text = "HP 3052 — " + "Документ" + (session.Dirty ? " *" : "") + $" — {session.Pages.Count} стр.";
    }
    void SetStatus(string text)
    {
        status.Text = text;
    }

    void Warn(string text) => MessageBox.Show(this, text, "HP 3052", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    void OpenLocation(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", path } });
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
                Warn("Папка ещё не существует. Сначала сохраните документ.");
        }
        catch (Exception e)
        {
            Warn(e.Message);
        }
    }

    void OpenResult()
    {
        try
        {
            if (lastExport != null && File.Exists(lastExport))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lastExport) { UseShellExecute = true });
            else if (lastExport != null && Directory.Exists(lastExport))
                OpenLocation(lastExport);
            else
                Warn("Сначала сохраните документ.");
        }
        catch (Exception e)
        {
            Warn(e.Message);
        }
    }

    void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(AppFiles.DataRoot);
            string path = Path.Combine(AppFiles.DataRoot, "application.log");
            if (!File.Exists(path))
                File.WriteAllText(path, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = true, ArgumentList = { path } });
        }
        catch (Exception e)
        {
            Warn(e.Message);
        }
    }

    static void TryDelete(string? path)
    {
        if (path != null)
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
    }

    string? AskText(string title, string prompt, string value)
    {
        using var dialog = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(400, 130),
            MaximizeBox = false,
            MinimizeBox = false
        };
        var label = new Label
        {
            Text = prompt,
            Location = new Point(12, 12),
            AutoSize = true
        };
        var text = new TextBox
        {
            Text = value,
            Location = new Point(12, 38),
            Width = 374,
            MaxLength = 100
        };
        Button ok = Button("Сохранить"), back = Button("Отмена");
        ok.DialogResult = DialogResult.OK;
        back.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft
        };
        buttons.Controls.AddRange(new Control[] { back, ok });
        dialog.Controls.AddRange(new Control[] { label, text, buttons });
        dialog.AcceptButton = ok;
        dialog.CancelButton = back;
        dialog.Shown += (_, _) =>
        {
            text.Focus();
            text.SelectAll();
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? text.Text : null;
    }
}
