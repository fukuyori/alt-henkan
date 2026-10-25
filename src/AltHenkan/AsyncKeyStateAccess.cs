using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AltHenkan;

// GetAsyncKeyState returns zero both for "up" and for unavailable desktop/UIPI access.
// This guard is used only by the maintenance timer, never by keyboard callbacks.
internal static class AsyncKeyStateAccess
{
    public static bool TryReadAlt(out nint foreground, out bool leftDown, out bool rightDown)
        => TryReadModifiers(out foreground, out leftDown, out rightDown, out _, out _);

    public static bool TryReadModifiers(out nint foreground, out bool leftDown, out bool rightDown,
        out bool leftControlDown, out bool rightControlDown)
    {
        foreground = NativeMethods.GetForegroundWindow();
        leftDown = rightDown = false;
        leftControlDown = rightControlDown = false;
        if (foreground == 0 || !IsInputDesktop()) return false;
        if (NativeMethods.GetWindowThreadProcessId(foreground, out var processId) == 0) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            if (!TryGetDword(identity.AccessToken, 26 /* TokenUIAccess */, out var uiAccess)) return false;
            if (uiAccess == 0 && processId != (uint)Environment.ProcessId)
            {
                if (!TryGetIntegrity(identity.AccessToken, out var ownIntegrity)) return false;
                using var process = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, processId);
                if (process.IsInvalid || !OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
                    return false;
                using (token)
                {
                    if (!TryGetIntegrity(token, out var targetIntegrity) || targetIntegrity > ownIntegrity)
                        return false;
                }
            }
            leftDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VkLMenu) & 0x8000) != 0;
            rightDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VkRMenu) & 0x8000) != 0;
            leftControlDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VkLControl) & 0x8000) != 0;
            rightControlDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VkRControl) & 0x8000) != 0;
            return NativeMethods.GetForegroundWindow() == foreground && IsInputDesktop();
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsInputDesktop() => GetUserObjectInformation(
        GetThreadDesktop(GetCurrentThreadId()), 6 /* UOI_IO */, out var receivingInput, sizeof(int), out _) &&
        receivingInput != 0;

    private static bool TryGetDword(SafeAccessTokenHandle token, int infoClass, out int value)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            var success = GetTokenInformation(token, infoClass, buffer, sizeof(int), out _);
            value = success ? Marshal.ReadInt32(buffer) : 0;
            return success;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool TryGetIntegrity(SafeAccessTokenHandle token, out int level)
    {
        level = 0;
        GetTokenInformation(token, 25 /* TokenIntegrityLevel */, 0, 0, out var size);
        if (size <= 0 || size > 4096) return false;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, size, out _)) return false;
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_MANDATORY_LABEL.Label.Sid
            if (sid == 0 || !IsValidSid(sid)) return false;
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) return false;
            level = Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(nint handle, int index, out int value, int length, out int needed);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, nint buffer, int length, out int needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidSid(nint sid);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")]
    private static extern nint GetSidSubAuthority(nint sid, uint index);
}
