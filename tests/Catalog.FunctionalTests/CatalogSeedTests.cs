using System.Text.Json;
using System.IO;
using System.Net.Http.Json;
using eShop.Catalog.API.Infrastructure;
using eShop.Catalog.API.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogSeedTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>
{
    [Fact]
    public async Task SeedRerunsPreserveExistingCatalog()
    {
        using var client = fixture.CreateClient();
        using (var scope = fixture.Services.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
            var item = await context.CatalogItems.OrderBy(x => x.Id).FirstAsync(TestContext.Current.CancellationToken);
            item.Price += 7.25m;
            item.AvailableStock = 37;
            context.CatalogItems.Add(new CatalogItem("Local seed regression sentinel")
            {
                Id = 10000,
                Description = "Preserved across seed reruns",
                Price = 9.99m,
                CatalogBrandId = item.CatalogBrandId,
                CatalogTypeId = item.CatalogTypeId,
                AvailableStock = 8,
                MaxStockThreshold = 20,
                RestockThreshold = 2,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var before = await SnapshotAsync();
        await RunSeederAsync();
        await RunSeederAsync();
        var after = await SnapshotAsync();

        Assert.Equal(before, after);
        Assert.Contains("Local seed regression sentinel", after);
    }

    [Fact]
    public async Task NonDevelopmentSeedLeavesEmptyAndPopulatedCatalogsUntouched()
    {
        using var client = fixture.CreateClient();
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<CatalogContextSeed>>();
        var populated = await SnapshotAsync();

        foreach (var environmentName in new[] { "Production", "Staging" })
        {
            await new CatalogContextSeed(new TestWebHostEnvironment(environmentName), logger).SeedAsync(context);
            Assert.Equal(populated, await SnapshotAsync());
        }

        using (var productionFactory = fixture.WithWebHostBuilder(builder => builder.UseEnvironment("Production")))
        using (var productionClient = productionFactory.CreateClient())
        {
            var types = await productionClient.GetFromJsonAsync<CatalogType[]>("/api/catalog/catalogtypes?api-version=1.0", TestContext.Current.CancellationToken);
            Assert.Equal(8, types!.Length);
            Assert.Equal(populated, await SnapshotAsync());
        }

        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM \"Catalog\"", TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM \"CatalogBrand\"", TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync("DELETE FROM \"CatalogType\"", TestContext.Current.CancellationToken);
            foreach (var environmentName in new[] { "Production", "Staging" })
            {
                context.ChangeTracker.Clear();
                await new CatalogContextSeed(new TestWebHostEnvironment(environmentName), logger).SeedAsync(context);
                Assert.Equal(0, await context.CatalogItems.CountAsync(TestContext.Current.CancellationToken));
                Assert.Equal(0, await context.CatalogBrands.CountAsync(TestContext.Current.CancellationToken));
                Assert.Equal(0, await context.CatalogTypes.CountAsync(TestContext.Current.CancellationToken));
            }
            await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        });
    }

    [Fact]
    public async Task SeedIsDeterministicAcrossDatabases()
    {
        using var client = fixture.CreateClient();
        var databaseA = $"seed_a_{Guid.NewGuid():N}";
        var databaseB = $"seed_b_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(fixture.PostgresConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var database in new[] { databaseA, databaseB })
        {
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\"";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            var contextA = CreateContext(databaseA);
            var contextB = CreateContext(databaseB);
            await using (contextA)
            await using (contextB)
            {
                await contextA.Database.MigrateAsync(TestContext.Current.CancellationToken);
                await contextB.Database.MigrateAsync(TestContext.Current.CancellationToken);
                await AdvanceLookupSequencesAsync(contextA, 100);
                await AdvanceLookupSequencesAsync(contextB, 900);
                var environment = new TestWebHostEnvironment("Development") { ContentRootPath = FindCatalogApiRoot() };
                var logger = NullLogger<CatalogContextSeed>.Instance;
                await new CatalogContextSeed(environment, logger).SeedAsync(contextA);
                await new CatalogContextSeed(environment, logger).SeedAsync(contextB);
            }

            await using var reopenedA = CreateContext(databaseA);
            await using var reopenedB = CreateContext(databaseB);
            Assert.Equal(await SnapshotAsync(reopenedA), await SnapshotAsync(reopenedB));
            Assert.Equal(101, await reopenedA.CatalogItems.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(13, await reopenedA.CatalogBrands.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(8, await reopenedA.CatalogTypes.CountAsync(TestContext.Current.CancellationToken));
            Assert.All(await reopenedA.CatalogItems.ToArrayAsync(TestContext.Current.CancellationToken), item => Assert.Null(item.Embedding));
        }
        finally
        {
            foreach (var database in new[] { databaseA, databaseB })
            {
                await using var drop = admin.CreateCommand();
                drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)";
                await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    private CatalogContext CreateContext(string database)
    {
        var connection = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString) { Database = database }.ConnectionString;
        var options = new DbContextOptionsBuilder<CatalogContext>()
            .UseNpgsql(connection, postgres => postgres.UseVector())
            .Options;
        var context = new CatalogContext(options, new ConfigurationBuilder().Build())
        {
            CatalogItems = null!,
            CatalogBrands = null!,
            CatalogTypes = null!,
        };
        context.CatalogItems = context.Set<CatalogItem>();
        context.CatalogBrands = context.Set<CatalogBrand>();
        context.CatalogTypes = context.Set<CatalogType>();
        return context;
    }

    private static async Task AdvanceLookupSequencesAsync(CatalogContext context, long value)
    {
        foreach (var table in new[] { "CatalogBrand", "CatalogType" })
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"SELECT setval(pg_get_serial_sequence(format('%I', {table}), 'Id'), {value}, true)", TestContext.Current.CancellationToken);
        }
    }

    private static string FindCatalogApiRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Catalog.API");
            if (File.Exists(Path.Combine(candidate, "Setup", "catalog.json")))
            {
                return candidate;
            }
        }
        throw new DirectoryNotFoundException("Could not locate src/Catalog.API/Setup/catalog.json.");
    }

    private async Task RunSeederAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<IDbSeeder<CatalogContext>>();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        await seeder.SeedAsync(context);
    }

    private async Task<string> SnapshotAsync()
    {
        using var scope = fixture.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        return await SnapshotAsync(context);
    }

    private static async Task<string> SnapshotAsync(CatalogContext context)
    {
        var items = await context.CatalogItems.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Name, x.Description, x.Price, x.PictureFileName, x.CatalogTypeId, x.CatalogBrandId, x.AvailableStock, x.RestockThreshold, x.MaxStockThreshold, x.OnReorder, HasEmbedding = x.Embedding != null })
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var brands = await context.CatalogBrands.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Brand }).ToArrayAsync(TestContext.Current.CancellationToken);
        var types = await context.CatalogTypes.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Type }).ToArrayAsync(TestContext.Current.CancellationToken);
        return JsonSerializer.Serialize(new { items, brands, types });
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Catalog.FunctionalTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
