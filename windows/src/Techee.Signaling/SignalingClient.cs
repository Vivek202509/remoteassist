using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Techee.Crypto;
using Techee.Protocol;

namespace Techee.Signaling;

/// <summary>
/// The Windows endpoint's connection to the Techee signaling broker.
/// </summary>
/// <remarks>
/// <para>
/// Speaks the same handshake as <c>com.remoteassist.signaling.SignalingClient</c>:
/// <c>register</c> → <c>register-challenge</c> → sign the transcript with the device
/// identity key → <c>register-proof</c> → <c>registered</c>. The private key never
/// leaves CNG; only the signature crosses the wire.
/// </para>
/// <para>
/// The broker is treated as untrusted throughout. It can see and alter anything on
/// this socket, so nothing here is a security decision — media authenticity is
/// established separately by signing DTLS fingerprints with the same identity key.
/// This class's security obligations are narrow: never leak the private key, never
/// log secrets, and never let a malformed broker frame take the process down.
/// </para>
/// </remarks>
public sealed class SignalingClient : IAsyncDisposable
{
    private readonly Uri _url;
    private readonly IDeviceIdentity _identity;
    private readonly EndpointMeta? _meta;
    private readonly Action<string>? _log;

    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Channel<SignalingMessage> _inbox =
        Channel.CreateUnbounded<SignalingMessage>(new UnboundedChannelOptions { SingleReader = true });

    private TaskCompletionSource<RegistrationResult>? _registration;
    private CancellationTokenSource? _receiveLoop;

    public SignalingClient(
        Uri url,
        IDeviceIdentity identity,
        EndpointMeta? meta = null,
        Action<string>? log = null)
    {
        _url = url;
        _identity = identity;
        _meta = meta;
        _log = log;
    }

    /// <summary>This endpoint's device ID, as the broker will know it.</summary>
    public string DeviceId => _identity.DeviceId;

    public bool IsConnected => _socket?.State == WebSocketState.Open;

    /// <summary>ICE servers from the most recent registration. Credentials expire.</summary>
    public IReadOnlyList<IceServer> IceServers { get; private set; } = [];

    /// <summary>The protocol version the broker advertised, or null if it did not.</summary>
    public int? BrokerProtocolVersion { get; private set; }

    /// <summary>
    /// Messages that are not part of the registration handshake.
    /// </summary>
    /// <remarks>
    /// A channel rather than an event so a slow consumer applies back-pressure to
    /// itself instead of blocking the receive loop, and so handler exceptions cannot
    /// escape into the socket read.
    /// </remarks>
    public ChannelReader<SignalingMessage> Messages => _inbox.Reader;

    /// <summary>
    /// Connects and completes the registration handshake.
    /// </summary>
    /// <remarks>
    /// Returns a result rather than throwing on rejection: a refused registration is
    /// an expected operational state (identity mismatch, clock skew, a broker that
    /// has not been told about this device) and the caller must be able to report it
    /// rather than crash a service on it.
    /// </remarks>
    public async Task<RegistrationResult> ConnectAndRegisterAsync(CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var socket = new ClientWebSocket();
        _socket = socket;

        try
        {
            await socket.ConnectAsync(_url, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException)
        {
            _log?.Invoke($"[signaling] connect failed: {e.Message}");
            return new RegistrationResult(RegistrationOutcome.Disconnected, Reason: e.Message);
        }

        _registration = new TaskCompletionSource<RegistrationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _receiveLoop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReceiveLoopAsync(socket, _receiveLoop.Token), CancellationToken.None);

        // Step 1: claim an identity and present the key it is derived from. The
        // broker re-derives the device ID from this key and refuses the pair if they
        // disagree, so nothing is registered on our say-so.
        await SendAsync(w =>
        {
            w.WriteString("type", "register");
            w.WriteString("deviceId", _identity.DeviceId);
            w.WriteString("publicKey", _identity.PublicKeyB64);
            if (_meta is not null)
            {
                w.WriteStartObject("meta");
                w.WriteString("platform", _meta.Platform);
                w.WriteString("version", _meta.Version);
                w.WriteStartArray("capabilities");
                foreach (var c in _meta.Capabilities) w.WriteStringValue(c);
                w.WriteEndArray();
                w.WriteEndObject();
            }
        }, ct).ConfigureAwait(false);

        return await _registration.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Requests fresh TURN credentials. They are time-limited and expire.</summary>
    public Task RequestTurnCredentialsAsync(CancellationToken ct = default) =>
        SendAsync(w => w.WriteString("type", "turn-credentials"), ct);

    /// <summary>Registers a pairing edge. The broker requires <c>myPub</c> to be our own ID.</summary>
    public Task RegisterPairingAsync(string peerDeviceId, CancellationToken ct = default) =>
        SendAsync(w =>
        {
            w.WriteString("type", "register-pairing");
            w.WriteString("myPub", _identity.DeviceId);
            w.WriteString("peerPub", peerDeviceId);
        }, ct);

    /// <summary>Publishes a standing unattended grant for a paired controller.</summary>
    /// <remarks>
    /// The broker stores this so it can tell a dialling controller that the session
    /// will be unattended. It is <b>not</b> the authority: this host re-checks its own
    /// local copy before executing anything, because a compromised broker could
    /// otherwise manufacture an unattended session.
    /// </remarks>
    public Task RegisterGrantAsync(Grant grant, CancellationToken ct = default) =>
        SendAsync(w =>
        {
            w.WriteString("type", "register-grant");
            w.WriteStartObject("grant");
            w.WriteString("grantId", grant.GrantId);
            w.WriteString("controllerId", grant.ControllerId);
            w.WriteBoolean("active", grant.Active);
            if (grant.ExpiresAt is { } exp) w.WriteNumber("expiresAt", exp);
            else w.WriteNull("expiresAt");
            w.WriteStartArray("permissions");
            foreach (var p in grant.EffectivePermissions) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteEndObject();
        }, ct);

    public Task RevokeGrantAsync(string grantId, CancellationToken ct = default) =>
        SendAsync(w =>
        {
            w.WriteString("type", "revoke-grant");
            w.WriteString("grantId", grantId);
        }, ct);

    /// <summary>Dials a paired host directly by device ID.</summary>
    public Task JoinAsync(string hostDeviceId, CancellationToken ct = default) =>
        SendAsync(w =>
        {
            w.WriteString("type", "join");
            w.WriteString("hostId", hostDeviceId);
        }, ct);

    /// <summary>Sends a raw message. Used for the SDP/ICE relay, which is shaped by the peer layer.</summary>
    public Task SendAsync(Action<Utf8JsonWriter> build, CancellationToken ct = default)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            build(w);
            w.WriteEndObject();
        }
        return SendRawAsync(buffer.ToArray(), ct);
    }

    private async Task SendRawAsync(byte[] payload, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open) return;

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or InvalidOperationException)
        {
            // The socket died mid-send. The receive loop will observe the same thing
            // and drive reconnection; throwing here would only duplicate that.
            _log?.Invoke($"[signaling] send dropped: {e.GetType().Name}");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var accumulated = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log?.Invoke("[signaling] broker closed the connection");
                    break;
                }

                accumulated.Write(buffer, 0, result.Count);

                // A frame larger than any legitimate signaling message is dropped
                // rather than accumulated: the broker is untrusted and must not be
                // able to grow this buffer without bound.
                if (accumulated.Length > TecheeProtocol.MaxFrameBytes * 4)
                {
                    _log?.Invoke("[signaling] oversized frame discarded");
                    accumulated.SetLength(0);
                    continue;
                }

                if (!result.EndOfMessage) continue;

                var payload = accumulated.ToArray();
                accumulated.SetLength(0);
                Dispatch(payload);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException)
        {
            _log?.Invoke($"[signaling] receive loop ended: {e.GetType().Name}");
        }
        finally
        {
            // Never leave a caller awaiting a handshake that can no longer complete.
            _registration?.TrySetResult(new RegistrationResult(
                RegistrationOutcome.Disconnected, Reason: "socket closed"));
            _inbox.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Parses and routes one broker frame.
    /// </summary>
    /// <remarks>
    /// Never throws. The broker is untrusted, and this runs on the socket read loop of
    /// a service that must stay up — Android's equivalent uses throwing JSON getters
    /// here, so one malformed frame kills its connection. This does not repeat that.
    /// </remarks>
    private void Dispatch(byte[] payload)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            _log?.Invoke("[signaling] dropped a frame that was not valid JSON");
            return;
        }

        try
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return;

            var type = typeEl.GetString()!;
            switch (type)
            {
                case "register-challenge":
                    OnChallenge(root);
                    return;

                case "registered":
                    OnRegistered(root);
                    return;

                case "register-failed":
                    _registration?.TrySetResult(new RegistrationResult(
                        RegistrationOutcome.Rejected,
                        Reason: root.TryGetProperty("reason", out var r) ? r.GetString() : "unknown"));
                    return;

                case "not-authenticated":
                    _registration?.TrySetResult(new RegistrationResult(
                        RegistrationOutcome.Rejected, Reason: "not-authenticated"));
                    break;

                case "turn-credentials":
                    IceServers = ParseIceServers(root);
                    break;
            }

            // Everything else — join-request, consent, offer/answer/ice, pairing —
            // is the session layer's business. Clone because the JsonDocument is
            // disposed as soon as this method returns.
            _inbox.Writer.TryWrite(new SignalingMessage(type, root.Clone()));
        }
        finally
        {
            doc.Dispose();
        }
    }

    private void OnChallenge(JsonElement root)
    {
        if (!root.TryGetProperty("challenge", out var c) || c.ValueKind != JsonValueKind.String)
        {
            _registration?.TrySetResult(new RegistrationResult(
                RegistrationOutcome.Rejected, Reason: "malformed-challenge"));
            return;
        }

        var challenge = c.GetString()!;

        byte[] signature;
        try
        {
            // Step 2: prove possession of the identity private key. The transcript is
            // domain-separated so this signature cannot be replayed into the SDP or
            // peer-challenge protocols, which use the same key.
            signature = _identity.Sign(TecheeCrypto.RegistrationTranscript(_identity.DeviceId, challenge));
        }
        catch (Exception e)
        {
            // A TPM that refuses to sign is a real failure mode — the device simply
            // cannot register, and retrying will not help.
            _log?.Invoke($"[signaling] failed to sign the registration challenge: {e.GetType().Name}");
            _registration?.TrySetResult(new RegistrationResult(
                RegistrationOutcome.Rejected, Reason: "signing-failed"));
            return;
        }

        _ = SendAsync(w =>
        {
            w.WriteString("type", "register-proof");
            w.WriteString("deviceId", _identity.DeviceId);
            w.WriteString("signature", TecheeCrypto.B64(signature));
        });
    }

    private void OnRegistered(JsonElement root)
    {
        var deviceId = root.TryGetProperty("deviceId", out var d) ? d.GetString() : null;
        IceServers = ParseIceServers(root);

        if (root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("version", out var pv) && pv.ValueKind == JsonValueKind.Number)
        {
            BrokerProtocolVersion = pv.GetInt32();
        }

        // A broker that echoes a different device ID is either broken or hostile.
        // Either way this endpoint is not registered as itself, so treat it as a
        // rejection rather than proceeding under an identity we did not claim.
        if (deviceId != _identity.DeviceId)
        {
            _log?.Invoke("[signaling] broker echoed a different device id; refusing the registration");
            _registration?.TrySetResult(new RegistrationResult(
                RegistrationOutcome.Rejected, Reason: "device-id-mismatch"));
            return;
        }

        _registration?.TrySetResult(new RegistrationResult(
            RegistrationOutcome.Registered,
            deviceId,
            IceServers: IceServers,
            ProtocolVersion: BrokerProtocolVersion));
    }

    private static IReadOnlyList<IceServer> ParseIceServers(JsonElement root)
    {
        if (!root.TryGetProperty("iceServers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<IceServer>();
        foreach (var s in arr.EnumerateArray())
        {
            if (s.ValueKind != JsonValueKind.Object) continue;
            var urls = s.TryGetProperty("urls", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()
                : null;
            if (urls is null) continue;

            list.Add(new IceServer(
                urls,
                s.TryGetProperty("username", out var un) ? un.GetString() : null,
                s.TryGetProperty("credential", out var cr) ? cr.GetString() : null));
        }
        return list;
    }

    public async Task DisconnectAsync()
    {
        if (_receiveLoop is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
            _receiveLoop = null;
        }

        if (_socket is { } socket)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or OperationCanceledException)
            {
                // Already gone. Closing cleanly is a courtesy, not a requirement.
            }

            socket.Dispose();
            _socket = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }
}
