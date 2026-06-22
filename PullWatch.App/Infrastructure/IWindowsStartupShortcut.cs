namespace PullWatch;

public interface IWindowsStartupShortcut
{
    Task SyncAsync(StartupSettings settings);
}
