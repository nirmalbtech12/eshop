using System.Diagnostics;
using System.Text.Json;

namespace eShop.Catalog.AccessibilityTests;

[TestClass]
[DoNotParallelize]
public sealed class CatalogAccessibilityTest
{
    private static readonly Lazy<Task<IReadOnlyDictionary<string, bool>>> PlaywrightResults = new(RunPlaywrightSuite);

    [TestMethod(DisplayName = "CatalogAccessibilityTest.spec.ts › catalog keyboard navigation")]
    public Task CatalogKeyboardNavigation() => AssertPlaywrightCase("catalog keyboard navigation");

    [TestMethod(DisplayName = "CatalogAccessibilityTest.spec.ts › catalog WCAG AA audit")]
    public Task CatalogWcagAaAudit() => AssertPlaywrightCase("catalog WCAG AA audit");

    [TestMethod(DisplayName = "CatalogAccessibilityTest.spec.ts › catalog accessible names and states")]
    public Task CatalogAccessibleNamesAndStates() => AssertPlaywrightCase("catalog accessible names and states");

    [TestMethod(DisplayName = "CatalogAccessibilityTest.spec.ts › catalog visual regression")]
    public Task CatalogVisualRegression() => AssertPlaywrightCase("catalog visual regression");

    private static async Task AssertPlaywrightCase(string testName)
    {
        var results = await PlaywrightResults.Value;
        Assert.IsTrue(results.TryGetValue(testName, out var passed) && passed,
            $"Playwright test '{testName}' did not pass. See artifacts/catalog-accessibility-tests/catalog-audit.json.");
    }

    private static async Task<IReadOnlyDictionary<string, bool>> RunPlaywrightSuite()
    {
        var repositoryRoot = FindRepositoryRoot();
        var logDirectory = Path.Combine(repositoryRoot, "artifacts", "catalog-accessibility-tests");
        Directory.CreateDirectory(logDirectory);
        var resultPath = Path.Combine(logDirectory, "catalog-audit.json");
        var startInfo = new ProcessStartInfo(Path.Combine(repositoryRoot, "node_modules", ".bin", "playwright"))
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add("e2e/CatalogAccessibilityTest.spec.ts");
        startInfo.ArgumentList.Add("--project=e2e tests without logged in");
        startInfo.ArgumentList.Add("--reporter=json");
        startInfo.Environment["ESHOP_USE_HTTP_ENDPOINTS"] = "1";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Playwright.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("Playwright catalog accessibility suite exceeded six minutes.");
        }

        var output = await stdout;
        var error = await stderr;
        await File.WriteAllTextAsync(resultPath, output + Environment.NewLine + error);
        if (process.ExitCode != 0)
        {
            throw new AssertFailedException($"Playwright catalog suite failed. Full output: {resultPath}");
        }

        using var report = JsonDocument.Parse(output);
        var results = new Dictionary<string, bool>(StringComparer.Ordinal);
        CollectResults(report.RootElement, results);
        var expected = new[]
        {
            "catalog keyboard navigation",
            "catalog WCAG AA audit",
            "catalog accessible names and states",
            "catalog visual regression"
        };
        CollectionAssert.AreEquivalent(expected, results.Keys.ToArray(),
            $"Expected all four catalog Playwright cases. Full output: {resultPath}");
        return results;
    }

    private static void CollectResults(JsonElement element, IDictionary<string, bool> results)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("title", out var titleElement) &&
                titleElement.ValueKind == JsonValueKind.String &&
                element.TryGetProperty("tests", out var testsElement) &&
                testsElement.ValueKind == JsonValueKind.Array)
            {
                var title = titleElement.GetString()!;
                var outcomes = testsElement
                    .EnumerateArray()
                    .SelectMany(test => test.TryGetProperty("results", out var testResults) && testResults.ValueKind == JsonValueKind.Array
                        ? testResults.EnumerateArray()
                        : [])
                    .Select(result => result.TryGetProperty("status", out var status) && status.GetString() == "passed")
                    .ToArray();
                if (title.StartsWith("catalog ", StringComparison.Ordinal))
                {
                    results[title] = outcomes.Length > 0 && outcomes.All(outcome => outcome);
                }
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectResults(property.Value, results);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectResults(item, results);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "eShop.Web.slnf")) &&
                File.Exists(Path.Combine(directory.FullName, "package.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root containing eShop.Web.slnf and package.json.");
    }
}
