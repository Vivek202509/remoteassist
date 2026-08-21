using Techee.WebRtc;
using Techee.Windows.Host;

namespace Techee.Session;

/// <summary>
/// Adapts a <see cref="TecheePeerConnection"/> onto the capture pipeline's
/// <see cref="IEncodedVideoSink"/>.
/// </summary>
/// <remarks>
/// <para>
/// This class is the entire coupling between capture and transport, and it is
/// deliberately trivial: the pipeline knows nothing about WebRTC, and the peer
/// connection knows nothing about DXGI, profiles, or encoders. Everything either side
/// needs to say to the other is "here is a VP8 frame, paced at this rate".
/// </para>
/// <para>
/// <b>One sink, one peer.</b> A sink is bound to the peer it was constructed with and
/// cannot be repointed. Recovery replaces the peer, so it replaces the sink too — which
/// is what stops a stale transport from continuing to receive frames after an
/// authenticated peer recreation.
/// </para>
/// <para>
/// Frames are dropped, never queued. <see cref="TecheePeerConnection.SendVideoFrame"/>
/// already discards anything offered before the link is connected, so frames encoded
/// during ICE or DTLS negotiation cost a counter increment and nothing else. That is the
/// correct trade for live video: a frame held back is a frame the operator sees late.
/// </para>
/// </remarks>
public sealed class PeerVideoSink : IEncodedVideoSink
{
    private readonly TecheePeerConnection _peer;
    private readonly Action<string>? _log;

    private long _framesOffered;
    private long _framesDroppedNotConnected;

    public PeerVideoSink(TecheePeerConnection peer, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        _peer = peer;
        _log = log;
    }

    /// <summary>Frames handed to this sink by the pipeline.</summary>
    public long FramesOffered => Interlocked.Read(ref _framesOffered);

    /// <summary>
    /// Frames dropped because the link was not carrying media yet.
    /// </summary>
    /// <remarks>
    /// Expected to be non-zero on every session: capture starts as soon as a controller
    /// is authorised, which is before ICE and DTLS finish. A count that keeps climbing
    /// after the link reports connected is the signal that something is wrong.
    /// </remarks>
    public long FramesDroppedNotConnected => Interlocked.Read(ref _framesDroppedNotConnected);

    /// <summary>The peer this sink feeds. Fixed at construction.</summary>
    public TecheePeerConnection Peer => _peer;

    public void SendEncodedFrame(EncodedFrame frame, int fps)
    {
        Interlocked.Increment(ref _framesOffered);

        // Checked here as well as inside SendVideoFrame, purely so the drop is
        // attributable: without it, "frames encoded" and "frames sent" diverge with no
        // recorded reason and the first guess is always an encoder bug.
        if (_peer.State != LinkState.Connected)
        {
            Interlocked.Increment(ref _framesDroppedNotConnected);
            return;
        }

        _peer.SendVideoFrame(frame.Payload, fps);
    }

    /// <summary>A one-line summary for the diagnostics view.</summary>
    public override string ToString() =>
        $"PeerVideoSink(peer={_peer.PeerId}, offered={FramesOffered}, droppedPreConnect={FramesDroppedNotConnected})";
}
