using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace AboEzzatReceiver.Windows;

/// <summary>
/// Isolated VLC playback prototype. Kept separate from the current MediaElement player
/// until the new playback path is built and tested.
/// </summary>
public partial class VlcPlaybackPrototype : UserControl, IDisposable
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _mediaPlayer;
    private Media? _currentMedia;

    public VlcPlaybackPrototype()
    {
        InitializeComponent();
        Core.Initialize();
        _libVlc = new LibVLC();
        _mediaPlayer = new MediaPlayer(_libVlc);
        VlcView.MediaPlayer = _mediaPlayer;
        Unloaded += (_, _) => Stop();
    }

    public bool Play(Uri uri, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (uri is null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        Stop();
        _currentMedia = new Media(_libVlc, uri);

        // LibVLC HTTP options for User-Agent and Referer.
        if (headers is not null)
        {
            if (TryGetHeader(headers, "User-Agent", out var userAgent))
                _currentMedia.AddOption($":http-user-agent={userAgent}");
            if (TryGetHeader(headers, "Referer", out var referer))
                _currentMedia.AddOption($":http-referrer={referer}");
        }

        return _mediaPlayer.Play(_currentMedia);
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

    public void Pause() => _mediaPlayer.Pause();
    public void Resume() => _mediaPlayer.Play();

    public void Stop()
    {
        _mediaPlayer.Stop();
        _currentMedia?.Dispose();
        _currentMedia = null;
    }

    public void Dispose()
    {
        Stop();
        _mediaPlayer.Dispose();
        _libVlc.Dispose();
    }
}
