using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KoVideo;

internal sealed partial class PlayerWindow
{
    private double? loopStart, loopEnd;
    private bool compact;
    private Rectangle beforeCompact;
    private FormWindowState stateBeforeCompact;
    private Action? restoreOutputTracks;

    private static string MpvNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private void ChangeVolume(int change)
    {
        if (player is not { } current)
            return;

        double volume = Math.Clamp(current.Number("volume") + change, 0, 100);
        current.Set("volume", MpvNumber(volume));
        if (loaded && desktop is null)
            current.Command("show-text", $"{volume:0}%", "1200");
    }

    private void ToggleMute()
    {
        if (player is { } current)
            current.Set("mute", current.Flag("mute") ? "no" : "yes");
    }

    private void AddPlaybackMenus(ContextMenuStrip menu)
    {
        var volume = new ToolStripMenuItem($"음량 {player?.Number("volume") ?? session.Volume:0}%") { Enabled = player is not null };
        AddAction(volume, "음량 높이기 (↑)", () => ChangeVolume(5));
        AddAction(volume, "음량 낮추기 (↓)", () => ChangeVolume(-5));
        var mute = AddAction(volume, "음소거 (M)", ToggleMute);
        mute.Checked = player?.Flag("mute") ?? session.Muted;
        menu.Items.Add(volume);

        var speed = new ToolStripMenuItem("재생 속도") { Enabled = player is not null };
        foreach (double rate in new[] { 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 })
            AddAction(speed, $"{rate:0.##}×", () => player!.Set("speed", MpvNumber(rate))).Checked =
                player?.Number("speed") == rate;
        menu.Items.Add(speed);

        menu.Items.Add(TrackMenu("오디오 트랙", "audio", "aid"));
        var subtitles = TrackMenu("자막", "sub", "sid");
        subtitles.DropDownItems.Add(new ToolStripSeparator());
        AddAction(subtitles, "외부 자막 열기…", () => BeginInvoke(() => Try(OpenSubtitles)));
        double delay = player?.Number("sub-delay") ?? 0;
        var sync = new ToolStripMenuItem($"자막 싱크 ({delay:+0.0;-0.0;0.0}초)");
        AddAction(sync, "0.1초 늦추기", () => SetSubtitleDelay(delay + 0.1));
        AddAction(sync, "0.1초 앞당기기", () => SetSubtitleDelay(delay - 0.1));
        AddAction(sync, "초기화", () => SetSubtitleDelay(0));
        subtitles.DropDownItems.Add(sync);
        menu.Items.Add(subtitles);

        var loop = new ToolStripMenuItem("A–B 구간 반복")
        {
            Enabled = loaded && !IsPhoto(path) && player?.Flag("seekable") == true
        };
        AddAction(loop, loopStart is double a ? $"A 다시 지정 ({a:F2}초)" : "현재 위치를 A로 지정", MarkLoopStart);
        AddAction(loop, loopEnd is double b ? $"B 다시 지정 ({b:F2}초)" : "현재 위치를 B로 지정", MarkLoopEnd)
            .Enabled = loopStart.HasValue;
        AddAction(loop, "구간 반복 해제", ResetLoop).Enabled = loopStart.HasValue;
        menu.Items.Add(loop);
        var compactItem = new ToolStripMenuItem("미니 플레이어 · 항상 위 (Ctrl+P)") { Checked = compact };
        compactItem.Click += (_, _) => ToggleCompact();
        menu.Items.Add(compactItem);
    }

    private ToolStripMenuItem AddAction(ToolStripMenuItem parent, string label, Action action)
    {
        var item = new ToolStripMenuItem(label);
        item.Click += (_, _) => Try(action);
        parent.DropDownItems.Add(item);
        return item;
    }

    private ToolStripMenuItem TrackMenu(string label, string type, string property)
    {
        var menu = new ToolStripMenuItem(label) { Enabled = loaded && !IsPhoto(path) };
        if (player is not { } current || !menu.Enabled)
            return menu;
        string? selected = current.Get(property);
        AddAction(menu, "끄기", () => current.Set(property, "no")).Checked = selected is "no" or null;
        for (int i = 0; i < current.Number("track-list/count"); i++)
        {
            string track = $"track-list/{i}/";
            if (current.Get(track + "type") != type || current.Get(track + "id") is not { } id)
                continue;
            string details = string.Join(" · ", new[] { current.Get(track + "title"), current.Get(track + "lang"), current.Get(track + "codec") }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            AddAction(menu, details.Length == 0 ? id : $"{id}  {details}", () => current.Set(property, id))
                .Checked = selected == id;
        }
        return menu;
    }

    private void OpenSubtitles()
    {
        using var dialog = new OpenFileDialog { Filter = "자막 (*.srt;*.ass;*.ssa;*.vtt;*.sub)|*.srt;*.ass;*.ssa;*.vtt;*.sub|모든 파일|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            player?.Command("sub-add", dialog.FileName, "select");
    }

    private void SetSubtitleDelay(double delay) =>
        player?.Set("sub-delay", MpvNumber(Math.Round(delay, 1)));

    private void MarkLoopStart()
    {
        ResetLoop();
        loopStart = player!.Number("time-pos");
    }

    private void MarkLoopEnd()
    {
        double end = player!.Number("time-pos");
        if (loopStart is not double start || end <= start)
            throw new InvalidOperationException("B 지점은 A 지점보다 뒤에 있어야 합니다.");
        player.Set("ab-loop-a", MpvNumber(start));
        player.Set("ab-loop-b", MpvNumber(end));
        loopEnd = end;
        player.Command("seek", MpvNumber(start), "absolute+exact");
    }

    private void ResetLoop()
    {
        loopStart = loopEnd = null;
        player?.Set("ab-loop-a", "no");
        player?.Set("ab-loop-b", "no");
    }

    private void CopyPlaybackOptions(Mpv previous, Mpv replacement)
    {
        foreach (string property in new[] { "volume", "mute", "speed", "sub-delay", "ab-loop-a", "ab-loop-b" })
            if (previous.Get(property) is { } value)
                replacement.Set(property, value);

        string? audio = previous.Get("aid");
        string? subtitle = previous.Get("sid");
        string? externalSubtitle = previous.Get("current-tracks/sub/external-filename");
        restoreOutputTracks = () =>
        {
            if (audio is not null)
                replacement.Set("aid", audio);
            if (externalSubtitle is not null && replacement.Get("current-tracks/sub/external-filename") != externalSubtitle)
                replacement.Command("sub-add", externalSubtitle, "select");
            else if (subtitle is not null)
                replacement.Set("sid", subtitle);
        };
    }

    private static string? PositionFile(string? source)
    {
        if (source is null || !Path.IsPathFullyQualified(source) || IsPhoto(source))
            return null;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoView", "Resume", hash);
    }

    private void SavePosition()
    {
        if (!loaded || player is not { } current || !current.Flag("seekable") || PositionFile(path) is not { } file)
            return;
        double position = current.Number("time-pos");
        try
        {
            if (position <= 0 || current.Flag("eof-reached"))
                File.Delete(file);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string temporary = file + "." + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temporary, MpvNumber(position));
                    File.Move(temporary, file, overwrite: true);
                }
                finally { File.Delete(temporary); }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ReadPosition(string source)
    {
        try
        {
            if (PositionFile(source) is { } file && double.TryParse(File.ReadAllText(file),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double position) &&
                double.IsFinite(position) && position > 0)
                return MpvNumber(position);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private void ToggleCompact()
    {
        if (compact)
        {
            compact = false;
            TopMost = false;
            Bounds = beforeCompact;
            WindowState = stateBeforeCompact;
            return;
        }
        if (fullscreen)
            ToggleFullscreen();
        stateBeforeCompact = WindowState;
        beforeCompact = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowState = FormWindowState.Normal;
        compact = true;
        sizeToVideo = false;
        TopMost = true;
        var work = Screen.FromControl(this).WorkingArea;
        int width = Math.Min(work.Width, Math.Max(MinimumSize.Width, 384));
        int height = Math.Clamp((int)(width / (resizeAspect > 0 ? resizeAspect : 16d / 9)), MinimumSize.Height, work.Height);
        Bounds = new Rectangle(work.Right - width - 12, work.Bottom - height - 12, width, height);
    }

    private void AddPlaylistMenu(ListBox list, Form window)
    {
        var menu = new ContextMenuStrip();
        var remove = menu.Items.Add("목록에서 제거 (Delete)", null, (_, _) => Try(() => RemovePlaylistItem(PlaylistIndex(list.SelectedIndex))));
        var up = menu.Items.Add("위로 이동", null, (_, _) => MovePlaylistItem(PlaylistIndex(list.SelectedIndex), -1));
        var down = menu.Items.Add("아래로 이동", null, (_, _) => MovePlaylistItem(PlaylistIndex(list.SelectedIndex), 1));
        menu.Items.Add(new ToolStripSeparator());
        var save = menu.Items.Add("재생 목록 저장…", null, (_, _) => window.BeginInvoke(() => Try(() => SavePlaylistDialog(window))));
        menu.Items.Add("재생 목록 불러오기…", null, (_, _) => window.BeginInvoke(() => Try(() => LoadPlaylistDialog(window))));
        menu.Opening += (_, _) =>
        {
            int index = PlaylistIndex(list.SelectedIndex);
            remove.Enabled = index >= 0 && index < folderVideos.Length;
            up.Enabled = remove.Enabled && index > 0;
            down.Enabled = remove.Enabled && index < folderVideos.Length - 1;
            save.Enabled = folderVideos.Length > 0;
        };
        list.ContextMenuStrip = menu;
        list.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                list.SelectedIndex = list.IndexFromPoint(e.Location);
        };
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
            {
                Try(() => RemovePlaylistItem(PlaylistIndex(list.SelectedIndex)));
                e.SuppressKeyPress = true;
            }
        };
        list.Disposed += (_, _) => menu.Dispose();
    }

    private void MovePlaylistItem(int index, int change)
    {
        ReorderPlaylist(index, index + change);
    }

    private void RemovePlaylistItem(int index)
    {
        if (index < 0 || index >= folderVideos.Length)
            return;
        bool wasPlaying = index == folderIndex;
        failedFiles.Remove(folderVideos[index]);
        folderVideos = folderVideos.Where((_, item) => item != index).ToArray();
        if (folderVideos.Length == 0)
        {
            SavePosition();
            player?.Command("stop");
            loaded = false;
            playOnLoad = false;
            sizeToVideo = false;
            path = null;
            folderIndex = -1;
            caption.Text = "KoView";
            video.Controls[0].Visible = true;
        }
        else if (wasPlaying)
            PlayPlaylistItem(index % folderVideos.Length);
        else if (index < folderIndex)
            folderIndex--;
        RefreshPlaylist();
    }

    private const string PlaylistFilter = "M3U 재생 목록 (*.m3u8;*.m3u)|*.m3u8;*.m3u";

    private void SavePlaylistDialog(Form owner)
    {
        using var dialog = new SaveFileDialog { Filter = PlaylistFilter, DefaultExt = "m3u8" };
        if (dialog.ShowDialog(owner) == DialogResult.OK)
            SavePlaylist(dialog.FileName);
    }

    private void SavePlaylist(string file) =>
        File.WriteAllLines(file, new[] { "#EXTM3U" }.Concat(folderVideos), Encoding.UTF8);

    private void LoadPlaylistDialog(Form owner)
    {
        using var dialog = new OpenFileDialog { Filter = PlaylistFilter };
        if (dialog.ShowDialog(owner) == DialogResult.OK)
            LoadPlaylist(dialog.FileName);
    }

    private void LoadPlaylist(string file)
    {
        string directory = Path.GetDirectoryName(file)!;
        string[] entries = File.ReadLines(file).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line =>
            {
                try { return Path.GetFullPath(line, directory); }
                catch (ArgumentException) { return null; }
            })
            .OfType<string>()
            .Where(entry => VideoExtensions.Contains(Path.GetExtension(entry)) || IsPhoto(entry))
            .ToArray();
        if (entries.Length == 0)
            throw new InvalidOperationException("재생 목록에 열 수 있는 파일이 없습니다.");
        failedFiles.Clear();
        OpenMedia(entries[0], Path.GetFileName(entries[0]), repeat: entries.Length == 1);
        folderVideos = entries;
        folderIndex = 0;
        RefreshPlaylist();
    }
}
