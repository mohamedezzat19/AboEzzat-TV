using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AboEzzatReceiver.Windows;

/// <summary>
/// Loopback reverse proxy for media servers that require arbitrary request headers
/// which LibVLC's HTTP access module cannot attach. HLS playlists are rewritten so
/// child playlists, segments, encryption keys and initialization maps use this proxy.
/// </summary>
internal sealed class MediaHeaderProxy : IDisposable
{
    private const int MaxConcurrentClients = 24;
    private const int MaxRequestHeaderBytes = 32 * 1024;
    internal const int MaxPlaylistBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 8;

    private static readonly byte[] CrLf = Encoding.ASCII.GetBytes("\r\n");
    private static readonly byte[] ChunkTerminator = Encoding.ASCII.GetBytes("0\r\n\r\n");
    private static readonly Regex HlsUriAttribute = new(
        @"(?<prefix>\bURI\s*=\s*)(?<quote>[""'])(?<uri>.*?)(?:\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Content-Length", "Transfer-Encoding", "Proxy-Connection",
        "Keep-Alive", "TE", "Trailer", "Upgrade", "Expect", "Proxy-Authorization"
    };

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookie", "Authorization", "Proxy-Authorization", "X-Api-Key", "X-Auth-Token",
        "X-Access-Token", "X-Authorization"
    };

    private readonly ConcurrentDictionary<string, ProxySessionState> _sessions = new();
    private readonly SemaphoreSlim _clientSlots = new(MaxConcurrentClients, MaxConcurrentClients);
    private readonly object _startLock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private HttpClient? _http;
    private Task? _acceptTask;
    private int _port;
    private bool _disposed;

    public Session CreateSession(Uri rootUri, IReadOnlyDictionary<string, string>? headers)
    {
        ThrowIfDisposed();
        if (!IsHttpUri(rootUri))
            throw new ArgumentException("Only HTTP or HTTPS media URLs can use the header proxy.", nameof(rootUri));

        EnsureStarted();
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var normalizedHeaders = NormalizeHeaders(headers);
        var cookieOrigin = GetCookieOrigin(rootUri, normalizedHeaders);
        var state = new ProxySessionState(rootUri, cookieOrigin, normalizedHeaders);
        if (!_sessions.TryAdd(id, state))
            throw new InvalidOperationException("Could not create a media proxy session.");
        return new Session(this, id);
    }

    private void EnsureStarted()
    {
        lock (_startLock)
        {
            ThrowIfDisposed();
            if (_listener is not null) return;

            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                MaxConnectionsPerServer = MaxConcurrentClients,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start(64);
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _lifetime = new CancellationTokenSource();
            _acceptTask = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _clientSlots.WaitAsync(token).ConfigureAwait(false);
                bool slotTransferred = false;
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    slotTransferred = true;
                    _ = Task.Run(() => HandleClientWithSlotAsync(client, token));
                }
                finally
                {
                    if (!slotTransferred) _clientSlots.Release();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (SocketException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media header proxy accept loop stopped: {ex.Message}");
        }
    }

    private async Task HandleClientWithSlotAsync(TcpClient client, CancellationToken serverToken)
    {
        try
        {
            using (client)
                await HandleClientAsync(client, serverToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media header proxy client failed: {ex.Message}");
        }
        finally
        {
            _clientSlots.Release();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using var stream = client.GetStream();
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(15));

        var localRequest = await ReadLocalRequestAsync(stream, requestTimeout.Token).ConfigureAwait(false);
        if (localRequest is null) return;

        if (!localRequest.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !localRequest.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            await WriteSimpleResponseAsync(stream, 405, "Method Not Allowed",
                "Only GET and HEAD are supported.", serverToken).ConfigureAwait(false);
            return;
        }

        if (!TryResolveProxyTarget(localRequest.Target, out string sessionId, out Uri? remoteUri)
            || !_sessions.TryGetValue(sessionId, out var session))
        {
            await WriteSimpleResponseAsync(stream, 404, "Not Found",
                "Media proxy session not found.", serverToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var (response, finalUri) = await SendUpstreamAsync(
                localRequest, remoteUri!, session, serverToken).ConfigureAwait(false);
            using (response)
                await WriteUpstreamResponseAsync(
                    stream, response, localRequest.Method, remoteUri!, finalUri, sessionId, serverToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Media header proxy upstream failed: {ex.Message}");
            try
            {
                await WriteSimpleResponseAsync(stream, 502, "Bad Gateway",
                    "The upstream media server could not be reached.", serverToken).ConfigureAwait(false);
            }
            catch { }
        }
    }

    private async Task<(HttpResponseMessage Response, Uri FinalUri)> SendUpstreamAsync(
        LocalRequest localRequest, Uri originalUri, ProxySessionState session, CancellationToken token)
    {
        Uri currentUri = originalUri;
        for (int redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(
                localRequest.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                    ? HttpMethod.Head : HttpMethod.Get, currentUri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            bool sameRootOrigin = IsSameOrigin(session.RootUri, currentUri);
            bool sameCookieOrigin = IsSameOrigin(session.CookieOriginUri, currentUri);
            foreach (var pair in session.Headers)
            {
                if (HopByHopHeaders.Contains(pair.Key)) continue;
                if (pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) && !sameCookieOrigin)
                    continue;
                if (!pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                    && SensitiveHeaders.Contains(pair.Key) && !sameRootOrigin)
                    continue;
                request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            if (!session.Headers.ContainsKey("User-Agent"))
                request.Headers.TryAddWithoutValidation("User-Agent", "AboEzzat-TV/1.0");

            request.Headers.Remove("Accept-Encoding");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            foreach (string name in new[] { "Range", "If-Range", "If-None-Match", "If-Modified-Since" })
            {
                if (localRequest.Headers.TryGetValue(name, out var value))
                    request.Headers.TryAddWithoutValidation(name, value);
            }

            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            var response = await _http!.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token).ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode) || response.Headers.Location is null || redirect == MaxRedirects)
                return (response, currentUri);

            Uri nextUri = new(currentUri, response.Headers.Location);
            response.Dispose();
            if (!IsHttpUri(nextUri))
                throw new InvalidDataException("The media server redirected to an unsupported URL scheme.");
            currentUri = nextUri;
        }

        throw new HttpRequestException("The media server exceeded the redirect limit.");
    }

    private async Task WriteUpstreamResponseAsync(
        NetworkStream clientStream, HttpResponseMessage response, string method,
        Uri requestedUri, Uri finalUri, string sessionId, CancellationToken token)
    {
        bool noBody = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            || response.StatusCode == HttpStatusCode.NoContent
            || response.StatusCode == HttpStatusCode.NotModified;

        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        bool isHls = !noBody && response.IsSuccessStatusCode
            && (requestedUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || finalUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase));

        if (isHls)
        {
            // Reject an oversized declared body before opening it, then enforce the same
            // limit while streaming so chunked or dishonest Content-Length responses
            // cannot be fully buffered before the size check.
            if (response.Content.Headers.ContentLength is long declaredLength
                && declaredLength > MaxPlaylistBytes)
                throw new InvalidDataException("The HLS playlist is larger than the proxy limit.");

            await using var manifestStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] manifest = await ReadBoundedAsync(manifestStream, MaxPlaylistBytes, token).ConfigureAwait(false);

            string text = Encoding.UTF8.GetString(manifest);
            byte[] rewritten = Encoding.UTF8.GetBytes(RewriteHlsPlaylist(text, finalUri, sessionId));
            await WriteResponseHeadersAsync(clientStream, response, "application/vnd.apple.mpegurl",
                rewritten.LongLength, false, token).ConfigureAwait(false);
            await clientStream.WriteAsync(rewritten, token).ConfigureAwait(false);
            return;
        }

        long? contentLength = response.Content.Headers.ContentLength;
        await WriteResponseHeadersAsync(clientStream, response, mediaType, contentLength,
            !noBody && !contentLength.HasValue, token).ConfigureAwait(false);

        if (noBody) return;
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        if (contentLength.HasValue)
        {
            await body.CopyToAsync(clientStream, 64 * 1024, token).ConfigureAwait(false);
            return;
        }

        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
        {
            byte[] size = Encoding.ASCII.GetBytes(read.ToString("X") + "\r\n");
            await clientStream.WriteAsync(size, token).ConfigureAwait(false);
            await clientStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            await clientStream.WriteAsync(CrLf, token).ConfigureAwait(false);
        }
        await clientStream.WriteAsync(ChunkTerminator, token).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadBoundedAsync(
        Stream source, int maxBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            int remaining = maxBytes - checked((int)output.Length);
            int readLimit = remaining < buffer.Length ? remaining + 1 : buffer.Length;
            int read = await source.ReadAsync(buffer.AsMemory(0, readLimit), token).ConfigureAwait(false);
            if (read == 0) break;

            if (read > remaining)
                throw new InvalidDataException("The HLS playlist is larger than the proxy limit.");

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private async Task WriteResponseHeadersAsync(
        NetworkStream stream, HttpResponseMessage response, string contentType,
        long? contentLength, bool chunked, CancellationToken token)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ")
            .Append((int)response.StatusCode).Append(' ')
            .Append(SafeHeader(response.ReasonPhrase ?? "Response")).Append("\r\n");
        builder.Append("Content-Type: ").Append(SafeHeader(contentType)).Append("\r\n");

        if (contentLength.HasValue)
            builder.Append("Content-Length: ").Append(contentLength.Value).Append("\r\n");
        else if (chunked)
            builder.Append("Transfer-Encoding: chunked\r\n");

        string? contentRange = response.Content.Headers.ContentRange?.ToString();
        if (!string.IsNullOrWhiteSpace(contentRange))
            builder.Append("Content-Range: ").Append(SafeHeader(contentRange)).Append("\r\n");

        var acceptRanges = response.Headers.AcceptRanges;
        if (acceptRanges.Count > 0)
            builder.Append("Accept-Ranges: ").Append(SafeHeader(string.Join(", ", acceptRanges))).Append("\r\n");

        if (response.Headers.ETag is not null)
            builder.Append("ETag: ").Append(SafeHeader(response.Headers.ETag.ToString())).Append("\r\n");
        if (response.Content.Headers.LastModified.HasValue)
            builder.Append("Last-Modified: ").Append(response.Content.Headers.LastModified.Value.ToString("R")).Append("\r\n");
        if (response.Headers.Location is not null)
            builder.Append("Location: ").Append(SafeHeader(response.Headers.Location.ToString())).Append("\r\n");

        builder.Append("Connection: close\r\n\r\n");
        byte[] bytes = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private string RewriteHlsPlaylist(string playlist, Uri baseUri, string sessionId)
    {
        string[] lines = playlist.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith('#'))
            {
                lines[i] = HlsUriAttribute.Replace(line, match =>
                {
                    string value = match.Groups["uri"].Value;
                    if (!Uri.TryCreate(baseUri, value, out var target) || !IsHttpUri(target))
                        return match.Value;

                    return match.Groups["prefix"].Value + match.Groups["quote"].Value
                        + BuildProxyUri(sessionId, target).AbsoluteUri + match.Groups["quote"].Value;
                });
                continue;
            }

            if (Uri.TryCreate(baseUri, trimmed, out var mediaUri) && IsHttpUri(mediaUri))
            {
                int leading = line.Length - line.TrimStart().Length;
                int trailing = line.Length - line.TrimEnd().Length;
                string suffix = trailing > 0 ? line[^trailing..] : string.Empty;
                lines[i] = line[..leading] + BuildProxyUri(sessionId, mediaUri).AbsoluteUri + suffix;
            }
        }
        return string.Join("\n", lines);
    }

    private async Task<LocalRequest?> ReadLocalRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var bytes = new List<byte>(1024);
        byte[] one = new byte[1];
        while (bytes.Count < MaxRequestHeaderBytes)
        {
            int read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read == 0) return null;
            bytes.Add(one[0]);
            int count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10
                && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                break;
        }

        if (bytes.Count >= MaxRequestHeaderBytes)
            throw new InvalidDataException("Local media request headers are too large.");

        string[] lines = Encoding.ASCII.GetString(bytes.ToArray())
            .Split("\r\n", StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length < 2)
            throw new InvalidDataException("Invalid local media request.");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return new LocalRequest(requestLine[0], requestLine[1], headers);
    }

    private bool TryResolveProxyTarget(string target, out string sessionId, out Uri? remoteUri)
    {
        sessionId = string.Empty;
        remoteUri = null;
        Uri localUri;
        if (Uri.TryCreate(target, UriKind.Absolute, out var absoluteUri))
            localUri = absoluteUri;
        else if (Uri.TryCreate(new Uri($"http://127.0.0.1:{_port}/"), target, out var relativeUri))
            localUri = relativeUri;
        else
            return false;

        string[] segments = localUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments[0].Equals("p", StringComparison.Ordinal))
            return false;

        sessionId = segments[1];
        string? value = GetQueryValue(localUri.Query, "u");
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out remoteUri)
            && IsHttpUri(remoteUri);
    }

    private static string? GetQueryValue(string query, string name)
    {
        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string key = Uri.UnescapeDataString(equals >= 0 ? part[..equals] : part);
            if (!key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            string value = equals >= 0 ? part[(equals + 1)..] : string.Empty;
            return Uri.UnescapeDataString(value);
        }
        return null;
    }

    private static Uri GetCookieOrigin(Uri rootUri, IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Origin", out var origin)
            && Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && IsHttpUri(originUri))
            return originUri;

        if (headers.TryGetValue("Referer", out var referer)
            && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) && IsHttpUri(refererUri))
            return refererUri;

        return rootUri;
    }

    private static Dictionary<string, string> NormalizeHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return result;

        foreach (var pair in headers)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)
                || !Regex.IsMatch(pair.Key, @"^[!#$%&'*+.^_|~0-9A-Za-z-]+$")
                || HopByHopHeaders.Contains(pair.Key)
                || string.IsNullOrWhiteSpace(pair.Value)
                || pair.Value.Contains('\r') || pair.Value.Contains('\n'))
                continue;
            result[pair.Key] = pair.Value;
        }
        return result;
    }

    private static bool IsHttpUri(Uri? uri) =>
        uri is not null && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrigin(Uri first, Uri second) =>
        first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase)
        && first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase)
        && first.Port == second.Port;

    private static bool IsRedirect(HttpStatusCode status) =>
        (int)status is 301 or 302 or 303 or 307 or 308;

    private static string SafeHeader(string value) =>
        value.Replace("\r", string.Empty).Replace("\n", string.Empty);

    private Uri BuildProxyUri(string sessionId, Uri remoteUri) =>
        new($"http://127.0.0.1:{_port}/p/{sessionId}?u={Uri.EscapeDataString(remoteUri.AbsoluteUri)}");

    private static async Task WriteSimpleResponseAsync(
        NetworkStream stream, int status, string reason, string bodyText, CancellationToken token)
    {
        byte[] body = Encoding.UTF8.GetBytes(bodyText);
        byte[] header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    private void RemoveSession(string id) => _sessions.TryRemove(id, out _);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MediaHeaderProxy));
    }

    public void Dispose()
    {
        lock (_startLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _lifetime?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _sessions.Clear();
            _http?.Dispose();
            _lifetime?.Dispose();
        }
    }

    private sealed record ProxySessionState(
        Uri RootUri, Uri CookieOriginUri, Dictionary<string, string> Headers);
    private sealed record LocalRequest(string Method, string Target, Dictionary<string, string> Headers);

    internal sealed class Session : IDisposable
    {
        private readonly MediaHeaderProxy _owner;
        private readonly string _id;
        private int _disposed;

        internal Session(MediaHeaderProxy owner, string id)
        {
            _owner = owner;
            _id = id;
        }

        public Uri GetProxyUri(Uri remoteUri)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Session));
            if (!IsHttpUri(remoteUri))
                throw new ArgumentException("Only HTTP and HTTPS URLs can be proxied.", nameof(remoteUri));
            return _owner.BuildProxyUri(_id, remoteUri);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _owner.RemoveSession(_id);
        }
    }
}
