using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace eShop.Basket.FunctionalTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly IHost _app;
    private readonly IResourceBuilder<RedisResource> _redis;
    private IConnectionMultiplexer? _connection;

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await _app.StartAsync(timeout.Token);
            var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();
            await notifications.WaitForResourceHealthyAsync(_redis.Resource.Name, timeout.Token);

            var connectionString = await _redis.Resource.ConnectionStringExpression.GetValueAsync(timeout.Token);
            _connection = await ConnectionMultiplexer.ConnectAsync(connectionString!).WaitAsync(timeout.Token);
        }
        catch
        {
            await DisposeHostAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            _connection.Dispose();
        }

        await DisposeHostAsync();
    }

    private async ValueTask DisposeHostAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // The bounded cleanup deadline prevents a failed container from hanging the test process.
        }
        if (_app is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        }
        else
        {
            _app.Dispose();
        }
    }
}
