using System.Net;
using System.Security.Claims;
using System.Text.Json;
using eShop.Basket.API.Grpc;
using eShop.WebApp.Services;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using Grpc.Core;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using GrpcBasketItem = eShop.Basket.API.Grpc.BasketItem;
using GrpcBasketClient = eShop.Basket.API.Grpc.Basket.BasketClient;
using WebBasketItem = eShop.WebApp.Services.BasketItem;

namespace eShop.WebApp.UnitTests;

[TestClass]
public class BasketStateTests
{
    [TestMethod]
    public async Task SetQuantityAndAddPreserveBasketSemantics()
    {
        var invoker = new BasketCallInvoker([new BasketQuantity(42, 3), new BasketQuantity(43, 2)]);
        var client = new GrpcBasketClient(invoker);
        var basketService = new BasketService(client);
        var catalog = new CatalogService(new HttpClient(new CatalogHandler()) { BaseAddress = new Uri("http://localhost") });
        var state = new BasketState(basketService, catalog, new OrderingService(new HttpClient()), new TestAuthenticationStateProvider());

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => state.SetQuantityAsync(42, -1));
        Assert.AreEqual(0, invoker.UpdateRequests.Count);
        Assert.AreEqual(0, invoker.GetRequests);

        await state.SetQuantityAsync(42, 5);
        AssertBasket(invoker.UpdateRequests[0], (42, 5), (43, 2));
        var itemsAfterUpdate = await state.GetBasketItemsAsync();
        Assert.AreEqual(7, BasketTotals.Quantity(itemsAfterUpdate));
        Assert.AreEqual(66.50m, BasketTotals.Price(itemsAfterUpdate));
        Assert.AreEqual(5, itemsAfterUpdate.Single(item => item.ProductId == 42).Quantity);
        Assert.AreEqual(12.50m, itemsAfterUpdate.Single(item => item.ProductId == 42).UnitPrice);

        await state.SetQuantityAsync(43, 0);
        AssertBasket(invoker.UpdateRequests[1], (42, 5));

        await state.AddAsync(Catalog(42));
        AssertBasket(invoker.UpdateRequests[2], (42, 6));

        await state.AddAsync(Catalog(44));
        AssertBasket(invoker.UpdateRequests[3], (42, 6), (44, 1));

        var finalItems = await state.GetBasketItemsAsync();
        Assert.AreEqual(7, BasketTotals.Quantity(finalItems));
        Assert.AreEqual(77m, BasketTotals.Price(finalItems));
    }

    private static CatalogItem Catalog(int id) => new(id, $"Product {id}", "", id == 42 ? 12.50m : 2m, "", 0, null!, 0, null!);

    private static void AssertBasket(IReadOnlyCollection<BasketQuantity> actual, params (int ProductId, int Quantity)[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Count);
        foreach (var (productId, quantity) in expected)
        {
            var item = actual.Single(item => item.ProductId == productId);
            Assert.AreEqual(quantity, item.Quantity);
        }
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "buyer-1")], "test"))));
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var ids = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Where(part => part.StartsWith("ids=", StringComparison.Ordinal))
                .Select(part => int.Parse(part[4..]))
                .Distinct();
            var items = ids.Select(Catalog).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
            });
        }
    }

    private sealed class BasketCallInvoker(List<BasketQuantity> items) : CallInvoker
    {
        public List<BasketQuantity[]> UpdateRequests { get; } = [];
        public int GetRequests { get; private set; }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            if (method.Name == "GetBasket")
            {
                GetRequests++;
                var response = new CustomerBasketResponse();
                response.Items.AddRange(items.Select(item => new GrpcBasketItem { ProductId = item.ProductId, Quantity = item.Quantity }));
                return Unary((TResponse)(object)response);
            }

            if (method.Name == "UpdateBasket")
            {
                var update = (UpdateBasketRequest)(object)request!;
                var updated = update.Items.Select(item => new BasketQuantity(item.ProductId, item.Quantity)).ToArray();
                UpdateRequests.Add(updated);
                items.Clear();
                items.AddRange(updated);
                var response = new CustomerBasketResponse();
                response.Items.AddRange(items.Select(item => new GrpcBasketItem { ProductId = item.ProductId, Quantity = item.Quantity }));
                return Unary((TResponse)(object)response);
            }

            throw new NotSupportedException($"Unexpected basket method {method.Name}");
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();

        private static AsyncUnaryCall<T> Unary<T>(T response)
            => new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }
}
