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
    private string? _connectionString;

    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");

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
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await _app.StartAsync(deadline.Token);
            _started = true;
            var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();
            await notifications.WaitForResourceHealthyAsync(_redis.Resource.Name, deadline.Token);

            _connectionString = await _redis.Resource.ConnectionStringExpression.GetValueAsync(deadline.Token);
            var options = ConfigurationOptions.Parse(_connectionString!);
            options.ConnectTimeout = 30_000;
            options.AbortOnConnectFail = true;
            _connection = await ConnectionMultiplexer.ConnectAsync(options);
        }
        catch
        {
            await DisposeAsync();
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

        if (_started)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await _app.StopAsync(deadline.Token);
            _started = false;
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
