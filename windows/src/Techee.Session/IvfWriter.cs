using System.Buffers.Binary;

namespace Techee.Session;

/// <summary>
/// Writes received frames as an IVF-wrapped VP8 elementary stream.
/// </summary>
/// <remarks>
/// <para>
/// The evidence artefact for an acceptance run. A screenshot proves a picture appeared
/// once; a recording proves what actually crossed the link for the whole session, can be
/// replayed by a tool that is not Techee (<c>ffplay</c>, <c>ffprobe</c>, VLC), and
/// survives the session it came from. "It looked fine on the day" is not a matrix row.
/// </para>
/// <para>
/// Deliberately stores the frames <b>as received</b>, before decoding. A recording
/// written from decoded output would prove only that this controller's decoder agreed
/// with itself; one written from the wire can be handed to a different decoder, which is
/// the only way the packetisation question gets an independent answer.
/// </para>
/// <para>
/// IVF's timebase is set to the RTP video clock — 90 kHz — so the timestamps are the
/// ones from the wire rather than a re-derived guess at the frame rate. Uneven pacing
/// therefore shows up in the file instead of being flattened into a constant rate.
/// </para>
/// </remarks>
public sealed class IvfWriter : IDisposable
{
    private const int HeaderBytes = 32;

    // Frames are written from the receive thread and the file is finalised from the
    // shutdown path, so the two genuinely race at the end of every session.
    private readonly object _gate = new();

    private readonly FileStream _file;
    private readonly byte[] _frameHeader = new byte[12];

    private uint _frames;
    private ushort _width;
    private ushort _height;
    private uint _firstTimestamp;
    private bool _haveFirstTimestamp;
    private bool _disposed;
    private long _closedLength;

    public IvfWriter(string path)
    {
        Path = path;
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        _file.Write(new byte[HeaderBytes]);
    }

    public string Path { get; }

    public long Frames => _frames;

    /// <summary>Bytes written so far, or the final size once closed.</summary>
    public long Bytes
    {
        get
        {
            lock (_gate)
            {
                // Read under the lock and guarded, because the status line polls this
                // while the shutdown path may already have closed the file.
                return _disposed ? _closedLength : _file.Length;
            }
        }
    }

    /// <summary>
    /// Records the picture size, once decoding has revealed it.
    /// </summary>
    /// <remarks>
    /// The dimensions are not knowable when the file is opened — they live inside the
    /// first key frame — so the header is written blank and patched on close. Taking the
    /// first size seen rather than the newest is deliberate: a mid-session profile change
    /// alters the resolution, and IVF has one header for the whole file, so the honest
    /// choice is the size the file starts at.
    /// </remarks>
    public void SetDimensions(int width, int height)
    {
        if (width is <= 0 or > ushort.MaxValue || height is <= 0 or > ushort.MaxValue) return;

        lock (_gate)
        {
            if (_width != 0 || _height != 0) return;

            _width = (ushort)width;
            _height = (ushort)height;
        }
    }

    /// <summary>Appends one encoded frame.</summary>
    public void Write(byte[] encoded, uint rtpTimestamp)
    {
        if (encoded.Length == 0) return;

        lock (_gate)
        {
            if (_disposed) return;

            // Rebased so the file starts at zero. An absolute RTP timestamp starts at a
            // random offset, which some players read as a very long empty lead-in.
            if (!_haveFirstTimestamp)
            {
                _firstTimestamp = rtpTimestamp;
                _haveFirstTimestamp = true;
            }

            // Unsigned subtraction, so a mid-session wrap of the 32-bit RTP clock stays
            // monotonic instead of producing an enormous negative jump.
            var relative = unchecked(rtpTimestamp - _firstTimestamp);

            BinaryPrimitives.WriteUInt32LittleEndian(_frameHeader.AsSpan(0, 4), (uint)encoded.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(_frameHeader.AsSpan(4, 8), relative);

            try
            {
                _file.Write(_frameHeader);
                _file.Write(encoded);
                _frames++;
            }
            catch (IOException)
            {
                // A full disk must not end a session that is otherwise working.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                PatchHeader();
                _file.Flush();
                _closedLength = _file.Length;
            }
            catch (IOException)
            {
                // The recording is a diagnostic. Failing to finalise it must not be the
                // reason an otherwise successful acceptance run reports an error.
            }
            finally
            {
                _file.Dispose();
            }
        }
    }

    private void PatchHeader()
    {
        var header = new byte[HeaderBytes];

        "DKIF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), 0);          // version
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), HeaderBytes);
        "VP80"u8.CopyTo(header.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12, 2), _width);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14, 2), _height);

        // Timebase 1/90000 — the RTP video clock, so the timestamps written above are
        // the wire's own and need no conversion.
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 90_000);    // rate
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), 1);         // scale
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), _frames);

        _file.Seek(0, SeekOrigin.Begin);
        _file.Write(header);
    }

    public override string ToString() =>
        $"IvfWriter({Path}, frames={_frames}, {_width}x{_height})";
}
