using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace KoVideo;

internal sealed partial class PlayerWindow : Form, IMessageFilter
{
    private const string VideoPatterns = "*.mp4;*.mkv;*.webm;*.mov;*.avi;*.wmv;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg;*.flv;*.ogv;*.3gp;*.y4m";
    private const string PhotoPatterns = "*.jpg;*.jpeg;*.png;*.bmp;*.webp;*.tif;*.tiff;*.avif";
    private static readonly HashSet<string> PhotoExtensions = PhotoPatterns.Split(';')
        .Select(pattern => pattern[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoExtensions = VideoPatterns.Split(';')
        .Select(pattern => pattern[1..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static readonly Color Chrome = Color.FromArgb(34, 34, 36);
    private static readonly Color WindowEdge = Color.FromArgb(46, 46, 49);
    private static readonly Color Ink = Color.FromArgb(213, 213, 217);
    private static readonly Padding FramePadding = new(2);
    private const string EmptyPlayerText = "파일을 여기에 놓으세요";
    private readonly Panel video = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label caption = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(9, 0, 0, 0) };
    private readonly Label clock = new() { TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
    private readonly Label frame = new() { Text = "프레임 —", TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true };
    private readonly PlaybackSlider timeline = new() { Dock = DockStyle.Fill, BackColor = Chrome, TabStop = false };
    private readonly Button playback;
    private readonly Panel bar;
    private readonly Panel controls;
    private bool fullscreen;
    private bool autoHideChrome = true;
    private long chromeDeadline;
    private Point lastCursorPosition = Cursor.Position;
    private ContextMenuStrip? settingsMenu;
    private bool settingsRequested;
    private Rectangle fullscreenBounds;
    private FormWindowState fullscreenState;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 35 };
    private DesktopSurface? desktop;
    private Mpv? player;
    private string? path;
    private string[] folderVideos = [];
    private int folderIndex;
    private int slideshowSeconds;
    private long nextSlideAt;
    private Form? playlistWindow;
    private bool playlistDocked;
    private ListBox? playlistList;
    private bool repeatVideo;
    private bool loaded, dragging, stepped;
    private bool resizingWindow;
    private bool playOnLoad;
    private bool sizeToVideo;
    private bool resizeAfterAspectChange;
    private double resizeAspect;
    private Keys held;
    private long nextJump;
    private Rectangle windowBounds;
    private FormWindowState windowStateBeforeDesktop;
    private string scaleMode = "0";
    private string aspectMode = "no";
    private int zoom = 100;
    private int ticks;

    public PlayerWindow(string? initialFile)
    {
        autoHideChrome = session.HideFrame;
        slideshowSeconds = session.Slideshow is 3 or 5 or 10 or 15 ? session.Slideshow : 0;
        repeatVideo = session.Repeat;
        Text = "KoView";
        caption.Text = Text;
        FormBorderStyle = FormBorderStyle.None;
        BackColor = WindowEdge;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9);
        UpdateResizePadding();
        MinimumSize = ResizeMinimum;
        ClientSize = new Size(840, 510);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        bar = CreateTitleBar(this, caption);
        LocationChanged += (_, _) => PositionDockedPlaylist();
        SizeChanged += (_, _) =>
        {
            UpdateResizePadding();
            PositionDockedPlaylist();
        };
        DpiChanged += (_, _) => BeginInvoke(() =>
        {
            UpdateResizePadding();
            KeepPlaylistOnScreen();
        });
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;

        controls = new Panel { Dock = DockStyle.Bottom, Height = LogicalToDeviceUnits(32), BackColor = Chrome };
        playback = MakeButton("▶", "재생 / 일시정지 (Space)", TogglePause, DockStyle.Left, 38);
        var information = new Panel { Dock = DockStyle.Right, BackColor = Chrome };
        information.Controls.AddRange([frame, clock]);
        controls.Controls.Add(timeline);
        controls.Controls.Add(information);
        var settings = MakeButton("⚙", "설정", ShowSettings, DockStyle.Right, 36);
        controls.Controls.Add(settings);
        controls.Controls.Add(playback);
        void LayoutControls()
        {
            int width = controls.ClientSize.Width;
            int Px(int size) => controls.LogicalToDeviceUnits(size);
            int minimumSlider = Px(34);
            bool stacked = width < Px(258);
            int infoWidth = stacked ? Math.Max(0, Math.Min(Px(112), width - minimumSlider)) : Px(218);
            int height = controls.ClientSize.Height;
            controls.SuspendLayout();
            information.Width = infoWidth;
            int frameWidth = stacked ? infoWidth : Px(94);
            frame.SetBounds(0, 0, Math.Max(0, frameWidth - Px(3)), stacked ? height / 2 : height);
            int clockLeft = stacked ? 0 : frameWidth;
            clock.SetBounds(clockLeft, stacked ? height / 2 : 0,
                Math.Max(0, infoWidth - clockLeft - Px(5)), stacked ? height - height / 2 : height);
            int remaining = width - infoWidth;
            foreach (Button button in new[] { playback, settings })
            {
                bool fits = remaining >= minimumSlider + button.Width;
                button.Visible = fits;
                if (fits)
                    remaining -= button.Width;
            }
            controls.ResumeLayout();
        }
        controls.ClientSizeChanged += (_, _) => LayoutControls();

        var empty = new Label
        {
            Text = EmptyPlayerText,
            ForeColor = Color.FromArgb(171, 178, 189),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        empty.Click += (_, _) =>
        {
            if (retrySource is not null)
                Try(CancelNetworkRetry);
        };
        video.Controls.Add(empty);
        Controls.Add(video);
        Controls.Add(controls);
        Controls.Add(bar);
        InitializeFileNavigation();
        ClientSizeChanged += (_, _) => PositionOverlayChrome();
        PaddingChanged += (_, _) => PositionOverlayChrome();
        ConfigureChromeLayout();
        LayoutControls();
        EnableFileDrop(this, files => OpenDroppedFiles(files));
        timeline.MouseDown += (_, e) => dragging = e.Button == MouseButtons.Left;
        timeline.MouseCaptureChanged += (_, _) =>
        {
            if (!timeline.Capture)
                dragging = false;
        };
        timeline.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (loaded && player?.Flag("seekable") == true)
                Try(() => player.Command("seek", (timeline.Value / 100d).ToString(CultureInfo.InvariantCulture), "absolute-percent+exact"));
        };
        Deactivate += (_, _) => held = Keys.None;
        timer.Tick += (_, _) => Try(UpdatePlayback);
        timer.Start();
        Application.AddMessageFilter(this);
        Shown += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(initialFile))
            {
                Try(RestoreSession);
                return;
            }
            if (Uri.TryCreate(initialFile, UriKind.Absolute, out var url) && YouTubeWindow.Parse(url) is { } selection)
                Try(() => ShowYouTube(selection));
            else
                Try(() => Open(initialFile));
        };
        FormClosing += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            SavePosition();
            SaveSession();
            timer.Dispose();
            Application.RemoveMessageFilter(this);
            player?.Dispose();
            desktop?.Dispose();
            tooltip.Dispose();
        };
    }

    private readonly ToolTip tooltip = new();

    private Panel CreateTitleBar(Form window, Label title)
    {
        var titleBar = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Chrome };
        titleBar.Controls.Add(title);
        var minimize = MakeCaptionButton("\uE921", "최소화", () => window.WindowState = FormWindowState.Minimized);
        var maximize = MakeCaptionButton("\uE922", "최대화", () => ToggleMaximize(window));
        var close = MakeCaptionButton("\uE8BB", "닫기", window.Close);
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(196, 43, 28);
        close.FlatAppearance.MouseDownBackColor = Color.FromArgb(160, 32, 25);
        titleBar.Controls.Add(minimize);
        titleBar.Controls.Add(maximize);
        titleBar.Controls.Add(close);
        window.Resize += (_, _) =>
        {
            bool maximized = window.WindowState == FormWindowState.Maximized;
            maximize.Text = maximized ? "\uE923" : "\uE922";
            maximize.AccessibleName = maximized ? "이전 크기로 복원" : "최대화";
            tooltip.SetToolTip(maximize, maximize.AccessibleName);
        };
        title.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                ReleaseCapture();
                SendMessage(window.Handle, 0x00A1, 2, 0);
            }
        };
        title.DoubleClick += (_, _) => ToggleMaximize(window);
        return titleBar;
    }

    private Button MakeCaptionButton(string glyph, string hint, Action action)
    {
        var button = MakeButton(glyph, hint, action, DockStyle.Right, 46);
        button.Font = new Font("Segoe MDL2 Assets", 8);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(61, 65, 72);
        return button;
    }

    private Button MakeButton(string text, string hint, Action action, DockStyle dock, int width)
    {
        var button = new Button { Text = text, Dock = dock, Width = width, FlatStyle = FlatStyle.Flat, TabStop = false, AccessibleName = hint };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 52, 60);
        button.Click += (_, _) => Try(action);
        tooltip.SetToolTip(button, hint);
        return button;
    }

    private void OpenDialog()
    {
        using var dialog = new OpenFileDialog { Filter = $"영상 및 사진|{VideoPatterns};{PhotoPatterns}|영상 파일|{VideoPatterns}|사진 파일|{PhotoPatterns}|모든 파일|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            Open(dialog.FileName);
    }

    private void Open(string file)
    {
        if (!File.Exists(file))
            throw new InvalidOperationException("파일을 찾을 수 없습니다.");
        string fullPath = Path.GetFullPath(file);
        bool includePhotos = IsPhoto(fullPath);
        string[] entries;
        try
        {
            entries = Directory.EnumerateFiles(Path.GetDirectoryName(fullPath)!)
                .Where(candidate => (VideoExtensions.Contains(Path.GetExtension(candidate)) ||
                    (includePhotos && IsPhoto(candidate))) &&
                    !string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase))
                .Append(fullPath)
                .OrderBy(Path.GetFileName, Comparer<string?>.Create(StrCmpLogicalW))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            entries = [fullPath];
        }
        failedFiles.Clear();
        OpenMedia(fullPath, Path.GetFileName(fullPath));
        folderVideos = entries;
        folderIndex = Array.IndexOf(entries, fullPath);
        RefreshPlaylist();
    }

    private void EnableFileDrop(Control target, Action<string[]> openFiles)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true
            ? e.AllowedEffect & DragDropEffects.Copy : DragDropEffects.None;
        target.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
                Try(() => openFiles(files));
        };
    }

    private void OpenDroppedFiles(string[] files, bool append = false)
    {
        string[] videos = files.Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, Comparer<string?>.Create(StrCmpLogicalW))
            .ToArray();
        if (videos.Length == 0)
            throw new InvalidOperationException("재생할 파일을 찾을 수 없습니다.");

        if (append && path is not null && folderVideos.Length > 0)
        {
            string[] combined = folderVideos.Concat(videos)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (combined.Length == folderVideos.Length)
                return;

            player!.Set("loop-file", "no");
            repeatVideo = false;
            folderVideos = combined;
            RefreshPlaylist();
            return;
        }

        failedFiles.Clear();
        OpenMedia(videos[0], Path.GetFileName(videos[0]), repeat: videos.Length == 1);
        folderVideos = videos;
        folderIndex = 0;
        RefreshPlaylist();
    }

    private void OpenNetworkDialog()
    {
        using var dialog = new Form
        {
            Text = "네트워크 스트림 열기",
            ClientSize = new Size(450, 108),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false
        };
        var label = new Label { Text = "주소 (RTSP, HTTP, YouTube)", AutoSize = true, Location = new Point(12, 12) };
        var address = new TextBox { AccessibleName = "스트림 주소", PlaceholderText = "rtsp://192.168.1.10:554/live", Bounds = new Rectangle(12, 34, 426, 24) };
        var open = new Button { Text = "재생", Bounds = new Rectangle(278, 70, 76, 27) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Bounds = new Rectangle(362, 70, 76, 27) };
        dialog.Controls.AddRange([label, address, open, cancel]);
        dialog.AcceptButton = open;
        dialog.CancelButton = cancel;
        dialog.ActiveControl = address;
        string source = "";
        YouTubeWindow.Selection? youtubeVideo = null;
        open.Click += (_, _) =>
        {
            source = address.Text.Trim();
            if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("rtsp" or "rtsps" or "http" or "https") ||
                string.IsNullOrEmpty(uri.Host) || source.Any(char.IsControl))
            {
                MessageBox.Show(dialog, "올바른 RTSP, RTSPS, HTTP 또는 HTTPS 주소를 입력하세요.", dialog.Text);
                address.Focus();
                return;
            }

            if (YouTubeWindow.IsYouTubeHost(uri.IdnHost))
            {
                youtubeVideo = YouTubeWindow.Parse(uri);
                if (youtubeVideo is null)
                {
                    MessageBox.Show(dialog, "유튜브 영상 또는 재생목록 링크를 입력하세요.", dialog.Text);
                    address.Focus();
                    return;
                }
            }
            dialog.DialogResult = DialogResult.OK;
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            if (youtubeVideo is not null)
            {
                ShowYouTube(youtubeVideo);
                return;
            }
            OpenStream(source);
        }
    }

    private void ShowYouTube(YouTubeWindow.Selection selection)
    {
        Remember(selection.Page.AbsoluteUri);
        bool resume = loaded && player?.Flag("pause") == false;
        if (resume)
            TogglePause();
        try
        {
            using var youtube = new YouTubeWindow(selection) { Icon = Icon };
            youtube.ShowDialog(this);
        }
        finally
        {
            if (resume)
                TogglePause();
        }
    }

    private void OpenMedia(string source, string title, bool? repeat = null)
        => LoadPlayback(source, title, repeat, ReadPosition(source));

    private void LoadPlayback(string source, string title, bool? repeat, string? start)
    {
        SavePosition();
        if (player is null)
        {
            player = new Mpv(desktop?.Handle ?? video.Handle);
            player.Set("volume", MpvNumber(double.IsFinite(session.Volume) ? Math.Clamp(session.Volume, 0, 100) : 100));
            player.Set("mute", session.Muted ? "yes" : "no");
        }
        restoreOutputTracks = null;
        ResetLoop();
        player.Set("loop-file", (repeat ?? repeatVideo) ? "inf" : "no");
        loaded = false;
        resizeAfterAspectChange = false;
        sizeToVideo = desktop is null && !fullscreen && !compact && WindowState == FormWindowState.Normal;
        scaleMode = "0";
        zoom = 100;
        path = source;
        LoadMedia(player, source, start);
        playOnLoad = true;
        ApplyScale();
        repeatVideo = repeat ?? repeatVideo;
        caption.Text = title;
        BeginNetworkAttempt(source);
        Remember(source);
    }

    private static bool IsPhoto(string? source) =>
        source is not null && Path.IsPathFullyQualified(source) &&
        PhotoExtensions.Contains(Path.GetExtension(source));

    private static void LoadMedia(Mpv target, string source, string? start = null)
    {
        if (IsPhoto(source))
            target.Command("loadfile", source, "replace", "-1", "image-display-duration=inf");
        else if (start is not null)
            target.Command("loadfile", source, "replace", "-1", $"start={start}");
        else
            target.Command("loadfile", source, "replace");
    }

    private void TogglePause()
    {
        if (loaded)
        {
            player?.Command("cycle", "pause");
            RestartSlideTimer();
        }
    }

    private void RestartSlideTimer() =>
        nextSlideAt = Environment.TickCount64 + slideshowSeconds * 1000L;

    private void UpdatePlayback()
    {
        UpdateFileNavigation();
        Point cursorPosition = Cursor.Position;
        if (autoHideChrome && desktop is null && !fullscreen && !resizingWindow && cursorPosition != lastCursorPosition &&
            Form.ActiveForm == this && Bounds.Contains(cursorPosition))
        {
            chromeDeadline = Environment.TickCount64 + 3000;
            if (!bar.Visible)
                SetChromeVisible(true);
        }
        lastCursorPosition = cursorPosition;

        if (autoHideChrome && desktop is null && !fullscreen && loaded && bar.Visible &&
            !dragging && !resizingWindow && settingsMenu?.Visible != true && Environment.TickCount64 >= chromeDeadline)
            SetChromeVisible(false);

        if (desktop is not null && ++ticks % 20 == 0)
        {
            if (!desktop.IsAlive || desktop.Monitor is not { } monitor)
                LeaveDesktop();
            else
                desktop.ResizeToMonitor(monitor);
        }
        if (player is null)
            return;
        string? failure = null;
        while (player.Poll(out bool ready, out bool videoReconfigured, out string? error))
        {
            if (videoReconfigured && resizeAfterAspectChange)
            {
                resizeAfterAspectChange = false;
                sizeToVideo = !compact;
            }
            if (ready)
            {
                loaded = true;
                if (retrySource is not null)
                {
                    connectDeadline = 0;
                    stableAfter = Environment.TickCount64 + 15000;
                    ShowPlayerStatus(null);
                }
                if (playOnLoad)
                {
                    playOnLoad = false;
                    player.Set("pause", "no");
                }
                var restore = restoreOutputTracks;
                restoreOutputTracks = null;
                restore?.Invoke();
                RestartSlideTimer();
                chromeDeadline = Environment.TickCount64 + 3000;
            }
            if (error is not null)
                failure = error;
        }
        if (failure is not null && path is not null && (!loaded || retrySource is not null) ||
            loaded && retrySource is not null && player.Flag("eof-reached") &&
            (!player.Flag("seekable") || player.NetworkError is "timeout" or "disconnected"))
        {
            HandlePlaybackFailure(failure ?? player.NetworkError ?? "disconnected");
            return;
        }
        if (UpdateNetworkTimers())
            return;

        if (loaded && sizeToVideo)
            FitWindowToVideo();

        if (loaded && slideshowSeconds > 0 && IsPhoto(path) &&
            Environment.TickCount64 >= nextSlideAt && !player.Flag("pause"))
        {
            RestartSlideTimer();
            for (int offset = 1; offset < folderVideos.Length; offset++)
            {
                int nextPhoto = (folderIndex + offset) % folderVideos.Length;
                if (IsPhoto(folderVideos[nextPhoto]))
                {
                    if (!failedFiles.Contains(folderVideos[nextPhoto]))
                        PlayPlaylistItemCore(nextPhoto);
                    else
                        continue;
                    break;
                }
            }
        }

        if (loaded && !repeatVideo && folderVideos.Length > 1 && player.Flag("eof-reached"))
        {
            TryNextPlaylistItem();
            return;
        }

        if (loaded && !IsPhoto(path) && held != Keys.None && Environment.TickCount64 >= nextJump)
            Jump();

        double positionNow = player.Number("time-pos");
        double duration = player.Number("duration");
        bool seekable = loaded && player.Flag("seekable");
        timeline.Enabled = seekable;
        if (!dragging)
            timeline.Value = duration > 0 ? (int)Math.Clamp(positionNow / duration * timeline.Maximum, 0, timeline.Maximum) : 0;
        clock.Text = $"{FormatTime(positionNow)} / {FormatTime(duration)}";
        frame.Text = loaded && player.TryNumber("estimated-frame-number", out double frameIndex)
            ? $"프레임 {frameIndex + 1:N0}"
            : "프레임 —";
        playback.Text = loaded && !player.Flag("pause") ? "Ⅱ" : "▶";
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 35999999));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
    }

    private void FitWindowToVideo()
    {
        if (fullscreen || desktop is not null || WindowState != FormWindowState.Normal)
        {
            sizeToVideo = false;
            return;
        }

        double width = player!.Number("dwidth");
        double height = player.Number("dheight");
        if (width <= 0 || height <= 0)
            return;

        sizeToVideo = false;
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int chromeWidth = Width - video.Width;
        int chromeHeight = Height - video.Height;
        double scale = Math.Min(1, Math.Min(
            (work.Width - chromeWidth) / width,
            (work.Height - chromeHeight) / height));
        int targetWidth = (int)Math.Round(width * scale) + chromeWidth;
        int targetHeight = (int)Math.Round(height * scale) + chromeHeight;
        var gripMinimum = ResizeMinimum;
        Size minimum = new(Math.Min(gripMinimum.Width, targetWidth), Math.Min(gripMinimum.Height, targetHeight));
        MinimumSize = minimum;
        Bounds = new Rectangle(
            Math.Clamp(Left, work.Left, work.Right - targetWidth),
            Math.Clamp(Top, work.Top, work.Bottom - targetHeight),
            targetWidth, targetHeight);
    }

    public bool PreFilterMessage(ref Message message)
    {
        if (message.Msg == 0x0204 && desktop is null && IsHandleCreated &&
            (message.HWnd == Handle || IsChild(Handle, message.HWnd)))
        {
            RequestSettings();
            return true;
        }
        if (settingsMenu?.Visible == true && message.Msg is 0x100 or 0x101)
        {
            held = Keys.None;
            return false;
        }

        if (autoHideChrome && desktop is null && !fullscreen && !resizingWindow &&
            message.Msg is 0x100 or 0x200 or 0x201 or 0x204 or 0x207 &&
            (message.Msg != 0x200 || Cursor.Position != lastCursorPosition) &&
            (message.HWnd == Handle || IsChild(Handle, message.HWnd)))
        {
            chromeDeadline = Environment.TickCount64 + 3000;
            if (!bar.Visible)
                SetChromeVisible(true);
        }

        if (message.Msg is not (0x100 or 0x101) || !ContainsFocus)
            return false;
        var key = (Keys)message.WParam.ToInt32();
        if (key is Keys.Left or Keys.Right && (ModifierKeys == Keys.None || held == key))
        {
            if (message.Msg == 0x100)
            {
                if (held != key && (message.LParam.ToInt64() & 0x40000000) == 0)
                {
                    held = key;
                    stepped = false;
                    nextJump = Environment.TickCount64 + 500;
                }
            }
            else if (held == key)
            {
                Try(() =>
                {
                    if (loaded && IsPhoto(path))
                    {
                        if (folderVideos.Length > 1)
                            PlayPlaylistItem((folderIndex + (key == Keys.Left ? -1 : 1) + folderVideos.Length) % folderVideos.Length);
                        return;
                    }
                    if (loaded && Environment.TickCount64 >= nextJump)
                        Jump();
                    if (loaded && !stepped)
                    {
                        player!.Set("pause", "yes");
                        player.Command(key == Keys.Left ? "frame-back-step" : "frame-step");
                    }
                });
                held = Keys.None;
            }
            return true;
        }
        if (message.Msg != 0x100)
            return false;
        if (ModifierKeys == Keys.None && key is Keys.Up or Keys.Down)
        {
            Try(() => ChangeVolume(key == Keys.Up ? 5 : -5));
            return true;
        }
        if ((message.LParam.ToInt64() & 0x40000000) != 0)
            return key == Keys.Space;
        if (key == Keys.Escape && ModifierKeys == Keys.None && desktop is not null)
            Try(LeaveDesktop);
        else if (key == Keys.Escape && ModifierKeys == Keys.None && retrySource is not null && (retryAt > 0 || connectDeadline > 0))
            Try(CancelNetworkRetry);
        else if (key == Keys.Space && ModifierKeys == Keys.None)
            Try(TogglePause);
        else if (key == Keys.M && ModifierKeys == Keys.None)
            Try(ToggleMute);
        else if (desktop is not null)
            return true;
        else if (key == Keys.N && ModifierKeys == Keys.Control)
            Try(OpenNetworkDialog);
        else if (key == Keys.L && ModifierKeys == Keys.Control)
            ShowPlaylist();
        else if (key == Keys.O && ModifierKeys == Keys.Control)
            Try(OpenDialog);
        else if (key == Keys.D && ModifierKeys == Keys.Control)
            Try(ToggleDesktop);
        else if (key == Keys.P && ModifierKeys == Keys.Control)
            ToggleCompact();
        else if (ModifierKeys == Keys.None && (key == Keys.F11 || (key == Keys.Escape && fullscreen)))
            ToggleFullscreen();
        else
            return false;
        return true;
    }

    private void Jump()
    {
        stepped = true;
        nextJump = Environment.TickCount64 + 500;
        if (player?.Flag("seekable") == true)
            player.Command("seek", held == Keys.Left ? "-10" : "10", "relative+exact");
    }

    private void ShowPlaylist()
    {
        if (playlistWindow is not null)
        {
            playlistWindow.Activate();
            return;
        }

        var window = new PlaylistForm
        {
            Text = "KoView 재생 목록",
            Icon = Icon,
            ClientSize = new Size(420, 300),
            MinimumSize = new Size(240, 160),
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            BackColor = WindowEdge,
            ForeColor = Ink,
            Font = Font,
            Padding = FramePadding
        };
        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Chrome,
            ForeColor = Ink,
            IntegralHeight = false,
            HorizontalScrollbar = true
        };
        list.DrawMode = DrawMode.OwnerDrawFixed;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0)
                return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(59, 59, 63) : Chrome);
            e.Graphics.FillRectangle(background, e.Bounds);
            var row = e.Bounds;
            row.X += 6;
            row.Width -= 6;
            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]?.ToString(), list.Font, row, Ink,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        };
        EnableFileDrop(list, files => OpenDroppedFiles(files, append: true));
        EnablePlaylistReorder(list);
        AddPlaylistMenu(list, window);
        list.MouseDoubleClick += (_, e) => PlayPlaylistItem(PlaylistIndex(list.IndexFromPoint(e.Location)));
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                PlayPlaylistItem(PlaylistIndex(list.SelectedIndex));
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
                window.Close();
        };
        window.Controls.Add(list);
        playlistSearch = new TextBox { Dock = DockStyle.Top, PlaceholderText = "재생 목록 검색", AccessibleName = "재생 목록 검색", BackColor = Chrome, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle };
        playlistSearch.TextChanged += (_, _) => RefreshPlaylist();
        window.Controls.Add(playlistSearch);
        window.Controls.Add(CreateTitleBar(window, new Label
        {
            Text = window.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true, Padding = new Padding(9, 0, 0, 0)
        }));
        window.UserMoving += (_, _) => playlistDocked = false;
        window.Resize += (_, _) => PositionDockedPlaylist();
        window.FormClosed += (_, _) =>
        {
            playlistDocked = false;
            playlistWindow = null;
            playlistList = null;
            playlistSearch = null;
        };
        playlistWindow = window;
        playlistDocked = true;
        playlistList = list;
        RefreshPlaylist();
        PositionDockedPlaylist();
        window.Show(this);
    }

    private void PositionDockedPlaylist()
    {
        if (!playlistDocked || playlistWindow is not { } window ||
            WindowState == FormWindowState.Minimized || window.WindowState != FormWindowState.Normal)
            return;

        Rectangle work = Screen.FromControl(this).WorkingArea;
        int width = Math.Min(window.Width, work.Width);
        int height = Math.Min(work.Height, Math.Max(Height, window.MinimumSize.Height));
        int left = Right + width <= work.Right ? Right : Left - width;
        window.SetBounds(Math.Clamp(left, work.Left, work.Right - width),
            Math.Clamp(Top, work.Top, work.Bottom - height), width, height);
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed && !Disposing)
            BeginInvoke(KeepPlaylistOnScreen);
    }

    private void KeepPlaylistOnScreen()
    {
        if (playlistDocked)
        {
            PositionDockedPlaylist();
            return;
        }
        if (playlistWindow is not { WindowState: FormWindowState.Normal } window)
            return;
        Rectangle work = Screen.FromControl(window).WorkingArea;
        int width = Math.Min(window.Width, work.Width);
        int height = Math.Min(window.Height, work.Height);
        window.SetBounds(Math.Clamp(window.Left, work.Left, work.Right - width),
            Math.Clamp(window.Top, work.Top, work.Bottom - height), width, height);
    }

    private void RefreshPlaylist()
    {
        if (playlistList is not { } list)
            return;

        list.BeginUpdate();
        list.Items.Clear();
        visiblePlaylist.Clear();
        string query = playlistSearch?.Text.Trim() ?? "";
        for (int index = 0; index < folderVideos.Length; index++)
        {
            string file = folderVideos[index];
            string name = Path.GetFileName(file);
            if (!name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                continue;
            visiblePlaylist.Add(index);
            string marker = failedFiles.Contains(file) ? "⚠  " : path is not null && index == folderIndex ? "▶  " : "     ";
            list.Items.Add(marker + name);
        }
        if (folderVideos.Length == 0)
            list.Items.Add(path is null ? "재생 목록이 비어 있습니다." : "▶  " + caption.Text);
        else if (path is not null)
            list.SelectedIndex = visiblePlaylist.IndexOf(folderIndex);
        list.EndUpdate();
    }

    private void PlayPlaylistItem(int index)
    {
        if (index < 0 || index >= folderVideos.Length)
            return;

        failedFiles.Remove(folderVideos[index]);
        Try(() => PlayPlaylistItemCore(index));
    }

    private void PlayPlaylistItemCore(int index)
    {
        string file = folderVideos[index];
        LoadPlayback(file, Path.GetFileName(file), null, "0");
        folderIndex = index;
        RefreshPlaylist();
    }

    private void ShowSettings()
    {
        if (desktop is not null || settingsMenu is { IsDisposed: false })
            return;
        var menu = new ContextMenuStrip();
        settingsMenu = menu;
        menu.Items.Add("파일 열기… (Ctrl+O)", null, (_, _) => BeginInvoke(() => Try(OpenDialog)));
        var slideshow = new ToolStripMenuItem("사진 슬라이드 보기");
        foreach (int seconds in new[] { 0, 3, 5, 10, 15 })
        {
            var option = new ToolStripMenuItem(seconds == 0 ? "끄기" : $"{seconds}초")
                { Checked = slideshowSeconds == seconds };
            option.Click += (_, _) =>
            {
                slideshowSeconds = seconds;
                RestartSlideTimer();
            };
            slideshow.DropDownItems.Add(option);
        }
        menu.Items.Add(slideshow);
        AddPlaybackMenus(menu);
        AddHistoryMenus(menu);
        menu.Items.Add("현재 프레임 PNG 저장…", null, (_, _) => BeginInvoke(() => Try(SaveFrame))).Enabled =
            loaded && player?.Number("dwidth") > 0;
        menu.Items.Add("재생 목록 보기 (Ctrl+L)", null, (_, _) => BeginInvoke(ShowPlaylist));
        var autoHide = new ToolStripMenuItem("프레임 자동 숨기기") { Checked = autoHideChrome };
        autoHide.Click += (_, _) =>
        {
            autoHideChrome = !autoHideChrome;
            chromeDeadline = Environment.TickCount64 + 3000;
            SetChromeVisible(true);
        };
        menu.Items.Add(autoHide);
        menu.Items.Add("바탕화면 재생 (Ctrl+D)", null, (_, _) => BeginInvoke(() => Try(ToggleDesktop))).Enabled = loaded;
        menu.Items.Add("네트워크 스트림 열기 (Ctrl+N)", null, (_, _) => BeginInvoke(() => Try(OpenNetworkDialog)));
        var playbackModes = new ToolStripMenuItem("재생 모드");
        foreach (var (label, repeat) in new[] { ("목록 순서대로 반복", false), ("현재 영상 반복", true) })
        {
            var item = new ToolStripMenuItem(label) { Checked = repeatVideo == repeat };
            item.Click += (_, _) => Try(() =>
            {
                player?.Set("loop-file", repeat ? "inf" : "no");
                repeatVideo = repeat;
            });
            playbackModes.DropDownItems.Add(item);
        }
        menu.Items.Add(playbackModes);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("화면에 맞춤", null, (_, _) => Try(() => SetScale("0", 100)));
        menu.Items.Add("화면 채우기", null, (_, _) => Try(() => SetScale("1", 100)));
        var aspects = new ToolStripMenuItem("영상 비율");
        foreach (var (label, value) in new[] { ("원본", "no"), ("4:3", "4:3"), ("16:9", "16:9"), ("16:10", "16:10"), ("1:1", "1:1") })
        {
            var item = new ToolStripMenuItem(label) { Checked = aspectMode == value };
            item.Click += (_, _) => Try(() =>
            {
                if (aspectMode == value)
                    return;
                player?.Set("video-aspect-override", value);
                aspectMode = value;
                resizeAfterAspectChange = loaded && WindowState == FormWindowState.Normal && desktop is null && !fullscreen;
            });
            aspects.DropDownItems.Add(item);
        }
        menu.Items.Add(aspects);
        var sizes = new ToolStripMenuItem("배율");
        foreach (int percent in new[] { 50, 75, 100, 125, 150, 200 })
            sizes.DropDownItems.Add($"{percent}%", null, (_, _) => Try(() => SetScale("0", percent)));
        menu.Items.Add(sizes);
        menu.Items.Add("전체 화면 (F11)", null, (_, _) => ToggleFullscreen()).Enabled = desktop is null;
        menu.Closed += (_, _) =>
        {
            settingsMenu = null;
            chromeDeadline = Environment.TickCount64 + 3000;
            BeginInvoke((Action)menu.Dispose);
        };
        menu.Show(Cursor.Position);
    }

    private void RequestSettings()
    {
        if (settingsRequested || settingsMenu is { IsDisposed: false } || desktop is not null || Disposing)
            return;
        settingsRequested = true;
        BeginInvoke(() =>
        {
            settingsRequested = false;
            Try(ShowSettings);
        });
    }

    private void SetChromeVisible(bool visible)
    {
        SuspendLayout();
        ConfigureChromeLayout();
        bar.Visible = visible;
        controls.Visible = visible;
        BackColor = visible ? WindowEdge : Color.Black;
        UpdateResizePadding();
        ResumeLayout();
    }

    private void ConfigureChromeLayout()
    {
        var titleDock = autoHideChrome ? DockStyle.None : DockStyle.Top;
        if (bar.Dock != titleDock)
        {
            bar.Dock = titleDock;
            controls.Dock = autoHideChrome ? DockStyle.None : DockStyle.Bottom;
            if (autoHideChrome)
            {
                controls.BringToFront();
                bar.BringToFront();
            }
            else
                video.BringToFront();
        }
        PositionOverlayChrome();
    }

    private void PositionOverlayChrome()
    {
        if (!autoHideChrome)
            return;
        int width = Math.Max(0, ClientSize.Width - Padding.Horizontal);
        bar.SetBounds(Padding.Left, Padding.Top, width, bar.Height);
        controls.SetBounds(Padding.Left, Math.Max(Padding.Top, ClientSize.Height - Padding.Bottom - controls.Height),
            width, controls.Height);
    }

    private void UpdateResizePadding()
    {
        var padding = fullscreen || WindowState != FormWindowState.Normal
            ? Padding.Empty : new Padding(LogicalToDeviceUnits(6));
        if (Padding != padding)
            Padding = padding;
    }

    private Size ResizeMinimum => new(LogicalToDeviceUnits(140), LogicalToDeviceUnits(86));

    private void SetScale(string mode, int percent)
    {
        scaleMode = mode;
        zoom = percent;
        ApplyScale();
    }

    private void ApplyScale()
    {
        player?.Set("video-aspect-override", aspectMode);
        player?.Set("panscan", scaleMode);
        player?.Set("video-zoom", Math.Log2(zoom / 100d).ToString("R", CultureInfo.InvariantCulture));
    }

    private void ToggleDesktop()
    {
        if (desktop is not null)
        {
            LeaveDesktop();
            return;
        }
        if (!loaded)
            return;

        var monitor = Screen.FromControl(this);
        var surface = new DesktopSurface(monitor);
        try
        {
            SwitchOutput(surface.Handle);
        }
        catch
        {
            surface.Dispose();
            throw;
        }
        desktop = surface;
        HideFileNavigation();
        playlistWindow?.Close();
        if (fullscreen)
            ToggleFullscreen();
        windowStateBeforeDesktop = WindowState;
        windowBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowState = FormWindowState.Normal;
        var work = monitor.WorkingArea;
        Size = new Size(Math.Min(530, work.Width), MinimumSize.Height);
        Location = new Point(work.Right - Width - 12, work.Bottom - Height - 12);
        video.Visible = false;
        bar.Hide();
        controls.Hide();
        // Keep the keyboard target active without drawing a window over the desktop.
        Opacity = 0;
        ActiveControl = null;
        Focus();
    }

    private void LeaveDesktop()
    {
        var previous = desktop!;
        if (path is not null)
            SwitchOutput(video.Handle);
        else
        {
            player?.Dispose();
            player = null;
        }
        desktop = null;
        previous.Dispose();
        SetChromeVisible(true);
        chromeDeadline = Environment.TickCount64 + 3000;
        video.Visible = true;
        video.Controls[0].Visible = path is null;
        Bounds = previous.Monitor is null ? OnAvailableMonitor(windowBounds) : windowBounds;
        WindowState = windowStateBeforeDesktop;
        Opacity = 1;
    }

    private static Rectangle OnAvailableMonitor(Rectangle bounds)
    {
        Rectangle work = Screen.FromRectangle(bounds).WorkingArea;
        int width = Math.Min(bounds.Width, work.Width);
        int height = Math.Min(bounds.Height, work.Height);
        return new Rectangle(Math.Clamp(bounds.X, work.Left, work.Right - width),
            Math.Clamp(bounds.Y, work.Top, work.Bottom - height), width, height);
    }

    private void SwitchOutput(nint target)
    {
        var previous = player!;
        bool paused = previous.Flag("pause");
        double position = previous.Flag("seekable") ? previous.Number("time-pos") : 0;
        var replacement = new Mpv(target);
        try
        {
            CopyPlaybackOptions(previous, replacement);
            replacement.Set("loop-file", repeatVideo ? "inf" : "no");
            replacement.Set("pause", paused ? "yes" : "no");
            replacement.Set("panscan", scaleMode);
            replacement.Set("video-aspect-override", aspectMode);
            replacement.Set("video-zoom", Math.Log2(zoom / 100d).ToString("R", CultureInfo.InvariantCulture));
            if (position > 0)
            {
                // Start before the saved timestamp by a fraction of a millisecond so
                // rounding cannot put the seek target just past the displayed frame.
                string start = Math.Max(0, position - 0.0001).ToString("F6", CultureInfo.InvariantCulture);
                LoadMedia(replacement, path!, start);
            }
            else
                LoadMedia(replacement, path!);
        }
        catch
        {
            restoreOutputTracks = null;
            replacement.Dispose();
            throw;
        }
        previous.Dispose();
        player = replacement;
        loaded = false;
        resizeAfterAspectChange = false;
        sizeToVideo = false;
    }

    private void Try(Action action)
    {
        try { action(); }
        catch (DllNotFoundException)
        {
            MessageBox.Show(this, "KoView.exe 옆에 Windows x64용 libmpv-2.dll을 넣어주세요.", Text);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, Text);
        }
    }

    private static void ToggleMaximize(Form window) =>
        window.WindowState = window.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;

    private void ToggleFullscreen()
    {
        if (compact)
            ToggleCompact();
        if (fullscreen)
        {
            fullscreen = false;
            SetChromeVisible(true);
            chromeDeadline = Environment.TickCount64 + 3000;
            Bounds = fullscreenBounds;
            WindowState = fullscreenState;
            return;
        }
        if (desktop is not null)
            return;

        Rectangle monitor = Screen.FromControl(this).Bounds;
        fullscreenState = WindowState;
        fullscreenBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowState = FormWindowState.Normal;
        fullscreen = true;
        bar.Hide();
        controls.Hide();
        Padding = Padding.Empty;
        Bounds = monitor;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var settings = base.CreateParams;
            settings.Style &= ~0x00C00000; // KoView draws its own caption.
            settings.Style |= 0x00020000 | 0x00010000 | 0x00080000 | 0x00040000; // WS_THICKFRAME enables native resizing.
            return settings;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        const int nonClientRenderingPolicy = 2;
        int disabled = 1;
        DwmSetWindowAttribute(Handle, nonClientRenderingPolicy, ref disabled, sizeof(int));
    }

    protected override void WndProc(ref Message message)
    {
        // The native libmpv window can receive mouse input on another thread.
        if (message.Msg == 0x0210 && (message.WParam.ToInt64() & 0xffff) == 0x0204)
            RequestSettings();
        if (message.Msg is 0x0083 or 0x0085) // WM_NCCALCSIZE or WM_NCPAINT
        {
            // Keep the full client area and let KoView paint its custom frame.
            message.Result = 0;
            return;
        }
        if (message.Msg == 0x0086) // WM_NCACTIVATE
        {
            nint original = message.LParam;
            message.LParam = -1; // Update activation without repainting a native caption.
            base.WndProc(ref message);
            message.LParam = original;
        }
        else
            base.WndProc(ref message);
        if (message.Msg == 0x0024) // WM_GETMINMAXINFO
        {
            var monitor = Screen.FromHandle(message.HWnd);
            var work = monitor.WorkingArea;
            var sizing = Marshal.PtrToStructure<WindowSizing>(message.LParam);
            sizing.MaxPosition = new Point(work.Left - monitor.Bounds.Left, work.Top - monitor.Bounds.Top);
            sizing.MaxSize = work.Size;
            Marshal.StructureToPtr(sizing, message.LParam, false);
        }
        if (message.Msg == 0x0232) // WM_EXITSIZEMOVE
        {
            resizingWindow = false;
            chromeDeadline = Environment.TickCount64 + 3000;
        }
        if (fullscreen || WindowState != FormWindowState.Normal || desktop is not null)
            return;

        if (message.Msg == 0x0231) // WM_ENTERSIZEMOVE
        {
            resizingWindow = true;
            double width = loaded ? player!.Number("dwidth") : 0;
            double height = loaded ? player!.Number("dheight") : 0;
            resizeAspect = width > 0 && height > 0 ? width / height : 0;
        }
        if (message.Msg == 0x0214 && resizeAspect > 0 && message.WParam.ToInt32() is 4 or 5 or 7 or 8)
        {
            ResizeCorner(message.LParam, message.WParam.ToInt32());
            message.Result = 1;
            return;
        }
        if (message.Msg != 0x0084)
            return;
        ApplyResizeHitTest(this, ref message);
    }

    private static void ApplyResizeHitTest(Form window, ref Message message)
    {
        long packed = message.LParam.ToInt64();
        Point point = window.PointToClient(new Point((short)packed, (short)(packed >> 16)));
        int edge = window.Padding.Left;
        int corner = window.LogicalToDeviceUnits(14);
        bool left = point.X < edge, right = point.X >= window.ClientSize.Width - edge;
        bool top = point.Y < edge, bottom = point.Y >= window.ClientSize.Height - edge;
        bool nearLeft = point.X < corner, nearRight = point.X >= window.ClientSize.Width - corner;
        bool nearTop = point.Y < corner, nearBottom = point.Y >= window.ClientSize.Height - corner;
        if (top)
            message.Result = nearLeft ? 13 : nearRight ? 14 : 12;
        else if (bottom)
            message.Result = nearLeft ? 16 : nearRight ? 17 : 15;
        else if (left)
            message.Result = nearTop ? 13 : nearBottom ? 16 : 10;
        else if (right)
            message.Result = nearTop ? 14 : nearBottom ? 17 : 11;
    }

    private sealed class PlaylistForm : Form
    {
        public event EventHandler? UserMoving;

        protected override CreateParams CreateParams
        {
            get
            {
                var settings = base.CreateParams;
                settings.Style |= 0x00020000 | 0x00010000 | 0x00080000;
                return settings;
            }
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x0216) // WM_MOVING
                UserMoving?.Invoke(this, EventArgs.Empty);
            if (message.Msg == 0x0084 && WindowState == FormWindowState.Normal)
                ApplyResizeHitTest(this, ref message);
        }
    }

    private void ResizeCorner(nint rectangle, int corner)
    {
        var bounds = Marshal.PtrToStructure<SizingRect>(rectangle);
        int chromeWidth = Width - video.Width;
        int chromeHeight = Height - video.Height;
        int proposedWidth = bounds.Right - bounds.Left - chromeWidth;
        int proposedHeight = bounds.Bottom - bounds.Top - chromeHeight;

        // Project the dragged corner onto the aspect-ratio line.
        double height = (proposedWidth * resizeAspect + proposedHeight) / (resizeAspect * resizeAspect + 1);
        height = Math.Max(height, Math.Max(Math.Max(1, MinimumSize.Height - chromeHeight),
            Math.Max(1, MinimumSize.Width - chromeWidth) / resizeAspect));
        int widthWithChrome = (int)Math.Round(height * resizeAspect) + chromeWidth;
        int heightWithChrome = (int)Math.Round(height) + chromeHeight;

        if (corner is 4 or 7)
            bounds.Left = bounds.Right - widthWithChrome;
        else
            bounds.Right = bounds.Left + widthWithChrome;
        if (corner is 4 or 5)
            bounds.Top = bounds.Bottom - heightWithChrome;
        else
            bounds.Bottom = bounds.Top + heightWithChrome;

        Marshal.StructureToPtr(bounds, rectangle, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSizing
    {
        public Point Reserved;
        public Size MaxSize;
        public Point MaxPosition, MinTrackSize, MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizingRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string? left, string? right);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
