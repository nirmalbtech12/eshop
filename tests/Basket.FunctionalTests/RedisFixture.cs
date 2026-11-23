using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace eShop.Basket.FunctionalTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly IHost _app;
    private readonly IResourceBuilder<RedisResource> _redis;
    private IConnectionMultiplexer? _connection;
    private bool _started;

    public IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");
    public string ConnectionString { get; private set; } = null!;

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
        await _app.StartAsync().WaitAsync(TimeSpan.FromSeconds(90));
        _started = true;
        var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceHealthyAsync(_redis.Resource.Name).WaitAsync(TimeSpan.FromSeconds(90));

        ConnectionString = (await _redis.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None))!;
        _connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString).WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _connection.Dispose();
        }

        if (_started)
        {
            await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }

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
