namespace KoVideo;

internal sealed class PlaybackSlider : Control
{
    public const int MaximumValue = 10000;
    public int Maximum => MaximumValue;
    private int value;

    public PlaybackSlider()
    {
        DoubleBuffered = true;
        AccessibleName = "재생 위치";
        AccessibleRole = AccessibleRole.Slider;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => value;
        set
        {
            int nextValue = Math.Clamp(value, 0, Maximum);
            if (this.value == nextValue)
                return;
            this.value = nextValue;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width <= 16)
            return;
        int y = Height / 2;
        int available = Width - 16;
        int played = (int)Math.Round(available * Value / (double)Maximum);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var track = new Pen(Color.FromArgb(96, 130, 130, 136), 1.5f);
        e.Graphics.DrawLine(track, 8, y, Width - 8, y);
        if (!Enabled)
            return;
        using var progress = new Pen(Color.FromArgb(127, 127, 134), 1.5f);
        e.Graphics.DrawLine(progress, 8, y, 8 + played, y);
        using var thumb = new SolidBrush(Color.FromArgb(151, 151, 158));
        e.Graphics.FillEllipse(thumb, 5 + played, y - 3, 6, 6);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
            return;
        Capture = true;
        SetFromPointer(e.X);
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Capture)
            SetFromPointer(e.X);
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || !Capture)
            return;
        SetFromPointer(e.X);
        Capture = false;
        base.OnMouseUp(e);
    }

    private void SetFromPointer(int x) =>
        Value = (int)Math.Round(Maximum * Math.Clamp((x - 8) / (double)Math.Max(1, Width - 16), 0, 1));
}
