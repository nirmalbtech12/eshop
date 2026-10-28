using System.Text.Json;
using eShop.Catalog.API.Services;

namespace eShop.Catalog.API.Infrastructure;

public partial class CatalogContextSeed(
    IWebHostEnvironment env,
    IOptions<CatalogOptions> settings,
    ICatalogAI catalogAI) : IDbSeeder<CatalogContext>
{
    public async Task SeedAsync(CatalogContext context)
    {
        if (!env.IsDevelopment()) return;

        var contentRootPath = env.ContentRootPath;

        // Workaround from https://github.com/npgsql/efcore.pg/issues/292#issuecomment-388608426
        if (await context.CatalogItems.AnyAsync() || await context.CatalogBrands.AnyAsync() || await context.CatalogTypes.AnyAsync()) return;

        context.Database.OpenConnection();
        ((NpgsqlConnection)context.Database.GetDbConnection()).ReloadTypes();

        var sourcePath = Path.Combine(contentRootPath, "Setup", "catalog.json");
        var sourceJson = await File.ReadAllTextAsync(sourcePath);
        var sourceItems = JsonSerializer.Deserialize<CatalogSourceEntry[]>(sourceJson) ?? throw new InvalidDataException("Catalog fixture is empty.");
        if (sourceItems.Length == 0 || sourceItems.Any(x => x.Id <= 0 || string.IsNullOrWhiteSpace(x.Name) || string.IsNullOrWhiteSpace(x.Brand) || string.IsNullOrWhiteSpace(x.Type)) || sourceItems.Select(x => x.Id).Distinct().Count() != sourceItems.Length)
            throw new InvalidDataException("Catalog fixture contains invalid or duplicate entries.");

        var brands = sourceItems.Select(x => x.Brand!).Distinct(StringComparer.Ordinal).ToArray();
        var types = sourceItems.Select(x => x.Type!).Distinct(StringComparer.Ordinal).ToArray();
        var brandIdsByName = brands.Select((name, index) => new CatalogBrand(name) { Id = index + 1 }).ToDictionary(x => x.Brand, x => x.Id, StringComparer.Ordinal);
        var typeIdsByName = types.Select((name, index) => new CatalogType(name) { Id = index + 1 }).ToDictionary(x => x.Type, x => x.Id, StringComparer.Ordinal);
        var catalogItems = sourceItems.Select(source => new CatalogItem(source.Name!)
        {
            Id = source.Id, Description = source.Description, Price = source.Price,
            CatalogBrandId = brandIdsByName[source.Brand!], CatalogTypeId = typeIdsByName[source.Type!],
            AvailableStock = 100, MaxStockThreshold = 200, RestockThreshold = 10,
            PictureFileName = $"{source.Id}.webp",
        }).ToArray();

        if (settings.Value.GenerateSeedEmbeddings && catalogAI.IsEnabled)
        {
            var embeddings = await catalogAI.GetEmbeddingsAsync(catalogItems);
            if (embeddings?.Count != catalogItems.Length) throw new InvalidDataException("Embedding provider returned an unexpected result count.");
            for (var i = 0; i < catalogItems.Length; i++) catalogItems[i].Embedding = embeddings[i];
        }

        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.CatalogBrands.AddRangeAsync(brands.Select((name, index) => new CatalogBrand(name) { Id = index + 1 }));
        await context.CatalogTypes.AddRangeAsync(types.Select((name, index) => new CatalogType(name) { Id = index + 1 }));
        await context.CatalogItems.AddRangeAsync(catalogItems);
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("""SELECT setval(pg_get_serial_sequence('"Catalog"', 'Id'), COALESCE((SELECT MAX("Id") FROM "Catalog"), 1), true)""");
        await context.Database.ExecuteSqlRawAsync("""SELECT setval(pg_get_serial_sequence('"CatalogBrand"', 'Id'), COALESCE((SELECT MAX("Id") FROM "CatalogBrand"), 1), true)""");
        await context.Database.ExecuteSqlRawAsync("""SELECT setval(pg_get_serial_sequence('"CatalogType"', 'Id'), COALESCE((SELECT MAX("Id") FROM "CatalogType"), 1), true)""");
        await transaction.CommitAsync();
    }

    private class CatalogSourceEntry
    {
        public int Id { get; set; }
        public string? Type { get; set; }
        public string? Brand { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public decimal Price { get; set; }
    }
}
