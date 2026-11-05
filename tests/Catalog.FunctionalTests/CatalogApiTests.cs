using System.IO;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Catalog.API.Model;
using eShop.Catalog.API.Infrastructure;
using eShop.Catalog.API;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Pgvector.EntityFrameworkCore;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogApiTests : IClassFixture<CatalogApiFixture>
{
    private readonly CatalogApiFixture _fixture;
    private readonly WebApplicationFactory<Program> _webApplicationFactory;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public CatalogApiTests(CatalogApiFixture fixture)
    {
        _fixture = fixture;
        _webApplicationFactory = fixture;
    }

    [Fact]
    public async Task SeedProducesDeterministicDataAndIsIdempotent()
    {
        using var client = _webApplicationFactory.CreateClient();
        using var scope = _webApplicationFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var before = await ReadSeedSnapshot(context);

        var scopedServices = scope.ServiceProvider;
        var seeder = new CatalogContextSeed(
            _webApplicationFactory.Services.GetRequiredService<IWebHostEnvironment>(),
            _webApplicationFactory.Services.GetRequiredService<IOptions<CatalogOptions>>(),
            scopedServices.GetRequiredService<eShop.Catalog.API.Services.ICatalogAI>(),
            _webApplicationFactory.Services.GetRequiredService<ILogger<CatalogContextSeed>>());
        await seeder.SeedAsync(context);
        context.ChangeTracker.Clear();
        var after = await ReadSeedSnapshot(context);

        Assert.Equal(before.Items, after.Items);
        Assert.Equal(before.Brands, after.Brands);
        Assert.Equal(before.Types, after.Types);
        Assert.NotEmpty(before.Items);
        Assert.NotEmpty(before.Brands);
        Assert.NotEmpty(before.Types);
    }

    [Fact]
    public async Task SeededDemoCoversSourceCategoriesAndBrands()
    {
        using var client = _webApplicationFactory.CreateClient();
        using var scope = _webApplicationFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        await context.CatalogItems.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.CatalogBrands.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.CatalogTypes.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await SeedCatalog(scope.ServiceProvider, context);
        var items = await context.CatalogItems.Include(x => x.CatalogBrand).Include(x => x.CatalogType).AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        var sourcePath = Path.Combine(_webApplicationFactory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "Setup", "catalog.json");
        var source = JsonSerializer.Deserialize<SeedSource[]>(await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken))!;

        Assert.Equal(source.Length, items.Count);
        foreach (var entry in source)
        {
            var item = Assert.Single(items, x => x.Id == entry.Id);
            Assert.Equal(entry.Name, item.Name);
            Assert.Equal(entry.Description, item.Description);
            Assert.Equal(entry.Price, item.Price);
            Assert.Equal(entry.Brand, item.CatalogBrand!.Brand);
            Assert.Equal(entry.Type, item.CatalogType!.Type);
            Assert.Equal(Array.IndexOf(source.Select(x => x.Brand).Distinct(StringComparer.Ordinal).ToArray(), entry.Brand) + 1, item.CatalogBrand.Id);
            Assert.Equal(Array.IndexOf(source.Select(x => x.Type).Distinct(StringComparer.Ordinal).ToArray(), entry.Type) + 1, item.CatalogType.Id);
        }
        Assert.Equal(source.Select(x => x.Brand).Distinct().Order(StringComparer.Ordinal),
            items.Select(x => x.CatalogBrand!.Brand).Distinct().Order(StringComparer.Ordinal));
        Assert.Equal(source.Select(x => x.Type).Distinct().Order(StringComparer.Ordinal),
            items.Select(x => x.CatalogType!.Type).Distinct().Order(StringComparer.Ordinal));

        var firstCleanRun = await ReadSeedSnapshot(context);
        await context.CatalogItems.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.CatalogBrands.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await context.CatalogTypes.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await SeedCatalog(scope.ServiceProvider, context);
        var secondCleanRun = await ReadSeedSnapshot(context);
        Assert.Equal(firstCleanRun.Items, secondCleanRun.Items);
        Assert.Equal(firstCleanRun.Brands, secondCleanRun.Brands);
        Assert.Equal(firstCleanRun.Types, secondCleanRun.Types);
    }

    [Fact]
    public async Task ProductionStartupLeavesCatalogUntouched()
    {
        using var fixtureClient = _webApplicationFactory.CreateClient();
        var fixtureContext = _webApplicationFactory.Services.CreateScope();
        var databaseName = $"catalog_production_{Guid.NewGuid():N}";
        var adminConnection = new NpgsqlConnectionStringBuilder(await _fixture.Postgres.Resource.GetConnectionStringAsync())
        {
            Database = "postgres",
            Pooling = false
        }.ConnectionString;
        var productionConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;

        using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var createDatabase = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
        {
            await createDatabase.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            SeedSnapshot before;
            var options = new DbContextOptionsBuilder<CatalogContext>()
                .UseNpgsql(productionConnection, postgres => postgres.UseVector())
                .Options;
            using (var sentinelContext = new CatalogContext(options, fixtureContext.ServiceProvider.GetRequiredService<IConfiguration>())
            {
                CatalogItems = null!,
                CatalogBrands = null!,
                CatalogTypes = null!
            })
            {
                await sentinelContext.Database.MigrateAsync(TestContext.Current.CancellationToken);
                sentinelContext.CatalogItems = sentinelContext.Set<CatalogItem>();
                sentinelContext.CatalogBrands = sentinelContext.Set<CatalogBrand>();
                sentinelContext.CatalogTypes = sentinelContext.Set<CatalogType>();
                sentinelContext.CatalogBrands.Add(new CatalogBrand("production-sentinel-brand") { Id = 800001 });
                sentinelContext.CatalogTypes.Add(new CatalogType("production-sentinel-type") { Id = 800001 });
                await sentinelContext.SaveChangesAsync(TestContext.Current.CancellationToken);
                before = await ReadSeedSnapshot(sentinelContext);
            }

            Assert.Empty(before.Items);
            Assert.Equal(new[] { "800001|production-sentinel-brand" }, before.Brands);
            Assert.Equal(new[] { "800001|production-sentinel-type" }, before.Types);

            using var productionFactory = new ProductionCatalogApiFactory(productionConnection);
            using var productionClient = productionFactory.CreateClient();
            Assert.Equal("Production", productionFactory.Services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
            using var productionScope = productionFactory.Services.CreateScope();
            productionScope.ServiceProvider.GetRequiredService<CatalogContext>().ChangeTracker.Clear();
            var after = await ReadSeedSnapshot(productionScope.ServiceProvider.GetRequiredService<CatalogContext>());

            Assert.Equal(before.Items, after.Items);
            Assert.Equal(before.Brands, after.Brands);
            Assert.Equal(before.Types, after.Types);
            Assert.Empty(after.Items);
            Assert.Single(after.Brands);
            Assert.Single(after.Types);
        }
        finally
        {
            await admin.CloseAsync();
            await using var dropDatabase = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\"", admin);
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await dropDatabase.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            fixtureContext.Dispose();
        }
    }

    private static async Task<SeedSnapshot> ReadSeedSnapshot(CatalogContext context)
    {
        var items = await context.CatalogItems.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => $"{x.Id}|{x.Name}|{x.Description}|{x.Price}|{x.CatalogBrandId}|{x.CatalogTypeId}|{x.AvailableStock}|{x.RestockThreshold}|{x.MaxStockThreshold}|{x.PictureFileName}").ToArrayAsync();
        var brands = await context.CatalogBrands.AsNoTracking().OrderBy(x => x.Id).Select(x => $"{x.Id}|{x.Brand}").ToArrayAsync();
        var types = await context.CatalogTypes.AsNoTracking().OrderBy(x => x.Id).Select(x => $"{x.Id}|{x.Type}").ToArrayAsync();
        return new SeedSnapshot(items, brands, types);
    }

    private async Task SeedCatalog(IServiceProvider services, CatalogContext context)
    {
        var seeder = new CatalogContextSeed(
            _webApplicationFactory.Services.GetRequiredService<IWebHostEnvironment>(),
            _webApplicationFactory.Services.GetRequiredService<IOptions<CatalogOptions>>(),
            services.GetRequiredService<eShop.Catalog.API.Services.ICatalogAI>(),
            _webApplicationFactory.Services.GetRequiredService<ILogger<CatalogContextSeed>>());
        await seeder.SeedAsync(context);
        context.ChangeTracker.Clear();
    }

    private sealed record SeedSnapshot(string[] Items, string[] Brands, string[] Types);
    private sealed class ProductionCatalogApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string>
            {
                ["ConnectionStrings:catalogdb"] = connectionString
            }));
            return base.CreateHost(builder);
        }
    }

    private sealed class SeedSource
    {
        public int Id { get; set; }
        public string Brand { get; set; } = "";
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public decimal Price { get; set; }
    }

    private HttpClient CreateHttpClient(ApiVersion apiVersion)
    {
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), apiVersion);
        return _webApplicationFactory.CreateDefaultClient(handler);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemsRespectsPageSize(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = await _httpClient.GetAsync("/api/catalog/items?pageIndex=0&pageSize=5", TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        Assert.Equal(5, result.Data.Count());
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task UpdateCatalogItemWorksWithoutPriceUpdate(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act - 1
        var response = await _httpClient.GetAsync("/api/catalog/items/1", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var itemToUpdate = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

        // Act - 2
        var priorAvailableStock = itemToUpdate.AvailableStock;
        itemToUpdate.AvailableStock -= 1;
        response = version switch
        {
            1.0 => await _httpClient.PutAsJsonAsync("/api/catalog/items", itemToUpdate, TestContext.Current.CancellationToken),
            2.0 => await _httpClient.PutAsJsonAsync($"/api/catalog/items/{itemToUpdate.Id}", itemToUpdate, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };
        response.EnsureSuccessStatusCode();

        // Act - 3
        response = await _httpClient.GetAsync("/api/catalog/items/1", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var updatedItem = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

        // Assert - 1
        Assert.Equal(itemToUpdate.Id, updatedItem.Id);
        Assert.NotEqual(priorAvailableStock, updatedItem.AvailableStock);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task UpdateCatalogItemWorksWithPriceUpdate(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act - 1
        var response = await _httpClient.GetAsync("/api/catalog/items/1", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var itemToUpdate = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

        // Act - 2
        var priorAvailableStock = itemToUpdate.AvailableStock;
        itemToUpdate.AvailableStock -= 1;
        itemToUpdate.Price = 1.99m;
        response = version switch
        {
            1.0 => await _httpClient.PutAsJsonAsync("/api/catalog/items", itemToUpdate, TestContext.Current.CancellationToken),
            2.0 => await _httpClient.PutAsJsonAsync($"/api/catalog/items/{itemToUpdate.Id}", itemToUpdate, TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };
        response.EnsureSuccessStatusCode();

        // Act - 3
        response = await _httpClient.GetAsync("/api/catalog/items/1", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var updatedItem = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

        // Assert - 1
        Assert.Equal(itemToUpdate.Id, updatedItem.Id);
        Assert.Equal(1.99m, updatedItem.Price);
        Assert.NotEqual(priorAvailableStock, updatedItem.AvailableStock);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemsbyIds(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = await _httpClient.GetAsync("/api/catalog/items/by?ids=1&ids=2&ids=3", TestContext.Current.CancellationToken);

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<List<CatalogItem>>(body, _jsonSerializerOptions);

        // Assert 3 items
        Assert.Equal(3, result.Count);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemWithId(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = await _httpClient.GetAsync("/api/catalog/items/2", TestContext.Current.CancellationToken);

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

        // Assert
        Assert.Equal(2, result.Id);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemWithExactName(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = version switch
        {
            1.0 => await _httpClient.GetAsync("api/catalog/items/by/Wanderer%20Black%20Hiking%20Boots?PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            2.0 => await _httpClient.GetAsync("api/catalog/items?name=Wanderer%20Black%20Hiking%20Boots&PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        // Assert
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Count);
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
        Assert.Equal("Wanderer Black Hiking Boots", result.Data.ToList().FirstOrDefault().Name);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemWithPartialName(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = version switch
        {
            1.0 => await _httpClient.GetAsync("api/catalog/items/by/Alpine?PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            2.0 => await _httpClient.GetAsync("api/catalog/items?name=Alpine&PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        // Assert
        Assert.NotNull(result.Data);
        Assert.Equal(4, result.Count);
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
        Assert.Contains("Alpine", result.Data.ToList().FirstOrDefault().Name);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemPicWithId(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = await _httpClient.GetAsync("api/catalog/items/1/pic", TestContext.Current.CancellationToken);

        // Arrange
        response.EnsureSuccessStatusCode();
        var result = response.Content.Headers.ContentType.MediaType;

        // Assert
        Assert.Equal("image/webp", result);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemWithsemanticrelevance(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = version switch
        {
            1.0 => await _httpClient.GetAsync("api/catalog/items/withsemanticrelevance/Wanderer?PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            2.0 => await _httpClient.GetAsync("api/catalog/items/withsemanticrelevance?text=Wanderer&PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        // Assert
        Assert.Equal(1, result.Count);
        Assert.NotNull(result.Data);
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemWithTypeIdBrandId(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = version switch
        {
            1.0 => await _httpClient.GetAsync("api/catalog/items/type/3/brand/3?PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            2.0 => await _httpClient.GetAsync("api/catalog/items?type=3&brand=3&PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        // Assert
        Assert.NotNull(result.Data);
        Assert.Equal(4, result.Count);
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
        Assert.Equal(3, result.Data.ToList().FirstOrDefault().CatalogTypeId);
        Assert.Equal(3, result.Data.ToList().FirstOrDefault().CatalogBrandId);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetAllCatalogTypeItemWithBrandId(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = version switch
        {
            1.0 => await _httpClient.GetAsync("api/catalog/items/type/all/brand/3?PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            2.0 => await _httpClient.GetAsync("api/catalog/items?brand=3&PageSize=5&PageIndex=0", TestContext.Current.CancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(version), version, null)
        };

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<PaginatedItems<CatalogItem>>(body, _jsonSerializerOptions);

        // Assert
        Assert.NotNull(result.Data);
        Assert.Equal(11, result.Count);
        Assert.Equal(0, result.PageIndex);
        Assert.Equal(5, result.PageSize);
        Assert.Equal(3, result.Data.ToList().FirstOrDefault().CatalogBrandId);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetAllCatalogTypes(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = await _httpClient.GetAsync("api/catalog/catalogtypes", TestContext.Current.CancellationToken);

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<List<CatalogType>>(body, _jsonSerializerOptions);

        // Assert
        Assert.Equal(8, result.Count);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetAllCatalogBrands(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        // Act
        var response = await _httpClient.GetAsync("api/catalog/catalogbrands", TestContext.Current.CancellationToken);

        // Arrange
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var result = JsonSerializer.Deserialize<List<CatalogBrand>>(body, _jsonSerializerOptions);

        // Assert
        Assert.Equal(13, result.Count);
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task AddCatalogItem(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        var id = version switch {
            1.0 => 10015,
            2.0 => 10016,
            _ => 0
        };

        // Act - 1
        var bodyContent = new CatalogItem("TestCatalog1") {
            Id = id,
            Description = "Test catalog description 1",
            Price = 11000.08m,
            PictureFileName = null,
            CatalogTypeId = 8,
            CatalogType = null,
            CatalogBrandId = 13,
            CatalogBrand = null,
            AvailableStock = 100,
            RestockThreshold = 10,
            MaxStockThreshold = 200,
            OnReorder = false
        };
        var response = await _httpClient.PostAsJsonAsync("/api/catalog/items", bodyContent, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // Act - 2
        response = await _httpClient.GetAsync($"/api/catalog/items/{id}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var addedItem = JsonSerializer.Deserialize<CatalogItem>(body, _jsonSerializerOptions);

        // Assert - 1
        Assert.Equal(bodyContent.Id, addedItem.Id);

    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task DeleteCatalogItem(double version)
    {
        var _httpClient = CreateHttpClient(new ApiVersion(version));

        var id = version switch {
            1.0 => 5,
            2.0 => 6,
            _ => 0
        };

        //Act - 1
        var response = await _httpClient.DeleteAsync($"/api/catalog/items/{id}", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // Act - 2
        var response1 = await _httpClient.GetAsync($"/api/catalog/items/{id}", TestContext.Current.CancellationToken);
        var responseStatus = response1.StatusCode;

        // Assert - 1
        Assert.Equal("NoContent", response.StatusCode.ToString());
        Assert.Equal("NotFound", responseStatus.ToString());
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetCatalogItemRejectsInvalidId(double version)
    {
        var httpClient = CreateHttpClient(new ApiVersion(version));

        var response = await httpClient.GetAsync("/api/catalog/items/0", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task GetAndDeleteMissingCatalogItemReturnNotFound(double version)
    {
        var httpClient = CreateHttpClient(new ApiVersion(version));
        const int missingId = int.MaxValue;

        var getResponse = await httpClient.GetAsync($"/api/catalog/items/{missingId}", TestContext.Current.CancellationToken);
        var deleteResponse = await httpClient.DeleteAsync($"/api/catalog/items/{missingId}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, deleteResponse.StatusCode);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public async Task CatalogPaginationBeyondLastPageReturnsEmptyData(double version)
    {
        var httpClient = CreateHttpClient(new ApiVersion(version));

        var response = await httpClient.GetAsync(
            "/api/catalog/items?pageIndex=1000&pageSize=10",
            TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PaginatedItems<CatalogItem>>(
            _jsonSerializerOptions,
            TestContext.Current.CancellationToken);
        Assert.Empty(result!.Data);
        Assert.Equal(1000, result.PageIndex);
    }
}
