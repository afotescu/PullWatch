namespace PullWatch;

public interface IUiDispatcher
{
    void Post(Action action);
}
