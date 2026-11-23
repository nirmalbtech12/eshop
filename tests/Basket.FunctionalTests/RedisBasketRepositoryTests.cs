namespace eShop.Basket.FunctionalTests;

public sealed class RedisBasketRepositoryTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task ReplacementAndRemovalSurviveFreshConnections()
    {
        var buyerId = $"buyer-{Guid.NewGuid():N}";
        var repository = new RedisBasketRepository(
            NullLogger<RedisBasketRepository>.Instance,
            fixture.Connection);
        var original = new CustomerBasket
        {
            BuyerId = buyerId,
            Items =
            [
                new BasketItem { ProductId = 42, Quantity = 1, ProductName = "First", UnitPrice = 12.50m },
                new BasketItem { ProductId = 43, Quantity = 2, ProductName = "Second", UnitPrice = 7.25m }
            ]
        };

        try
        {
            await repository.UpdateBasketAsync(original);
            var firstRead = await ReadWithFreshConnection(buyerId);
            Assert.Equal(2, firstRead!.Items.Count);
            Assert.Contains(firstRead.Items, item => item.ProductId == 42 && item.Quantity == 1 && item.ProductName == "First" && item.UnitPrice == 12.50m);
            Assert.Contains(firstRead.Items, item => item.ProductId == 43 && item.Quantity == 2 && item.ProductName == "Second" && item.UnitPrice == 7.25m);

            await repository.UpdateBasketAsync(new CustomerBasket
            {
                BuyerId = buyerId,
                Items = [new BasketItem { ProductId = 43, Quantity = 3, ProductName = "Second", UnitPrice = 7.25m }]
            });
            var replaced = await ReadWithFreshConnection(buyerId);
            Assert.Single(replaced!.Items);
            Assert.Equal(43, replaced.Items.Single().ProductId);
            Assert.Equal(3, replaced.Items.Single().Quantity);

            await repository.UpdateBasketAsync(new CustomerBasket { BuyerId = buyerId });
            Assert.Empty((await ReadWithFreshConnection(buyerId))!.Items);
            Assert.True(await repository.DeleteBasketAsync(buyerId));
            Assert.Null(await ReadWithFreshConnection(buyerId));
        }
        finally
        {
            await repository.DeleteBasketAsync(buyerId);
        }
    }

    [Fact]
    public async Task UpdateGetAndDeleteRoundTrip()
    {
        var repository = new RedisBasketRepository(
            NullLogger<RedisBasketRepository>.Instance,
            fixture.Connection);
        var buyerId = $"buyer-{Guid.NewGuid():N}";
        var basket = new CustomerBasket
        {
            BuyerId = buyerId,
            Items =
            [
                new BasketItem
                {
                    ProductId = 42,
                    Quantity = 3,
                    ProductName = "Test product",
                    UnitPrice = 12.50m
                }
            ]
        };

        var updated = await repository.UpdateBasketAsync(basket);
        var loaded = await repository.GetBasketAsync(buyerId);
        var deleted = await repository.DeleteBasketAsync(buyerId);
        var afterDelete = await repository.GetBasketAsync(buyerId);

        Assert.NotNull(updated);
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded.Items.Single().ProductId);
        Assert.Equal(3, loaded.Items.Single().Quantity);
        Assert.True(deleted);
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task MissingBasketReturnsNullAndDeleteReturnsFalse()
    {
        var repository = new RedisBasketRepository(
            NullLogger<RedisBasketRepository>.Instance,
            fixture.Connection);
        var buyerId = $"missing-{Guid.NewGuid():N}";

        Assert.Null(await repository.GetBasketAsync(buyerId));
        Assert.False(await repository.DeleteBasketAsync(buyerId));
    }

    private async Task<CustomerBasket?> ReadWithFreshConnection(string buyerId)
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString)
            .WaitAsync(TimeSpan.FromSeconds(15));
        var repository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, connection);
        return await repository.GetBasketAsync(buyerId).WaitAsync(TimeSpan.FromSeconds(15));
    }
}
