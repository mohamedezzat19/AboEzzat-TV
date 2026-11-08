using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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
    private bool _isMediaExpanded;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResizeMode;
    private double _savedWidth, _savedHeight, _savedLeft, _savedTop;

    public MainWindow()
    {
        InitializeComponent();
        IpText.Text = $"{GetLocalIPv4()}:{ReceiverPort}";
        Loaded += async (_, _) => await StartServerAsync();
        Closed += (_, _) => StopServer();
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
        RestoreHomeLayout();
        VideoPlayer.Stop();
        VideoPlayer.Source = null;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ReceivedImage.Source = null;
        ReceivedImage.Visibility = Visibility.Collapsed;
        WelcomePanel.Visibility = Visibility.Visible;
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => ShowHome();
    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e) { }

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