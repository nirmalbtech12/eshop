using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using eShop.AppHost;
using Microsoft.Extensions.Configuration;

namespace eShop.AppHost.UnitTests;

[TestClass]
[DoNotParallelize]
public class AppHostConfigurationTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("", false)]
    [DataRow("invalid", false)]
    [DataRow("false", false)]
    [DataRow("true", true)]
    public void FoundryFlagUsesSafeOptInDefault(string? configuredValue, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["UseFoundry"] = configuredValue })
            .Build();

        Assert.AreEqual(expected, Extensions.IsFoundryEnabled(configuration));
    }

    [TestMethod]
    public void FoundryExtensionAddsExpectedDeployments()
    {
        var builder = CreateBuilder();
        var catalog = builder.AddProject("catalog-api", ProjectPath("Catalog.API", "Catalog.API.csproj"));
        var webApp = builder.AddProject("webapp", ProjectPath("WebApp", "WebApp.csproj"));

        builder.AddFoundry(catalog, webApp);

        CollectionAssert.IsSubsetOf(
            new[] { "foundry", "chatModel", "textEmbeddingModel" },
            builder.Resources.Select(resource => resource.Name).ToArray());
    }

    [TestMethod]
    public void OllamaExtensionAddsExpectedModels()
    {
        var builder = CreateBuilder();
        var catalog = builder.AddProject("catalog-api", ProjectPath("Catalog.API", "Catalog.API.csproj"));
        var webApp = builder.AddProject("webapp", ProjectPath("WebApp", "WebApp.csproj"));

        builder.AddOllama(catalog, webApp);

        CollectionAssert.IsSubsetOf(
            new[] { "ollama", "embedding", "chat" },
            builder.Resources.Select(resource => resource.Name).ToArray());
    }

    [TestMethod]
    public async Task CheckoutDependenciesWaitForHealth()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appHost = File.ReadAllText(Path.Combine(repositoryRoot, "src", "eShop.AppHost", "Program.cs"));
        var basket = appHost.IndexOf("var basketApi =", StringComparison.Ordinal);
        var catalog = appHost.IndexOf("var catalogApi =", StringComparison.Ordinal);
        var ordering = appHost.IndexOf("var orderingApi =", StringComparison.Ordinal);
        var webApp = appHost.IndexOf("var webApp =", StringComparison.Ordinal);
        var foundry = appHost.IndexOf("// Set UseFoundry", StringComparison.Ordinal);

        Assert.IsTrue(appHost[basket..catalog].Contains("WithHttpHealthCheck(\"/health\", endpointName: \"health\")", StringComparison.Ordinal));
        Assert.IsTrue(appHost[catalog..ordering].Contains("WithHttpHealthCheck(\"/health\")", StringComparison.Ordinal));
        Assert.IsTrue(appHost[webApp..foundry].Contains(".WaitFor(basketApi)", StringComparison.Ordinal));
        Assert.IsTrue(appHost[webApp..foundry].Contains(".WaitFor(catalogApi)", StringComparison.Ordinal));
        Assert.IsTrue(appHost[webApp..foundry].Contains(".WaitFor(orderingApi)", StringComparison.Ordinal));

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.eShop_AppHost>(timeout.Token);
        builder.CreateResourceBuilder<ProjectResource>("order-processor").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("payment-processor").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("webhooks-api").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("webhooksclient").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("ordering-api").WithExplicitStart();
        await using var app = await builder.BuildAsync(timeout.Token);
        var startup = app.StartAsync(timeout.Token);
        var webAppReady = app.ResourceNotifications.WaitForResourceHealthyAsync("webapp", timeout.Token);
        await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        Assert.IsFalse(webAppReady.IsCompleted, "WebApp became healthy before Ordering API was started and healthy.");

        var startCommand = typeof(KnownResourceCommands).GetFields()
            .Single(field => field.Name.Contains("Start", StringComparison.Ordinal))
            .GetValue(null) as string;
        Assert.IsNotNull(startCommand);
        var started = await app.ResourceCommands.ExecuteCommandAsync("ordering-api", startCommand, timeout.Token);
        Assert.IsTrue(started.Success, "Ordering API did not accept the start command.");
        await startup;
        await app.ResourceNotifications.WaitForResourceHealthyAsync("basket-api", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("catalog-api", timeout.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("ordering-api", timeout.Token);
        await webAppReady;
    }

    [TestMethod]
    public async Task LocalAspireApplicationStartsReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.eShop_AppHost>(timeout.Token);
        builder.CreateResourceBuilder<ProjectResource>("order-processor").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("payment-processor").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("webhooks-api").WithExplicitStart();
        builder.CreateResourceBuilder<ProjectResource>("webhooksclient").WithExplicitStart();
        await using var app = await builder.BuildAsync(timeout.Token);
        await app.StartAsync(timeout.Token);

        foreach (var resource in new[] { "basket-api", "catalog-api", "ordering-api", "webapp" })
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, timeout.Token);
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(AppHostConfigurationTests).Assembly.FullName,
            DisableDashboard = true
        });

    private static string ProjectPath(string directory, string project) =>
        Path.Combine(FindRepositoryRoot(), "src", directory, project);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eShop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the eShop repository root.");
    }
}
