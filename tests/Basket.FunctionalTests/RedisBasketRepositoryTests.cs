namespace eShop.Basket.FunctionalTests;

public sealed class RedisBasketRepositoryTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
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
    public async Task QuantityUpdatesSurviveFreshConnection()
    {
        var repository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fixture.Connection);
        var buyerId = $"quantity-{Guid.NewGuid():N}";
        await repository.UpdateBasketAsync(new CustomerBasket
        {
            BuyerId = buyerId,
            Items = [new BasketItem { ProductId = 42, Quantity = 3 }, new BasketItem { ProductId = 43, Quantity = 2 }]
        });
        await repository.UpdateBasketAsync(new CustomerBasket
        {
            BuyerId = buyerId,
            Items = [new BasketItem { ProductId = 42, Quantity = 5 }, new BasketItem { ProductId = 43, Quantity = 2 }]
        });

        IConnectionMultiplexer? freshConnection = null;
        try
        {
            var options = ConfigurationOptions.Parse(fixture.ConnectionString);
            options.ConnectTimeout = 30_000;
            options.AbortOnConnectFail = true;
            freshConnection = await ConnectionMultiplexer.ConnectAsync(options);
            var loaded = await new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, freshConnection)
                .GetBasketAsync(buyerId);
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.Items.Count);
            Assert.Equal(5, loaded.Items.Single(item => item.ProductId == 42).Quantity);
            Assert.Equal(2, loaded.Items.Single(item => item.ProductId == 43).Quantity);
        }
        finally
        {
            await repository.DeleteBasketAsync(buyerId);
            if (freshConnection is not null)
            {
                await freshConnection.CloseAsync();
                freshConnection.Dispose();
            }
        }
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
