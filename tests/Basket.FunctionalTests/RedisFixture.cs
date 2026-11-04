using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace eShop.Basket.FunctionalTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly IHost _app;
    private readonly IResourceBuilder<RedisResource> _redis;
    private IConnectionMultiplexer? _connection;
    private string? _connectionString;

    public IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");

    public RedisFixture()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(RedisFixture).Assembly.FullName,
            DisableDashboard = true
        });
        _redis = builder.AddRedis("redis");
        _app = builder.Build();
    }

    public async ValueTask InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await _app.StartAsync(timeout.Token);
        var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceHealthyAsync(_redis.Resource.Name).WaitAsync(timeout.Token);

        _connectionString = await _redis.Resource.ConnectionStringExpression.GetValueAsync(timeout.Token);
        _connection = await ConnectionMultiplexer.ConnectAsync(_connectionString!).WaitAsync(timeout.Token);
    }

    public async Task<IConnectionMultiplexer> ConnectFreshAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = _connectionString ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");
        return await ConnectionMultiplexer.ConnectAsync(connectionString).WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30));
            _connection.Dispose();
        }

        await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (_app is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _app.Dispose();
        }
    }
}
