using Aspire.Hosting;
#nullable enable
using Aspire.Hosting.ApplicationModel;
using System.IO;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace eShop.Ordering.FunctionalTests;

public sealed class OrderingApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly IHost _app;

    public IResourceBuilder<PostgresServerResource> Postgres { get; private set; }
    public IResourceBuilder<PostgresServerResource> IdentityDB { get; private set; }
    public IResourceBuilder<ProjectResource> IdentityApi { get; private set; }

    private string? _postgresConnectionString;

    public OrderingApiFixture()
    {
        var options = new DistributedApplicationOptions { AssemblyName = typeof(OrderingApiFixture).Assembly.FullName, DisableDashboard = true };
        var appBuilder = DistributedApplication.CreateBuilder(options);
        Postgres = appBuilder.AddPostgres("OrderingDB");
        IdentityDB = appBuilder.AddPostgres("IdentityDB");
        IdentityApi = appBuilder.AddProject<Projects.Identity_API>("identity-api").WithReference(IdentityDB);
        _app = appBuilder.Build();
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { $"ConnectionStrings:{Postgres.Resource.Name}", _postgresConnectionString },
                { "Identity:Url", IdentityApi.GetEndpoint("http").Url }
            });
        });
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new AutoAuthorizeStartupFilter());
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
        _postgresConnectionString = await Postgres.Resource.GetConnectionStringAsync();
    }

    public WebApplicationFactory<Program> CreateOrderingApiFactory(CheckoutRequestGate? gate = null) =>
        new RestartableOrderingApiFactory(
            _postgresConnectionString ?? throw new InvalidOperationException("The Ordering database is not initialized."),
            IdentityApi.GetEndpoint("http").Url,
            gate);

    public sealed class CheckoutRequestGate
    {
        public TaskCompletionSource RequestCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RestartableOrderingApiFactory(string postgresConnectionString, string identityUrl, CheckoutRequestGate? gate) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:OrderingDB", postgresConnectionString },
                { "Identity:Url", identityUrl }
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new AutoAuthorizeStartupFilter(gate)));
        }
    }

    private class AutoAuthorizeStartupFilter(CheckoutRequestGate? gate = null) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseMiddleware<AutoAuthorizeMiddleware>();
                if (gate is not null)
                {
                    builder.Use(async (context, next) =>
                    {
                        if (HttpMethods.IsPost(context.Request.Method)
                            && context.Request.Path.StartsWithSegments("/api/orders")
                            && context.Request.Headers.ContainsKey("x-test-hold-checkout"))
                        {
                            var responseBody = context.Response.Body;
                            await using var bufferedBody = new MemoryStream();
                            context.Response.Body = bufferedBody;
                            try
                            {
                                await next(context);
                                gate.RequestCommitted.TrySetResult();
                                await gate.Release.Task.WaitAsync(context.RequestAborted);
                                bufferedBody.Position = 0;
                                await bufferedBody.CopyToAsync(responseBody, context.RequestAborted);
                            }
                            finally
                            {
                                context.Response.Body = responseBody;
                            }

                            return;
                        }

                        await next(context);
                    });
                }
                next(builder);
            };
        }
    }
}
