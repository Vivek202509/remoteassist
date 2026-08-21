using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Techee.Windows.Host;

/// <summary>
/// Detects when Windows has taken the input desktop away from us.
/// </summary>
/// <remarks>
/// <para>
/// The secure desktop is a separate desktop object that Windows switches to for UAC
/// elevation prompts, the logon screen, and Ctrl+Alt+Del. A process on the normal
/// <c>Default</c> desktop cannot post input to it, by design: that isolation is the
/// entire reason an elevation prompt cannot be clicked by the software requesting the
/// elevation.
/// </para>
/// <para>
/// <b>Techee does not attempt to defeat this.</b> A remote-support tool that could drive
/// UAC prompts would be a remote-support tool that could silently elevate itself, and
/// the techniques for doing it are the techniques malware uses. Per
/// <c>docs/WINDOWS_SECURITY.md</c>, the requirement is to <i>report</i> the state
/// clearly — an operator who is told "secure desktop active" asks the user at the machine
/// to click Yes; an operator whose clicks vanish silently concludes the product is
/// broken.
/// </para>
/// <para>
/// This is also why detection is best-effort and fails toward "available": a false
/// positive would refuse input that would in fact have worked.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class SecureDesktop
{
    private const string InteractiveDesktop = "Default";

    private const uint DESKTOP_READOBJECTS = 0x0001;
    private const int UOI_NAME = 2;

    /// <summary>
    /// The reason input cannot be delivered, or null when it can.
    /// </summary>
    /// <remarks>
    /// The string is operator-facing and is the exact wording
    /// <c>docs/WINDOWS_SECURITY.md</c> asks for.
    /// </remarks>
    internal static string? InputBlockedReason()
    {
        var desktop = IntPtr.Zero;
        try
        {
            desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
            if (desktop == IntPtr.Zero)
            {
                // Opening the input desktop is refused precisely when it belongs to
                // another window station or is the secure one. There is no way to learn
                // its name in that case, and no need to: we already know we cannot
                // reach it.
                return "secure desktop active — remote input temporarily unavailable";
            }

            var name = DesktopName(desktop);
            if (name is null) return null;

            return string.Equals(name, InteractiveDesktop, StringComparison.OrdinalIgnoreCase)
                ? null
                : "secure desktop active — remote input temporarily unavailable";
        }
        catch (Exception)
        {
            // Never let a probe decide the session is broken. If we cannot tell, assume
            // input works and let SendInput report the truth.
            return null;
        }
        finally
        {
            if (desktop != IntPtr.Zero) CloseDesktop(desktop);
        }
    }

    private static unsafe string? DesktopName(IntPtr desktop)
    {
        Span<byte> buffer = stackalloc byte[256];
        fixed (byte* p = buffer)
        {
            if (!GetUserObjectInformationW(desktop, UOI_NAME, p, (uint)buffer.Length, out var needed))
                return null;

            // The API reports a byte count including the terminating null.
            var chars = (int)needed / sizeof(char) - 1;
            if (chars <= 0) return null;

            return new string((char*)p, 0, chars);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr OpenInputDesktop(
        uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,
        uint desiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserObjectInformationW(
        IntPtr obj,
        int index,
        void* info,
        uint length,
        out uint lengthNeeded);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(IntPtr desktop);
}
