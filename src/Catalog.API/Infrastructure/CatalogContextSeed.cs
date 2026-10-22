using System.Text.Json;
using eShop.Catalog.API.Services;
using Pgvector;

namespace eShop.Catalog.API.Infrastructure;

public partial class CatalogContextSeed(
    IWebHostEnvironment env,
    ICatalogAI catalogAI,
    ILogger<CatalogContextSeed> logger) : IDbSeeder<CatalogContext>
{
    public async Task SeedAsync(CatalogContext context)
    {
        if (!env.IsDevelopment())
        {
            logger.LogInformation("Skipping demo catalog seed outside the Development environment");
            return;
        }

        // Workaround from https://github.com/npgsql/efcore.pg/issues/292#issuecomment-388608426
        context.Database.OpenConnection();
        ((NpgsqlConnection)context.Database.GetDbConnection()).ReloadTypes();

        var sourcePath = Path.Combine(env.ContentRootPath, "Setup", "catalog.json");
        var sourceJson = await File.ReadAllTextAsync(sourcePath);
        var sourceItems = JsonSerializer.Deserialize<CatalogSourceEntry[]>(sourceJson) ?? Array.Empty<CatalogSourceEntry>();

        var brandsByName = await context.CatalogBrands.ToDictionaryAsync(x => x.Brand);
        foreach (var brandName in sourceItems.Select(x => x.Brand).Where(x => x != null).Distinct())
        {
            if (!brandsByName.ContainsKey(brandName!))
            {
                var brand = new CatalogBrand(brandName!);
                await context.CatalogBrands.AddAsync(brand);
                brandsByName.Add(brandName!, brand);
            }
        }

        var typesByName = await context.CatalogTypes.ToDictionaryAsync(x => x.Type);
        foreach (var typeName in sourceItems.Select(x => x.Type).Where(x => x != null).Distinct())
        {
            if (!typesByName.ContainsKey(typeName!))
            {
                var type = new CatalogType(typeName!);
                await context.CatalogTypes.AddAsync(type);
                typesByName.Add(typeName!, type);
            }
        }

        await context.SaveChangesAsync();

        var existingItemIds = await context.CatalogItems.Select(x => x.Id).ToHashSetAsync();
        var catalogItems = sourceItems
            .Where(source => source.Name != null && source.Brand != null && source.Type != null)
            .Where(source => !existingItemIds.Contains(source.Id))
            .Select(source => new CatalogItem(source.Name!)
            {
                Id = source.Id,
                Description = source.Description,
                Price = source.Price,
                CatalogBrandId = brandsByName[source.Brand!].Id,
                CatalogTypeId = typesByName[source.Type!].Id,
                AvailableStock = 100,
                MaxStockThreshold = 200,
                RestockThreshold = 10,
                PictureFileName = $"{source.Id}.webp",
            }).ToArray();

        if (catalogAI.IsEnabled && catalogItems.Length > 0)
        {
            logger.LogInformation("Generating {NumItems} embeddings", catalogItems.Length);
            IReadOnlyList<Vector>? embeddings = await catalogAI.GetEmbeddingsAsync(catalogItems);
            for (int i = 0; i < catalogItems.Length; i++)
            {
                catalogItems[i].Embedding = embeddings?[i];
            }
        }

        if (catalogItems.Length > 0)
        {
            await context.CatalogItems.AddRangeAsync(catalogItems);
            await context.SaveChangesAsync();
        }

        logger.LogInformation("Demo catalog seed complete: {NumItems} items added", catalogItems.Length);
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
