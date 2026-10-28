using Aspire.Hosting;
using eShop.IntegrationEventLogEF;
using eShop.IntegrationEventLogEF.Services;
using eShop.Ordering.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using eShop.EventBus.Events;
using Aspire.Hosting.ApplicationModel;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace eShop.Ordering.FunctionalTests;

public sealed class OrderingApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly IHost _app;

    public IResourceBuilder<PostgresServerResource> Postgres { get; private set; }
    public IResourceBuilder<PostgresServerResource> IdentityDB { get; private set; }
    public IResourceBuilder<ProjectResource> IdentityApi { get; private set; }

    private string _postgresConnectionString;
    public string OrderingConnectionString => _postgresConnectionString;
    public string EventBusConnectionString { get; private set; }
    public string IdentityUrl => IdentityApi.GetEndpoint("http").Url;
    private readonly IResourceBuilder<RabbitMQServerResource> _rabbit;

    public OrderingApiFixture()
    {
        var options = new DistributedApplicationOptions { AssemblyName = typeof(OrderingApiFixture).Assembly.FullName, DisableDashboard = true };
        var appBuilder = DistributedApplication.CreateBuilder(options);
        Postgres = appBuilder.AddPostgres("OrderingDB");
        _rabbit = appBuilder.AddRabbitMQ("eventbus");
        IdentityDB = appBuilder.AddPostgres("IdentityDB");
        IdentityApi = appBuilder.AddProject<Projects.Identity_API>("identity-api").WithReference(IdentityDB);
        _app = appBuilder.Build();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string>
            {
                { $"ConnectionStrings:{Postgres.Resource.Name}", _postgresConnectionString },
                { "Identity:Url", IdentityApi.GetEndpoint("http").Url }
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new AutoAuthorizeStartupFilter());
            services.AddTransient<IIntegrationEventLogService, TestEventLogService>();
        });
        return base.CreateHost(builder);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
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
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
        var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.WaitForResourceHealthyAsync(Postgres.Resource.Name, timeout.Token);
        await notifications.WaitForResourceHealthyAsync(_rabbit.Resource.Name, timeout.Token);
        _postgresConnectionString = await Postgres.Resource.GetConnectionStringAsync();
        EventBusConnectionString = await _rabbit.Resource.ConnectionStringExpression.GetValueAsync(default);
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
}

// WebApplicationFactory runs under the test runner, whose entry assembly does not
// contain the ordering event types used by the production event-log service.
internal sealed class TestEventLogService(OrderingContext context) : IIntegrationEventLogService
{
    private readonly IntegrationEventLogService<OrderingContext> inner = new(context);

    public async Task<IEnumerable<IntegrationEventLogEntry>> RetrieveEventLogsPendingToPublishAsync(Guid transactionId)
    {
        var entries = await context.Set<IntegrationEventLogEntry>()
            .Where(e => e.TransactionId == transactionId && e.State == EventStateEnum.NotPublished)
            .OrderBy(e => e.CreationTime).ToListAsync();
        var assembly = typeof(eShop.Ordering.API.Application.IntegrationEvents.OrderingIntegrationEventService).Assembly;
        return entries.Select(e => e.DeserializeJsonContent(assembly.GetType(e.EventTypeName)!));
    }
    public Task SaveEventAsync(IntegrationEvent value, IDbContextTransaction transaction) => inner.SaveEventAsync(value, transaction);
    public Task MarkEventAsPublishedAsync(Guid id) => inner.MarkEventAsPublishedAsync(id);
    public Task MarkEventAsInProgressAsync(Guid id) => inner.MarkEventAsInProgressAsync(id);
    public Task MarkEventAsFailedAsync(Guid id) => inner.MarkEventAsFailedAsync(id);
}
