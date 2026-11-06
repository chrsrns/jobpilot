using System.Runtime.InteropServices;
using Pty.Net;

namespace JobPilot.Terminal.Sessions;

/// <summary>
/// Reads the slave's line discipline through the pty master fd. Bytes written while the tty is in
/// canonical mode echo and line-buffer (<c>icrnl</c> rewrites CR to LF), reaching the TUI as pasted
/// text where Enter never submits — so sends must wait for <c>ICANON</c> to clear.
/// </summary>
internal static class PtyTermios
{
    private const ulong IcanonLinux = 0x2;
    private const ulong IcanonDarwin = 0x100;

    // c_lflag follows iflag/oflag/cflag; tcflag_t is u32 on Linux, unsigned long on Darwin.
    private const int LflagOffsetLinux = 12;
    private const int LflagOffsetDarwin = 24;

    /// <summary>Best-effort: ConPTY has no canonical mode, and any lookup failure reads as raw.</summary>
    internal static bool IsCanonical(IPtyConnection connection)
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            // Pty.Net's Unix streams wrap the pty master, whose termios mirrors the slave's.
            if (connection.ReaderStream is not FileStream { SafeFileHandle: { IsInvalid: false, IsClosed: false } handle })
            {
                return false;
            }

            var termios = new byte[128];
            var referenced = false;
            try
            {
                handle.DangerousAddRef(ref referenced);
                if (tcgetattr((int)handle.DangerousGetHandle(), termios) != 0)
                {
                    return false;
                }
            }
            finally
            {
                if (referenced)
                {
                    handle.DangerousRelease();
                }
            }

            return OperatingSystem.IsMacOS()
                ? (BitConverter.ToUInt64(termios, LflagOffsetDarwin) & IcanonDarwin) != 0
                : (BitConverter.ToUInt32(termios, LflagOffsetLinux) & IcanonLinux) != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, [Out] byte[] termios);
}
