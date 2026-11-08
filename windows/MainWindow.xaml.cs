using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AboEzzatReceiver.Windows;

public partial class MainWindow : Window
{
    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private bool _isFullscreen;
    private bool _isMediaExpanded;
    private WindowStyle _savedStyle;
    private ResizeMode _savedResizeMode;
    private double _savedWidth, _savedHeight, _savedLeft, _savedTop;

    public MainWindow()
    {
        InitializeComponent();
        IpText.Text = $"{GetLocalIPv4()}:8081";
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
            _listener.Prefixes.Add("http://+:8081/");
            _listener.Start();
            _ = Task.Run(() => ListenLoopAsync(_serverCts.Token));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"تعذّر تشغيل منفذ 8081. قد يكون مستخدمًا أو يحتاج صلاحية.\n{ex.Message}",
                "AboEzzat Receiver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (token.IsCancellationRequested) { break; }
            catch { break; }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Temporary generic test routes. Exact Cast protocol routes will be
                    // ported after comparing them with the Android Receiver source.
                    var path = ctx.Request.Url?.AbsolutePath.Trim('/').ToLowerInvariant() ?? "";
                    var url = ctx.Request.QueryString["url"];
                    if (path is "play" or "video" && Uri.TryCreate(url, UriKind.Absolute, out var mediaUri))
                        Dispatcher.Invoke(() => PlayVideo(mediaUri));
                    else if (path is "image" && Uri.TryCreate(url, UriKind.Absolute, out var imageUri))
                        Dispatcher.Invoke(() => ShowImage(imageUri));
                    var bytes = System.Text.Encoding.UTF8.GetBytes("AboEzzat Receiver Windows - OK");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                }
                catch { try { ctx.Response.StatusCode = 500; } catch { } }
                finally { try { ctx.Response.Close(); } catch { } }
            });
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

        // Give WPF MediaElement time to release the previous decoder/source.
        await Task.Delay(120);
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
    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e) { /* keep last frame until next request */ }

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