namespace Techee.Protocol;

/// <summary>
/// A decoded control message, platform-neutral.
/// </summary>
/// <remarks>
/// Every command a Techee endpoint can express appears here exactly once, regardless
/// of which wire dialect carried it. Nothing downstream of <see cref="ControlCodec"/>
/// knows whether a frame arrived as legacy <c>{"t":"tap"}</c> or versioned
/// <c>{"v":1,"t":"pointer.tap"}</c>.
/// </remarks>
public abstract record Control
{
    /// <summary>The v1 type name. Also the key used for permission lookup.</summary>
    public abstract string Kind { get; }

    /// <summary>Normalized 0..1 tap in the captured surface's coordinate space.</summary>
    public sealed record PointerTap(double X, double Y) : Control
    {
        public override string Kind => "pointer.tap";
    }

    public sealed record PointerMove(double X, double Y) : Control
    {
        public override string Kind => "pointer.move";
    }

    public sealed record PointerDown(double X, double Y, string Button) : Control
    {
        public override string Kind => "pointer.down";
    }

    public sealed record PointerUp(double X, double Y, string Button) : Control
    {
        public override string Kind => "pointer.up";
    }

    /// <summary><paramref name="Dx"/>/<paramref name="Dy"/> are wheel notches, not pixels.</summary>
    public sealed record PointerWheel(double X, double Y, double Dx, double Dy) : Control
    {
        public override string Kind => "pointer.wheel";
    }

    public sealed record PointerSwipe(double X1, double Y1, double X2, double Y2, long Ms) : Control
    {
        public override string Kind => "pointer.swipe";
    }

    /// <summary>Android navigation. Meaningless on a Windows host, which ignores it.</summary>
    public sealed record NavKey(string Key) : Control
    {
        public override string Kind => "nav.key";
    }

    /// <summary><paramref name="Code"/> is a W3C <c>KeyboardEvent.code</c>, not a virtual-key number.</summary>
    public sealed record KeyDown(string Code, IReadOnlyList<string>? Mods) : Control
    {
        public override string Kind => "keyboard.keyDown";
    }

    public sealed record KeyUp(string Code, IReadOnlyList<string>? Mods) : Control
    {
        public override string Kind => "keyboard.keyUp";
    }

    public sealed record KeyText(string Text) : Control
    {
        public override string Kind => "keyboard.text";
    }

    public sealed record ClipboardSet(string Mime, string Text) : Control
    {
        public override string Kind => "clipboard.set";
    }

    public sealed record ClipboardData(string Mime, string Text) : Control
    {
        public override string Kind => "clipboard.data";
    }

    public sealed record ClipboardRequest : Control
    {
        public override string Kind => "clipboard.request";
    }

    /// <summary>A power action. Each requires its own grant permission — none implies another.</summary>
    public sealed record SystemAction(string Action) : Control
    {
        public override string Kind => Action;
    }

    public sealed record DisplayList : Control
    {
        public override string Kind => "display.list";
    }

    public sealed record DisplaySelect(string Id) : Control
    {
        public override string Kind => "display.select";
    }

    public sealed record HostCallState(string State) : Control
    {
        public override string Kind => "host.callState";
    }

    /// <summary>
    /// A capability advertisement over the peer connection.
    /// </summary>
    /// <remarks>
    /// Trustworthy as <i>the peer's own claim</i>, because it arrives over the
    /// E2E-authenticated channel rather than via the broker. Still not authorization.
    /// </remarks>
    public sealed record Hello(EndpointMeta Meta, bool Ack) : Control
    {
        public override string Kind => Ack ? "hello.ack" : "hello";
    }

    /// <summary>Recognised but not modelled by this build. Ignored safely, not treated as hostile.</summary>
    public sealed record Opaque(string Type) : Control
    {
        public override string Kind => Type;
    }
}
