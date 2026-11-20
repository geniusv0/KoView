namespace KoVideo;

internal sealed partial class PlayerWindow
{
    private readonly Button previousFile = NavigationButton("‹", "이전 파일");
    private readonly Button nextFile = NavigationButton("›", "다음 파일");
    private NavigationOverlay? previousOverlay, nextOverlay;

    private sealed class NavigationOverlay : Form
    {
        public NavigationOverlay(Button button)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Chrome;
            Opacity = 0.55;
            button.Dock = DockStyle.Fill;
            button.Visible = true;
            Controls.Add(button);
        }

        protected override bool ShowWithoutActivation => true;
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                message.Result = 3; // MA_NOACTIVATE preserves the click without taking focus.
                return;
            }
            base.WndProc(ref message);
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var settings = base.CreateParams;
                settings.ExStyle |= 0x08000000 | 0x00000080; // No activation, tool window.
                return settings;
            }
        }
    }

    private sealed class NavigationArrow : Button
    {
        public bool PointsRight;

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ClientRectangle.Contains(PointToClient(Cursor.Position))
                ? FlatAppearance.MouseOverBackColor : BackColor);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            float centerX = ClientSize.Width / 2f;
            float centerY = ClientSize.Height / 2f;
            float halfHeight = Math.Min(LogicalToDeviceUnits(10), ClientSize.Height / 4f);
            float halfWidth = halfHeight / 2;
            float direction = PointsRight ? 1 : -1;
            using var pen = new Pen(ForeColor, LogicalToDeviceUnits(2))
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
                LineJoin = System.Drawing.Drawing2D.LineJoin.Round
            };
            e.Graphics.DrawLines(pen, [
                new PointF(centerX - direction * halfWidth, centerY - halfHeight),
                new PointF(centerX + direction * halfWidth, centerY),
                new PointF(centerX - direction * halfWidth, centerY + halfHeight)
            ]);
        }
    }

    private static Button NavigationButton(string glyph, string name)
    {
        var button = new NavigationArrow
        {
            PointsRight = glyph == "›",
            AccessibleName = name, TabStop = false, Visible = false,
            FlatStyle = FlatStyle.Flat, BackColor = Chrome, ForeColor = Ink,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 55, 58);
        return button;
    }

    private void InitializeFileNavigation()
    {
        previousOverlay = new NavigationOverlay(previousFile);
        nextOverlay = new NavigationOverlay(nextFile);
        previousFile.Click += (_, _) => NavigateFile(-1);
        nextFile.Click += (_, _) => NavigateFile(1);
        FormClosed += (_, _) =>
        {
            previousOverlay.Dispose();
            nextOverlay.Dispose();
        };
    }

    private void NavigateFile(int direction)
    {
        if (!loaded || folderVideos.Length < 2)
            return;
        held = Keys.None;
        HideFileNavigation();
        PlayPlaylistItem((folderIndex + direction + folderVideos.Length) % folderVideos.Length);
    }

    private void HideFileNavigation()
    {
        previousOverlay?.Hide();
        nextOverlay?.Hide();
    }

    private void UpdateFileNavigation()
    {
        if (previousOverlay is null || nextOverlay is null || !loaded || folderVideos.Length < 2 || desktop is not null ||
            resizingWindow || dragging || settingsMenu is not null ||
            !(ContainsFocus || previousOverlay.ContainsFocus || nextOverlay.ContainsFocus) ||
            WindowState == FormWindowState.Minimized || OwnedForms.Any(window => window.Modal))
        {
            HideFileNavigation();
            return;
        }

        Rectangle area = video.Bounds;
        if (area.Width < LogicalToDeviceUnits(80) || area.Height < LogicalToDeviceUnits(36))
        {
            HideFileNavigation();
            return;
        }

        Point cursor = PointToClient(Cursor.Position);
        int width = Math.Min(LogicalToDeviceUnits(36), area.Width / 4);
        int height = Math.Min(LogicalToDeviceUnits(64), area.Height);
        int zone = Math.Min(LogicalToDeviceUnits(64), area.Width / 3);
        int top = area.Top + (area.Height - height) / 2;
        previousOverlay.Bounds = new Rectangle(PointToScreen(new Point(area.Left, top)), new Size(width, height));
        nextOverlay.Bounds = new Rectangle(PointToScreen(new Point(area.Right - width, top)), new Size(width, height));
        SetNavigationVisible(previousOverlay, area.Contains(cursor) && cursor.X < area.Left + zone);
        SetNavigationVisible(nextOverlay, area.Contains(cursor) && cursor.X >= area.Right - zone);
    }

    private void SetNavigationVisible(Form overlay, bool visible)
    {
        if (visible && !overlay.Visible)
            overlay.Show(this);
        else if (!visible)
            overlay.Hide();
    }
}
