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
        Assert.NotNull(updated);
        Assert.NotNull(loaded);
        Assert.Equal(42, loaded.Items.Single().ProductId);
        Assert.Equal(3, loaded.Items.Single().Quantity);

        basket.Items.Single().Quantity = 7;
        var updatedQuantity = await repository.UpdateBasketAsync(basket);
        var reloaded = await repository.GetBasketAsync(buyerId);

        Assert.NotNull(updatedQuantity);
        Assert.NotNull(reloaded);
        Assert.Equal(7, reloaded.Items.Single().Quantity);
        var deleted = await repository.DeleteBasketAsync(buyerId);
        var afterDelete = await repository.GetBasketAsync(buyerId);
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
