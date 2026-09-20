namespace ReplyFive.Desktop.Platform;

public static class PlatformFactory
{
    public static IPlatform Create()
    {
#if WINDOWS
        if (OperatingSystem.IsWindows()) return new Windows.WindowsPlatform();
#endif
        if (OperatingSystem.IsLinux()) return new Linux.LinuxPlatform();
        if (OperatingSystem.IsMacOS()) return new Mac.MacDevPlatform();
        throw new PlatformNotSupportedException();
    }
}
