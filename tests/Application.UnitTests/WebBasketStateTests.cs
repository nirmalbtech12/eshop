using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using eShop.Basket.API.Grpc;
using eShop.WebApp.Services;
using eShop.WebAppComponents.Catalog;
using eShop.WebAppComponents.Services;
using Grpc.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using GrpcBasketClient = eShop.Basket.API.Grpc.Basket.BasketClient;

namespace eShop.Application.UnitTests;

[TestClass]
public class WebBasketStateTests
{
    [TestMethod]
    public async Task PositiveQuantityPersistsAndFailedSaveDoesNotMutateCachedRows()
    {
        var fixture = new BasketFixture([(42, 1), (43, 2)]);
        var notifications = 0;
        using var subscription = fixture.State.NotifyOnChange(EventCallback.Factory.Create(this, () => notifications++));
        var originalRows = await fixture.State.GetBasketItemsAsync();

        await fixture.State.SetQuantityAsync(42, 3);
        CollectionAssert.AreEqual(new[] { (42, 3), (43, 2) }, fixture.SavedItems.ToArray());
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(1, originalRows.Single(item => item.ProductId == 42).Quantity);
        Assert.AreEqual(3, (await fixture.State.GetBasketItemsAsync()).Single(item => item.ProductId == 42).Quantity);

        var committedRows = await fixture.State.GetBasketItemsAsync();
        fixture.FailNextUpdate = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.State.SetQuantityAsync(42, 5));
        Assert.AreEqual(3, committedRows.Single(item => item.ProductId == 42).Quantity);
        Assert.AreEqual(3, fixture.SavedItems.Single(item => item.ProductId == 42).Quantity);
        Assert.AreEqual(1, notifications);
        Assert.AreEqual(3, (await fixture.State.GetBasketItemsAsync()).Single(item => item.ProductId == 42).Quantity);
    }

    [TestMethod]
    public async Task ZeroQuantityRemovesOnlyTargetLine()
    {
        var fixture = new BasketFixture([(42, 1), (43, 2)]);
        var notifications = 0;
        using var subscription = fixture.State.NotifyOnChange(EventCallback.Factory.Create(this, () => notifications++));

        await fixture.State.SetQuantityAsync(42, 0);

        CollectionAssert.AreEqual(new[] { (43, 2) }, fixture.SavedItems.ToArray());
        CollectionAssert.AreEqual(new[] { (43, 2) }, (await fixture.State.GetBasketItemsAsync())
            .Select(item => (item.ProductId, item.Quantity)).ToArray());
        Assert.AreEqual(1, notifications);
    }

    [TestMethod]
    public async Task NegativeQuantityIsRejectedBeforeStateAccess()
    {
        var fixture = new BasketFixture([(42, 1)]);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.State.SetQuantityAsync(42, -1));

        Assert.AreEqual(0, fixture.GetCalls);
        Assert.AreEqual(0, fixture.UpdateCalls);
    }

    [TestMethod]
    public async Task AddCreatesOrIncrementsWithoutDuplicateLines()
    {
        var fixture = new BasketFixture([(42, 2)]);
        var newItem = BasketFixture.CatalogItems.Single(item => item.Id == 43);

        await fixture.State.AddAsync(newItem);
        CollectionAssert.AreEqual(new[] { (42, 2), (43, 1) }, fixture.SavedItems.ToArray());
        await fixture.State.AddAsync(newItem);

        CollectionAssert.AreEqual(new[] { (42, 2), (43, 2) }, fixture.SavedItems.ToArray());
        Assert.AreEqual(1, fixture.SavedItems.Count(item => item.ProductId == 43));
    }

    private sealed class BasketFixture
    {
        public static readonly CatalogItem[] CatalogItems =
        [
            new(42, "Product 42", "", 12.50m, "", 1, new(1, "Brand"), 1, new(1, "Type")),
            new(43, "Product 43", "", 8m, "", 1, new(1, "Brand"), 1, new(1, "Type"))
        ];

        private readonly List<(int ProductId, int Quantity)> _items;

        public BasketFixture((int ProductId, int Quantity)[] initialItems)
        {
            _items = [.. initialItems];
            Client = Substitute.For<GrpcBasketClient>(Substitute.For<ChannelBase>("localhost"));
            Client.GetBasketAsync(Arg.Any<GetBasketRequest>()).Returns(_ =>
            {
                GetCalls++;
                return UnaryCall(ToResponse());
            });
            Client.UpdateBasketAsync(Arg.Any<UpdateBasketRequest>()).Returns(call =>
            {
                UpdateCalls++;
                if (FailNextUpdate)
                {
                    FailNextUpdate = false;
                    throw new InvalidOperationException("Simulated basket persistence failure.");
                }

                var request = call.Arg<UpdateBasketRequest>();
                _items.Clear();
                _items.AddRange(request.Items.Select(item => (item.ProductId, item.Quantity)));
                SavedItems = [.. _items];
                return UnaryCall(ToResponse());
            });

            var handler = new CatalogHandler();
            var catalog = new CatalogService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });
            State = new BasketState(
                new BasketService(Client),
                catalog,
                null!,
                new TestAuthenticationStateProvider());
            SavedItems = [.. _items];
        }

        public GrpcBasketClient Client { get; }
        public BasketState State { get; }
        public List<(int ProductId, int Quantity)> SavedItems { get; private set; }
        public bool FailNextUpdate { get; set; }
        public int GetCalls { get; private set; }
        public int UpdateCalls { get; private set; }

        private CustomerBasketResponse ToResponse()
        {
            var response = new CustomerBasketResponse();
            response.Items.AddRange(_items.Select(item => new eShop.Basket.API.Grpc.BasketItem
            {
                ProductId = item.ProductId,
                Quantity = item.Quantity
            }));
            return response;
        }

        private static AsyncUnaryCall<CustomerBasketResponse> UnaryCall(CustomerBasketResponse response)
            => new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess,
                () => new Metadata(), () => { });
    }

    private sealed class CatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(BasketFixture.CatalogItems)
            });
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "buyer-1")], "test"))));
    }
}
