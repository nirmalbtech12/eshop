using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using eShop.Basket.API.Grpc;
using eShop.WebApp.Services;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using Grpc.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using GrpcBasketClient = eShop.Basket.API.Grpc.Basket.BasketClient;

namespace eShop.WebApp.Tests;

[TestClass]
public class BasketStateTests
{
    [TestMethod]
    public async Task SetQuantityPersistsAndRemovalKeepsTotalsConsistent()
    {
        var client = Substitute.For<GrpcBasketClient>();
        var stored = new List<BasketQuantity>();
        client.GetBasketAsync(Arg.Any<GetBasketRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Unary(new CustomerBasketResponse { Items = { stored.Select(item => new eShop.Basket.API.Grpc.BasketItem { ProductId = item.ProductId, Quantity = item.Quantity }) } }));
        client.UpdateBasketAsync(Arg.Any<UpdateBasketRequest>(), Arg.Any<Metadata>(), Arg.Any<DateTime?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                stored = call.Arg<UpdateBasketRequest>().Items.Select(item => new BasketQuantity(item.ProductId, item.Quantity)).ToList();
                return Unary(new CustomerBasketResponse());
            });

        var catalogItems = new[] { Item(1, 10m), Item(2, 5m), Item(3, 7m) };
        using var http = new HttpClient(new CatalogHandler(catalogItems)) { BaseAddress = new Uri("http://catalog/") };
        var catalog = new CatalogService(http);
        var auth = new TestAuthenticationStateProvider();
        var state = new BasketState(new BasketService(client), catalog, null!, auth);

        await state.AddAsync(catalogItems[0]);
        await state.AddAsync(catalogItems[1]);
        await state.AddAsync(catalogItems[0]);
        await state.SetQuantityAsync(1, 3);

        var refreshed = await state.GetBasketItemsAsync();
        Assert.AreEqual(3, refreshed.Single(item => item.ProductId == 1).Quantity);
        Assert.AreEqual(1, refreshed.Single(item => item.ProductId == 2).Quantity);
        Assert.AreEqual(35m, refreshed.Sum(item => item.UnitPrice * item.Quantity));
        Assert.AreEqual(4, refreshed.Sum(item => item.Quantity));

        await state.SetQuantityAsync(1, 0);
        refreshed = await state.GetBasketItemsAsync();
        Assert.AreEqual(1, refreshed.Count);
        Assert.AreEqual(2, refreshed.Single().ProductId);
        Assert.AreEqual(5m, refreshed.Sum(item => item.UnitPrice * item.Quantity));
        Assert.AreEqual(1, refreshed.Sum(item => item.Quantity));
    }

    private static CatalogItem Item(int id, decimal price) => new(id, $"Item {id}", "", price, "", 1, new(1, "Brand"), 1, new(1, "Type"));

    private static AsyncUnaryCall<T> Unary<T>(T response) => new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "buyer")], "test"))));
    }

    private sealed class CatalogHandler(CatalogItem[] items) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ids = request.RequestUri!.Query.TrimStart('?').Split('&').Select(part => int.Parse(part.Split('=')[1])).ToHashSet();
            var body = JsonSerializer.Serialize(items.Where(item => ids.Contains(item.Id)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
