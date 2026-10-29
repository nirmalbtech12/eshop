using Aspire.Hosting;
using Aspire.Hosting.Testing;
using System.Net;

namespace eShop.AppHost.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class AppHostSmokeTests
{
    [TestMethod]
    public async Task LocalAspireCheckoutDependenciesBecomeHealthy()
    {
        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.eShop_AppHost>(startupTimeout.Token);
        builder.Configuration["UseFoundry"] = "false";
        await using var app = await builder.BuildAsync(startupTimeout.Token);
        await app.StartAsync(startupTimeout.Token);

        foreach (var resource in new[] { "ordering-api", "catalog-api", "webapp" })
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, startupTimeout.Token);
        }

        using var webClient = app.CreateHttpClient("webapp");
        using var webResponse = await webClient.GetAsync("/", startupTimeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, webResponse.StatusCode);

        using var catalogClient = app.CreateHttpClient("catalog-api");
        using var catalogResponse = await catalogClient.GetAsync(
            "/api/catalog/items?pageIndex=0&pageSize=5&api-version=1.0", startupTimeout.Token);
        Assert.AreEqual(HttpStatusCode.OK, catalogResponse.StatusCode);
    }
}
