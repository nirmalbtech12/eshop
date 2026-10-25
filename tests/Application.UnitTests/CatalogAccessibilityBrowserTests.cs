using System.Diagnostics;

namespace eShop.Application.UnitTests;

[TestClass]
public class CatalogAccessibilityBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CatalogBrowserAccessibilityAndVisualChecksPass()
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo("node", "node_modules/@playwright/test/cli.js test e2e/CatalogAccessibilityTest.spec.ts --project=\"e2e tests without logged in\"")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the catalog Playwright tests.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken, timeout.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);
        var output = await stdoutTask + await stderrTask;

        Assert.AreEqual(0, process.ExitCode, output);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "package.json")) && Directory.Exists(Path.Combine(directory.FullName, "e2e")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root for the catalog browser test.");
    }
}
