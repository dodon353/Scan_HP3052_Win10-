using System.Drawing.Drawing2D;

namespace HP3052Scanner;
internal sealed class ScanViewer : Control
{
    Bitmap? image;
    Bitmap? reduced;
    double zoom = 1;
    PointF origin;
    Point start;
    PointF originalOrigin;
    PointF selectionStart;
    bool selecting, panning;
    string fit = "page";
    public Rectangle Selection { get; private set; }

    public event EventHandler? ViewChanged;
    public Bitmap? Image => image;
    public double Zoom => zoom;

    public ScanViewer()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(51, 57, 65);
        ForeColor = Color.White;
        Dock = DockStyle.Fill;
        TabStop = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public void SetImage(Bitmap? bitmap, Bitmap? small = null)
    {
        var old = image;
        image = bitmap;
        reduced?.Dispose();
        reduced = small;
        Selection = Rectangle.Empty;
        fit = "page";
        Fit();
        old?.Dispose();
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void FitPage()
    {
        fit = "page";
        Fit();
    }

    public void FitWidth()
    {
        fit = "width";
        Fit();
    }

    public void ActualSize()
    {
        fit = "none";
        zoom = 1;
        CenterImage();
        Changed();
    }

    void Fit()
    {
        if (image == null || Width < 1 || Height < 1)
            return;
        zoom = fit == "width" ? (double)Math.Max(1, Width - 28) / image.Width : Math.Min((double)Math.Max(1, Width - 28) / image.Width, (double)Math.Max(1, Height - 28) / image.Height);
        zoom = Math.Clamp(zoom, .01, 8);
        CenterImage();
        Changed();
    }

    void CenterImage()
    {
        if (image != null)
            origin = new PointF((float)((Width - image.Width * zoom) / 2), (float)((Height - image.Height * zoom) / 2));
    }

    void Changed()
    {
        Invalidate();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ChangeZoom(double multiplier, Point? anchor = null)
    {
        if (image == null)
            return;
        var p = anchor ?? new Point(Width / 2, Height / 2);
        double next = Math.Clamp(zoom * multiplier, .01, 8);
        origin = new PointF((float)(p.X - (p.X - origin.X) * next / zoom), (float)(p.Y - (p.Y - origin.Y) * next / zoom));
        zoom = next;
        fit = "none";
        Changed();
    }

    PointF ImagePoint(Point p) => image == null ? PointF.Empty : new PointF(Math.Clamp((p.X - origin.X) / (float)zoom, 0, image.Width), Math.Clamp((p.Y - origin.Y) / (float)zoom, 0, image.Height));
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (fit != "none")
            Fit();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ChangeZoom(e.Delta > 0 ? 1.2 : 1 / 1.2, e.Location);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (image == null)
            return;
        Capture = true;
        start = e.Location;
        if (e.Button is MouseButtons.Right or MouseButtons.Middle)
        {
            panning = true;
            originalOrigin = origin;
            Cursor = Cursors.Hand;
        }
        else if (e.Button == MouseButtons.Left)
        {
            var p = ImagePoint(e.Location);
            if (e.X < origin.X || e.Y < origin.Y || p.X >= image.Width || p.Y >= image.Height)
            {
                Capture = false;
                return;
            }

            selecting = true;
            selectionStart = p;
            Selection = Rectangle.Empty;
            Changed();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (panning)
        {
            origin = new PointF(originalOrigin.X + e.X - start.X, originalOrigin.Y + e.Y - start.Y);
            fit = "none";
            Invalidate();
        }
        else if (selecting && image != null)
        {
            var p = ImagePoint(e.Location);
            int l = (int)Math.Floor(Math.Min(p.X, selectionStart.X)), t = (int)Math.Floor(Math.Min(p.Y, selectionStart.Y));
            Selection = Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height), Rectangle.FromLTRB(l, t, (int)Math.Ceiling(Math.Max(p.X, selectionStart.X)), (int)Math.Ceiling(Math.Max(p.Y, selectionStart.Y))));
            Changed();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        selecting = panning = false;
        Capture = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture)
        {
            selecting = panning = false;
            Cursor = Cursors.Default;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (image == null)
        {
            using var f = new Font(Font.FontFamily, 12);
            TextRenderer.DrawText(e.Graphics, "Отсканируйте документ\n\nКолёсико — масштаб\nПравая кнопка — перемещение\nЛевая кнопка — выделение области", f, ClientRectangle, Color.LightGray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        var display = reduced != null && image.Width * zoom <= reduced.Width ? reduced : image;
        e.Graphics.InterpolationMode = zoom < 1 ? InterpolationMode.Bilinear : InterpolationMode.NearestNeighbor;
        e.Graphics.DrawImage(display, new RectangleF(origin.X, origin.Y, (float)(image.Width * zoom), (float)(image.Height * zoom)));
        if (Selection.Width > 0 && Selection.Height > 0)
        {
            var r = new RectangleF(origin.X + (float)(Selection.X * zoom), origin.Y + (float)(Selection.Y * zoom), (float)(Selection.Width * zoom), (float)(Selection.Height * zoom));
            using var shade = new SolidBrush(Color.FromArgb(30, 0, 190, 255));
            e.Graphics.FillRectangle(shade, r);
            using var pen = new Pen(Color.DeepSkyBlue, 2)
            {
                DashStyle = DashStyle.Dash
            };
            e.Graphics.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            image?.Dispose();
            reduced?.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Control || e.Alt) return;
        switch (e.KeyCode)
        {
            case Keys.Add: case Keys.Oemplus: ChangeZoom(1.25); break;
            case Keys.Subtract: case Keys.OemMinus: ChangeZoom(1 / 1.25); break;
            case Keys.D0: case Keys.NumPad0: ActualSize(); break;
            case Keys.F: FitPage(); break;
            case Keys.W: FitWidth(); break;
            case Keys.Escape: Selection = Rectangle.Empty; Changed(); break;
            case Keys.Left: origin.X += 50; fit = "none"; Changed(); break;
            case Keys.Right: origin.X -= 50; fit = "none"; Changed(); break;
            case Keys.Up: origin.Y += 50; fit = "none"; Changed(); break;
            case Keys.Down: origin.Y -= 50; fit = "none"; Changed(); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }
}
