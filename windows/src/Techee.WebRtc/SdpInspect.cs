namespace Techee.WebRtc;

/// <summary>One <c>a=rtpmap</c> entry: a payload type bound to a codec.</summary>
public sealed record RtpMap(int PayloadType, string EncodingName, int ClockRate)
{
    public bool Is(string encodingName) =>
        string.Equals(EncodingName, encodingName, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One <c>m=</c> section of an SDP.</summary>
/// <remarks>
/// <see cref="Port"/> is the negotiation outcome that is easiest to miss. A peer that
/// declines a media section answers it with port <c>0</c> rather than omitting it, so an
/// SDP can carry a complete-looking <c>m=video</c> block, an <c>a=rtpmap</c> for VP8 and
/// a direction attribute while still meaning "no video". <see cref="IsActive"/> is the
/// check that distinguishes the two.
/// </remarks>
public sealed record MediaSection(
    string Kind,
    int Port,
    IReadOnlyList<int> PayloadTypes,
    IReadOnlyList<RtpMap> RtpMaps,
    string? Direction,
    string? IceUfrag,
    string? IcePwd)
{
    /// <summary>False when the peer declined this media section by answering port 0.</summary>
    public bool IsActive => Port != 0;

    /// <summary>The payload type negotiated for a codec, or null if it is not present.</summary>
    /// <remarks>
    /// Only counts a codec whose payload type also appears on the <c>m=</c> line. An
    /// <c>a=rtpmap</c> naming a payload type the media line never offered is stale text,
    /// not a negotiated codec.
    /// </remarks>
    public int? PayloadTypeFor(string encodingName) => RtpMaps
        .Where(map => map.Is(encodingName) && PayloadTypes.Contains(map.PayloadType))
        .Select(map => (int?)map.PayloadType)
        .FirstOrDefault();

    public bool Offers(string encodingName) => PayloadTypeFor(encodingName) is not null;
}

/// <summary>
/// Reads what an SDP actually negotiated.
/// </summary>
/// <remarks>
/// <para>
/// Exists because "the encoder emits VP8" and "the peer agreed to receive VP8" are
/// different claims, and only the second one determines whether a viewer sees anything.
/// A session can be fully connected, with DTLS established and RTP flowing, while the
/// far end discards every packet because it negotiated a payload type this side never
/// sends.
/// </para>
/// <para>
/// Deliberately free of SIPSorcery types, like <see cref="SdpAuth"/>. The negotiation
/// rules are then ordinary unit tests over text, including the hostile cases a real
/// library would never produce for us.
/// </para>
/// <para>
/// This is an <b>observer</b>. It parses and reports; it never edits SDP, and in
/// particular it does not touch the fingerprint line that
/// <see cref="SdpAuth"/> authenticates. Rewriting SDP after signing would invalidate the
/// signature that makes a compromised broker survivable.
/// </para>
/// </remarks>
public static class SdpInspect
{
    /// <summary>The <c>m=video</c> section, or null when the SDP has none.</summary>
    public static MediaSection? Video(string sdp) => Section(sdp, "video");

    /// <summary>The <c>m=audio</c> section, or null when the SDP has none.</summary>
    public static MediaSection? Audio(string sdp) => Section(sdp, "audio");

    /// <summary>A named media section, or null.</summary>
    public static MediaSection? Section(string sdp, string kind)
    {
        if (string.IsNullOrEmpty(sdp)) return null;

        var lines = sdp.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Session-level ICE credentials apply to every media section that does not
        // override them, which is what SIPSorcery and libwebrtc both emit under BUNDLE.
        var sessionUfrag = SessionAttribute(lines, "a=ice-ufrag:");
        var sessionPwd = SessionAttribute(lines, "a=ice-pwd:");

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].StartsWith($"m={kind} ", StringComparison.Ordinal)) continue;

            // m=<kind> <port> <proto> <fmt> ...
            var parts = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 0;

            var payloadTypes = parts
                .Skip(3)
                .Select(t => int.TryParse(t, out var pt) ? pt : -1)
                .Where(pt => pt >= 0)
                .ToList();

            var rtpMaps = new List<RtpMap>();
            string? direction = null, ufrag = null, pwd = null;

            // Attributes belong to this section until the next m= line.
            for (var j = i + 1; j < lines.Count && !lines[j].StartsWith("m=", StringComparison.Ordinal); j++)
            {
                var line = lines[j];

                if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
                {
                    if (ParseRtpMap(line) is { } map) rtpMaps.Add(map);
                }
                else if (line is "a=sendonly" or "a=recvonly" or "a=sendrecv" or "a=inactive")
                {
                    direction = line[2..];
                }
                else if (line.StartsWith("a=ice-ufrag:", StringComparison.Ordinal))
                {
                    ufrag = line["a=ice-ufrag:".Length..];
                }
                else if (line.StartsWith("a=ice-pwd:", StringComparison.Ordinal))
                {
                    pwd = line["a=ice-pwd:".Length..];
                }
            }

            return new MediaSection(
                kind, port, payloadTypes, rtpMaps, direction,
                ufrag ?? sessionUfrag, pwd ?? sessionPwd);
        }

        return null;
    }

    /// <summary>The negotiated payload type for a codec in the video section, or null.</summary>
    public static int? VideoPayloadType(string sdp, string encodingName) =>
        Video(sdp)?.PayloadTypeFor(encodingName);

    /// <summary>Whether the video section actually negotiated a codec on an active port.</summary>
    public static bool NegotiatedVideo(string sdp, string encodingName) =>
        Video(sdp) is { IsActive: true } video && video.Offers(encodingName);

    /// <summary>The session's ICE username fragment.</summary>
    public static string? IceUfrag(string sdp) =>
        Video(sdp)?.IceUfrag ?? SessionAttribute(Lines(sdp), "a=ice-ufrag:");

    /// <summary>The session's ICE password.</summary>
    public static string? IcePwd(string sdp) =>
        Video(sdp)?.IcePwd ?? SessionAttribute(Lines(sdp), "a=ice-pwd:");

    /// <summary>
    /// The DTLS fingerprint line.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="SdpAuth.FingerprintOf"/> rather than re-parsing, so there
    /// is exactly one definition of what Techee treats as the fingerprint. Two parsers
    /// that could disagree about that line is precisely how a signature check ends up
    /// covering something other than what the media is bound to.
    /// </remarks>
    public static string? Fingerprint(string sdp) => SdpAuth.FingerprintOf(sdp);

    private static List<string> Lines(string sdp) =>
        (sdp ?? string.Empty).Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    /// <summary>An attribute appearing before the first <c>m=</c> line.</summary>
    private static string? SessionAttribute(List<string> lines, string prefix)
    {
        foreach (var line in lines)
        {
            if (line.StartsWith("m=", StringComparison.Ordinal)) return null;
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line[prefix.Length..];
        }
        return null;
    }

    /// <summary>Parses <c>a=rtpmap:96 VP8/90000</c>. Returns null for anything malformed.</summary>
    private static RtpMap? ParseRtpMap(string line)
    {
        var body = line["a=rtpmap:".Length..];

        var space = body.IndexOf(' ');
        if (space <= 0) return null;

        if (!int.TryParse(body[..space], out var payloadType)) return null;

        var codec = body[(space + 1)..].Split('/');
        if (codec.Length < 2) return null;
        if (!int.TryParse(codec[1], out var clockRate)) return null;

        return new RtpMap(payloadType, codec[0], clockRate);
    }
}
