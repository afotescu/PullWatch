using Velopack;

namespace PullWatch;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        SemanticVersion? restartedVersion = null;
        VelopackApp.Build().OnRestarted(version => restartedVersion = version).Run();

        App app = new() { RestartedVersion = restartedVersion };
        app.InitializeComponent();
        app.Run();
    }
}
