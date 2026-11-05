using System.Text.Json;

namespace eShop.Catalog.API.Infrastructure;

public partial class CatalogContextSeed(
    IWebHostEnvironment env,
    ILogger<CatalogContextSeed> logger) : IDbSeeder<CatalogContext>
{
    public async Task SeedAsync(CatalogContext context)
    {
        if (!env.IsDevelopment())
        {
            return;
        }

        var contentRootPath = env.ContentRootPath;

        // Workaround from https://github.com/npgsql/efcore.pg/issues/292#issuecomment-388608426
        await context.Database.OpenConnectionAsync();
        ((NpgsqlConnection)context.Database.GetDbConnection()).ReloadTypes();

        if (!context.CatalogItems.Any())
        {
            var sourcePath = Path.Combine(contentRootPath, "Setup", "catalog.json");
            var sourceJson = File.ReadAllText(sourcePath);
            var sourceItems = JsonSerializer.Deserialize<CatalogSourceEntry[]>(sourceJson) ?? Array.Empty<CatalogSourceEntry>();

            await using var transaction = await context.Database.BeginTransactionAsync();
            var brands = sourceItems.Select(x => x.Brand).Where(x => x != null).Select(x => x!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            context.CatalogBrands.AddRange(brands.Select((name, index) => new CatalogBrand(name) { Id = index + 1 }));
            logger.LogInformation("Seeded catalog with {NumBrands} brands", context.CatalogBrands.Count());

            var types = sourceItems.Select(x => x.Type).Where(x => x != null).Select(x => x!)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            context.CatalogTypes.AddRange(types.Select((name, index) => new CatalogType(name) { Id = index + 1 }));
            logger.LogInformation("Seeded catalog with {NumTypes} types", context.CatalogTypes.Count());

            await context.SaveChangesAsync();

            var brandIdsByName = await context.CatalogBrands.ToDictionaryAsync(x => x.Brand, x => x.Id);
            var typeIdsByName = await context.CatalogTypes.ToDictionaryAsync(x => x.Type, x => x.Id);

            var catalogItems = sourceItems
                .Where(source => source.Name != null && source.Brand != null && source.Type != null)
                .Select(source => new CatalogItem(source.Name!)
            {
                Id = source.Id,
                Description = source.Description,
                Price = source.Price,
                CatalogBrandId = brandIdsByName[source.Brand!],
                CatalogTypeId = typeIdsByName[source.Type!],
                AvailableStock = 100,
                MaxStockThreshold = 200,
                RestockThreshold = 10,
                PictureFileName = $"{source.Id}.webp",
            }).ToArray();

            await context.CatalogItems.AddRangeAsync(catalogItems);
            logger.LogInformation("Seeded catalog with {NumItems} items", context.CatalogItems.Count());
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
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
