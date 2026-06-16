using System.Runtime.InteropServices;

namespace PullWatch;

internal sealed class RecordingPrerequisiteChecker(
    Func<bool> isWindowsVersionSupported,
    Func<bool> is64BitProcess,
    Func<string, bool> canLoadNativeLibrary,
    Func<int> startMediaFoundation,
    Action stopMediaFoundation)
{
    private const int MfStartupFull = 0;
    private const int MfVersion = 0x00020070;

    public static RecordingPrerequisiteChecker Default { get; } = new(
        () => OperatingSystem.IsWindowsVersionAtLeast(6, 2),
        () => Environment.Is64BitProcess,
        CanLoadNativeLibrary,
        () => MediaFoundationNative.MFStartup(MfVersion, MfStartupFull),
        MediaFoundationNative.MFShutdown);

    public void EnsureSatisfied()
    {
        if (!isWindowsVersionSupported())
        {
            throw new RecordingPrerequisiteException(
                "Screen recording requires Windows 8 or newer.");
        }

        if (!is64BitProcess())
        {
            throw new RecordingPrerequisiteException(
                "This PullWatch build requires a 64-bit process. Use the win-x64 PullWatch build.");
        }

        EnsureVisualCRuntimeAvailable();
        EnsureMediaFoundationAvailable();
    }

    private void EnsureVisualCRuntimeAvailable()
    {
        if (canLoadNativeLibrary("vcruntime140.dll") &&
            canLoadNativeLibrary("msvcp140.dll"))
        {
            return;
        }

        throw new RecordingPrerequisiteException(
            "Screen recording requires Microsoft Visual C++ Redistributable 2015-2022 x64. " +
            "Install it, then restart PullWatch.");
    }

    private void EnsureMediaFoundationAvailable()
    {
        var started = false;

        try
        {
            var result = startMediaFoundation();

            if (result < 0)
            {
                throw new RecordingPrerequisiteException(
                    $"Screen recording requires Windows Media Foundation, but Media Foundation startup failed with HRESULT 0x{result:X8}. " +
                    "Install the Media Feature Pack for your Windows edition, then restart PullWatch.");
            }

            started = true;
        }
        catch (DllNotFoundException exception)
        {
            throw new RecordingPrerequisiteException(
                "Screen recording requires Windows Media Foundation. " +
                "Install the Media Feature Pack for your Windows edition, then restart PullWatch.",
                exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw new RecordingPrerequisiteException(
                "Screen recording requires Windows Media Foundation, but this Windows installation does not expose the required Media Foundation APIs.",
                exception);
        }
        finally
        {
            if (started)
            {
                stopMediaFoundation();
            }
        }
    }

    private static bool CanLoadNativeLibrary(string libraryName)
    {
        try
        {
            if (!NativeLibrary.TryLoad(libraryName, out var handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static partial class MediaFoundationNative
    {
        [DllImport("mfplat.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int MFStartup(int version, int flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern void MFShutdown();
    }
}
