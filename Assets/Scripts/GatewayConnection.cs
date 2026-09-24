using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Plain-C# transport for the gateway WebSocket: TCP + TLS with certificate
/// pinning, the HTTP upgrade handshake, a background receive loop, and
/// thread-safe sends. No Unity dependency, so it can be exercised headless
/// (see NetworkSmokeTest).
///
/// Why not ClientWebSocket: Unity's implementation ignores both
/// ServicePointManager's validation callback and
/// ClientWebSocketOptions.RemoteCertificateValidationCallback (verified with
/// NetworkSmokeTest), so a self-signed, pinned gateway cert can't be
/// accepted. Instead this owns the TLS layer via SslStream — whose callback
/// UnityTLS does honour — performs the RFC 6455 upgrade itself, and hands the
/// stream to WebSocket.CreateFromStream for framing.
///
/// Everything runs off the main thread (ConfigureAwait(false)). Received text
/// frames land in <see cref="Inbox"/>; the owner (NetworkClient) drains it on
/// the main thread.
/// </summary>
public sealed class GatewayConnection : IDisposable
{
    public enum ConnectionState { Disconnected, Connecting, Connected }

    public ConcurrentQueue<string> Inbox { get; } = new();
    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);
    public string LastError { get; private set; }
    /// <summary>True once the handshake succeeded at least once on this instance.</summary>
    public bool EverConnected { get; private set; }
    /// <summary>SHA-256 of the certificate the gateway presented, for diagnosing pin mismatches.</summary>
    public string PresentedFingerprint { get; private set; }

    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly Uri _uri;
    private readonly string _pinnedSha256;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient _tcp;
    private WebSocket _socket;
    private int _state;

    public GatewayConnection(Uri uri, string pinnedSha256)
    {
        _uri = uri;
        _pinnedSha256 = NormaliseFingerprint(pinnedSha256);
    }

    /// <summary>Connects, then runs the receive loop until the socket closes or fails. Returns when disconnected.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        SetState(ConnectionState.Connecting);
        _tcp = new TcpClient { NoDelay = true }; // small, latency-sensitive frames
        using var abortOnCancel = token.Register(Dispose);

        try
        {
            int port = _uri.IsDefaultPort ? (_uri.Scheme == "wss" ? 443 : 80) : _uri.Port;
            await _tcp.ConnectAsync(_uri.Host, port).ConfigureAwait(false);
            Stream stream = _tcp.GetStream();

            if (_uri.Scheme == "wss")
            {
                var ssl = new SslStream(stream, false, ValidateCertificate);
                await ssl.AuthenticateAsClientAsync(_uri.Host, null, SslProtocols.Tls12, false).ConfigureAwait(false);
                stream = ssl;
            }

            await UpgradeAsync(stream, token).ConfigureAwait(false);
            _socket = WebSocket.CreateFromStream(stream, false, null, TimeSpan.FromSeconds(10));

            SetState(ConnectionState.Connected);
            EverConnected = true;
            LastError = null;
            await ReceiveLoopAsync(token).ConfigureAwait(false);
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception e)
        {
            LastError = Describe(e);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>Sends one text frame. Safe to call from any thread; sends are serialised because WebSocket allows only one at a time.</summary>
    public async Task<bool> SendAsync(string json, CancellationToken token = default)
    {
        var socket = _socket;
        if (socket == null || socket.State != WebSocketState.Open) return false;

        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e)
        {
            LastError = Describe(e);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        SetState(ConnectionState.Disconnected);
        _socket?.Abort();
        _socket?.Dispose();
        _socket = null;
        _tcp?.Close();
        _tcp = null;
    }

    // ---- TLS pinning ----

    /// <summary>
    /// With a pin: the certificate's SHA-256 must match — chain errors are
    /// ignored (the gateway cert is self-signed) and a CA-valid but unpinned
    /// cert is still rejected. Without a pin: normal CA validation.
    /// </summary>
    private bool ValidateCertificate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
    {
        if (cert == null) return false;
        PresentedFingerprint = Fingerprint(cert);
        return string.IsNullOrEmpty(_pinnedSha256)
            ? errors == SslPolicyErrors.None
            : PresentedFingerprint == _pinnedSha256;
    }

    public static string Fingerprint(X509Certificate cert)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(cert.GetRawCertData()));
    }

    internal static string NormaliseFingerprint(string fp) =>
        string.IsNullOrWhiteSpace(fp) ? "" : fp.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();

    // ---- RFC 6455 opening handshake ----

    private async Task UpgradeAsync(Stream stream, CancellationToken token)
    {
        var keyBytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(keyBytes);
        var key = Convert.ToBase64String(keyBytes);

        var request =
            $"GET {_uri.PathAndQuery} HTTP/1.1\r\n" +
            $"Host: {_uri.Authority}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n";
        var requestBytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(requestBytes, 0, requestBytes.Length, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);

        // Read the response headers byte-by-byte up to the blank line, so no
        // WebSocket frame bytes are consumed from the stream.
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, 0, 1, token).ConfigureAwait(false) == 0)
                throw new IOException("Gateway closed the connection during the WebSocket handshake.");
            header.Append((char)one[0]);
            if (header.Length > 8192) throw new IOException("WebSocket handshake response too large.");
        }

        var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].Contains(" 101 "))
            throw new IOException($"WebSocket upgrade refused: '{(lines.Length > 0 ? lines[0] : "")}'");

        string expectedAccept;
        using (var sha1 = SHA1.Create())
            expectedAccept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

        foreach (var line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line.Substring(0, colon).Trim().Equals("Sec-WebSocket-Accept", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Substring(colon + 1).Trim() == expectedAccept) return;
                break;
            }
        }
        throw new IOException("WebSocket upgrade response had a missing or wrong Sec-WebSocket-Accept.");
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new ArraySegment<byte>(new byte[8192]);
        using var message = new MemoryStream();

        while (_socket != null && _socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var result = await _socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                LastError = $"closed by gateway ({result.CloseStatus} {result.CloseStatusDescription})";
                return;
            }

            message.Write(buffer.Array, buffer.Offset, result.Count);
            if (!result.EndOfMessage) continue;

            if (result.MessageType == WebSocketMessageType.Text)
                Inbox.Enqueue(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
            message.SetLength(0);
        }
    }

    private void SetState(ConnectionState s) => Volatile.Write(ref _state, (int)s);

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("X2"));
        return sb.ToString();
    }

    private static string Describe(Exception e)
    {
        while (e.InnerException != null) e = e.InnerException;
        return $"{e.GetType().Name}: {e.Message}";
    }
}
