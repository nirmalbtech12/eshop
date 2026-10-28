using System.Text.Json;
using System.Net.Http.Json;
using System.Threading;
using System.IO;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Catalog.API;
using eShop.Catalog.API.Infrastructure;
using eShop.Catalog.API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;
using eShop.Catalog.API.Model;
using Microsoft.AspNetCore.Mvc.Testing;

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogSeedTests(CatalogApiFixture fixture) : IClassFixture<CatalogApiFixture>
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FreshDatabasesProduceIdenticalCatalog()
    {
        await using var first = await CreateDatabaseAsync();
        await using var second = await CreateDatabaseAsync();
        await using (var context = first.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("""SELECT setval(pg_get_serial_sequence('"CatalogBrand"', 'Id'), 900, true)""");
            await context.Database.ExecuteSqlRawAsync("""SELECT setval(pg_get_serial_sequence('"CatalogType"', 'Id'), 700, true)""");
        }

        await SeedAsync(first);
        await SeedAsync(second);
        var left = await ReadSnapshotAsync(first);
        var right = await ReadSnapshotAsync(second);
        Assert.Equal(left, right);
        Assert.All(left, item => Assert.Null(item.Embedding));
        Assert.Equal(0, _ai.Calls);
    }

    [Fact]
    public async Task RerunsPreserveExistingCatalog()
    {
        await using var database = await CreateDatabaseAsync();
        await SeedAsync(database);
        await using (var context = database.CreateContext())
        {
            var item = await context.CatalogItems.SingleAsync(x => x.Id == 1);
            item.Price = 42m;
            await context.SaveChangesAsync();
        }
        await SeedAsync(database);
        await using (var context = database.CreateContext())
        {
            Assert.Equal(42m, (await context.CatalogItems.SingleAsync(x => x.Id == 1)).Price);
            Assert.Equal(101, await context.CatalogItems.CountAsync());
        }

        await using var lookupOnly = await CreateDatabaseAsync();
        await using (var context = lookupOnly.CreateContext())
        {
            context.CatalogBrands.Add(new CatalogBrand("Existing"));
            await context.SaveChangesAsync();
        }
        await SeedAsync(lookupOnly);
        await using (var context = lookupOnly.CreateContext())
        {
            Assert.Equal(["Existing"], await context.CatalogBrands.Select(x => x.Brand).ToArrayAsync());
            Assert.Empty(await context.CatalogItems.ToListAsync());
        }
    }

    [Fact]
    public async Task NonDevelopmentNeverSeedsDemoData()
    {
        foreach (var environmentName in new[] { Environments.Production, Environments.Staging })
        {
            var environment = new TestEnvironment { EnvironmentName = environmentName, ContentRootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) };
            var options = new DbContextOptionsBuilder<CatalogContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=missing").Options;
            await using var context = new CatalogContext(options, new ConfigurationBuilder().Build()) { CatalogItems = null!, CatalogBrands = null!, CatalogTypes = null! };
            await new CatalogContextSeed(environment, Options.Create(new CatalogOptions()), _ai).SeedAsync(context);

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environmentName });
            builder.AddApplicationServices();
            Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IDbSeeder<CatalogContext>));
            Assert.Contains(builder.Services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        }
        var development = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        development.AddApplicationServices();
        Assert.Contains(development.Services, descriptor => descriptor.ServiceType == typeof(IDbSeeder<CatalogContext>));
        Assert.Equal(0, _ai.Calls);
    }

    [Fact]
    public async Task SeedPreservesIdentityAndOptionalEnrichmentContracts()
    {
        var ai = new RecordingCatalogAI();
        await using var database = await CreateDatabaseAsync();
        await SeedAsync(database, new CatalogOptions { GenerateSeedEmbeddings = true }, ai);
        Assert.Equal(1, ai.Calls);
        await using (var context = database.CreateContext())
        {
            Assert.All(await context.CatalogItems.Select(x => x.Embedding).ToListAsync(), embedding => Assert.NotNull(embedding));
            var brand = new CatalogBrand("Generated brand");
            var type = new CatalogType("Generated type");
            context.CatalogBrands.Add(brand);
            context.CatalogTypes.Add(type);
            await context.SaveChangesAsync();
            var item = new CatalogItem("Generated item") { CatalogBrandId = brand.Id, CatalogTypeId = type.Id };
            context.CatalogItems.Add(item);
            await context.SaveChangesAsync();
            Assert.True(brand.Id > 13 && type.Id > 8 && item.Id > 101);
        }

        await using var failed = await CreateDatabaseAsync();
        await using (var context = failed.CreateContext(new FailSaveChangesInterceptor()))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SeedAsync(context));
        }
        await using (var context = failed.CreateContext())
        {
            Assert.Equal(0, await context.CatalogItems.CountAsync());
            Assert.Equal(0, await context.CatalogBrands.CountAsync());
            Assert.Equal(0, await context.CatalogTypes.CountAsync());
        }
    }

    [Fact]
    public async Task SeedCoversCanonicalDemoCategories()
    {
        using var client = fixture.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));
        var response = await client.GetAsync("/api/catalog/catalogtypes", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var types = await response.Content.ReadFromJsonAsync<List<CatalogType>>(_jsonOptions, TestContext.Current.CancellationToken);

        Assert.Equal(["Bags", "Climbing", "Cycling", "Footwear", "Jackets", "Navigation", "Ski/boarding", "Trekking"], types!.Select(x => x.Type).Order(StringComparer.Ordinal));

        response = await client.GetAsync("/api/catalog/items?pageIndex=0&pageSize=200", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var items = await response.Content.ReadFromJsonAsync<PaginatedItems<CatalogItem>>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(101, items!.Data.Count());
    }

    private readonly RecordingCatalogAI _ai = new();
    private async Task SeedAsync(SeedDatabase database, CatalogOptions? options = null, RecordingCatalogAI? ai = null)
    {
        await using var context = database.CreateContext();
        await SeedAsync(context, options, ai);
    }

    private static async Task SeedAsync(CatalogContext context, CatalogOptions? options = null, RecordingCatalogAI? ai = null)
    {
        await new CatalogContextSeed(new TestEnvironment
        {
            EnvironmentName = Environments.Development,
            ContentRootPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "src/Catalog.API")),
        }, Options.Create(options ?? new CatalogOptions()), ai ?? new RecordingCatalogAI()).SeedAsync(context);
    }

    private async Task<SeedDatabase> CreateDatabaseAsync()
    {
        var name = "catalog_seed_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(fixture.PostgresConnectionString) { Database = "postgres" };
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(admin.ConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
                await command.ExecuteNonQueryAsync();
                lastError = null;
                break;
            }
            catch (NpgsqlException error)
            {
                lastError = error;
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }
        }
        if (lastError is not null) throw new TimeoutException("PostgreSQL did not become ready within 60 seconds.", lastError);
        var target = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = name };
        var database = new SeedDatabase(target.ConnectionString, name, admin.ConnectionString);
        await using (var context = database.CreateContext()) await context.Database.EnsureCreatedAsync();
        return database;
    }

    private static async Task<(int Id, string Name, decimal Price, int BrandId, int TypeId, Vector? Embedding)[]> ReadSnapshotAsync(SeedDatabase database)
    {
        await using var context = database.CreateContext();
        return await context.CatalogItems.OrderBy(x => x.Id).Select(x => new ValueTuple<int, string, decimal, int, int, Vector?>(x.Id, x.Name, x.Price, x.CatalogBrandId, x.CatalogTypeId, x.Embedding)).ToArrayAsync();
    }

    private sealed class SeedDatabase(string connectionString, string name, string adminConnectionString) : IAsyncDisposable
    {
        public CatalogContext CreateContext(params IInterceptor[] interceptors)
        {
            var builder = new DbContextOptionsBuilder<CatalogContext>().UseNpgsql(connectionString, options => options.UseVector());
            if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
            var context = new CatalogContext(builder.Options, new ConfigurationBuilder().Build()) { CatalogItems = null!, CatalogBrands = null!, CatalogTypes = null! };
            context.CatalogItems = context.Set<CatalogItem>();
            context.CatalogBrands = context.Set<CatalogBrand>();
            context.CatalogTypes = context.Set<CatalogType>();
            return context;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "CatalogSeedTests";
        public string WebRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class RecordingCatalogAI : ICatalogAI
    {
        public int Calls { get; private set; }
        public bool IsEnabled => true;
        public ValueTask<Vector?> GetEmbeddingAsync(string text) => ValueTask.FromResult<Vector?>(null);
        public ValueTask<Vector?> GetEmbeddingAsync(CatalogItem item) => ValueTask.FromResult<Vector?>(null);
        public ValueTask<IReadOnlyList<Vector>?> GetEmbeddingsAsync(IEnumerable<CatalogItem> items)
        {
            Calls++;
            IReadOnlyList<Vector> result = items.Select(_ => new Vector(new float[384])).ToArray();
            return ValueTask.FromResult<IReadOnlyList<Vector>?>(result);
        }
    }

    private sealed class FailSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromException<InterceptionResult<int>>(new InvalidOperationException("forced save failure"));
    }
}
