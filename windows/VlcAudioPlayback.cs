using LibVLCSharp.Shared;
using System;

namespace AboEzzatReceiver.Windows;

/// <summary>
/// Headless VLC audio deck used for separately supplied Cast audio streams.
/// It runs independently from the video deck so both can play at once.
/// </summary>
public sealed class VlcAudioPlayback : IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _media;
    private Uri? _source;
    private double _volume = 0.8;
    private bool _isMuted;
    private bool _disposed;

    public VlcAudioPlayback()
    {
        Core.Initialize();
        _libVlc = new LibVLC();
        _player = new MediaPlayer(_libVlc);
    }

    public IReadOnlyDictionary<string, string>? RequestHeaders { get; set; }

    public Uri? Source
    {
        get => _source;
        set
        {
            ThrowIfDisposed();
            Stop();
            if (value is null)
            {
                _source = null;
                return;
            }

            if (value.Scheme != Uri.UriSchemeHttp && value.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Only HTTP and HTTPS audio URLs are supported.", nameof(value));

            _source = value;
            _media = new Media(_libVlc, value);
            ApplyRequestHeaders(_media, RequestHeaders);
        }
    }

    /// <summary>
    /// Current playback position, exposed in the same units as WPF MediaElement.
    /// VLC reports and accepts the position in milliseconds.
    /// </summary>
    public TimeSpan Position
    {
        get => TimeSpan.FromMilliseconds(Math.Max(0, _player.Time));
        set
        {
            ThrowIfDisposed();
            var milliseconds = Math.Max(0, value.TotalMilliseconds);
            var length = _player.Length;
            if (length > 0) milliseconds = Math.Min(milliseconds, length);
            _player.Time = (long)milliseconds;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            _player.Volume = (int)Math.Round(_volume * 100);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            _player.Mute = value;
        }
    }

    public void Play() => Play(TimeSpan.Zero);

    public void Play(TimeSpan startAt)
    {
        ThrowIfDisposed();
        if (_media is null) return;

        _player.Volume = (int)Math.Round(_volume * 100);
        _player.Mute = _isMuted;

        long targetMilliseconds = (long)Math.Max(0, startAt.TotalMilliseconds);
        if (targetMilliseconds > 0)
        {
            EventHandler<EventArgs>? onPlaying = null;
            onPlaying = (_, _) =>
            {
                if (onPlaying is not null) _player.Playing -= onPlaying;
                _player.Time = targetMilliseconds;
            };
            _player.Playing += onPlaying;
        }

        _player.Play(_media);
    }

    public void Pause() => _player.Pause();

    public void Stop()
    {
        if (_disposed) return;
        _player.Stop();
        _media?.Dispose();
        _media = null;
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

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(VlcAudioPlayback));
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _player.Dispose();
        _libVlc.Dispose();
        _disposed = true;
    }
}
