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
    public async Task MissingBasketReturnsNullAndDeleteReturnsFalse()
    {
        var repository = new RedisBasketRepository(
            NullLogger<RedisBasketRepository>.Instance,
            fixture.Connection);
        var buyerId = $"missing-{Guid.NewGuid():N}";

        Assert.Null(await repository.GetBasketAsync(buyerId));
        Assert.False(await repository.DeleteBasketAsync(buyerId));
    }

    [Fact]
    public async Task QuantityUpdatesAndRemovalSurviveReconnect()
    {
        var buyerId = $"buyer-{Guid.NewGuid():N}";
        var repository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fixture.Connection);
        var otherLine = new BasketItem { ProductId = 43, Quantity = 4, ProductName = "Other", UnitPrice = 8m };

        try
        {
            await repository.UpdateBasketAsync(new CustomerBasket
            {
                BuyerId = buyerId,
                Items =
                [
                    new BasketItem { ProductId = 42, Quantity = 1, ProductName = "Updated", UnitPrice = 12.5m },
                    otherLine
                ]
            });
            await repository.UpdateBasketAsync(new CustomerBasket
            {
                BuyerId = buyerId,
                Items =
                [
                    new BasketItem { ProductId = 42, Quantity = 3, ProductName = "Updated", UnitPrice = 12.5m },
                    otherLine
                ]
            });

            await using (var updatedConnection = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString)
                .WaitAsync(TimeSpan.FromSeconds(15)))
            {
                var updated = await new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, updatedConnection)
                    .GetBasketAsync(buyerId).WaitAsync(TestContext.Current.CancellationToken);
                Assert.NotNull(updated);
                Assert.Equal(buyerId, updated.BuyerId);
                Assert.Equal(2, updated.Items.Count);
                Assert.Equal(3, updated.Items.Single(item => item.ProductId == 42).Quantity);
                Assert.Equal(4, updated.Items.Single(item => item.ProductId == 43).Quantity);

                await repository.UpdateBasketAsync(new CustomerBasket { BuyerId = buyerId, Items = [otherLine] })
                    .WaitAsync(TestContext.Current.CancellationToken);
                var removed = await new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, updatedConnection)
                    .GetBasketAsync(buyerId).WaitAsync(TestContext.Current.CancellationToken);
                Assert.NotNull(removed);
                Assert.Equal(buyerId, removed.BuyerId);
                Assert.Single(removed.Items);
                Assert.Equal(43, removed.Items[0].ProductId);
                Assert.Equal(4, removed.Items[0].Quantity);
            }
        }
        finally
        {
            await repository.DeleteBasketAsync(buyerId).WaitAsync(TestContext.Current.CancellationToken);
        }
    }
}
