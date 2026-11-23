using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using System.Runtime.CompilerServices;

namespace eShop.Basket.FunctionalTests;

public sealed class RedisFixture : IAsyncLifetime
{
    private readonly IHost _app;
    private readonly IResourceBuilder<RedisResource> _redis;
    private IConnectionMultiplexer? _connection;
    private string? _connectionString;
    private bool _started;

    public IConnectionMultiplexer Connection =>
        _connection ?? throw new InvalidOperationException("The Redis fixture has not been initialized.");

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await _app.StartAsync(timeout.Token);
        _started = true;
        var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceHealthyAsync(_redis.Resource.Name).WaitAsync(timeout.Token);

        _connectionString = await _redis.Resource.ConnectionStringExpression.GetValueAsync(timeout.Token);
        _connection = await ConnectionMultiplexer.ConnectAsync(_connectionString!).WaitAsync(TimeSpan.FromSeconds(15));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_connection is not null)
            {
                await _connection.CloseAsync().WaitAsync(TimeSpan.FromSeconds(30));
                _connection.Dispose();
            }

            if (_started)
            {
                await _app.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
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
}

internal static class RedisFixturePlatformSetup
{
    [ModuleInitializer]
    internal static void ConfigureAspireDcpForWindows()
    {
        if (!OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPIRE_DCP_PATH")))
        {
            return;
        }

        var packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(packageRoot))
        {
            packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        var packageVersions = Path.Combine(packageRoot, "aspire.hosting.orchestration.win-x64");
        if (!Directory.Exists(packageVersions))
        {
            return;
        }

        var dcpToolsPath = Directory.EnumerateDirectories(packageVersions)
            .Select(versionPath => Path.Combine(versionPath, "tools"))
            .FirstOrDefault(toolsPath => File.Exists(Path.Combine(toolsPath, "dcp.exe")));

        if (dcpToolsPath is not null)
        {
            Environment.SetEnvironmentVariable("ASPIRE_DCP_PATH", dcpToolsPath);
        }
    }
}
