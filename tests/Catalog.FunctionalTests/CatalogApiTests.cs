using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.IO;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Catalog.API.Model;
using eShop.Catalog.API.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pgvector.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace eShop.Catalog.FunctionalTests;

public sealed class CatalogApiTests : IClassFixture<CatalogApiFixture>
{
    private readonly WebApplicationFactory<Program> _webApplicationFactory;
    private readonly CatalogApiFixture _fixture;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);

    public CatalogApiTests(CatalogApiFixture fixture)
    {
        _fixture = fixture;
        _webApplicationFactory = fixture.Factory;
    }

    private HttpClient CreateHttpClient(ApiVersion apiVersion)
    {
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), apiVersion);
        return _webApplicationFactory.CreateDefaultClient(handler);
    }

    [Fact]
    public async Task CatalogSeedProducesSourceBackedStableCatalog()
    {
        async Task<string[]> Snapshot(WebApplicationFactory<Program> factory)
        {
            using var scope = factory.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
            return await context.CatalogItems.Include(x => x.CatalogBrand).Include(x => x.CatalogType)
                .OrderBy(x => x.Id)
                .Select(x => $"{x.Id}|{x.Name}|{x.Description}|{x.Price}|{x.AvailableStock}|{x.MaxStockThreshold}|{x.RestockThreshold}|{x.PictureFileName}|{x.CatalogBrand!.Id}:{x.CatalogBrand.Brand}|{x.CatalogType!.Id}:{x.CatalogType.Type}")
                .ToArrayAsync(TestContext.Current.CancellationToken);
        }

        var firstConnection = await _fixture.CreateDatabaseConnectionStringAsync();
        var secondConnection = await _fixture.CreateDatabaseConnectionStringAsync();
        using var firstFactory = _fixture.CreateFactory(firstConnection);
        using var secondFactory = _fixture.CreateFactory(secondConnection);
        var first = await Snapshot(firstFactory);
        var second = await Snapshot(secondFactory);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Catalog.API", "Setup", "catalog.json")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        var sourcePath = Path.Combine(directory.FullName, "src", "Catalog.API", "Setup", "catalog.json");
        using var source = JsonDocument.Parse(await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken));
        var validSource = source.RootElement.EnumerateArray()
            .Where(x => x.GetProperty("Name").ValueKind == JsonValueKind.String)
            .ToDictionary(x => x.GetProperty("Id").GetInt32());

        Assert.Equal(first, second);
        Assert.Equal(validSource.Keys.Order(), first.Select(x => int.Parse(x.Split('|')[0])).Order());
        foreach (var item in first)
        {
            var fields = item.Split('|');
            var expected = validSource[int.Parse(fields[0])];
            Assert.Equal(expected.GetProperty("Name").GetString(), fields[1]);
            Assert.Equal(expected.GetProperty("Description").GetString(), fields[2]);
            Assert.Equal(expected.GetProperty("Price").GetDecimal(), decimal.Parse(fields[3]));
            Assert.Equal(expected.GetProperty("Brand").GetString(), fields[8].Split(':', 2)[1]);
            Assert.Equal(expected.GetProperty("Type").GetString(), fields[9].Split(':', 2)[1]);
        }
    }

    [Fact]
    public async Task CatalogSeedRerunIsIdempotent()
    {
        await using var scope = _webApplicationFactory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        async Task<string[]> Snapshot()
        {
            var items = await context.CatalogItems.OrderBy(x => x.Id)
                .Select(x => $"I|{x.Id}|{x.Name}|{x.Description}|{x.Price}|{x.AvailableStock}|{x.RestockThreshold}|{x.MaxStockThreshold}|{x.PictureFileName}|{x.CatalogBrandId}|{x.CatalogTypeId}")
                .ToArrayAsync(TestContext.Current.CancellationToken);
            var brands = await context.CatalogBrands.OrderBy(x => x.Id)
                .Select(x => $"B|{x.Id}|{x.Brand}").ToArrayAsync(TestContext.Current.CancellationToken);
            var types = await context.CatalogTypes.OrderBy(x => x.Id)
                .Select(x => $"T|{x.Id}|{x.Type}").ToArrayAsync(TestContext.Current.CancellationToken);
            return items.Concat(brands).Concat(types).ToArray();
        }

        var before = await Snapshot();
        await scope.ServiceProvider.GetRequiredService<IDbSeeder<CatalogContext>>().SeedAsync(context);
        Assert.Equal(before, await Snapshot());
        Assert.Equal(before, await Snapshot());
    }

    [Fact]
    public async Task SeededDemoCatalogCoversKeyCategories()
    {
        using var client = CreateHttpClient(new ApiVersion(2.0));
        using var response = await client.GetAsync("/api/catalog/items?pageIndex=0&pageSize=1000", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<PaginatedItems<CatalogItem>>(_jsonSerializerOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(page);
        using var scope = _webApplicationFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var types = await context.CatalogTypes.ToDictionaryAsync(x => x.Id, x => x.Type, TestContext.Current.CancellationToken);
        var brands = await context.CatalogBrands.ToDictionaryAsync(x => x.Id, x => x.Brand, TestContext.Current.CancellationToken);
        var categories = types.Values.ToHashSet();
        Assert.Equal(new[] { "Bags", "Climbing", "Cycling", "Footwear", "Jackets", "Navigation", "Ski/boarding", "Trekking" }.ToHashSet(), categories);
        Assert.All(types.Keys, typeId => Assert.Contains(page.Data, item => item.CatalogTypeId == typeId));
        Assert.Contains(page.Data, item => item.Id == 1 && types[item.CatalogTypeId] == "Footwear" && brands[item.CatalogBrandId] == "Daybird");
    }

    [Fact]
    public async Task ProductionStartupDoesNotModifyCatalogData()
    {
        var connectionString = await _fixture.CreateDatabaseConnectionStringAsync();
        var options = new DbContextOptionsBuilder<CatalogContext>().UseNpgsql(connectionString, options => options.UseVector()).Options;
        await using (var seedContext = new CatalogContext(options, new ConfigurationBuilder().Build())
        {
            CatalogItems = null!,
            CatalogBrands = null!,
            CatalogTypes = null!
        })
        {
            seedContext.CatalogItems = seedContext.Set<CatalogItem>();
            seedContext.CatalogBrands = seedContext.Set<CatalogBrand>();
            seedContext.CatalogTypes = seedContext.Set<CatalogType>();
            await seedContext.Database.MigrateAsync("20231009153249_Initial", TestContext.Current.CancellationToken);
            var brand = new CatalogBrand("Sentinel brand") { Id = 1 };
            var type = new CatalogType("Sentinel type") { Id = 1 };
            seedContext.CatalogBrands.Add(brand);
            seedContext.CatalogTypes.Add(type);
            await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            seedContext.CatalogItems.Add(new CatalogItem("Sentinel product")
            {
                Id = 987654,
                Description = "Keep this production record",
                Price = 12.34m,
                AvailableStock = 5,
                RestockThreshold = 1,
                MaxStockThreshold = 10,
                PictureFileName = "sentinel.webp",
                CatalogBrandId = brand.Id,
                CatalogTypeId = type.Id
            });
            await seedContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var productionFactory = _fixture.CreateFactory(connectionString, "Production");
        using var client = productionFactory.CreateClient();
        using var scope = productionFactory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<CatalogContext>();
        var items = await context.CatalogItems.Include(x => x.CatalogBrand).Include(x => x.CatalogType)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        var migrations = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        Assert.Contains("20231026091140_Outbox", migrations);
        Assert.Collection(items, item =>
        {
            Assert.Equal(987654, item.Id);
            Assert.Equal("Keep this production record", item.Description);
            Assert.Equal("Sentinel brand", item.CatalogBrand!.Brand);
            Assert.Equal("Sentinel type", item.CatalogType!.Type);
        });
        Assert.Equal(new[] { "Sentinel brand" }, await context.CatalogBrands.Select(x => x.Brand).ToArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "Sentinel type" }, await context.CatalogTypes.Select(x => x.Type).ToArrayAsync(TestContext.Current.CancellationToken));
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
