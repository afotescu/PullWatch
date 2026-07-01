using Velopack;

namespace PullWatch;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        App app = new();
        app.InitializeComponent();
        app.Run();
    }
}
