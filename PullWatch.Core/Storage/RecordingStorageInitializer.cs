using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PullWatch;

internal interface IRecordingStorageInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

public sealed class RecordingStorageInitializer(
    SqliteConnectionFactory connectionFactory,
    ILoggerFactory loggerFactory
) : IRecordingStorageInitializer
{
    private readonly SqliteConnectionFactory _connectionFactory =
        connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    private readonly ILoggerFactory _loggerFactory =
        loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _connectionFactory.EnsureDatabaseDirectory();

        using var serviceProvider = CreateServiceProvider();
        using var scope = serviceProvider.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();

        return Task.CompletedTask;
    }

    private ServiceProvider CreateServiceProvider()
    {
        return new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner =>
                runner
                    .AddSQLite()
                    .WithGlobalConnectionString(_connectionFactory.ConnectionString)
                    .ScanIn(typeof(CreateRecordings).Assembly)
                    .For.Migrations()
            )
            .AddLogging(logging =>
                logging.AddProvider(new ForwardingLoggerProvider(_loggerFactory))
            )
            .BuildServiceProvider(validateScopes: false);
    }

    private sealed class ForwardingLoggerProvider(ILoggerFactory loggerFactory) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return loggerFactory.CreateLogger(categoryName);
        }

        public void Dispose() { }
    }
}

internal sealed class NoOpRecordingStorageInitializer : IRecordingStorageInitializer
{
    public static NoOpRecordingStorageInitializer Instance { get; } = new();

    private NoOpRecordingStorageInitializer() { }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
