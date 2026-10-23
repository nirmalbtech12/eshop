using NSubstitute;
using System.Security.Claims;
using eShop.WebApp.Services;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace eShop.WebApp.UnitTests;

[TestClass]
public class BasketStateTests
{
    private static readonly CatalogItem Product1 = new(1, "One", "", 10m, "", 1, new(1, "Brand"), 1, new(1, "Type"));
    private static readonly CatalogItem Product2 = new(2, "Two", "", 5m, "", 1, new(1, "Brand"), 1, new(1, "Type"));

    [TestMethod]
    public async Task QuantityUpdatePersistsAndRefreshesItemsAndTotals()
    {
        var (state, basket, catalog) = CreateState([new(1, 1), new(2, 1)]);
        catalog.GetCatalogItems(Arg.Any<IEnumerable<int>>()).Returns(call =>
            Task.FromResult(call.Arg<IEnumerable<int>>().Select(id => id == 1 ? Product1 : Product2).ToList()));

        await state.SetQuantityAsync(1, 3);

        await basket.Received(1).UpdateBasketAsync(Arg.Is<IReadOnlyCollection<BasketQuantity>>(items =>
            items.Any(i => i.ProductId == 1 && i.Quantity == 3) && items.Any(i => i.ProductId == 2 && i.Quantity == 1)));
        basket.GetBasketAsync().Returns([new(1, 3), new(2, 1)]);
        var refreshed = await state.GetBasketItemsAsync();
        Assert.AreEqual(3, refreshed.Single(i => i.ProductId == 1).Quantity);
        Assert.AreEqual(35m, refreshed.Sum(i => i.UnitPrice * i.Quantity));
    }

    [TestMethod]
    public async Task ZeroRemovesOnlySelectedLineAndAddStillIncrements()
    {
        var (state, basket, catalog) = CreateState([new(1, 2), new(2, 1)]);
        catalog.GetCatalogItems(Arg.Any<IEnumerable<int>>()).Returns(call => Task.FromResult(call.Arg<IEnumerable<int>>().Select(id => id == 1 ? Product1 : Product2).ToList()));

        await state.SetQuantityAsync(1, 0);
        await basket.Received(1).UpdateBasketAsync(Arg.Is<IReadOnlyCollection<BasketQuantity>>(items => items.Count == 1 && items.Single().ProductId == 2));

        basket.GetBasketAsync().Returns([new(2, 1)]);
        await state.AddAsync(Product2);
        await basket.Received(1).UpdateBasketAsync(Arg.Is<IReadOnlyCollection<BasketQuantity>>(items => items.Single().Quantity == 2));
    }

    [TestMethod]
    public async Task NegativeQuantityIsRejectedWithoutFetchingOrPersisting()
    {
        var (state, basket, _) = CreateState([]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => state.SetQuantityAsync(1, -1));

        await basket.DidNotReceive().GetBasketAsync();
        await basket.DidNotReceive().UpdateBasketAsync(Arg.Any<IReadOnlyCollection<BasketQuantity>>());
    }

    private static (BasketState State, IBasketService Basket, ICatalogService Catalog) CreateState(IReadOnlyCollection<BasketQuantity> initial)
    {
        var basket = Substitute.For<IBasketService>();
        basket.GetBasketAsync().Returns(initial);
        var catalog = Substitute.For<ICatalogService>();
        var authentication = new TestAuthenticationStateProvider();
        return (new BasketState(basket, catalog, new OrderingService(new HttpClient()), authentication), basket, catalog);
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(
            new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "shopper")], "test"))));
    }
}
