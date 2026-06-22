using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace PullWatch;

public sealed class WindowsStartupShortcut : IWindowsStartupShortcut
{
    private const string ShortcutFileName = "PullWatch.lnk";

    public Task SyncAsync(StartupSettings settings)
    {
        if (settings.StartWithWindows)
        {
            CreateShortcut();
        }
        else
        {
            DeleteShortcut();
        }

        return Task.CompletedTask;
    }

    private static void CreateShortcut()
    {
        var executablePath = GetExecutablePath();
        var shortcutPath = GetShortcutPath();
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        object? shellLinkObject = null;

        try
        {
            shellLinkObject = new ShellLink();
            var shellLink = (IShellLinkW)shellLinkObject;
            shellLink.SetPath(executablePath);
            shellLink.SetArguments(ApplicationLaunchArguments.WindowsStartup);
            shellLink.SetWorkingDirectory(
                Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory
            );
            shellLink.SetDescription("Start PullWatch when Windows starts.");
            shellLink.SetIconLocation(executablePath, 0);

            ((IPersistFile)shellLink).Save(shortcutPath, true);
        }
        finally
        {
            if (shellLinkObject is not null)
            {
                Marshal.FinalReleaseComObject(shellLinkObject);
            }
        }
    }

    private static void DeleteShortcut()
    {
        var shortcutPath = GetShortcutPath();

        if (File.Exists(shortcutPath))
        {
            File.Delete(shortcutPath);
        }
    }

    private static string GetShortcutPath()
    {
        var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

        if (string.IsNullOrWhiteSpace(startupFolder))
        {
            throw new InvalidOperationException("Windows Startup folder could not be found.");
        }

        return Path.Combine(startupFolder, ShortcutFileName);
    }

    private static string GetExecutablePath()
    {
        var executablePath = Environment.ProcessPath;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("PullWatch executable path could not be found.");
        }

        return Path.GetFullPath(executablePath);
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags
        );

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName,
            int cchMaxName
        );

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir,
            int cchMaxPath
        );

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs,
            int cchMaxPath
        );

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon
        );

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
