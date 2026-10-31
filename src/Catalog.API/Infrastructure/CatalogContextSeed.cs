using System.Text.Json;
using eShop.Catalog.API.Services;
using eShop.Catalog.API.Model;
using Pgvector;

namespace eShop.Catalog.API.Infrastructure;

public partial class CatalogContextSeed(
    IWebHostEnvironment env,
    IOptions<CatalogOptions> settings,
    ICatalogAI catalogAI,
    ILogger<CatalogContextSeed> logger) : IDbSeeder<CatalogContext>
{
    public async Task SeedAsync(CatalogContext context)
    {
        if (env.IsProduction())
        {
            logger.LogInformation("Skipping catalog demo seed in production");
            return;
        }

        var useCustomizationData = settings.Value.UseCustomizationData;
        var contentRootPath = env.ContentRootPath;
        var picturePath = env.WebRootPath;

        // Workaround from https://github.com/npgsql/efcore.pg/issues/292#issuecomment-388608426
        context.Database.OpenConnection();
        ((NpgsqlConnection)context.Database.GetDbConnection()).ReloadTypes();

        if (!context.CatalogItems.Any())
        {
            var sourcePath = Path.Combine(contentRootPath, "Setup", "catalog.json");
            var sourceJson = File.ReadAllText(sourcePath);
            var sourceItems = JsonSerializer.Deserialize<CatalogSourceEntry[]>(sourceJson) ?? Array.Empty<CatalogSourceEntry>();

            var brands = sourceItems.Select(x => x.Brand).Where(x => x != null).Distinct().ToArray();
            var existingBrands = await context.CatalogBrands.ToDictionaryAsync(x => x.Brand, x => x.Id);
            var nextBrandId = existingBrands.Values.DefaultIfEmpty().Max();
            await context.CatalogBrands.AddRangeAsync(brands.Where(x => !existingBrands.ContainsKey(x!))
                .Select((brandName, index) => new CatalogBrand(brandName!) { Id = nextBrandId + index + 1 }));
            logger.LogInformation("Seeded catalog with {NumBrands} brands", context.CatalogBrands.Count());

            var types = sourceItems.Select(x => x.Type).Where(x => x != null).Distinct().ToArray();
            var existingTypes = await context.CatalogTypes.ToDictionaryAsync(x => x.Type, x => x.Id);
            var nextTypeId = existingTypes.Values.DefaultIfEmpty().Max();
            await context.CatalogTypes.AddRangeAsync(types.Where(x => !existingTypes.ContainsKey(x!))
                .Select((typeName, index) => new CatalogType(typeName!) { Id = nextTypeId + index + 1 }));
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

            if (catalogAI.IsEnabled)
            {
                logger.LogInformation("Generating {NumItems} embeddings", catalogItems.Length);
                IReadOnlyList<Vector>? embeddings = await catalogAI.GetEmbeddingsAsync(catalogItems);
                for (int i = 0; i < catalogItems.Length; i++)
                {
                    catalogItems[i].Embedding = embeddings?[i];
                }
            }

            await context.CatalogItems.AddRangeAsync(catalogItems);
            logger.LogInformation("Seeded catalog with {NumItems} items", context.CatalogItems.Count());
            await context.SaveChangesAsync();
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
