using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private UdpClient? _discoveryClient;
    private bool _isFullscreen;
    private readonly DispatcherTimer _subtitleTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _controlsTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _controlsHideTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _isMuted;
    private readonly DispatcherTimer _resumeSaveTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Dictionary<string, double> _resumePositions = new(StringComparer.OrdinalIgnoreCase);
    private string? _currentVideoUrl;
    private bool _resumePromptHandled;
    private string ResumeFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AboEzzatTV", "resume-positions.json");
    private List<(TimeSpan Start, TimeSpan End, string Text)> _subtitleLines = new();
    private bool _isMediaExpanded;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResizeMode;
    private double _savedWidth, _savedHeight, _savedLeft, _savedTop;

    private sealed record HistoryEntry(string Title, string Url, DateTime PlayedAt);
    private readonly List<HistoryEntry> _history = new();
    private string HistoryFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AboEzzatTV", "playback-history.json");

    public MainWindow()
    {
        InitializeComponent();
        LoadHistory();
        LoadResumePositions();
        RefreshHistoryList();
        IpText.Text = $"{GetLocalIPv4()}:{ReceiverPort}";
        Loaded += async (_, _) => await StartServerAsync();
        _subtitleTimer.Tick += (_, _) => UpdateSubtitleOverlay();
        _controlsTimer.Tick += (_, _) => UpdatePlaybackControls();
        _controlsHideTimer.Tick += (_, _) => HidePlaybackControlsIfNeeded();
        _resumeSaveTimer.Tick += (_, _) => SaveCurrentResumePosition();
        Closed += (_, _) => { SaveCurrentResumePosition(); SaveResumePositions(); StopServer(); };
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
            _listener.Prefixes.Add($"http://+:{ReceiverPort}/");
            _listener.Start();
            _ = Task.Run(() => ListenLoopAsync(_serverCts.Token));
            _ = Task.Run(() => DiscoveryLoopAsync(_serverCts.Token));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"تعذّر تشغيل منفذ {ReceiverPort}. قد يكون مستخدمًا أو يحتاج صلاحية.\n{ex.Message}",
                "AboEzzat Receiver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch when (token.IsCancellationRequested) { break; }
            catch { break; }
            _ = Task.Run(() => HandleHttpRequestAsync(ctx));
        }
    }

    // Compatible with Android Receiver's HTTP contract:
    // POST /play {"url":"...","audio":"...","headers":{...}}
    // GET /?url=... or /?video=...&audio=...; command=ping/status.
    private async Task HandleHttpRequestAsync(HttpListenerContext ctx)
    {
        string? mediaUrl = null, audioUrl = null, command = null;
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
            }
            else
            {
                var q = request.QueryString;
                mediaUrl = q["url"] ?? q["video"];
                audioUrl = q["audio"];
                command = q["command"];
            }

            bool valid = (string.IsNullOrWhiteSpace(mediaUrl) || IsHttpUrl(mediaUrl))
                && (string.IsNullOrWhiteSpace(audioUrl) || IsHttpUrl(audioUrl));
            bool accepted = command is "ping" or "status" || valid
                && (!string.IsNullOrWhiteSpace(mediaUrl) || !string.IsNullOrWhiteSpace(audioUrl));

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

            if (command is "ping" or "status") return;
            if (accepted && (!string.IsNullOrWhiteSpace(mediaUrl) || !string.IsNullOrWhiteSpace(audioUrl)))
            {
                var video = mediaUrl;
                var audio = audioUrl;
                Dispatcher.Invoke(() => ReceiveMedia(video, audio));
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

    private void ReceiveMedia(string? videoUrl, string? audioUrl)
    {
        if (!string.IsNullOrWhiteSpace(videoUrl) && IsHttpUrl(videoUrl) && !IsImageUrl(new Uri(videoUrl))) AddHistory(videoUrl);
        // Cast may send video and audio as two independent URLs.
        if (!string.IsNullOrWhiteSpace(videoUrl) && IsHttpUrl(videoUrl)
            && Uri.TryCreate(videoUrl, UriKind.Absolute, out var videoUri))
        {
            if (IsImageUrl(videoUri))
            {
                StopAudioDeck();
                ShowImage(videoUri);
            }
            else
            {
                PlayVideo(videoUri);
                if (!string.IsNullOrWhiteSpace(audioUrl) && IsHttpUrl(audioUrl)
                    && Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioUri))
                    PlaySeparateAudio(audioUri);
                else StopAudioDeck();
            }
        }
        else if (!string.IsNullOrWhiteSpace(audioUrl) && IsHttpUrl(audioUrl)
            && Uri.TryCreate(audioUrl, UriKind.Absolute, out var audioOnlyUri))
        {
            PlayAudioOnly(audioOnlyUri);
        }
    }

    private void AddHistory(string url)
    {
        string title = GuessSubtitleTitle(new Uri(url));
        if (string.IsNullOrWhiteSpace(title)) title = url;
        _history.RemoveAll(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, new HistoryEntry(title, url, DateTime.Now));
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

    private void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int index = HistoryList.SelectedIndex;
        if (index < 0 || index >= _history.Count) return;
        string url = _history[index].Url;
        HistoryList.SelectedIndex = -1;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHttpUrl(url))
            ReceiveMedia(url, null);
    }

    private void PlaySeparateAudio(Uri uri)
    {
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
        AudioPlayer.Source = uri;
        AudioPlayer.Position = TimeSpan.Zero;
        AudioPlayer.Play();
    }

    private void PlayAudioOnly(Uri uri)
    {
        ExpandMediaForPlayback();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ReceivedImage.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Collapsed;
        PlaySeparateAudio(uri);
    }

    private void StopAudioDeck()
    {
        AudioPlayer.Stop();
        AudioPlayer.Source = null;
    }

    private async void Subtitle_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source == null || VideoPlayer.Visibility != Visibility.Visible)
        {
            MessageBox.Show("شغّل فيديو أولًا علشان نبحث عن الترجمة.", "الترجمة العربية");
            return;
        }

        string title = GuessSubtitleTitle(VideoPlayer.Source);
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
        var path = uri.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png")
            || path.EndsWith(".webp") || path.EndsWith(".gif") || path.EndsWith(".bmp");
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

    private async void PlayVideo(Uri uri)
    {
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _currentVideoUrl = uri.AbsoluteUri;
        _resumePromptHandled = false;
        ExpandMediaForPlayback();
        ReceivedImage.Visibility = Visibility.Collapsed;
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        WelcomePanel.Visibility = Visibility.Collapsed;
        VideoPlayer.Visibility = Visibility.Visible;
        await Task.Delay(150);
        VideoPlayer.Source = uri;
        VideoPlayer.Position = TimeSpan.Zero;
        VideoPlayer.Play();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (VideoPlayer.Source == null || VideoPlayer.Visibility != Visibility.Visible) return;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Space: PlayPause_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case System.Windows.Input.Key.Left: Back10_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case System.Windows.Input.Key.Right: Forward10_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case System.Windows.Input.Key.Up: VolumeSlider.Value = Math.Min(1, VolumeSlider.Value + 0.05); e.Handled = true; break;
            case System.Windows.Input.Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 0.05); e.Handled = true; break;
            case System.Windows.Input.Key.M: Mute_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case System.Windows.Input.Key.F: Fullscreen_Click(this, new RoutedEventArgs()); e.Handled = true; break;
            case System.Windows.Input.Key.Escape: if (_isFullscreen) Fullscreen_Click(this, new RoutedEventArgs()); else ShowHome(); e.Handled = true; break;
            case System.Windows.Input.Key.Back: ShowHome(); e.Handled = true; break;
        }
        if (e.Handled) ShowPlaybackControls();
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
        if (VideoPlayer.Source == null) return;
        if (PlayPauseButton.Content?.ToString() == "تشغيل")
        {
            VideoPlayer.Play();
            PlayPauseButton.Content = "إيقاف مؤقت";
            ShowPlaybackControls();
        }
        else
        {
            VideoPlayer.Pause();
            PlayPauseButton.Content = "تشغيل";
            _controlsHideTimer.Stop();
            PlaybackControls.Visibility = Visibility.Visible;
        }
    }

    private void Back10_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source != null)
            VideoPlayer.Position = TimeSpan.FromSeconds(Math.Max(0, VideoPlayer.Position.TotalSeconds - 10));
    }

    private void Forward10_Click(object sender, RoutedEventArgs e)
    {
        if (VideoPlayer.Source != null && VideoPlayer.NaturalDuration.HasTimeSpan)
            VideoPlayer.Position = TimeSpan.FromSeconds(Math.Min(VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds, VideoPlayer.Position.TotalSeconds + 10));
    }

    private void SeekSlider_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (VideoPlayer.Source != null && VideoPlayer.NaturalDuration.HasTimeSpan)
            VideoPlayer.Position = TimeSpan.FromSeconds(VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds * SeekSlider.Value / 100.0);
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
        StopButton.Margin = new Thickness(0, 8, 0, 0);
        MediaPanel.Padding = new Thickness(0);
        MediaPanel.CornerRadius = new CornerRadius(0);
    }

    private void RestoreHomeLayout()
    {
        _isMediaExpanded = false;
        InfoColumn.Width = new GridLength(260);
        GapColumn.Width = new GridLength(14);
        InfoPanel.Visibility = Visibility.Visible;
        StopButton.Margin = new Thickness(260, 8, 0, 0);
        MediaPanel.Padding = new Thickness(16);
        MediaPanel.CornerRadius = new CornerRadius(18);
    }

    private void ShowImage(Uri uri)
    {
        ExpandMediaForPlayback();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Collapsed;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            ReceivedImage.Source = bitmap;
            ReceivedImage.Visibility = Visibility.Visible;
        }
        catch { ShowHome(); }
    }

    private void ShowHome()
    {
        SaveCurrentResumePosition();
        _resumeSaveTimer.Stop();
        _controlsTimer.Stop();
        _controlsHideTimer.Stop();
        PlaybackControls.Visibility = Visibility.Collapsed;
        RestoreHomeLayout();
        _subtitleTimer.Stop();
        _subtitleLines.Clear();
        SubtitleOverlay.Text = ""; SubtitleOverlay.Visibility = Visibility.Collapsed;
        StopAudioDeck();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ReceivedImage.Source = null;
        ReceivedImage.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => ShowHome();

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (!_isFullscreen)
        {
            _savedStyle = WindowStyle; _savedResizeMode = ResizeMode;
            _savedWidth = Width; _savedHeight = Height; _savedLeft = Left; _savedTop = Top;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized; _isFullscreen = true;
        }
        else
        {
            WindowState = WindowState.Normal; WindowStyle = _savedStyle; ResizeMode = _savedResizeMode;
            Width = _savedWidth; Height = _savedHeight; Left = _savedLeft; Top = _savedTop;
            _isFullscreen = false;
        }
    }

    private void StopServer()
    {
        try { _serverCts?.Cancel(); _listener?.Stop(); _listener?.Close(); } catch { }
    }
}