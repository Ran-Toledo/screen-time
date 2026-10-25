namespace ScreenTime.Tracker;

/// <summary>When the user last touched the keyboard or mouse (R4).</summary>
internal static class IdleProbe
{
    public static long GetLastInputUtc(long nowUtc)
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info))
            return nowUtc;

        // Both are 32-bit millisecond tick counts; unsigned subtraction survives the 49.7-day wrap.
        var idleMs = unchecked((uint)Environment.TickCount - info.dwTime);
        return nowUtc - idleMs / 1000;
    }
}
