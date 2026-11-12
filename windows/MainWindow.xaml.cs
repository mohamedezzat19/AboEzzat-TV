using System.IO;
using System.Net.Http;
using System.Windows.Controls;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AboEzzatReceiver.Windows;

public partial class MainWindow : Window
{
    private const int ReceiverPort = 8081;
    private const int DiscoveryPort = 37020;
    private const string DiscoveryGroup = "239.255.42.99";
    private readonly VlcAudioPlayback AudioPlayer;
    private HttpListener? _listener;
    // Keep HTTP request handling FIFO: remote-image HEAD checks can complete out of order,
    // otherwise an older Cast URL could be dispatched after a newer one and replace it.
    private readonly Channel<HttpListenerContext> _incomingRequests = Channel.CreateUnbounded<HttpListenerContext>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private CancellationTokenSource? _serverCts;
    private UdpClient? _discoveryClient;
    private bool _isFullscreen;
    private int _playRequestVersion;
    private WindowState _savedWindowState = WindowState.Maximized;
    private readonly DispatcherTimer _imageSlideshowTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<Uri> _imagePlaylist = new();
    private int _imageIndex;
    private bool _imageSlideshowEnabled;

    private readonly DispatcherTimer _subtitleTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _controlsHideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _isMuted;
    private readonly DispatcherTimer _resumeSaveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, double> _resumePositions = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentVideoUrl;
    private Uri? _pendingSeparateAudio;
    private IReadOnlyDictionary<string, string>? _pendingAudioHeaders;
    private bool _resumePromptHandled;
    private string ResumeFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AboEzzatTV", "resume-positions.json");
    private List<(TimeSpan Start, TimeSpan End, string Text)> _subtitleLines = new();
    private bool _isMediaExpanded;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResizeMode;
    private double _savedWidth, _savedHeight, _savedLeft, _savedTop;

    private sealed record HistoryEntry(string Title, string Url, DateTime PlayedAt);
    private readonly List<HistoryEntry> _history = new();
    private int _historyNavigationIndex;
    private bool _navigatingHistory;
    private string HistoryFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AboEzzatTV", "playback-history.json");

    public MainWindow()
    {
        InitializeComponent();
        AudioPlayer = new VlcAudioPlayback();
        VideoPlayer.OverlayContent = VideoOverlayLayer;
        LoadHistory();
        LoadResumePositions();
        RefreshHistoryList();
        IpText.Text = $"{GetLocalIPv4()}:{ReceiverPort}";
        Loaded += async (_, _) => await StartServerAsync();
        _subtitleTimer.Tick += (_, _) => UpdateSubtitleOverlay();
        _controlsTimer.Tick += (_, _) => UpdatePlaybackControls();
        _controlsHideTimer.Tick += (_, _) => HidePlaybackControlsIfNeeded();
        _resumeSaveTimer.Tick += (_, _) => SaveCurrentResumePosition();
        _imageSlideshowTimer.Tick += (_, _) => AdvanceImage(1);
        Closed += (_, _) => { SaveCurrentResumePosition(); SaveResumePositions(); StopServer(); StopAudioDeck(); AudioPlayer.Dispose(); VideoPlayer.Dispose(); };
    }

    private static string GetLocalIPv4()
    {
        try
        {
            return Dns.GetHostEntry(Dns.GetHostName()).AddressList
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                    && !IPAddress.IsLoopback(a))?.ToString() ?? "IP غير معروف";
        }
        catch { return "IP غير معروف"; }
    }

    private async Task StartServerAsync()
    {
        try
        {
            _serverCts = new CancellationTokenSource();
            _listener = new HttpListener();
            // Listen on all local interfaces so AboEzzat Cast on another device can connect.
            // This custom TcpListener binds directly to all local interfaces; HTTP.sys URLACL is not required.
            _listener.Prefixes.Add($"http://+:{ReceiverPort}/");
            _listener.Start();
            ReceiverStatusText.Text = "● الاستقبال يعمل — جاهز للاستقبال";
            ReceiverStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 222, 151));
            _ = Task.Run(() => ListenLoopAsync(_serverCts.Token));
            _ = Task.Run(() => ProcessIncomingRequestsAsync(_serverCts.Token));
            _ = Task.Run(() => DiscoveryLoopAsync(_serverCts.Token));
        }
        catch (Exception ex)
        {
            ReceiverStatusText.Text = "● تعذّر تشغيل خدمة الاستقبال";
            ReceiverStatusText.Foreground = Brushes.OrangeRed;
            MessageBox.Show($"تعذّر تشغيل منفذ {ReceiverPort}. تأكد أن المنفذ غير مستخدم وأن جدار حماية Windows يسمح بالاتصال على الشبكة الخاصة.\n{ex.Message}",
                "AboEzzat Receiver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested || _listener?.IsListening != true) break;
                // A malformed/disconnected client must not permanently stop the receiver.
                System.Diagnostics.Debug.WriteLine($"HTTP accept error; receiver remains active: {ex.Message}");
                continue;
            }

            if (!_incomingRequests.Writer.TryWrite(ctx))
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
        _incomingRequests.Writer.TryComplete();
    }

    private async Task ProcessIncomingRequestsAsync(CancellationToken token)
    {
        try
        {
            // Process in socket-accept order. A delayed image-detection request must not
            // let an older video overwrite a more recent Cast send.
            await foreach (var ctx in _incomingRequests.Reader.ReadAllAsync(token))
                await HandleHttpRequestAsync(ctx);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    // Compatible with Android Receiver's HTTP contract:
    // POST /play {"url":"...","audio":"...","headers":{...}}
    // GET /?url=... or /?video=...&audio=...; command=ping/status.
    private async Task HandleHttpRequestAsync(HttpListenerContext ctx)
    {
        string? mediaUrl = null, audioUrl = null, command = null;
        List<string> imageUrls = new();
        var mediaHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var request = ctx.Request;
            if (request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase)
                && request.Url?.AbsolutePath.Equals("/play", StringComparison.OrdinalIgnoreCase) == true)
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                if (root.TryGetProperty("url", out var u)) mediaUrl = u.GetString();
                if (root.TryGetProperty("audio", out var a)) audioUrl = a.GetString();
                if (root.TryGetProperty("command", out var jsonCommand))
                    command = jsonCommand.GetString()?.Trim().ToLowerInvariant();
                if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
                {
                    foreach (var header in h.EnumerateObject())
                    {
                        if (header.Value.ValueKind != JsonValueKind.String) continue;
                        var headerValue = header.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(headerValue) && !headerValue.Contains('\r') && !headerValue.Contains('\n'))
                            mediaHeaders[header.Name] = headerValue;
                    }
                }
                if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                    imageUrls.AddRange(images.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .Where(IsHttpUrl));
            }
            else
            {
                var q = request.QueryString;
                mediaUrl = q["url"] ?? q["video"];
                audioUrl = q["audio"];
                command = q["command"]?.Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(q["images"]))
                    imageUrls.AddRange(q["images"].Split('|').Where(IsHttpUrl));
            }

            bool valid = (string.IsNullOrWhiteSpace(mediaUrl) || IsHttpUrl(mediaUrl))
                && (string.IsNullOrWhiteSpace(audioUrl) || IsHttpUrl(audioUrl));
            bool imagesValid = imageUrls.All(IsHttpUrl);
            bool isStatusCommand = command is "ping" or "status";
            bool isRemoteCommand = IsSupportedRemoteCommand(command);
            bool hasCommand = !string.IsNullOrWhiteSpace(command);
            bool accepted = isStatusCommand || isRemoteCommand
                || (!hasCommand && valid && imagesValid
                    && (!string.IsNullOrWhiteSpace(mediaUrl) || !string.IsNullOrWhiteSpace(audioUrl) || imageUrls.Count > 0));

            string responseText = accepted ? "OK" : "Invalid or unsupported request";
            string contentType = "text/plain; charset=utf-8";
            if (command == "status")
            {
                responseText = "{\"extracting\":false,\"attempt\":0,\"status\":\"ready\",\"message\":\"Windows Receiver ready\"}";
                contentType = "application/json; charset=utf-8";
            }

            ctx.Response.StatusCode = accepted ? 200 : 400;
            ctx.Response.ContentType = contentType;
            var responseBytes = Encoding.UTF8.GetBytes(responseText);
            ctx.Response.ContentLength64 = responseBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(responseBytes);
            // The custom TCP listener transmits the buffered response on Close().
            // Acknowledge Cast before any optional CDN image probe so the sender does not time out.
            ctx.Response.Close();

            if (command is "ping" or "status") return;
            if (isRemoteCommand)
            {
                Dispatcher.Invoke(() => HandleRemoteCommand(command!));
                return;
            }
            if (accepted && !string.IsNullOrWhiteSpace(mediaUrl) && imageUrls.Count == 0
                && ShouldProbeRemoteImage(mediaUrl) && await IsRemoteImageAsync(mediaUrl))
                imageUrls.Add(mediaUrl);

            if (accepted && (!string.IsNullOrWhiteSpace(mediaUrl)
                || !string.IsNullOrWhiteSpace(audioUrl) || imageUrls.Count > 0))
            {
                var video = mediaUrl;
                var audio = audioUrl;
                var images = imageUrls.ToArray();
                Dispatcher.Invoke(() =>
                {
                    if (images.Length > 0) StartImagePlaylist(images);
                    else ReceiveMedia(video, audio, mediaHeaders);
                });
            }
        }
        catch (Exception ex)
        {
            try
            {
                ctx.Response.StatusCode = 400;
                var bytes = Encoding.UTF8.GetBytes("Invalid request");
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch { }
            System.Diagnostics.Debug.WriteLine($"Receiver request error: {ex}");
        }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsSupportedRemoteCommand(string? command) => command is
        "stop" or "play_pause" or "rw" or "ff" or "prev" or "previous" or "next"
        or "volume_up" or "volume_down" or "mute";

    private void HandleRemoteCommand(string command)
    {
        switch (command)
        {
            case "stop":
                ShowHome();
                break;
            case "play_pause":
                PlayPause_Click(this, new RoutedEventArgs());
                break;
            case "rw":
                if (VideoPlayer.Source != null && VideoPlayer.Visibility == Visibility.Visible)
                    Back10_Click(this, new RoutedEventArgs());
                else if (AudioPlayer.HasSource)
                    AudioPlayer.Seek(TimeSpan.FromSeconds(Math.Max(0, AudioPlayer.Position.TotalSeconds - 10)));
                break;
            case "ff":
                if (VideoPlayer.Source != null && VideoPlayer.Visibility == Visibility.Visible)
                    Forward10_Click(this, new RoutedEventArgs());
                else if (AudioPlayer.HasSource)
                    AudioPlayer.Seek(AudioPlayer.Position + TimeSpan.FromSeconds(10));
                break;
            case "prev":
            case "previous": // Keep the descriptive alias for older/custom senders.
                if (_imagePlaylist.Count > 0) AdvanceImage(-1);
                else NavigateHistory(1);
                break;
            case "next":
                if (_imagePlaylist.Count > 0) AdvanceImage(1);
                else NavigateHistory(-1);
                break;
            case "volume_up":
                VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05);
                break;
            case "volume_down":
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05);
                break;
            case "mute":
                Mute_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;
        int target = _historyNavigationIndex + direction;
        if (target < 0 || target >= _history.Count) return;
        _historyNavigationIndex = target;
        _navigatingHistory = true;
        ReceiveMedia(_history[target].Url, null);
    }

    private static bool ShouldProbeRemoteImage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || IsImageUrl(uri)) return false;
        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        string[] knownMediaExtensions =
        {
            ".mp4", ".m4v", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv",
            ".m3u8", ".mpd", ".ts", ".m4s", ".mp3", ".m4a", ".aac", ".ogg",
            ".opus", ".wav", ".flac"
        };
        return !knownMediaExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> IsRemoteImageAsync(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AboEzzat-TV/1.0");
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    private void ReceiveMedia(string? videoUrl, string? audioUrl, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (!string.IsNullOrWhiteSpace(videoUrl) && IsHttpUrl(videoUrl) && !IsImageUrl(new Uri(videoUrl))) AddHistory(videoUrl);
        // Cast may send video and audio as two independent URLs.
        if (!string.IsNullOrWhiteSpace(videoUrl) && IsHttpUrl(videoUrl)
            && Uri.TryCreate(videoUrl, UriKind.Absolute, out var videoUri))
        {
            if (IsImageUrl(videoUri))
            {
                _imageSlideshowTimer.Stop();
                _imagePlaylist.Clear();
                _imageIndex = 0;
                _imageSlideshowEnabled = false;
                StopAudioDeck();
                ShowImage(videoUri);
                UpdateImageControls();
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(audioUrl) && IsHttpUrl(audioUrl)
                    && Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                    PlayVideo(videoUri, headers, audioUri);
                else
                {
                    StopAudioDeck();
                    PlayVideo(videoUri, headers);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(audioUrl) && IsHttpUrl(audioUrl)
            && Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioOnlyUri))
        {
            // Cast can deliver the audio companion after the video request.
            if (!string.IsNullOrWhiteSpace(_currentVideoUrl) && VideoPlayer.Visibility == Visibility.Visible)
            {
                _pendingSeparateAudio = audioOnlyUri;
                _pendingAudioHeaders = headers;
                if (_resumePromptHandled && VideoPlayer.Source != null)
                {
                    _pendingSeparateAudio = null;
                    _pendingAudioHeaders = null;
                    PlaySeparateAudio(audioOnlyUri, headers, VideoPlayer.Position);
                }
            }
            else PlayAudioOnly(audioOnlyUri, headers);
        }
    }

    private void AddHistory(string url)
    {
        if (_navigatingHistory)
        {
            _navigatingHistory = false;
            return;
        }
        string title = GuessSubtitleTitle(new Uri(url));
        if (string.IsNullOrWhiteSpace(title)) title = url;
        _history.RemoveAll(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, new HistoryEntry(title, url, DateTime.Now));
        _historyNavigationIndex = 0;
        if (_history.Count > 50) _history.RemoveRange(50, _history.Count - 50);
        SaveHistory();
        RefreshHistoryList();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryFile)) return;
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(HistoryFile));
            if (loaded != null) _history.AddRange(loaded.Take(50));
        }
        catch { _history.Clear(); }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            File.WriteAllText(HistoryFile, JsonSerializer.Serialize(_history));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Save history failed: {ex.Message}"); }
    }

    private void RefreshHistoryList()
    {
        if (HistoryList == null) return;
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _history.Select(x => $"{x.Title}  •  {x.PlayedAt:MM/dd HH:mm}").ToList();
    }

    private void HistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        bool showHistory = HistoryList.Visibility != Visibility.Visible;
        HistoryList.Visibility = showHistory ? Visibility.Visible : Visibility.Collapsed;
        HistoryList.Height = showHistory ? 190 : 0;
        HistoryToggleButton.Content = showHistory ? "إخفاء سجل المشاهدة" : "سجل المشاهدة";
        if (showHistory) RefreshHistoryList();
    }

    private void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int index = HistoryList.SelectedIndex;
        if (index < 0 || index >= _history.Count) return;
        string url = _history[index].Url;
        HistoryList.SelectedIndex = -1;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHttpUrl(url))
            ReceiveMedia(url, null);
    }

    private void PlaySeparateAudio(Uri uri, IReadOnlyDictionary<string, string>? headers = null, TimeSpan startAt = default)
    {
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        AudioPlayer.RequestHeaders = headers;
        AudioPlayer.Source = uri;
        AudioPlayer.Play(startAt);
    }

    private void PlayAudioOnly(Uri uri, IReadOnlyDictionary<string, string>? headers = null)
    {
        ++_playRequestVersion;
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        ClearSubtitleOverlay();
        _currentVideoUrl = null;
        _resumePromptHandled = false;
        _pendingSeparateAudio = null;
        _pendingAudioHeaders = null;
        ExpandMediaForPlayback();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ReceivedImage.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Collapsed;
        PlaySeparateAudio(uri, headers);
        PlayPauseButton.Content = "إيقاف مؤقت";
    }

    private void StopAudioDeck()
    {
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
    }

    private void ClearSubtitleOverlay()
    {
        _subtitleTimer.Stop();
        _subtitleLines.Clear();
        SubtitleOverlay.Text = "";
        SubtitleOverlay.Visibility = Visibility.Collapsed;
    }

    private async void Subtitle_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source == null || VideoPlayer.Visibility != Visibility.Visible)
        {
            MessageBox.Show("شغّل فيديو أولًا علشان نبحث عن الترجمة.", "الترجمة العربية");
            return;
        }

        Uri requestedVideo = VideoPlayer.Source;
        string requestedVideoUrl = requestedVideo.AbsoluteUri;
        string title = GuessSubtitleTitle(requestedVideo);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = AskSubtitleTitle();
            if (string.IsNullOrWhiteSpace(title)) return;
        }

        SubtitleButton.IsEnabled = false;
        SubtitleButton.Content = "جاري البحث…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            string endpoint = "https://subtitles.website/strapi/api/search/subtitles?title="
                + Uri.EscapeDataString(title) + "&language=ar&format=srt";
            using var response = await http.GetAsync(endpoint);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;
            JsonElement results;
            if (!root.TryGetProperty("data", out results) && !root.TryGetProperty("subtitles", out results))
                throw new InvalidOperationException("ملف الترجمة غير متاح في نتائج البحث.");

            string? downloadUrl = null;
            int bestDownloads = -1;
            foreach (var item in results.EnumerateArray())
            {
                string candidate = GetJsonString(item, "download_url");
                if (string.IsNullOrWhiteSpace(candidate)) candidate = GetJsonString(item, "url");
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                int downloads = item.TryGetProperty("downloads", out var d) && d.TryGetInt32(out var n) ? n : 0;
                if (downloadUrl == null || downloads > bestDownloads) { downloadUrl = candidate; bestDownloads = downloads; }
            }
            if (downloadUrl == null) throw new InvalidOperationException("ملقيناش ترجمة عربية للفيديو ده.");
            if (downloadUrl.StartsWith("/")) downloadUrl = "https://subtitles.website" + downloadUrl;
            else if (!downloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                downloadUrl = "https://subtitles.website/" + downloadUrl;

            var subtitleBytes = await http.GetByteArrayAsync(downloadUrl);
            string subtitleText;
            if (subtitleBytes.Length >= 2 && subtitleBytes[0] == (byte)'P' && subtitleBytes[1] == (byte)'K')
            {
                using var zipStream = new MemoryStream(subtitleBytes);
                using var archive = new System.IO.Compression.ZipArchive(zipStream);
                var entry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".srt", StringComparison.OrdinalIgnoreCase));
                if (entry == null) throw new InvalidOperationException("ملف ZIP مفيهوش ترجمة SRT.");
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
                subtitleText = await reader.ReadToEndAsync();
            }
            else subtitleText = Encoding.UTF8.GetString(subtitleBytes);

            if (VideoPlayer.Source == null
                || !string.Equals(VideoPlayer.Source.AbsoluteUri, requestedVideoUrl, StringComparison.OrdinalIgnoreCase)
                || VideoPlayer.Visibility != Visibility.Visible)
            {
                MessageBox.Show("الفيديو اتغير أثناء البحث؛ لم يتم تطبيق الترجمة عليه.", "الترجمة العربية");
                return;
            }

            _subtitleLines = ParseSrt(subtitleText);
            if (_subtitleLines.Count == 0) throw new InvalidOperationException("ملف الترجمة فارغ أو غير مفهوم.");
            SubtitleOverlay.Visibility = Visibility.Visible;
            _subtitleTimer.Start();
            MessageBox.Show("تم تحميل الترجمة العربية. هتظهر متزامنة مع الفيديو.", "الترجمة العربية");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "تعذّر البحث عن الترجمة العربية", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            SubtitleButton.IsEnabled = true;
            SubtitleButton.Content = "ترجمة عربية";
        }
    }

    private string AskSubtitleTitle()
    {
        var dialog = new Window { Title = "اسم الفيلم أو المسلسل", Width = 420, Height = 155, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        var input = new TextBox { Margin = new Thickness(12), FontSize = 16 };
        var search = new Button { Content = "بحث", Width = 90, Margin = new Thickness(8), IsDefault = true };
        var cancel = new Button { Content = "إلغاء", Width = 90, Margin = new Thickness(8), IsCancel = true };
        string result = "";
        search.Click += (_, _) => { result = input.Text.Trim(); dialog.DialogResult = true; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(search); buttons.Children.Add(cancel);
        var panel = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(input);
        dialog.Content = panel; dialog.ShowDialog(); return result;
    }

    private static string GetJsonString(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static string GuessSubtitleTitle(Uri uri)
    {
        string name = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "");
        name = Path.GetFileNameWithoutExtension(name).Replace('_', ' ').Replace('.', ' ').Replace('-', ' ');
        name = Regex.Replace(name, @"(?i)\b(480p|720p|1080p|2160p|4k|web[- ]?dl|webrip|bluray|x264|x265|h264|h265|hevc|aac|dts)\b", " ");
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return Regex.IsMatch(name, @"(?i)^(index|master|playlist|video|stream|manifest|segment|file|media)$") ? "" : name;
    }

    private static List<(TimeSpan Start, TimeSpan End, string Text)> ParseSrt(string subtitleText)
    {
        string normalized = subtitleText.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n');
        var list = new List<(TimeSpan Start, TimeSpan End, string Text)>();
        var blocks = Regex.Split(normalized, @"\n\s*\n");
        foreach (var block in blocks)
        {
            var match = Regex.Match(block, @"(?<start>\d{2}:\d{2}:\d{2}[,.]\d{3})\s*-->\s*(?<end>\d{2}:\d{2}:\d{2}[,.]\d{3})");
            if (!match.Success) continue;
            if (!TimeSpan.TryParse(match.Groups["start"].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var start)
                || !TimeSpan.TryParse(match.Groups["end"].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var end) || end <= start) continue;
            string text = block[(match.Index + match.Length)..];
            text = Regex.Replace(text, @"(?m)^\s*\d+\s*$", "");
            text = Regex.Replace(text, @"<[^>]+>", "").Trim();
            if (!string.IsNullOrWhiteSpace(text)) list.Add((start, end, text));
        }
        return list.OrderBy(x => x.Start).ToList();
    }

    private void UpdateSubtitleOverlay()
    {
        if (VideoPlayer.Source == null || VideoPlayer.Visibility != Visibility.Visible || VideoPlayer.NaturalDuration.HasTimeSpan == false)
        { SubtitleOverlay.Text = ""; return; }
        var position = VideoPlayer.Position;
        var line = _subtitleLines.FirstOrDefault(x => position >= x.Start && position <= x.End);
        SubtitleOverlay.Text = line.Text ?? "";
        SubtitleOverlay.Visibility = string.IsNullOrWhiteSpace(SubtitleOverlay.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool IsImageUrl(Uri uri)
    {
        // Social media image links often have no extension or encode the image
        // format in query parameters (for example ?format=jpg or mime=image/jpeg).
        var path = Uri.UnescapeDataString(uri.AbsolutePath).ToLowerInvariant();
        var query = Uri.UnescapeDataString(uri.Query).ToLowerInvariant();
        var full = Uri.UnescapeDataString(uri.AbsoluteUri).ToLowerInvariant();
        return path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png")
            || path.EndsWith(".webp") || path.EndsWith(".gif") || path.EndsWith(".bmp")
            || path.EndsWith(".avif") || path.EndsWith(".heic") || path.EndsWith(".heif")
            || path.EndsWith(".tif") || path.EndsWith(".tiff")
            || Regex.IsMatch(query, @"(?:^|[&?])(?:format|fm|type)=(?:jpg|jpeg|png|webp|gif|bmp|avif|heic|heif)(?:&|$)")
            || query.Contains("mime=image/") || query.Contains("media_type=image/")
            || full.Contains("mime=image%2f") || full.Contains("media_type=image%2f");
    }

    private async Task DiscoveryLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var udp = new UdpClient();
                udp.EnableBroadcast = true;
                var data = Encoding.UTF8.GetBytes($"ABOEZZAT_RECEIVER|{GetLocalIPv4()}|{ReceiverPort}");
                await udp.SendAsync(data, new IPEndPoint(IPAddress.Parse(DiscoveryGroup), DiscoveryPort));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Discovery heartbeat: {ex.Message}"); }
            try { await Task.Delay(1200, token); } catch (OperationCanceledException) { break; }
        }
    }

    private async void PlayVideo(Uri uri, IReadOnlyDictionary<string, string>? headers = null, Uri? separateAudio = null)
    {
        int requestVersion = ++_playRequestVersion;
        StopAudioDeck();
        _pendingSeparateAudio = separateAudio;
        _pendingAudioHeaders = headers;
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        ClearSubtitleOverlay();
        _currentVideoUrl = uri.AbsoluteUri;
        _resumePromptHandled = false;
        ExpandMediaForPlayback();
        ReceivedImage.Visibility = Visibility.Collapsed;
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.RequestHeaders = headers;
        WelcomePanel.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;
        await Task.Delay(150);
        // A newer Cast request may have arrived while the old source was clearing.
        if (requestVersion != _playRequestVersion
            || !string.Equals(_currentVideoUrl, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            return;
        VideoPlayer.Source = uri;
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();

    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool videoActive = VideoPlayer.Source != null && VideoPlayer.Visibility == Visibility.Visible;
        bool imageActive = ReceivedImage.Source != null && ReceivedImage.Visibility == Visibility.Visible;
        bool mediaActive = videoActive || imageActive;

        // Fullscreen and back navigation must work for images as well as videos.
        switch (e.Key)
        {
            case System.Windows.Input.Key.F:
                if (mediaActive) { Fullscreen_Click(this, new RoutedEventArgs()); e.Handled = true; }
                break;
            case System.Windows.Input.Key.Escape:
                if (_isFullscreen) Fullscreen_Click(this, new RoutedEventArgs());
                else if (mediaActive) ShowHome();
                e.Handled = mediaActive || _isFullscreen;
                break;
            case System.Windows.Input.Key.Back:
                if (mediaActive) { ShowHome(); e.Handled = true; }
                break;
        }

        // Playback shortcuts are video-only.
        if (videoActive && !e.Handled)
        {
            switch (e.Key)
            {
                case System.Windows.Input.Key.Space: PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case System.Windows.Input.Key.Left: Back10_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case System.Windows.Input.Key.Right: Forward10_Click(this, new RoutedEventArgs()); e.Handled = true; break;
                case System.Windows.Input.Key.Up: VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05); e.Handled = true; break;
                case System.Windows.Input.Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05); e.Handled = true; break;
                case System.Windows.Input.Key.M: Mute_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            }
        }

        if (e.Handled && videoActive) ShowPlaybackControls();
    }

    private void MediaPanel_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (VideoPlayer.Source != null && VideoPlayer.Visibility == Visibility.Visible) ShowPlaybackControls();
    }

    private void ShowPlaybackControls()
    {
        PlaybackControls.Visibility = Visibility.Visible;
        _controlsHideTimer.Stop();
        if (PlayPauseButton.Content?.ToString() != "تشغيل") _controlsHideTimer.Start();
    }

    private void HidePlaybackControlsIfNeeded()
    {
        _controlsHideTimer.Stop();
        if (VideoPlayer.Source != null && VideoPlayer.Visibility == Visibility.Visible
            && PlayPauseButton.Content?.ToString() != "تشغيل" && !PlaybackControls.IsMouseOver)
            PlaybackControls.Visibility = Visibility.Collapsed;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        bool videoActive = VideoPlayer.Source != null && VideoPlayer.Visibility == Visibility.Visible;
        if (!videoActive && !AudioPlayer.HasSource) return;
        if (PlayPauseButton.Content?.ToString() == "تشغيل")
        {
            if (videoActive) VideoPlayer.Play();
            if (AudioPlayer.HasSource) AudioPlayer.Resume();
            PlayPauseButton.Content = "إيقاف مؤقت";
            if (videoActive) ShowPlaybackControls();
        }
        else
        {
            if (videoActive) VideoPlayer.Pause();
            if (AudioPlayer.HasSource) AudioPlayer.Pause();
            PlayPauseButton.Content = "تشغيل";
            _controlsHideTimer.Stop();
            if (videoActive) PlaybackControls.Visibility = Visibility.Visible;
        }
    }

    private void Back10_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source != null)
        {
            VideoPlayer.Position = TimeSpan.FromSeconds(Math.Max(0, VideoPlayer.Position.TotalSeconds - 10));
            if (AudioPlayer.HasSource) AudioPlayer.Seek(VideoPlayer.Position);
        }
    }

    private void Forward10_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source != null && VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoPlayer.Position = TimeSpan.FromSeconds(Math.Min(VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds, VideoPlayer.Position.TotalSeconds + 10));
            if (AudioPlayer.HasSource) AudioPlayer.Seek(VideoPlayer.Position);
        }
    }

    private void SeekSlider_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (VideoPlayer.Source != null && VideoPlayer.NaturalDuration.HasTimeSpan)
        {
            VideoPlayer.Position = TimeSpan.FromSeconds(VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds * SeekSlider.Value / 100.0);
            if (AudioPlayer.HasSource) AudioPlayer.Seek(VideoPlayer.Position);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VideoPlayer != null) VideoPlayer.Volume = e.NewValue;
        if (AudioPlayer != null) AudioPlayer.Volume = e.NewValue;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        VideoPlayer.IsMuted = _isMuted;
        AudioPlayer.IsMuted = _isMuted;
        MuteButton.Content = _isMuted ? "إلغاء الكتم" : "كتم";
    }

    private void UpdatePlaybackControls()
    {
        if (VideoPlayer.Source == null || !VideoPlayer.NaturalDuration.HasTimeSpan) return;
        var pos = VideoPlayer.Position;
        var duration = VideoPlayer.NaturalDuration.TimeSpan;
        PlaybackTimeText.Text = $"{FormatPlaybackTime(pos)} / {FormatPlaybackTime(duration)}";
        if (!SeekSlider.IsMouseCaptureWithin)
            SeekSlider.Value = duration.TotalSeconds > 0 ? pos.TotalSeconds / duration.TotalSeconds * 100 : 0;
    }

    private static string FormatPlaybackTime(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (_resumePromptHandled || VideoPlayer.Source == null) return;
        _resumePromptHandled = true;
        VideoPlayer.Pause();

        if (!string.IsNullOrWhiteSpace(_currentVideoUrl)
            && _resumePositions.TryGetValue(_currentVideoUrl, out double savedSeconds)
            && savedSeconds >= 10
            && VideoPlayer.NaturalDuration.HasTimeSpan
            && savedSeconds < VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds - 15)
        {
            var saved = TimeSpan.FromSeconds(savedSeconds);
            if (AskResumeChoice(saved) == ResumeChoice.Continue)
                VideoPlayer.Position = saved;
            else
            {
                VideoPlayer.Position = TimeSpan.Zero;
                _resumePositions.Remove(_currentVideoUrl);
                SaveResumePositions();
            }
        }
        else VideoPlayer.Position = TimeSpan.Zero;

        VideoPlayer.Play();
        // Start separate audio only after the resume/restart choice is settled.
        // VLC seeks the audio deck on its Playing event so both streams begin at the same position.
        if (_pendingSeparateAudio is not null)
        {
            var audioUri = _pendingSeparateAudio;
            var audioHeaders = _pendingAudioHeaders;
            _pendingSeparateAudio = null;
            _pendingAudioHeaders = null;
            PlaySeparateAudio(audioUri, audioHeaders, VideoPlayer.Position);
        }
        _resumeSaveTimer.Start();
        PlaybackControls.Visibility = Visibility.Visible;
        PlayPauseButton.Content = "إيقاف مؤقت";
        _controlsTimer.Start();
        ShowPlaybackControls();
    }

    private enum ResumeChoice { Continue, Restart }

    private ResumeChoice AskResumeChoice(TimeSpan savedPosition)
    {
        string FormatTime(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        var dialog = new Window
        {
            Title = "استكمال المشاهدة؟", Width = 460, Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            ResizeMode = ResizeMode.NoResize, WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(18, 29, 59)),
            Foreground = Brushes.White, ShowInTaskbar = false
        };
        var heading = new TextBlock
        {
            Text = "استكمال المشاهدة؟", FontSize = 23, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(10, 12, 10, 6)
        };
        var detail = new TextBlock
        {
            Text = $"هل تريد استكمال الفيديو من آخر نقطة؟ ({FormatTime(savedPosition)})",
            FontSize = 15, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(16, 4, 16, 14)
        };
        var continueButton = new Button
        {
            Content = "استكمال المشاهدة", MinWidth = 160, Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(8), IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(35, 143, 112)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        var restartButton = new Button
        {
            Content = "البدء من البداية", MinWidth = 145, Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(8), IsCancel = true,
            Background = new SolidColorBrush(Color.FromRgb(47, 62, 101)),
            Foreground = Brushes.White, BorderThickness = new Thickness(0)
        };
        ResumeChoice result = ResumeChoice.Restart;
        continueButton.Click += (_, _) => { result = ResumeChoice.Continue; dialog.DialogResult = true; };
        restartButton.Click += (_, _) => { result = ResumeChoice.Restart; dialog.DialogResult = false; };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        buttons.Children.Add(continueButton); buttons.Children.Add(restartButton);
        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(detail);
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    private void LoadResumePositions()
    {
        try
        {
            if (!File.Exists(ResumeFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(ResumeFile));
            if (saved != null)
                foreach (var item in saved.Where(x => IsHttpUrl(x.Key) && x.Value > 0))
                    _resumePositions[item.Key] = item.Value;
        }
        catch { _resumePositions.Clear(); }
    }

    private void SaveResumePositions()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ResumeFile)!);
            File.WriteAllText(ResumeFile, JsonSerializer.Serialize(_resumePositions));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Save resume positions failed: {ex.Message}"); }
    }

    private void SaveCurrentResumePosition()
    {
        if (string.IsNullOrWhiteSpace(_currentVideoUrl) || VideoPlayer.Source == null
            || VideoPlayer.Visibility != Visibility.Visible || !VideoPlayer.NaturalDuration.HasTimeSpan)
            return;
        double current = VideoPlayer.Position.TotalSeconds;
        double duration = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
        if (current >= 10 && duration > 0 && current < duration - 15)
            _resumePositions[_currentVideoUrl] = current;
        else if (duration > 0 && current >= duration - 15)
            _resumePositions.Remove(_currentVideoUrl);
        SaveResumePositions();
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        PlayPauseButton.Content = "تشغيل";
        if (!string.IsNullOrWhiteSpace(_currentVideoUrl)) _resumePositions.Remove(_currentVideoUrl);
        SaveResumePositions();
    }

    private void ExpandMediaForPlayback()
    {
        if (_isMediaExpanded) return;
        _isMediaExpanded = true;
        InfoColumn.Width = new GridLength(0);
        GapColumn.Width = new GridLength(0);
        InfoPanel.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        MediaPanel.Padding = new Thickness(0);
        MediaPanel.CornerRadius = new CornerRadius(0);

        // Android Receiver switches to a clean, edge-to-edge media screen.
        _savedStyle = WindowStyle;
        _savedResizeMode = ResizeMode;
        _savedWindowState = WindowState;
        _savedWidth = RestoreBounds.Width;
        _savedHeight = RestoreBounds.Height;
        _savedLeft = RestoreBounds.Left;
        _savedTop = RestoreBounds.Top;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        _isFullscreen = true;
    }

    private void RestoreHomeLayout()
    {
        _isMediaExpanded = false;
        if (_isFullscreen)
        {
            WindowStyle = _savedStyle;
            ResizeMode = _savedResizeMode;
            WindowState = _savedWindowState;
            if (_savedWindowState == WindowState.Normal)
            {
                Width = _savedWidth; Height = _savedHeight; Left = _savedLeft; Top = _savedTop;
            }
            _isFullscreen = false;
        }
        InfoColumn.Width = new GridLength(0);
        GapColumn.Width = new GridLength(0);
        InfoPanel.Visibility = Visibility.Visible;
        StopButton.Visibility = Visibility.Collapsed;
        MediaPanel.Padding = new Thickness(0);
        MediaPanel.CornerRadius = new CornerRadius(0);
    }

    private void StartImagePlaylist(IEnumerable<string> urls)
    {
        _imageSlideshowTimer.Stop();
        _imagePlaylist.Clear();
        _imagePlaylist.AddRange(urls.Where(IsHttpUrl).Select(x => new Uri(x)).Where(IsImageUrl));
        if (_imagePlaylist.Count == 0) return;

        // Switching from video/audio to images must stop the previous media deck.
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        _subtitleTimer.Stop();
        _subtitleLines.Clear();
        SubtitleOverlay.Text = "";
        SubtitleOverlay.Visibility = Visibility.Collapsed;
        StopAudioDeck();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        _currentVideoUrl = null;
        _imageIndex = 0;
        ShowImage(_imagePlaylist[_imageIndex]);
        _imageSlideshowEnabled = _imagePlaylist.Count > 1;
        if (_imageSlideshowEnabled) _imageSlideshowTimer.Start();
        UpdateImageControls();
    }

    private void AdvanceImage(int direction)
    {
        if (_imagePlaylist.Count == 0) return;
        _imageIndex = (_imageIndex + direction + _imagePlaylist.Count) % _imagePlaylist.Count;
        ShowImage(_imagePlaylist[_imageIndex]);
        UpdateImageControls();
    }

    private void ImagePrevious_Click(object sender, RoutedEventArgs e) => AdvanceImage(-1);
    private void ImageNext_Click(object sender, RoutedEventArgs e) => AdvanceImage(1);

    private void ImageSlideshow_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePlaylist.Count < 2) return;
        _imageSlideshowEnabled = !_imageSlideshowEnabled;
        if (_imageSlideshowEnabled) _imageSlideshowTimer.Start();
        else _imageSlideshowTimer.Stop();
        UpdateImageControls();
    }

    private void ImageInterval_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ImageInterval.SelectedItem is System.Windows.Controls.ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int seconds))
        {
            _imageSlideshowTimer.Interval = TimeSpan.FromSeconds(seconds);
        }
    }

    private void UpdateImageControls()
    {
        if (ImageControls == null) return;
        ImageControls.Visibility = _imagePlaylist.Count > 0 && ReceivedImage.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        ImagePositionText.Text = _imagePlaylist.Count == 0 ? "" : $"{_imageIndex + 1} / {_imagePlaylist.Count}";
        ImageSlideshowButton.Content = _imageSlideshowEnabled ? "إيقاف العرض" : "تشغيل العرض";
    }

    private async void ShowImage(Uri uri)
    {
        int imageRequestVersion = ++_playRequestVersion;
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        ClearSubtitleOverlay();
        StopAudioDeck();
        _pendingSeparateAudio = null;
        _pendingAudioHeaders = null;
        _currentVideoUrl = null;
        _resumePromptHandled = false;
        ExpandMediaForPlayback();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Collapsed;
        try
        {
            // Download to a local stream rather than relying on WPF's direct URI
            // loader; social CDN URLs may redirect or contain long query strings.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AboEzzat-TV/1.0");
            http.DefaultRequestHeaders.Accept.ParseAdd("image/*");
            var bytes = await http.GetByteArrayAsync(uri);
            if (imageRequestVersion != _playRequestVersion) return;
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            if (imageRequestVersion != _playRequestVersion) return;
            ReceivedImage.Source = bitmap;
            ReceivedImage.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Image load failed ({uri}): {ex}");
            ShowHome();
        }
    }

    private void ShowHome()
    {
        ++_playRequestVersion;
        _pendingSeparateAudio = null;
        _pendingAudioHeaders = null;
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        RestoreHomeLayout();
        ClearSubtitleOverlay();
        StopAudioDeck();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        _imageSlideshowTimer.Stop();
        _imagePlaylist.Clear();
        _imageIndex = 0;
        _imageSlideshowEnabled = false;
        ReceivedImage.Source = null;
        ReceivedImage.Visibility = Visibility.Collapsed;
        UpdateImageControls();
        WelcomePanel.Visibility = Visibility.Visible;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (!_isFullscreen)
        {
            _savedStyle = WindowStyle; _savedResizeMode = ResizeMode;
            _savedWindowState = WindowState;
            _savedWidth = RestoreBounds.Width; _savedHeight = RestoreBounds.Height;
            _savedLeft = RestoreBounds.Left; _savedTop = RestoreBounds.Top;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized; _isFullscreen = true;
        }
        else
        {
            WindowStyle = _savedStyle; ResizeMode = _savedResizeMode;
            WindowState = _savedWindowState;
            if (_savedWindowState == WindowState.Normal)
            {
                Width = _savedWidth; Height = _savedHeight; Left = _savedLeft; Top = _savedTop;
            }
            _isFullscreen = false;
        }
    }

    private void StopServer()
    {
        try
        {
            _serverCts?.Cancel();
            _incomingRequests.Writer.TryComplete();
            while (_incomingRequests.Reader.TryRead(out var pending))
                try { pending.Response.Close(); } catch { }
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
    }
}