using PullWatch;

var logsDirectory = @"E:\World of Warcraft\_retail_\Logs";

using var cancellation = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var reader = new CombatLogReader(logsDirectory);
var eventHandler = new CombatLogEventHandler();

try
{
    await reader.ReadAsync(eventHandler.HandleAsync, cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
}
