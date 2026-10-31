extern alias webapp;

using System.Net;
using System.Net.Http;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Grpc.Core;
using WebBasketState = webapp::eShop.WebApp.Services.BasketState;
using WebBasketService = webapp::eShop.WebApp.Services.IBasketService;
using WebBasketQuantity = webapp::eShop.WebApp.Services.BasketQuantity;
using WebConcreteBasketService = webapp::eShop.WebApp.Services.BasketService;
using WebBasketClient = webapp::eShop.Basket.API.Grpc.Basket.BasketClient;
using WebExtensions = webapp::Extensions;
using WebCatalogService = eShop.WebAppComponents.Services.CatalogService;
using WebOrderingService = webapp::eShop.WebApp.Services.OrderingService;
using WebCatalogItem = eShop.WebAppComponents.Catalog.CatalogItem;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketStateTests
{
    [TestMethod]
    public async Task SetQuantityPersistsAndHandlesZeroNegativeTotalsAndAdd()
    {
        var registrations = new ServiceCollection();
        registrations.AddSingleton(new WebBasketClient(Substitute.For<CallInvoker>()));
        WebExtensions.AddBasketServices(registrations);
        using (var provider = registrations.BuildServiceProvider())
        {
            Assert.IsInstanceOfType<WebConcreteBasketService>(provider.GetRequiredService<WebBasketService>());
        }

        var saved = new List<WebBasketQuantity> { new(1, 1), new(2, 2) };
        var updateCalls = 0;
        var basket = Substitute.For<WebBasketService>();
        basket.GetBasketAsync().Returns(_ => Task.FromResult<IReadOnlyCollection<WebBasketQuantity>>(saved.ToArray()));
        basket.UpdateBasketAsync(Arg.Any<IReadOnlyCollection<WebBasketQuantity>>())
            .Returns(call =>
            {
                updateCalls++;
                saved = call.Arg<IReadOnlyCollection<WebBasketQuantity>>().ToList();
                return Task.CompletedTask;
            });

        var catalogJson = "[{\"id\":1,\"name\":\"One\",\"description\":\"\",\"price\":10,\"pictureUrl\":\"\",\"catalogBrandId\":1,\"catalogBrand\":{\"id\":1,\"brand\":\"Brand\"},\"catalogTypeId\":1,\"catalogType\":{\"id\":1,\"type\":\"Type\"}},{\"id\":2,\"name\":\"Two\",\"description\":\"\",\"price\":5,\"pictureUrl\":\"\",\"catalogBrandId\":1,\"catalogBrand\":{\"id\":1,\"brand\":\"Brand\"},\"catalogTypeId\":1,\"catalogType\":{\"id\":1,\"type\":\"Type\"}},{\"id\":3,\"name\":\"Three\",\"description\":\"\",\"price\":2,\"pictureUrl\":\"\",\"catalogBrandId\":1,\"catalogBrand\":{\"id\":1,\"brand\":\"Brand\"},\"catalogTypeId\":1,\"catalogType\":{\"id\":1,\"type\":\"Type\"}}]";
        var catalog = new WebCatalogService(new HttpClient(new JsonHandler(catalogJson)) { BaseAddress = new Uri("http://catalog/") });
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "shopper")], "test")))));
        var state = new WebBasketState(basket, catalog, null!, auth);

        await state.SetQuantityAsync(1, 4);
        var refreshed = await state.GetBasketItemsAsync();
        Assert.AreEqual(4, refreshed.Single(item => item.ProductId == 1).Quantity);
        Assert.AreEqual(50m, refreshed.Sum(item => item.UnitPrice * item.Quantity));

        await state.SetQuantityAsync(1, 0);
        refreshed = await state.GetBasketItemsAsync();
        Assert.AreEqual(1, refreshed.Count);
        Assert.AreEqual(2, refreshed.Single().ProductId);
        Assert.AreEqual(10m, refreshed.Sum(item => item.UnitPrice * item.Quantity));

        var updatesBeforeNegative = updateCalls;
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => state.SetQuantityAsync(2, -1));
        Assert.AreEqual(updatesBeforeNegative, updateCalls);

        await state.SetQuantityAsync(2, 0);
        Assert.IsEmpty(await state.GetBasketItemsAsync());
        await basket.Received().UpdateBasketAsync(Arg.Is<IReadOnlyCollection<WebBasketQuantity>>(items => items.Count == 0));

        await state.AddAsync(new WebCatalogItem(3, "Three", "", 2m, "", 1, new(1, "Brand"), 1, new(1, "Type")));
        await state.AddAsync(new WebCatalogItem(3, "Three", "", 2m, "", 1, new(1, "Brand"), 1, new(1, "Type")));
        Assert.AreEqual(2, saved.Single().Quantity);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
