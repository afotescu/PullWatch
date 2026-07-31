using System.Diagnostics;

namespace PullWatch;

public sealed class WindowsExternalLinkLauncher : IExternalLinkLauncher
{
    public void Open(Uri uri)
    {
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
