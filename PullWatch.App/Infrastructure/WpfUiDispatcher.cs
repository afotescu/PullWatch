using System.Windows.Threading;

namespace PullWatch;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        dispatcher.BeginInvoke(action);
    }
}
