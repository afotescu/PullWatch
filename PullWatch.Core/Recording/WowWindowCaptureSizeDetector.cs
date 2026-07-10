using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PullWatch;

public static class WowWindowCaptureSizeDetector
{
    private const string WowProcessName = "Wow";

    public static bool TryFindWowWindow(out Process wowProcess, out nint windowHandle)
    {
        var processes = Process.GetProcessesByName(WowProcessName);
        Process? selectedProcess = null;

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    windowHandle = process.MainWindowHandle;

                    if (windowHandle != nint.Zero)
                    {
                        selectedProcess = process;
                        wowProcess = selectedProcess;
                        return true;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited while it was being inspected.
                }
            }

            wowProcess = null!;
            windowHandle = nint.Zero;
            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!ReferenceEquals(process, selectedProcess))
                {
                    process.Dispose();
                }
            }
        }
    }

    public static bool TryGetCurrentCaptureSize(out VideoCaptureSize captureSize)
    {
        if (!TryFindWowWindow(out var wowProcess, out var windowHandle))
        {
            captureSize = default;
            return false;
        }

        using (wowProcess)
        {
            return TryGetCaptureSize(windowHandle, out captureSize);
        }
    }

    public static VideoCaptureSize GetCaptureSize(nint windowHandle)
    {
        if (TryGetCaptureSize(windowHandle, out var captureSize))
        {
            return captureSize;
        }

        throw new CaptureTargetUnavailableException(
            "Could not determine the World of Warcraft window size."
        );
    }

    private static bool TryGetCaptureSize(nint windowHandle, out VideoCaptureSize captureSize)
    {
        return TryGetPositiveSize(GetClientRect, windowHandle, out captureSize)
            || TryGetPositiveSize(GetWindowRect, windowHandle, out captureSize);
    }

    private static bool TryGetPositiveSize(
        TryGetNativeRect tryGetRect,
        nint windowHandle,
        out VideoCaptureSize captureSize
    )
    {
        if (tryGetRect(windowHandle, out var rect))
        {
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            if (width > 0 && height > 0)
            {
                captureSize = new VideoCaptureSize(width, height);
                return true;
            }
        }

        captureSize = default;
        return false;
    }

    private delegate bool TryGetNativeRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
#pragma warning disable CS0649
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
#pragma warning restore CS0649
}
