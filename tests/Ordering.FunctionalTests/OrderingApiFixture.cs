using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using eShop.Ordering.Domain.AggregatesModel.OrderAggregate;
using eShop.Ordering.Domain.Seedwork;
using eShop.Ordering.Infrastructure.Repositories;

namespace eShop.Ordering.FunctionalTests;

public sealed class OrderingApiFixture : IAsyncLifetime
{
    private readonly IHost _app;
    private WebApplicationFactory<Program> _api;

    public IResourceBuilder<PostgresServerResource> Postgres { get; private set; }
    public IResourceBuilder<PostgresServerResource> IdentityDB { get; private set; }
    private IResourceWithConnectionString _eventBus;
    public IResourceBuilder<ProjectResource> IdentityApi { get; private set; }

    private string _postgresConnectionString;
    private string _eventBusConnectionString;
    public CheckoutRestartGate CheckoutRestart { get; } = new();

    public string PostgresConnectionString => _postgresConnectionString;

    public OrderingApiFixture()
    {
        var options = new DistributedApplicationOptions { AssemblyName = typeof(OrderingApiFixture).Assembly.FullName, DisableDashboard = true };
        var appBuilder = DistributedApplication.CreateBuilder(options);
        Postgres = appBuilder.AddPostgres("OrderingDB");
        IdentityDB = appBuilder.AddPostgres("IdentityDB");
        _eventBus = appBuilder.AddRabbitMQ("eventbus").Resource;
        IdentityApi = appBuilder.AddProject<Projects.Identity_API>("identity-api").WithReference(IdentityDB);
        _app = appBuilder.Build();
    }

    public WebApplicationFactory<Program> Api => _api;

    public async Task RestartApiAsync()
    {
        CheckoutRestart.Enabled = false;
        CheckoutRestart.Interrupt();
        await _api.DisposeAsync();
        _api = CreateApi();
    }

    private WebApplicationFactory<Program> CreateApi() => new OrderingApiFactory(_postgresConnectionString, _eventBusConnectionString, IdentityApi.GetEndpoint("http").Url, CheckoutRestart);

    private sealed class OrderingApiFactory(string postgresConnectionString, string eventBusConnectionString, string identityUrl, CheckoutRestartGate restartGate) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string>
                {
                    { "ConnectionStrings:OrderingDB", postgresConnectionString },
                    { "ConnectionStrings:eventbus", eventBusConnectionString },
                    { "Identity:Url", identityUrl }
                });
            });
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter>(new AutoAuthorizeStartupFilter());
                services.RemoveAll<IOrderRepository>();
                services.AddScoped<OrderRepository>();
                services.AddScoped<IOrderRepository>(provider => new RestartableOrderRepository(provider.GetRequiredService<OrderRepository>(), restartGate));
            });
            return base.CreateHost(builder);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _app.StopAsync();
        if (_app is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _app.Dispose();
        }
    }

    public async ValueTask InitializeAsync()
    {
        await _app.StartAsync();
        _postgresConnectionString = await Postgres.Resource.GetConnectionStringAsync();
        _eventBusConnectionString = await _eventBus.GetConnectionStringAsync();
        _api = CreateApi();
    }

    private class AutoAuthorizeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseMiddleware<AutoAuthorizeMiddleware>();
                next(builder);
            };
        }
    }

    public sealed class CheckoutRestartGate
    {
        public bool Enabled { get; set; }
        public TaskCompletionSource OrderAdded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _interrupt = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Interrupt() => _interrupt.TrySetException(new CheckoutInterruptedException());

        public void PauseAfterOrderAdded()
        {
            OrderAdded.TrySetResult();
            _interrupt.Task.GetAwaiter().GetResult();
        }
    }

    private sealed class CheckoutInterruptedException() : OperationCanceledException("Checkout interrupted by API restart.");

    private sealed class RestartableOrderRepository(OrderRepository inner, CheckoutRestartGate restartGate) : IOrderRepository
    {
        public Order Add(Order order)
        {
            var added = inner.Add(order);
            if (restartGate.Enabled && order.OrderItems.Any(item => item.ProductName == "Restart Test"))
            {
                restartGate.PauseAfterOrderAdded();
            }
            return added;
        }

        public void Update(Order order) => inner.Update(order);
        public Task<Order> GetAsync(int orderId) => inner.GetAsync(orderId);
        public IUnitOfWork UnitOfWork => inner.UnitOfWork;
    }

}
