namespace HP3052Scanner;

internal sealed class ProcessingDialog : Form
{
    readonly Bitmap source;
    readonly CheckBox deskew = new() { Text = "Выровнять наклон до ±5°", Checked = true, AutoSize = true };
    readonly CheckBox crop = new() { Text = "Обрезать пустые поля", Checked = true, AutoSize = true };
    readonly CheckBox readable = new() { Text = "Осветлить фон и усилить текст", Checked = true, AutoSize = true };
    readonly CheckBox specks = new() { Text = "Убрать одиночные точки", Checked = false, AutoSize = true };
    readonly ComboBox strength = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    readonly NumericUpDown margin = new() { Minimum = 0, Maximum = 20, Value = 3, Width = 55 };
    readonly Button update = new() { Text = "Обновить предпросмотр", AutoSize = true };
    readonly Button apply = new() { Text = "Применить", AutoSize = true, Enabled = false, DialogResult = DialogResult.OK };
    readonly Button cancel = new() { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
    readonly Label details = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10), MaximumSize = new Size(1000, 0) };
    readonly FlowLayoutPanel settings = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10), WrapContents = true };
    readonly ScanViewer originalViewer = new(), processedViewer = new();
    readonly CancellationTokenSource lifetime = new();
    Task? rendering;
    bool closingAfterRender;
    public ProcessingPlan? Plan { get; private set; }
    public ProcessingOptions Options { get; private set; } = new();

    public ProcessingDialog(Bitmap previewSource)
    {
        source = previewSource;
        Text = "Автообработка страницы";
        Font = new Font("Segoe UI", 9);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Size = new Size(1160, 800); MinimumSize = new Size(760, 540); StartPosition = FormStartPosition.CenterParent;
        strength.Items.AddRange(new object[] { "Бережно", "Сильнее" }); strength.SelectedIndex = 0;
        settings.Controls.AddRange(new Control[] { deskew, crop, new Label { Text = "Поле, мм", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, margin, readable, strength, specks, update });
        var tips = new ToolTip();
        tips.SetToolTip(specks, "Необязательный фильтр одиночных пикселей. Проверьте мелкие точки и знаки препинания.");
        var views = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
        views.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); views.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        views.RowStyles.Add(new RowStyle(SizeType.AutoSize)); views.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        views.Controls.Add(new Label { Text = "Оригинал", AutoSize = true, Padding = new Padding(4) }, 0, 0);
        views.Controls.Add(new Label { Text = "После обработки", AutoSize = true, Padding = new Padding(4) }, 1, 0);
        views.Controls.Add(originalViewer, 0, 1); views.Controls.Add(processedViewer, 1, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(10) };
        buttons.Controls.AddRange(new Control[] { cancel, apply });
        Controls.Add(views); Controls.Add(details); Controls.Add(buttons); Controls.Add(settings);
        AcceptButton = apply; CancelButton = cancel;
        originalViewer.SetImage((Bitmap)source.Clone());
        void Changed() { Plan = null; apply.Enabled = false; details.Text = "Параметры изменены. Нажмите «Обновить предпросмотр»."; }
        foreach (var checkbox in new[] { deskew, crop, readable, specks }) checkbox.CheckedChanged += (_, _) => Changed();
        strength.SelectedIndexChanged += (_, _) => Changed(); margin.ValueChanged += (_, _) => Changed();
        update.Click += async (_, _) => await Render();
        Shown += async (_, _) =>
        {
            var area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            await Render();
        };
        FormClosing += (_, e) =>
        {
            if (rendering is { IsCompleted: false } && !closingAfterRender)
            {
                e.Cancel = true; lifetime.Cancel(); cancel.Enabled = false;
                _ = CloseAfterRender();
            }
        };
        FormClosed += (_, _) => { lifetime.Cancel(); tips.Dispose(); lifetime.Dispose(); };
    }
    async Task CloseAfterRender()
    {
        if (rendering != null) try { await rendering; } catch { }
        closingAfterRender = true;
        if (!IsDisposed) { DialogResult = DialogResult.Cancel; Close(); }
    }
    async Task Render()
    {
        if (rendering is { IsCompleted: false } || lifetime.IsCancellationRequested) return;
        Options = new(deskew.Checked, crop.Checked, readable.Checked, strength.SelectedIndex == 1, specks.Checked, (double)margin.Value);
        settings.Enabled = false; apply.Enabled = false; Plan = null; details.Text = "Подготовка предпросмотра…";
        rendering = RenderCore(Options);
        await rendering;
    }
    async Task RenderCore(ProcessingOptions options)
    {
        Bitmap? image = null;
        try
        {
            var result = await Task.Run(() =>
            {
                var plan = PageProcessing.Analyze(source, options, lifetime.Token);
                return (Plan: plan, Image: PageProcessing.Apply(source, plan, options, lifetime.Token));
            });
            image = result.Image;
            if (lifetime.IsCancellationRequested || IsDisposed) return;
            processedViewer.SetImage(image); image = null; Plan = result.Plan;
            var bounds = Plan.Bounds(source.Width, source.Height);
            details.Text = $"Поворот: {Plan.Angle:+0.0;-0.0;0}°. Размер результата: {bounds.Width * 25.4 / source.HorizontalResolution:0.#} × {bounds.Height * 25.4 / source.VerticalResolution:0.#} мм. {Plan.Note}\nПредпросмотр уменьшен; обработка будет применена к полному скану. Ctrl+Z отменяет результат после применения.";
            apply.Enabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { AppFiles.Log(e.ToString()); if (!IsDisposed) details.Text = "Не удалось подготовить предпросмотр: " + e.Message; }
        finally { image?.Dispose(); if (!IsDisposed) settings.Enabled = true; }
    }
}
