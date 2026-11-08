using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace AboEzzatReceiver.Windows;

/// <summary>
/// VLC-backed compatibility control exposing the small MediaElement API surface
/// currently used by MainWindow, allowing a staged player migration.
/// </summary>
public partial class VlcPlaybackPrototype : UserControl, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _currentMedia;
    private Uri? _source;
    private bool _openedRaised;
    private double _volume = 0.8;
    private bool _isMuted;

    public event RoutedEventHandler? MediaOpened;
    public event RoutedEventHandler? MediaEnded;

    public VlcPlaybackPrototype()
    {
        InitializeComponent();
        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        VlcView.MediaPlayer = _mediaPlayer;
        _mediaPlayer.Playing += (_, _) => OnMediaPlaying();
        _mediaPlayer.EndReached += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            MediaEnded?.Invoke(this, new RoutedEventArgs())));
        Unloaded += (_, _) => Stop();
    }

    public Uri? Source
    {
        get => _source;
        set
        {
            if (value is null)
            {
                Stop();
                _source = null;
                return;
            }

            if (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Only HTTP and HTTPS media URLs are supported.", nameof(value));

            Stop();
            _source = value;
            _openedRaised = false;
            _currentMedia = new Media(_libVlc, value);
            ApplyRequestHeaders(_currentMedia, RequestHeaders);
        }
    }

    public IReadOnlyDictionary<string, string>? RequestHeaders { get; set; }

    public UIElement? OverlayContent
    {
        get => OverlayHost.Children.Count > 0 ? OverlayHost.Children[0] as UIElement : null;
        set
        {
            OverlayHost.Children.Clear();
            if (value is not null) OverlayHost.Children.Add(value);
        }
    }

    public TimeSpan Position
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, _mediaPlayer.Time));
        set
        {
            if (value < TimeSpan.Zero) value = TimeSpan.Zero;
            _mediaPlayer.Time = (long)value.TotalMilliseconds;
        }
    }

    public Duration NaturalDuration
    {
        get
        {
            long length = _mediaPlayer.Length;
            return length > 0 ? new Duration(TimeSpan.FromMilliseconds(length)) : Duration.Automatic;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _mediaPlayer.Volume = (int)Math.Round(_volume * 100);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            _mediaPlayer.Mute = value;
        }
    }

    public void Play()
    {
        if (_currentMedia is not null)
        {
            _mediaPlayer.Volume = (int)Math.Round(_volume * 100);
            _mediaPlayer.Mute = _isMuted;
            _mediaPlayer.Play(_currentMedia);
        }
    }

    public void Pause() => _mediaPlayer.Pause();

    public void Resume() => Play();

    public void Stop()
    {
        _mediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    private void OnMediaPlaying()
    {
        if (_openedRaised) return;
        _openedRaised = true;
        Dispatcher.BeginInvoke(new Action(() => MediaOpened?.Invoke(this, new RoutedEventArgs())));
    }

    private static void ApplyRequestHeaders(Media media, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        if (TryGetHeader(headers, "User-Agent", out var userAgent))
            media.AddOption($":http-user-agent={userAgent}");
        if (TryGetHeader(headers, "Referer", out var referer))
            media.AddOption($":http-referrer={referer}");
    }

    private static bool TryGetHeader(IReadOnlyDictionary<string, string> headers, string name, out string value)
    {
        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return !string.IsNullOrWhiteSpace(value);
            }
        }
        value = string.Empty;
        return false;
    }

    public void Dispose()
    {
        Stop();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
