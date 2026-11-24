using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace AboEzzatReceiver.Windows;

// Small HTTP/1.1 receiver built directly on TcpListener.
// It deliberately avoids System.Net.HttpListener and HTTP.sys URLACL reservations.
internal sealed class TcpHttpListener : IDisposable
{
    private const int MaxConcurrentRequestParsers = 8;
    private const int PendingClientCapacity = 32;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(15);

    private readonly List<string> _prefixes = new();
    private readonly Channel<AcceptedClient> _acceptedClients =
        Channel.CreateBounded<AcceptedClient>(new BoundedChannelOptions(PendingClientCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private readonly Channel<TcpHttpListenerContext> _readyContexts =
        Channel.CreateBounded<TcpHttpListenerContext>(new BoundedChannelOptions(PendingClientCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private TcpListener? _tcp;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;
    private Task[] _parserTasks = Array.Empty<Task>();
    private long _acceptedSequence;
    private volatile bool _isListening;

    public PrefixCollection Prefixes { get; }
    public bool IsListening => _isListening;
    public long LastAcceptedSequence => Interlocked.Read(ref _acceptedSequence);

    private sealed record AcceptedClient(TcpClient Client, long Sequence);

    public TcpHttpListener() => Prefixes = new PrefixCollection(_prefixes);

    public void Start()
    {
        if (_prefixes.Count == 0) throw new InvalidOperationException("No HTTP prefix configured.");
        var prefix = new Uri(_prefixes[0]);
        _tcp = new TcpListener(IPAddress.Any, prefix.Port);
        _tcp.Start();

        _lifetime = new CancellationTokenSource();
        _isListening = true;
        var token = _lifetime.Token;

        // Keep accepting sockets independently from HTTP parsing. A slow peer
        // occupies one parser worker, not the TCP accept loop.
        _acceptTask = Task.Run(() => AcceptLoopAsync(token));
        _parserTasks = Enumerable.Range(0, MaxConcurrentRequestParsers)
            .Select(_ => Task.Run(() => ParseWorkerAsync(token)))
            .ToArray();
        _ = CompleteReadyContextsAsync();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && IsListening)
            {
                var client = await _tcp!.AcceptTcpClientAsync(token).ConfigureAwait(false);
                client.NoDelay = true;
                long sequence = Interlocked.Increment(ref _acceptedSequence);
                try
                {
                    await _acceptedClients.Writer
                        .WriteAsync(new AcceptedClient(client, sequence), token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _isListening = false;
            System.Diagnostics.Debug.WriteLine($"HTTP accept loop stopped: {ex.Message}");
        }
        finally
        {
            _acceptedClients.Writer.TryComplete();
        }
    }

    private async Task ParseWorkerAsync(CancellationToken token)
    {
        try
        {
            await foreach (var accepted in _acceptedClients.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                TcpHttpListenerContext? context = null;
                try
                {
                    context = await ReadContextAsync(accepted.Client, accepted.Sequence, token)
                        .ConfigureAwait(false);
                    await _readyContexts.Writer.WriteAsync(context, token).ConfigureAwait(false);
                    context = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    try { context?.Response.Close(); } catch { }
                    accepted.Client.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    try { context?.Response.Close(); } catch { }
                    accepted.Client.Dispose();
                    System.Diagnostics.Debug.WriteLine(
                        $"HTTP request parse failed (client {accepted.Sequence}): {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task CompleteReadyContextsAsync()
    {
        try
        {
            if (_acceptTask is not null)
                await _acceptTask.ConfigureAwait(false);
            if (_parserTasks.Length > 0)
                await Task.WhenAll(_parserTasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"HTTP parser shutdown: {ex.Message}");
        }
        finally
        {
            _readyContexts.Writer.TryComplete();
        }
    }

    public async Task<TcpHttpListenerContext> GetContextAsync()
    {
        if (_lifetime is null)
            throw new InvalidOperationException("Receiver is not listening.");
        return await _readyContexts.Reader.ReadAsync(_lifetime.Token).ConfigureAwait(false);
    }

    private static async Task<TcpHttpListenerContext> ReadContextAsync(
        TcpClient client, long acceptSequence, CancellationToken serverToken)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            requestTimeout.CancelAfter(RequestReadTimeout);
            var requestToken = requestTimeout.Token;

            var headerBytes = new List<byte>(1024);
            var one = new byte[1];
            while (headerBytes.Count < 32768)
            {
                int read = await stream.ReadAsync(one.AsMemory(0, 1), requestToken).ConfigureAwait(false);
                if (read == 0) throw new IOException("Client closed before sending HTTP headers.");
                headerBytes.Add(one[0]);
                int n = headerBytes.Count;
                if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10
                    && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
            }
            if (headerBytes.Count >= 32768) throw new InvalidDataException("HTTP headers are too large.");

            string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.None);
            var requestLine = lines[0].Split(' ', 3);
            if (requestLine.Length < 2) throw new InvalidDataException("Invalid HTTP request line.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out var length)
                && (!int.TryParse(length, out contentLength) || contentLength < 0 || contentLength > 1_048_576))
                throw new InvalidDataException("Invalid HTTP content length.");

            var body = new byte[contentLength];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), requestToken)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Incomplete HTTP request body.");
                offset += read;
            }

            string host = headers.TryGetValue("Host", out var h)
                ? h
                : $"127.0.0.1:{((IPEndPoint)client.Client.LocalEndPoint!).Port}";
            var uri = new Uri(new Uri($"http://{host}"), requestLine[1]);
            var request = new TcpHttpListenerRequest(requestLine[0], uri, body,
                headers.TryGetValue("Content-Type", out var ct) ? ct : null);
            return new TcpHttpListenerContext(
                request, new TcpHttpListenerResponse(client, stream), acceptSequence);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        _isListening = false;
        try { _lifetime?.Cancel(); } catch { }
        try { _tcp?.Stop(); } catch { }

        while (_acceptedClients.Reader.TryRead(out var accepted))
            accepted.Client.Dispose();
        while (_readyContexts.Reader.TryRead(out var context))
        {
            try { context.Response.Close(); } catch { }
        }
    }

    public void Close() => Stop();
    public void Dispose() => Stop();

    internal sealed class PrefixCollection
    {
        private readonly List<string> _items;
        internal PrefixCollection(List<string> items) => _items = items;
        public void Add(string prefix) => _items.Add(prefix);
    }
}

internal sealed class TcpHttpListenerContext
{
    public TcpHttpListenerRequest Request { get; }
    public TcpHttpListenerResponse Response { get; }
    public long AcceptSequence { get; }

    internal TcpHttpListenerContext(
        TcpHttpListenerRequest request, TcpHttpListenerResponse response, long acceptSequence)
    {
        Request = request;
        Response = response;
        AcceptSequence = acceptSequence;
    }
}

internal sealed class TcpHttpListenerRequest
{
    public string HttpMethod { get; }
    public Uri Url { get; }
    public Stream InputStream { get; }
    public Encoding ContentEncoding { get; } = Encoding.UTF8;
    public NameValueCollection QueryString { get; } = new();

    internal TcpHttpListenerRequest(string method, Uri url, byte[] body, string? contentType)
    {
        HttpMethod = method;
        Url = url;
        InputStream = new MemoryStream(body, writable: false);
        foreach (var pair in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            string key = Decode(parts[0]);
            string value = parts.Length > 1 ? Decode(parts[1]) : "";
            QueryString.Add(key, value);
        }
    }

    private static string Decode(string value) =>
        Uri.UnescapeDataString(value.Replace("+", " "));
}

internal sealed class TcpHttpListenerResponse
{
    private readonly TcpClient _client;
    private readonly NetworkStream _network;
    private bool _closed;
    public int StatusCode { get; set; } = 200;
    public string ContentType { get; set; } = "text/plain; charset=utf-8";
    public long ContentLength64 { get; set; } = -1;
    public Stream OutputStream { get; } = new MemoryStream();

    internal TcpHttpListenerResponse(TcpClient client, NetworkStream network)
    { _client = client; _network = network; }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            var body = ((MemoryStream)OutputStream).ToArray();
            string reason = StatusCode switch
            {
                200 => "OK", 204 => "No Content", 400 => "Bad Request",
                404 => "Not Found", _ => "Response"
            };
            string headers = $"HTTP/1.1 {StatusCode} {reason}\r\nContent-Type: {ContentType}\r\nContent-Length: {body.Length}\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\nAccess-Control-Allow-Headers: Content-Type\r\nConnection: close\r\n\r\n";
            byte[] head = Encoding.ASCII.GetBytes(headers);
            _network.Write(head, 0, head.Length);
            if (body.Length > 0) _network.Write(body, 0, body.Length);
            _network.Flush();
        }
        catch { }
        finally
        {
            OutputStream.Dispose();
            _network.Dispose();
            _client.Dispose();
        }
    }
}
