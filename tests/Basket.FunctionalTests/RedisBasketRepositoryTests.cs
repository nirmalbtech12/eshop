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
                },
                new BasketItem
                {
                    ProductId = 43,
                    Quantity = 5,
                    ProductName = "Second test product",
                    UnitPrice = 8.25m
                }
            ]
        };

        var updated = await repository.UpdateBasketAsync(basket);
        using var freshConnection = await fixture.OpenConnectionAsync();
        var reopenedRepository = new RedisBasketRepository(
            NullLogger<RedisBasketRepository>.Instance,
            freshConnection);
        var loaded = await reopenedRepository.GetBasketAsync(buyerId);
        var deleted = await reopenedRepository.DeleteBasketAsync(buyerId);
        var afterDelete = await reopenedRepository.GetBasketAsync(buyerId);

        Assert.NotNull(updated);
        Assert.NotNull(loaded);
        Assert.Equal(buyerId, loaded.BuyerId);
        Assert.Equal(2, loaded.Items.Count);
        Assert.Collection(loaded.Items,
            item => { Assert.Equal(42, item.ProductId); Assert.Equal(3, item.Quantity); },
            item => { Assert.Equal(43, item.ProductId); Assert.Equal(5, item.Quantity); });
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
