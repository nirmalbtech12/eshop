extern alias payment;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using payment::eShop.PaymentProcessor;
using payment::eShop.PaymentProcessor.Gateway;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Threading;

namespace eShop.Ordering.FunctionalTests;

public sealed class PaymentGatewayFixture : IAsyncLifetime
{
    private readonly IHost app;
    private readonly IResourceBuilder<PostgresServerResource> postgres;
    public string ConnectionString { get; private set; }

    public PaymentGatewayFixture()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { AssemblyName = typeof(PaymentGatewayFixture).Assembly.FullName, DisableDashboard = true });
        postgres = builder.AddPostgres("payment-test");
        app = builder.Build();
    }
    public async ValueTask InitializeAsync()
    {
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(180));
        ConnectionString = await postgres.Resource.GetConnectionStringAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await using var source = NpgsqlDataSource.Create(ConnectionString);
        using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                await new SimulatedPaymentGateway(source, OptionsMonitor(new PaymentOptions())).InitializeAsync(readiness.Token);
                break;
            }
            catch (NpgsqlException) when (!readiness.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), readiness.Token);
            }
        }
    }
    public static IOptionsMonitor<PaymentOptions> OptionsMonitor(PaymentOptions value) => new FixedOptions(value);
    private sealed class FixedOptions(PaymentOptions value) : IOptionsMonitor<PaymentOptions>
    {
        public PaymentOptions CurrentValue => value;
        public PaymentOptions Get(string name) => value;
        public IDisposable OnChange(Action<PaymentOptions, string> listener) => null;
    }
    public async ValueTask DisposeAsync()
    {
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (app is IAsyncDisposable disposable) await disposable.DisposeAsync(); else app.Dispose();
    }
}
