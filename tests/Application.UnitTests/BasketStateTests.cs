using System.Security.Claims;
using eShop.WebApp.Services;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using Microsoft.AspNetCore.Components.Authorization;
using NSubstitute;

namespace eShop.Application.UnitTests;

[TestClass]
public class BasketStateTests
{
    private const int ProductId = 1;

    [TestMethod]
    public async Task SetQuantityPersistsPositiveQuantityAndTotalsUseUpdatedItems()
    {
        var (state, basket) = CreateState([new BasketQuantity(ProductId, 1)]);

        await state.SetQuantityAsync(ProductId, 3);
        var items = await state.GetBasketItemsAsync();

        Assert.AreEqual(3, basket.Items.Single().Quantity);
        Assert.AreEqual(3, items.Single().Quantity);
        Assert.AreEqual(30m, items.Sum(item => item.Quantity * item.UnitPrice));
        Assert.AreEqual(3, items.Sum(item => item.Quantity));
    }

    [TestMethod]
    public async Task SetQuantityZeroRemovesLine()
    {
        var (state, basket) = CreateState([new BasketQuantity(ProductId, 2)]);

        await state.SetQuantityAsync(ProductId, 0);

        Assert.AreEqual(0, basket.Items.Count);
        Assert.AreEqual(0, (await state.GetBasketItemsAsync()).Count);
    }

    [TestMethod]
    public async Task SetQuantityRejectsNegativeQuantityWithoutUpdatingBasket()
    {
        var (state, basket) = CreateState([new BasketQuantity(ProductId, 2)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => state.SetQuantityAsync(ProductId, -1));

        Assert.AreEqual(2, basket.Items.Single().Quantity);
    }

    [TestMethod]
    public async Task AddIncrementsExistingLineAndRemoveStillWorks()
    {
        var (state, basket) = CreateState([new BasketQuantity(ProductId, 1)]);

        await state.AddAsync(CreateCatalogItem(ProductId));
        Assert.AreEqual(2, basket.Items.Single().Quantity);

        await state.SetQuantityAsync(ProductId, 0);
        Assert.AreEqual(0, basket.Items.Count);
    }

    private static (BasketState State, MemoryBasketService Basket) CreateState(IReadOnlyCollection<BasketQuantity> initialItems)
    {
        var basket = new MemoryBasketService(initialItems);
        var catalog = Substitute.For<ICatalogService>();
        catalog.GetCatalogItems(Arg.Any<IEnumerable<int>>())
            .Returns(call => call.Arg<IEnumerable<int>>().Select(CreateCatalogItem).ToList());
        var auth = new TestAuthenticationStateProvider();
        return (new BasketState(basket, catalog, null!, auth), basket);
    }

    private static CatalogItem CreateCatalogItem(int id) => new(
        id, "Test item", "Description", 10m, "image.png", 1,
        new CatalogBrand(1, "Brand"), 1, new CatalogItemType(1, "Type"));

    private sealed class MemoryBasketService(IReadOnlyCollection<BasketQuantity> initialItems) : IBasketService
    {
        public List<BasketQuantity> Items { get; private set; } = [.. initialItems];

        public Task<IReadOnlyCollection<BasketQuantity>> GetBasketAsync() => Task.FromResult<IReadOnlyCollection<BasketQuantity>>(Items);
        public Task DeleteBasketAsync()
        {
            Items = [];
            return Task.CompletedTask;
        }
        public Task UpdateBasketAsync(IReadOnlyCollection<BasketQuantity> basket)
        {
            Items = [.. basket];
            return Task.CompletedTask;
        }
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "shopper")], "test");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }
}
