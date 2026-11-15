using System.Text.Json;

namespace KoVideo;

internal sealed partial class PlayerWindow
{
    private readonly SessionSettings session = ReadSession();
    private readonly HashSet<string> failedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> visiblePlaylist = [];
    private TextBox? playlistSearch;
    private string? retrySource;
    private string? historySelection;
    private int retryCount;
    private long retryAt, connectDeadline, stableAfter;

    private sealed class SessionSettings
    {
        public double Volume { get; set; } = 100;
        public bool Muted { get; set; }
        public bool HideFrame { get; set; } = true;
        public int Slideshow { get; set; }
        public bool Repeat { get; set; }
        public string? Selected { get; set; }
        public string[] Playlist { get; set; } = [];
        public List<string> Recent { get; set; } = [];
        public List<string> Favorites { get; set; } = [];
    }

    private static string SessionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoView", "Session.json");

    private static SessionSettings ReadSession()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SessionSettings>(File.ReadAllText(SessionPath)) ?? new();
            saved.Playlist ??= [];
            saved.Recent = (saved.Recent ?? []).Where(CanRemember).Distinct().Take(12).ToList();
            saved.Favorites = (saved.Favorites ?? []).Where(CanRemember).Distinct().ToList();
            return saved;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private void SaveSession()
    {
        session.HideFrame = autoHideChrome;
        session.Slideshow = slideshowSeconds;
        session.Repeat = repeatVideo;
        session.Playlist = folderVideos;
        session.Selected = folderIndex >= 0 && folderIndex < folderVideos.Length ? folderVideos[folderIndex] : null;
        if (player is { } current)
        {
            session.Volume = current.Number("volume");
            session.Muted = current.Flag("mute");
        }
        string temporary = SessionPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SessionPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(session));
            File.Move(temporary, SessionPath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void RestoreSession()
    {
        if (path is not null || folderVideos.Length != 0)
            return;
        folderVideos = session.Playlist.Where(item => item is not null && Path.IsPathFullyQualified(item)).ToArray();
        if (folderVideos.Length == 0)
            return;
        int selected = Array.IndexOf(folderVideos, session.Selected);
        PlayPlaylistItem(Math.Max(0, selected));
    }

    private static bool IsRtsp(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var url) && url.Scheme is "rtsp" or "rtsps";

    private static bool CanRemember(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;
        if (Path.IsPathFullyQualified(source))
            return true;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var url) || url.UserInfo.Length != 0)
            return false;
        return url.Scheme is "http" or "https" or "rtsp" or "rtsps" &&
            (url.Query.Length == 0 || YouTubeWindow.IsYouTubeHost(url.IdnHost));
    }

    private void Remember(string source)
    {
        if (!CanRemember(source))
            return;
        historySelection = source;
        session.Recent.Remove(source);
        session.Recent.Insert(0, source);
        if (session.Recent.Count > 12)
            session.Recent.RemoveAt(12);
    }

    private void OpenRemembered(string source)
    {
        if (Path.IsPathFullyQualified(source))
            Open(source);
        else if (YouTubeWindow.Parse(new Uri(source)) is { } selection)
            ShowYouTube(selection);
        else
            OpenStream(source);
    }

    private void OpenStream(string source)
    {
        var url = new Uri(source);
        OpenMedia(source, $"{url.Scheme.ToUpperInvariant()} · {url.Host}", repeat: false);
        folderVideos = [];
        folderIndex = -1;
        failedFiles.Clear();
        RefreshPlaylist();
    }

    private void AddHistoryMenus(ContextMenuStrip menu)
    {
        static string Display(string source) => Path.IsPathFullyQualified(source) ? Path.GetFileName(source) :
            Uri.TryCreate(source, UriKind.Absolute, out var url) ? url.Host + url.AbsolutePath : source;
        var recent = new ToolStripMenuItem("최근 연 파일·링크") { Enabled = session.Recent.Count > 0 };
        foreach (string source in session.Recent)
            AddAction(recent, Display(source), () => BeginInvoke(() => Try(() => OpenRemembered(source))));
        menu.Items.Add(recent);
        var favorites = new ToolStripMenuItem("즐겨찾기");
        var current = historySelection;
        if (CanRemember(current))
        {
            bool saved = session.Favorites.Contains(current!);
            AddAction(favorites, saved ? "현재 항목 제거" : "현재 항목 추가", () =>
            {
                if (saved) session.Favorites.Remove(current!);
                else session.Favorites.Add(current!);
            });
            favorites.DropDownItems.Add(new ToolStripSeparator());
        }
        foreach (string source in session.Favorites)
            AddAction(favorites, Display(source), () => BeginInvoke(() => Try(() => OpenRemembered(source))));
        favorites.Enabled = favorites.DropDownItems.Count > 0;
        menu.Items.Add(favorites);
    }

    private void SaveFrame()
    {
        if (!loaded || player is not { } current || current.Number("dwidth") <= 0)
            return;
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG 이미지 (*.png)|*.png", DefaultExt = "png",
            FileName = (Path.IsPathFullyQualified(path ?? "") ? Path.GetFileNameWithoutExtension(path) : "KoView") + ".png"
        };
        bool resume = !current.Flag("pause");
        if (resume)
            current.Set("pause", "yes");
        try
        {
            if (dialog.ShowDialog(this) == DialogResult.OK)
                ExportFrame(dialog.FileName);
        }
        finally
        {
            if (resume)
                current.Set("pause", "no");
        }
    }

    private void ExportFrame(string file) => player!.Command("screenshot-to-file", file, "video");

    private int PlaylistIndex(int row) => row >= 0 && row < visiblePlaylist.Count ? visiblePlaylist[row] : -1;

    private void ReorderPlaylist(int source, int destination)
    {
        if (source < 0 || destination < 0 || source == destination ||
            source >= folderVideos.Length || destination >= folderVideos.Length)
            return;
        var entries = folderVideos.ToList();
        string moved = entries[source];
        entries.RemoveAt(source);
        entries.Insert(destination, moved);
        if (folderIndex == source)
            folderIndex = destination;
        else if (source < folderIndex && folderIndex <= destination)
            folderIndex--;
        else if (destination <= folderIndex && folderIndex < source)
            folderIndex++;
        folderVideos = entries.ToArray();
        RefreshPlaylist();
        if (playlistList is { } list)
            list.SelectedIndex = visiblePlaylist.IndexOf(destination);
    }

    private void EnablePlaylistReorder(ListBox list)
    {
        const string format = "KoView.PlaylistRow";
        Point? origin = null;
        int dragged = -1;
        list.MouseDown += (_, e) =>
        {
            origin = e.Button == MouseButtons.Left ? e.Location : null;
            dragged = PlaylistIndex(list.IndexFromPoint(e.Location));
        };
        list.MouseMove += (_, e) =>
        {
            if (origin is not Point start || e.Button != MouseButtons.Left || dragged < 0)
                return;
            var threshold = SystemInformation.DragSize;
            if (Math.Abs(start.X - e.X) < threshold.Width / 2 && Math.Abs(start.Y - e.Y) < threshold.Height / 2)
                return;
            origin = null;
            var data = new DataObject(format, dragged);
            try { list.DoDragDrop(data, DragDropEffects.Move); }
            finally { dragged = -1; }
        };
        void Mark(object? _, DragEventArgs e)
        {
            if (dragged >= 0 && e.Data?.GetDataPresent(format) == true)
                e.Effect = DragDropEffects.Move;
        }
        list.DragEnter += Mark;
        list.DragOver += Mark;
        list.DragDrop += (_, e) =>
        {
            if (dragged < 0 || e.Data?.GetDataPresent(format) != true)
                return;
            int row = list.IndexFromPoint(list.PointToClient(new Point(e.X, e.Y)));
            ReorderPlaylist(dragged, row == ListBox.NoMatches ? visiblePlaylist.LastOrDefault(-1) : PlaylistIndex(row));
        };
    }

    private void BeginNetworkAttempt(string source)
    {
        retrySource = IsRtsp(source) ? source : null;
        retryCount = 0;
        retryAt = stableAfter = 0;
        connectDeadline = retrySource is not null ? Environment.TickCount64 + 12000 : 0;
        player?.ClearNetworkError();
        ShowPlayerStatus(connectDeadline > 0 ? "RTSP 연결 중…  Esc로 취소" : null);
    }

    private void ShowPlayerStatus(string? message)
    {
        var status = video.Controls[0];
        status.Text = message ?? "영상 열기  ·  Ctrl+O";
        status.Visible = desktop is null && (message is not null || path is null);
    }

    private void CancelNetworkRetry()
    {
        retrySource = null;
        retryAt = connectDeadline = stableAfter = 0;
        StopFailedPlayback();
        player?.Command("stop");
        ShowPlayerStatus("RTSP 연결을 취소했습니다.  ·  Ctrl+N");
    }

    private void StopFailedPlayback()
    {
        playOnLoad = loaded = sizeToVideo = false;
        path = null;
        caption.Text = "KoView";
        RefreshPlaylist();
    }

    private bool UpdateNetworkTimers()
    {
        long now = Environment.TickCount64;
        if (stableAfter > 0 && now >= stableAfter)
        {
            retryCount = 0;
            stableAfter = 0;
        }
        if (connectDeadline > 0 && now >= connectDeadline)
        {
            player!.Command("stop");
            HandlePlaybackFailure("timeout");
            return true;
        }
        if (retryAt == 0 || now < retryAt || retrySource is not { } source)
            return false;
        int attempt = retryCount;
        OpenStream(source);
        retryCount = attempt;
        return true;
    }

    private void HandlePlaybackFailure(string failure)
    {
        string? source = path;
        StopFailedPlayback();
        connectDeadline = stableAfter = 0;
        if (retrySource is not null)
        {
            string? reason = failure is "timeout" or "disconnected" ? failure : player?.NetworkError;
            if (reason is "timeout" or "disconnected" && retryCount < 3)
            {
                retryAt = Environment.TickCount64 + 1000L * ++retryCount;
                ShowPlayerStatus($"RTSP 재연결 대기 중 ({retryCount}/3)  ·  Esc로 취소");
                return;
            }
            retryAt = 0;
            retrySource = null;
            ShowPlayerStatus(reason switch
            {
                "denied" => "RTSP 서버가 접근을 거부했습니다.  ·  Ctrl+N",
                "timeout" => "RTSP 연결 시간이 초과됐습니다.  ·  Ctrl+N",
                "disconnected" => "RTSP 서버와 연결이 끊겼습니다.  ·  Ctrl+N",
                _ => "RTSP 스트림을 열 수 없습니다.  ·  Ctrl+N"
            });
            return;
        }
        if (source is not null && folderIndex >= 0 && folderIndex < folderVideos.Length &&
            string.Equals(folderVideos[folderIndex], source, StringComparison.OrdinalIgnoreCase))
        {
            failedFiles.Add(source);
            TryNextPlaylistItem();
            return;
        }
        ShowPlayerStatus("영상을 재생할 수 없습니다.");
        throw new InvalidOperationException($"영상을 재생할 수 없습니다. {failure}");
    }

    private void TryNextPlaylistItem()
    {
        for (int offset = 1; offset <= folderVideos.Length; offset++)
        {
            int index = (folderIndex + offset) % folderVideos.Length;
            if (failedFiles.Contains(folderVideos[index]))
                continue;
            PlayPlaylistItemCore(index);
            return;
        }
        StopFailedPlayback();
        ShowPlayerStatus($"재생 목록의 {failedFiles.Count}개 파일을 열 수 없습니다.");
    }
}
