using System.Diagnostics;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogApiFixture : IAsyncLifetime
{
    private readonly string _containerName = $"eshop-catalog-functional-tests-{Guid.NewGuid():N}";
    private string _adminConnectionString = string.Empty;
    private int _databaseNumber;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await RunDockerAsync("run", "--detach", "--name", _containerName, "--env", "POSTGRES_PASSWORD=postgres",
            "--publish", "127.0.0.1::5432", "ankane/pgvector:latest");

        var portOutput = await RunDockerAsync("port", _containerName, "5432/tcp");
        var port = int.Parse(portOutput.Trim().Split(':').Last());
        _adminConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Database = "postgres",
            Username = "postgres",
            Password = "postgres",
            Timeout = 5,
            CommandTimeout = 10
        }.ConnectionString;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_adminConnectionString);
                await connection.OpenAsync(timeout.Token);
                break;
            }
            catch (NpgsqlException) when (!timeout.IsCancellationRequested)
            {
                await Task.Delay(500, timeout.Token);
            }
        }

        var defaultConnection = await CreateDatabaseConnectionStringAsync();
        Factory = CreateFactory(defaultConnection);
    }

    public WebApplicationFactory<Program> CreateFactory(string connectionString, string environment = "Development")
        => new CatalogWebApplicationFactory(connectionString, environment);

    public async Task<string> CreateDatabaseConnectionStringAsync()
    {
        var databaseName = $"catalog_test_{Interlocked.Increment(ref _databaseNumber)}";
        await using var connection = new NpgsqlConnection(_adminConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {databaseName}", connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = databaseName };
        return builder.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        Factory.Dispose();
        await RunDockerAsync("rm", "--force", _containerName);
    }

    private static async Task<string> RunDockerAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Docker CLI.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Docker CLI failed: {error}");
        }
        return output;
    }

    private sealed class CatalogWebApplicationFactory(string connectionString, string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string> { ["ConnectionStrings:CatalogDB"] = connectionString }));
            return base.CreateHost(builder);
        }
    }
}
