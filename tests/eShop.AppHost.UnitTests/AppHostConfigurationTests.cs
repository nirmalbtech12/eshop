using Aspire.Hosting;
using eShop.AppHost;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace eShop.AppHost.UnitTests;

[TestClass]
public class AppHostConfigurationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CatalogAccessibilityBrowserChecksPass()
    {
        var repo = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(repo, "package.json")) && Directory.GetParent(repo) is { } parent)
        {
            repo = parent.FullName;
        }

        Assert.IsTrue(File.Exists(Path.Combine(repo, "package.json")), "Repository root with package.json was not found.");
        var start = new ProcessStartInfo("npx")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("playwright");
        start.ArgumentList.Add("test");
        start.ArgumentList.Add("e2e/CatalogAccessibilityTest.spec.ts");
        using var process = Process.Start(start);
        Assert.IsNotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.CancellationToken);
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(TestContext.CancellationToken));
        Assert.AreEqual(0, process.ExitCode, $"Playwright browser check failed.\n{stdout.Result}\n{stderr.Result}");
    }

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
