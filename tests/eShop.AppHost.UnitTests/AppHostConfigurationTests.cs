using Aspire.Hosting;
using eShop.AppHost;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.Text;

namespace eShop.AppHost.UnitTests;

[TestClass]
public class AppHostConfigurationTests
{
    [TestMethod]
    public void ServiceHealthChecksGateConsumers()
    {
        var appHost = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "eShop.AppHost", "Program.cs"));

        Assert.Contains(".WithHttpHealthCheck(\"/health\")", appHost);
        Assert.Contains(".WaitFor(orderingApi)", appHost);
        Assert.Contains(".WaitFor(catalogApi)", appHost);
        Assert.Contains(".WaitFor(identityApi)", appHost);
        var webhooks = appHost.Split("var webHooksApi =")[1].Split("// Reverse proxies")[0];
        Assert.Contains(".WaitFor(identityApi)", webhooks);
        Assert.Contains("var basketApi = builder.AddProject<Projects.Basket_API>", appHost);
        Assert.Contains(".WithReference(redis).WaitFor(redis)", appHost);
        Assert.Contains("basketApi.WithHealthCheck(\"basket-api-http2\")", appHost);
        Assert.Contains("Version = HttpVersion.Version20", appHost);
        Assert.Contains(".WithReference(basketApi)\n    .WaitFor(basketApi)", appHost);
    }

    [TestMethod]
    public async Task LocalAspireRunReachesServiceReadiness()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appHostProject = Path.Combine(repositoryRoot, "src", "eShop.AppHost", "eShop.AppHost.csproj");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var runInfo = CreateAspireStartInfo(repositoryRoot,
            "run", "--non-interactive", "--no-build", "--apphost", appHostProject);
        runInfo.Environment["ESHOP_USE_HTTP_ENDPOINTS"] = "1";
        using var appHost = Process.Start(runInfo)
            ?? throw new InvalidOperationException("Could not start the Aspire CLI.");
        var runOutput = new StringBuilder();
        var dashboardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        appHost.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                runOutput.AppendLine(eventArgs.Data);
                if (eventArgs.Data.Contains("Dashboard:", StringComparison.Ordinal))
                {
                    dashboardStarted.TrySetResult();
                }
            }
        };
        appHost.BeginOutputReadLine();
        var standardError = appHost.StandardError.ReadToEndAsync();
        try
        {
            var startupTask = await Task.WhenAny(dashboardStarted.Task, appHost.WaitForExitAsync(timeout.Token));
            Assert.AreEqual(dashboardStarted.Task, startupTask,
                $"Aspire CLI exited before the AppHost dashboard started (exit {appHost.ExitCode}).\n{runOutput}\n{await standardError}");

            foreach (var resource in new[] { "postgres", "eventbus", "identity-api", "basket-api", "catalog-api", "ordering-api", "webhooks-api", "webapp" })
            {
                var result = await RunAspireCommandAsync(repositoryRoot, timeout.Token,
                    "wait", resource, "--timeout", "45", "--apphost", appHostProject);
                Assert.AreEqual(0, result.ExitCode,
                    $"Aspire resource '{resource}' did not become healthy.\n{result.StandardOutput}\n{result.StandardError}");
            }
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await RunAspireCommandAsync(repositoryRoot, stopTimeout.Token,
                    "stop", "--force", "--non-interactive", "--apphost", appHostProject);
            }
            catch
            {
                if (!appHost.HasExited)
                {
                    appHost.Kill(entireProcessTree: true);
                }
            }
            try
            {
                await appHost.WaitForExitAsync(stopTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (!appHost.HasExited)
                {
                    appHost.Kill(entireProcessTree: true);
                    await appHost.WaitForExitAsync();
                }
            }
            await standardError;
        }
    }

    private static ProcessStartInfo CreateAspireStartInfo(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("aspire")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunAspireCommandAsync(
        string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        using var process = Process.Start(CreateAspireStartInfo(workingDirectory, arguments))
            ?? throw new InvalidOperationException("Could not start the Aspire CLI command.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await standardOutput, await standardError);
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
