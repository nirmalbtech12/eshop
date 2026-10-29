using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace eShop.AppHost.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class AppHostReadinessTests
{
    [TestMethod]
    public async Task CheckoutConsumersWaitForUnhealthyDependencies()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.eShop_AppHost>(timeout.Token);
        builder.Configuration["UseFoundry"] = "false";

        var dependencyGates = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["identitydb"] = false,
            ["catalogdb"] = false,
            ["orderingdb"] = false,
            ["redis"] = false,
            ["eventbus"] = false,
            ["identity-api"] = false,
            ["basket-api"] = false,
            ["catalog-api"] = false,
            ["ordering-api"] = false
        };
        foreach (var dependency in dependencyGates.Keys)
        {
            var healthCheckName = $"controlled-{dependency}";
            RegisterHealthGate(builder, healthCheckName, () => dependencyGates[dependency]);
            builder.CreateResourceBuilder<IResource>(dependency).WithHealthCheck(healthCheckName);
        }

        await using var app = await builder.BuildAsync(timeout.Token);

        var startup = app.StartAsync(timeout.Token);
        try
        {
            var consumerReadiness = new[] { "identity-api", "basket-api", "catalog-api", "ordering-api", "webapp" }
                .ToDictionary(resource => resource, resource => app.ResourceNotifications.WaitForResourceHealthyAsync(resource, timeout.Token));
            await Task.Delay(TimeSpan.FromSeconds(3), timeout.Token);
            AssertUnavailable("identity-api", consumerReadiness);
            AssertUnavailable("basket-api", consumerReadiness);
            AssertUnavailable("catalog-api", consumerReadiness);
            AssertUnavailable("ordering-api", consumerReadiness);
            AssertUnavailable("webapp", consumerReadiness);

            ReleaseGate("identitydb", dependencyGates);
            AssertUnavailable("identity-api", consumerReadiness);
            ReleaseGate("identity-api", dependencyGates);
            await consumerReadiness["identity-api"].WaitAsync(timeout.Token);
            AssertUnavailable("webapp", consumerReadiness);
            ReleaseGate("redis", dependencyGates);
            AssertUnavailable("basket-api", consumerReadiness);
            ReleaseGate("catalogdb", dependencyGates);
            AssertUnavailable("catalog-api", consumerReadiness);
            ReleaseGate("orderingdb", dependencyGates);
            AssertUnavailable("ordering-api", consumerReadiness);
            ReleaseGate("eventbus", dependencyGates);
            AssertUnavailable("basket-api", consumerReadiness);
            AssertUnavailable("catalog-api", consumerReadiness);
            AssertUnavailable("ordering-api", consumerReadiness);
            ReleaseGate("basket-api", dependencyGates);
            await consumerReadiness["basket-api"].WaitAsync(timeout.Token);
            AssertUnavailable("webapp", consumerReadiness);
            ReleaseGate("catalog-api", dependencyGates);
            await consumerReadiness["catalog-api"].WaitAsync(timeout.Token);
            AssertUnavailable("webapp", consumerReadiness);
            ReleaseGate("ordering-api", dependencyGates);
            await consumerReadiness["ordering-api"].WaitAsync(timeout.Token);
            await consumerReadiness["webapp"].WaitAsync(timeout.Token);
            await startup;
            Assert.IsTrue(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested);
        }
        finally
        {
            timeout.Cancel();
            try { await startup; } catch (OperationCanceledException) { }
        }
    }

    private static void ReleaseGate(string dependency, IDictionary<string, bool> dependencyGates)
    {
        dependencyGates[dependency] = true;
    }

    private static void AssertUnavailable(string resource, IReadOnlyDictionary<string, Task<ResourceEvent>> readiness)
    {
        Assert.IsFalse(readiness[resource].IsCompleted, $"{resource} became healthy before all configured dependency health signals were healthy.");
    }

    private static void RegisterHealthGate(IDistributedApplicationTestingBuilder builder, string name, Func<bool> isHealthy)
    {
        var hostingAssembly = typeof(DistributedApplication).Assembly;
        var exports = hostingAssembly.GetType("Aspire.Hosting.Ats.HealthCheckExports", throwOnError: true)!;
        var addHealthCheck = exports.GetMethod("AddHealthCheck", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var resultType = hostingAssembly.GetType("Aspire.Hosting.Ats.HealthCheckResult", throwOnError: true)!;
        var callbackFactory = typeof(AppHostReadinessTests).GetMethod(nameof(CreateHealthGateCallback), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var callback = (Delegate)callbackFactory.MakeGenericMethod(resultType).Invoke(null, [isHealthy])!;
        addHealthCheck.Invoke(null, [(IDistributedApplicationBuilder)builder, name, callback]);
    }

    private static Delegate CreateHealthGateCallback<TResult>(Func<bool> isHealthy) where TResult : class, new()
    {
        return new Func<Task<TResult>>(() =>
        {
            var result = new TResult();
            var status = typeof(TResult).GetProperty("Status")!;
            status.SetValue(result, Enum.Parse(status.PropertyType, isHealthy() ? "Healthy" : "Unhealthy"));
            return Task.FromResult(result);
        });
    }
}
