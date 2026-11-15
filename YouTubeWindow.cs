using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KoVideo;

internal sealed class YouTubeWindow : Form
{
    private const string PlayerPage = "https://koview.player/youtube";
    private readonly Uri videoPage;
    private readonly Uri embedPage;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    private readonly Label status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.Gainsboro
    };

    internal sealed record Selection(Uri Page, Uri Embed);

    public YouTubeWindow(Selection selection)
    {
        videoPage = selection.Page;
        embedPage = selection.Embed;
        Text = "KoView · YouTube";
        ClientSize = new Size(780, 476);
        MinimumSize = new Size(500, 340);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(34, 34, 36);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        var open = new Button
        {
            Text = "브라우저에서 열기", Dock = DockStyle.Right, Width = 142,
            FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro
        };
        open.FlatAppearance.BorderSize = 0;
        open.Click += (_, _) => OpenInBrowser(videoPage);
        status.Text = "유튜브 플레이어를 여는 중…";
        footer.Controls.Add(status);
        if (HttpUtility.ParseQueryString(videoPage.Query)["list"] is not null)
        {
            var actions = new Panel { Dock = DockStyle.Left, Width = 104 };
            var next = new Button { Text = "다음", Dock = DockStyle.Left, Width = 52, ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var previous = new Button { Text = "이전", Dock = DockStyle.Left, Width = 52, ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            next.FlatAppearance.BorderSize = previous.FlatAppearance.BorderSize = 0;
            previous.Click += async (_, _) => await ChangePlaylistVideo("previousVideo");
            next.Click += async (_, _) => await ChangePlaylistVideo("nextVideo");
            actions.Controls.Add(next);
            actions.Controls.Add(previous);
            footer.Controls.Add(actions);
        }
        footer.Controls.Add(open);
        Controls.Add(browser);
        Controls.Add(footer);
        Shown += async (_, _) => await OpenPlayer();
    }

    internal static bool IsYouTubeHost(string host) => host is
        "youtube.com" or "www.youtube.com" or "m.youtube.com" or "youtu.be" or "www.youtu.be" or
        "youtube-nocookie.com" or "www.youtube-nocookie.com";

    internal static string? VideoId(Uri url)
    {
        if (url.Scheme is not ("http" or "https") || !IsYouTubeHost(url.IdnHost))
            return null;

        string[] segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? id = url.IdnHost switch
        {
            "youtu.be" or "www.youtu.be" when segments.Length == 1 => segments[0],
            _ when segments is ["watch"] => HttpUtility.ParseQueryString(url.Query)["v"],
            _ when segments is ["shorts" or "live" or "embed", var video] => video,
            _ => null
        };
        return id is not null && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z") ? id : null;
    }

    internal static Selection? Parse(Uri url)
    {
        if (url.Scheme is not ("http" or "https") || !IsYouTubeHost(url.IdnHost))
            return null;
        string? id = VideoId(url);
        var query = HttpUtility.ParseQueryString(url.Query);
        string? list = query["list"];
        if (list is not null && !Regex.IsMatch(list, @"\A[A-Za-z0-9_-]{2,150}\z"))
            list = null;
        if (id is null && (list is null || url.AbsolutePath.TrimEnd('/') is not ("/playlist" or "/watch" or "/embed/videoseries")))
            return null;

        int start = StartSeconds(query["t"] ?? query["start"] ?? HttpUtility.ParseQueryString(url.Fragment.TrimStart('#'))["t"]);
        var embed = HttpUtility.ParseQueryString("");
        embed["autoplay"] = "1";
        embed["enablejsapi"] = "1";
        embed["origin"] = new Uri(PlayerPage).GetLeftPart(UriPartial.Authority);
        var watch = HttpUtility.ParseQueryString("");
        if (id is not null)
            watch["v"] = id;
        if (list is not null)
        {
            watch["list"] = list;
            embed["list"] = list;
            embed["listType"] = "playlist";
        }
        if (start > 0)
        {
            watch["t"] = start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            embed["start"] = watch["t"];
        }
        return new(
            new Uri($"https://www.youtube.com/{(id is null ? "playlist" : "watch")}?{watch}"),
            new Uri($"https://www.youtube.com/embed/{id ?? "videoseries"}?{embed}"));
    }

    private static int StartSeconds(string? value)
    {
        if (int.TryParse(value, out int seconds) && seconds >= 0)
            return seconds;
        if (value is null)
            return 0;
        var time = Regex.Match(value, @"\A(?:(?<h>[0-9]{1,6})h)?(?:(?<m>[0-9]{1,6})m)?(?:(?<s>[0-9]{1,6})s)?\z");
        if (!time.Success)
            return 0;
        static long Unit(Group group) => group.Success ? long.Parse(group.Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return (int)Math.Min(int.MaxValue, Unit(time.Groups["h"]) * 3600 + Unit(time.Groups["m"]) * 60 + Unit(time.Groups["s"]));
    }

    private async Task OpenPlayer()
    {
        try
        {
            // WebView2 cannot share a profile between hosts with different environment settings.
            string host = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..16];
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoView", "WebView2", host);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: data);
            if (IsDisposed)
                return;
            await browser.EnsureCoreWebView2Async(environment);
            if (IsDisposed)
                return;

            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != PlayerPage)
                    return;
                status.Text = e.TryGetWebMessageAsString() switch
                {
                    "ready" => "",
                    "blocked" => "플레이어의 ▶ 버튼을 눌러 재생하세요.",
                    "error:101" or "error:150" => "이 영상은 앱 내 재생이 제한되어 있습니다.",
                    "error:100" => "영상이 비공개이거나 삭제됐습니다.",
                    "error:153" => "유튜브가 앱의 재생 요청을 확인하지 못했습니다.",
                    _ => "유튜브에서 이 영상을 재생할 수 없습니다."
                };
            };
            core.AddWebResourceRequestedFilter(PlayerPage, CoreWebView2WebResourceContext.Document);
            core.WebResourceRequested += (_, e) =>
            {
                if (e.Request.Uri != PlayerPage)
                    return;
                string html = $$"""
                    <!doctype html><html lang="ko"><head><meta charset="utf-8">
                    <meta name="referrer" content="strict-origin-when-cross-origin">
                    <title>KoView · YouTube</title>
                    <style>html,body,iframe{width:100%;height:100%;margin:0;border:0;background:#000}iframe{display:block}</style>
                    </head><body><iframe id="youtube" title="YouTube" src="{{WebUtility.HtmlEncode(embedPage.AbsoluteUri)}}"
                    allow="autoplay; encrypted-media; fullscreen; picture-in-picture" allowfullscreen
                    referrerpolicy="strict-origin-when-cross-origin"></iframe>
                    <script>
                    function onYouTubeIframeAPIReady() {
                        const notify = value => window.chrome.webview.postMessage(value);
                        window.koViewYouTube = new YT.Player('youtube', {events: {
                            onReady: () => notify('ready'),
                            onStateChange: event => { if (event.data === 1) notify('ready'); },
                            onError: event => notify('error:' + event.data),
                            onAutoplayBlocked: () => notify('blocked')
                        } });
                    }
                    </script><script src="https://www.youtube.com/iframe_api"></script></body></html>
                    """;
                var response = new MemoryStream(Encoding.UTF8.GetBytes(html));
                e.Response = environment.CreateWebResourceResponse(response, 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\nContent-Security-Policy: default-src 'none'; frame-src https://www.youtube.com; style-src 'unsafe-inline'; script-src 'unsafe-inline' https://www.youtube.com https://s.ytimg.com");
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri == PlayerPage)
                    return;
                e.Cancel = true;
                if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && IsBrowserTarget(target))
                    OpenInBrowser(target);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.IsUserInitiated && Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && IsBrowserTarget(target))
                    OpenInBrowser(target);
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                    status.Text = "유튜브에 연결할 수 없습니다.";
                if (e.IsSuccess)
                    browser.Focus();
            };
            core.Navigate(PlayerPage);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!IsDisposed)
                status.Text = "앱 안에서 재생하려면 Microsoft Edge WebView2 런타임이 필요합니다.";
        }
        catch (Exception error) when (error is InvalidOperationException or
            System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("WebView2 initialization failed: {0:X8}", error.HResult);
            if (!IsDisposed)
                status.Text = "유튜브 플레이어를 시작할 수 없습니다.";
        }
    }

    private static bool IsBrowserTarget(Uri target) =>
        target.Scheme == "https" && IsYouTubeHost(target.IdnHost);

    private async Task ChangePlaylistVideo(string action)
    {
        if (browser.CoreWebView2 is not { } core)
            return;
        try { await core.ExecuteScriptAsync($"window.koViewYouTube?.{action}()"); }
        catch (InvalidOperationException) when (IsDisposed || Disposing) { }
    }

    private void OpenInBrowser(Uri url)
    {
        try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception)
        {
            status.Text = "기본 브라우저를 열 수 없습니다.";
        }
    }
}
