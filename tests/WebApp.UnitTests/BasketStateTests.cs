using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using eShop.WebApp.Services;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace eShop.WebApp.UnitTests;

[TestClass]
public class BasketStateTests
{
    private const int ProductId = 42;

    [TestMethod]
    public async Task SetQuantityPersistsAndRefetchesUpdatedQuantityAndTotals()
    {
        var service = CreateBasketService([new(ProductId, 1)]);
        var state = CreateState(service);
        var initial = await state.GetBasketItemsAsync();

        await state.SetQuantityAsync(ProductId, 3);
        var updated = await state.GetBasketItemsAsync();

        Assert.AreEqual(3, updated.Single().Quantity);
        Assert.AreEqual(37.5m, updated.Sum(item => item.UnitPrice * item.Quantity));
        Assert.AreEqual(3, updated.Sum(item => item.Quantity));
        Assert.AreNotSame(initial, updated);
        Assert.AreEqual(2, service.GetCalls);
    }

    [TestMethod]
    public async Task SetQuantityZeroRemovesExistingLineAndPersistsEmptyBasket()
    {
        var service = CreateBasketService([new(ProductId, 1)]);
        var state = CreateState(service);

        await state.SetQuantityAsync(ProductId, 0);

        Assert.IsEmpty(service.PersistedBasket);
        Assert.IsEmpty(await state.GetBasketItemsAsync());
    }

    [TestMethod]
    public async Task SetQuantityNegativeRejectsWithoutPersistence()
    {
        var service = CreateBasketService([new(ProductId, 1)]);
        var state = CreateState(service);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => state.SetQuantityAsync(ProductId, -1));

        Assert.AreEqual(0, service.GetCalls);
        Assert.AreEqual(0, service.UpdateCalls);
    }

    [TestMethod]
    public async Task AddCreatesAndIncrementsBasketLines()
    {
        var service = CreateBasketService([]);
        var state = CreateState(service);
        var product = CreateProduct();

        await state.AddAsync(product);
        await state.AddAsync(product);

        Assert.AreEqual(2, service.PersistedBasket.Single().Quantity);
    }

    private static BasketServiceFake CreateBasketService(IReadOnlyCollection<BasketQuantity> initial)
    {
        var service = new BasketServiceFake(initial);
        return service;
    }

    private static BasketState CreateState(IBasketService service)
    {
        var catalog = new CatalogService(new HttpClient(new CatalogHandler()) { BaseAddress = new Uri("http://catalog/") });
        var auth = new TestAuthenticationStateProvider();
        return new BasketState(service, catalog, new OrderingService(new HttpClient()), auth);
    }

    private static CatalogItem CreateProduct()
        => new(ProductId, "Test product", "", 12.50m, "", 1, new(1, "Test"), 1, new(1, "Test"));

    private sealed class BasketServiceFake(IReadOnlyCollection<BasketQuantity> initial) : IBasketService
    {
        private IReadOnlyCollection<BasketQuantity> _basket = initial;
        public IReadOnlyCollection<BasketQuantity> PersistedBasket { get; private set; } = initial;
        public int GetCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public Task<IReadOnlyCollection<BasketQuantity>> GetBasketAsync()
        {
            GetCalls++;
            return Task.FromResult(_basket);
        }
        public Task DeleteBasketAsync() => Task.CompletedTask;
        public Task UpdateBasketAsync(IReadOnlyCollection<BasketQuantity> basket)
        {
            UpdateCalls++;
            PersistedBasket = basket.ToArray();
            _basket = PersistedBasket;
            return Task.CompletedTask;
        }
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { CreateProduct() })
            });
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity("test"))));
    }
}
