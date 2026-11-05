namespace eShop.Basket.FunctionalTests;

public sealed class RedisBasketRepositoryTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task QuantityReplacementAndRemovalSurviveRepositoryReconnection()
    {
        var buyerId = $"replacement-{Guid.NewGuid():N}";
        var repository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fixture.Connection);
        try
        {
            await repository.UpdateBasketAsync(new CustomerBasket
            {
                BuyerId = buyerId,
                Items = [new BasketItem { ProductId = 42, Quantity = 1 }, new BasketItem { ProductId = 43, Quantity = 2 }]
            });
            await repository.UpdateBasketAsync(new CustomerBasket
            {
                BuyerId = buyerId,
                Items = [new BasketItem { ProductId = 42, Quantity = 3 }, new BasketItem { ProductId = 43, Quantity = 4 }]
            });

            var freshConnection = await ConnectionMultiplexer.ConnectAsync(fixture.Connection.Configuration);
            try
            {
                var freshRepository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, freshConnection);
                var replaced = await freshRepository.GetBasketAsync(buyerId);
                Assert.NotNull(replaced);
                Assert.Equal([(42, 3), (43, 4)], replaced.Items.Select(item => (item.ProductId, item.Quantity)).OrderBy(item => item.ProductId));

                await freshRepository.UpdateBasketAsync(new CustomerBasket
                {
                    BuyerId = buyerId,
                    Items = [new BasketItem { ProductId = 43, Quantity = 4 }]
                });
                var removed = await freshRepository.GetBasketAsync(buyerId);
                Assert.NotNull(removed);
                Assert.Equal([(43, 4)], removed.Items.Select(item => (item.ProductId, item.Quantity)));
            }
            finally
            {
                await freshConnection.CloseAsync();
                freshConnection.Dispose();
            }
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
}
