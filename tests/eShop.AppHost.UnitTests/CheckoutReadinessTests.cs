using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace eShop.AppHost.UnitTests;

[TestClass]
[DoNotParallelize]
public class CheckoutReadinessTests
{
    [TestMethod]
    public async Task CheckoutConsumerWaitsForHealthyDependency()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.eShop_AppHost>(timeout.Token);
        IsolateContainers(builder);
        var healthy = false;
        builder.Services.AddHealthChecks().AddCheck("checkout-test-gate", () =>
            Volatile.Read(ref healthy) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
        var basket = (ProjectResource)builder.Resources.Single(r => r.Name == "basket-api");
        builder.CreateResourceBuilder(basket).WithHealthCheck("checkout-test-gate");
        await using var app = await builder.BuildAsync(timeout.Token);
        var startup = app.StartAsync(timeout.Token);
        try
        {
            await app.ResourceNotifications.WaitForResourceAsync("basket-api", KnownResourceStates.Running, timeout.Token);
            foreach (var dependency in new[] { "identity-api", "catalog-api", "ordering-api" })
            {
                await app.ResourceNotifications.WaitForResourceHealthyAsync(dependency, timeout.Token);
            }
            using var blocked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            blocked.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await app.ResourceNotifications.WaitForResourceAsync("webapp", KnownResourceStates.Running, blocked.Token);
                Assert.Fail("The checkout consumer started while its dependency was unhealthy.");
            }
            catch (OperationCanceledException) when (blocked.IsCancellationRequested && !timeout.IsCancellationRequested)
            {
                // The consumer stayed stopped throughout the observation window.
            }
            Volatile.Write(ref healthy, true);
            await startup;
            await app.ResourceNotifications.WaitForResourceHealthyAsync("webapp", timeout.Token);
        }
        finally
        {
            timeout.Cancel();
            try { await startup; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task LocalAspireCheckoutResourcesBecomeHealthy()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.eShop_AppHost>(timeout.Token);
        IsolateContainers(builder);
        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);
        foreach (var resource in new[] { "identity-api", "basket-api", "catalog-api", "ordering-api", "webapp" })
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, timeout.Token);
        }
        using var client = app.CreateHttpClient("webapp");
        using var response = await client.GetAsync("/", timeout.Token);
        response.EnsureSuccessStatusCode();
    }
    private static void IsolateContainers(IDistributedApplicationBuilder builder)
    {
        foreach (var resource in builder.Resources.OfType<ContainerResource>())
        {
            builder.CreateResourceBuilder(resource).WithLifetime(ContainerLifetime.Session);
        }
    }
}

