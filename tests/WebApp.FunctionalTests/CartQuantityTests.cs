using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace eShop.WebApp.FunctionalTests;

[TestClass]
[DoNotParallelize]
public sealed class CartQuantityTests
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] ScenarioTitles =
    [
        "typed quantity survives immediate navigation and reload",
        "zero removes only the selected line and totals remain consistent",
        "invalid quantity submissions preserve the basket"
    ];
    private static readonly object ScenarioRunLock = new();
    private static Task<HashSet<string>>? scenarioRun;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TypedQuantitySurvivesImmediateNavigationAndReload()
        => RunBrowserScenarioAsync("typed quantity survives immediate navigation and reload");

    [TestMethod]
    public Task ZeroRemovesOnlySelectedLineAndTotalsRemainConsistent()
        => RunBrowserScenarioAsync("zero removes only the selected line and totals remain consistent");

    [TestMethod]
    public Task InvalidQuantitySubmissionsPreserveBasket()
        => RunBrowserScenarioAsync("invalid quantity submissions preserve the basket");

    private async Task RunBrowserScenarioAsync(string scenarioTitle)
    {
        Task<HashSet<string>> run;
        lock (ScenarioRunLock)
        {
            scenarioRun ??= RunBrowserScenariosAsync();
            run = scenarioRun;
        }

        var passedScenarios = await run;
        Assert.IsTrue(
            passedScenarios.Contains(scenarioTitle),
            $"The selected Playwright invocation did not report '{scenarioTitle}' as passed.");
    }

    private async Task<HashSet<string>> RunBrowserScenariosAsync()
    {
        var repositoryRoot = FindRepositoryRoot();
        var runDirectory = Path.Combine(
            repositoryRoot,
            "artifacts",
            "test-results",
            "webapp-functional",
            $"cart-quantity-{Guid.NewGuid():N}");
        var playwrightOutput = Path.Combine(runDirectory, "playwright-output");
        Directory.CreateDirectory(playwrightOutput);
        await WaitForCartPortToBeAvailableAsync();

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "node.exe" : "node",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(Path.Combine(repositoryRoot, "node_modules", "@playwright", "test", "cli.js"));
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add("e2e/CartTest.spec.ts");
        startInfo.ArgumentList.Add("--project=e2e tests logged in");
        startInfo.ArgumentList.Add("--grep");
        startInfo.ArgumentList.Add($".*({string.Join('|', ScenarioTitles.Select(RegexEscape))})$");
        startInfo.ArgumentList.Add("--reporter=json");
        startInfo.ArgumentList.Add("--retries=0");
        startInfo.ArgumentList.Add("--workers=1");
        startInfo.ArgumentList.Add($"--output={playwrightOutput}");

        startInfo.Environment["CI"] = "true";
        startInfo.Environment["ESHOP_USE_HTTP_ENDPOINTS"] = "1";
        startInfo.Environment["USERNAME1"] = "bob";
        startInfo.Environment["PASSWORD"] = "Pass123$";

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            Assert.Fail("Could not start the repository-local Playwright CLI.");
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token,
            TestContext.CancellationToken);

        try
        {
            await process.WaitForExitAsync(linkedCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StopOwnedProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
            var timedOutOutput = await standardOutputTask;
            var timedOutError = await standardErrorTask;
            await SaveLogsAsync(runDirectory, timedOutOutput, timedOutError);

            if (TestContext.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            Assert.Fail($"Playwright scenario exceeded {ProcessTimeout}. Logs: {runDirectory}");
        }
        finally
        {
            if (!process.HasExited)
            {
                StopOwnedProcess(process);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }

        var standardOutput = await standardOutputTask;
        var standardError = await standardErrorTask;
        await SaveLogsAsync(runDirectory, standardOutput, standardError);

        if (process.ExitCode != 0)
        {
            Assert.Fail($"Playwright exited with code {process.ExitCode}. {SummarizeReport(standardOutput)} {LastLines(standardError)} Logs: {runDirectory}");
        }

        return AssertScenariosPassed(standardOutput, runDirectory);
    }

    private static HashSet<string> AssertScenariosPassed(string reportJson, string reportDirectory)
    {
        JsonDocument report;
        try
        {
            report = JsonDocument.Parse(reportJson);
        }
        catch (JsonException exception)
        {
            Assert.Fail($"Playwright did not produce valid JSON output ({exception.Message}). Logs: {reportDirectory}");
            return [];
        }

        using (report)
        {
            var expectedTitles = new HashSet<string>(ScenarioTitles, StringComparer.Ordinal);
            var passedTitles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var spec in EnumerateSpecs(report.RootElement)
                .Where(spec => spec.TryGetProperty("title", out var title) && expectedTitles.Contains(title.GetString() ?? string.Empty)))
            {
                var title = spec.GetProperty("title").GetString()!;
                var results = EnumerateArrayProperty(spec, "tests")
                    .Where(test => test.TryGetProperty("projectName", out var project) && project.GetString() == "e2e tests logged in")
                    .SelectMany(test => EnumerateArrayProperty(test, "results"))
                    .ToArray();
                Assert.AreEqual(1, results.Length, $"Expected exactly one '{title}' result in the authenticated browser project. Logs: {reportDirectory}");
                Assert.AreEqual("passed", results[0].GetProperty("status").GetString(), $"Browser scenario '{title}' did not pass. Logs: {reportDirectory}");
                passedTitles.Add(title);
            }

            Assert.IsTrue(expectedTitles.SetEquals(passedTitles), $"The browser report did not include all required cart scenarios. Logs: {reportDirectory}");
            return passedTitles;
        }
    }

    private static IEnumerable<JsonElement> EnumerateArrayProperty(JsonElement node, string propertyName)
    {
        if (node.TryGetProperty(propertyName, out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                yield return value;
            }
        }
    }

    private static string SummarizeReport(string reportJson)
    {
        try
        {
            using var report = JsonDocument.Parse(reportJson);
            var expectedTitles = new HashSet<string>(ScenarioTitles, StringComparer.Ordinal);
            var specs = EnumerateSpecs(report.RootElement)
                .Where(spec => spec.TryGetProperty("title", out var title) && expectedTitles.Contains(title.GetString() ?? string.Empty))
                .ToArray();
            if (specs.Length == 0)
            {
                return "No required cart scenarios were reported.";
            }

            var errors = specs
                .SelectMany(spec => EnumerateArrayProperty(spec, "tests"))
                .Where(test => test.TryGetProperty("projectName", out var project) && project.GetString() == "e2e tests logged in")
                .SelectMany(test => EnumerateArrayProperty(test, "results"))
                .SelectMany(result => EnumerateArrayProperty(result, "errors"))
                .Select(error => error.TryGetProperty("message", out var message) ? message.GetString() : null)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Take(3);

            var excerpt = string.Join(" | ", errors);
            return string.IsNullOrEmpty(excerpt) ? "A required cart scenario did not pass." : excerpt[..Math.Min(excerpt.Length, 2000)];
        }
        catch (JsonException)
        {
            return "Playwright JSON report could not be parsed.";
        }
    }

    private static IEnumerable<JsonElement> EnumerateSpecs(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (node.TryGetProperty("specs", out var specs) && specs.ValueKind == JsonValueKind.Array)
        {
            foreach (var spec in specs.EnumerateArray())
            {
                yield return spec;
            }
        }

        if (node.TryGetProperty("suites", out var suites) && suites.ValueKind == JsonValueKind.Array)
        {
            foreach (var suite in suites.EnumerateArray())
            {
                foreach (var spec in EnumerateSpecs(suite))
                {
                    yield return spec;
                }
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eShop.slnx")) &&
                File.Exists(Path.Combine(directory.FullName, "package.json")))
            {
                return directory.FullName;
            }
        }

        Assert.Fail($"Could not locate the repository root from {AppContext.BaseDirectory}.");
        return string.Empty;
    }

    private async Task WaitForCartPortToBeAvailableAsync()
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", 5045, TestContext.CancellationToken);
            }
            catch (SocketException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.CancellationToken);
        }

        Assert.Fail("Port 5045 remained occupied after the previous browser scenario; refusing to reuse an existing server.");
    }

    private static async Task SaveLogsAsync(string directory, string standardOutput, string standardError)
    {
        await File.WriteAllTextAsync(Path.Combine(directory, "playwright-report.json"), standardOutput);
        await File.WriteAllTextAsync(Path.Combine(directory, "playwright-stderr.log"), standardError);
    }

    private static void StopOwnedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The child exited while cancellation cleanup was starting.
        }
    }

    private static string RegexEscape(string value)
        => System.Text.RegularExpressions.Regex.Escape(value);

    private static string LastLines(string value)
    {
        var excerpt = string.Join(Environment.NewLine, value.Split(Environment.NewLine).TakeLast(12));
        return excerpt.Length <= 2000 ? excerpt : excerpt[^2000..];
    }
}
