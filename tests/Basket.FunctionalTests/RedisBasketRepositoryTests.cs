namespace eShop.Basket.FunctionalTests;

using Grpc.Core;
using Microsoft.AspNetCore.Http;
using BasketService = eShop.Basket.API.Grpc.BasketService;
using UpdateBasketRequest = eShop.Basket.API.Grpc.UpdateBasketRequest;

public sealed class RedisBasketRepositoryTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task ServiceZeroUpdateRemovesOnlySelectedProductAcrossFreshConnection()
    {
        var buyerId = $"buyer-{Guid.NewGuid():N}";
        var repository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fixture.Connection);
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var request = new UpdateBasketRequest();
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 42, Quantity = 0 });
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 43, Quantity = 2 });

        try
        {
            await service.UpdateBasket(request, CreateContext(buyerId));
            using var fresh = await fixture.ConnectFreshAsync();
            var saved = await new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fresh).GetBasketAsync(buyerId);

            Assert.NotNull(saved);
            Assert.Equal(buyerId, saved.BuyerId);
            Assert.Equal(1, saved.Items.Count);
            Assert.Equal(43, saved.Items.Single().ProductId);
            Assert.Equal(2, saved.Items.Single().Quantity);
        }
        finally
        {
            await repository.DeleteBasketAsync(buyerId);
        }
    }

    [Fact]
    public async Task ServiceNegativeUpdatePreservesEntireBasketAcrossFreshConnection()
    {
        var buyerId = $"buyer-{Guid.NewGuid():N}";
        var repository = new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fixture.Connection);
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        await repository.UpdateBasketAsync(new CustomerBasket
        {
            BuyerId = buyerId,
            Items = [new BasketItem { ProductId = 42, Quantity = 3 }, new BasketItem { ProductId = 43, Quantity = 2 }]
        });
        var request = new UpdateBasketRequest();
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 42, Quantity = 4 });
        request.Items.Add(new eShop.Basket.API.Grpc.BasketItem { ProductId = 43, Quantity = -1 });

        try
        {
            var error = await Assert.ThrowsAsync<RpcException>(() => service.UpdateBasket(request, CreateContext(buyerId)));
            Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
            using var fresh = await fixture.ConnectFreshAsync();
            var saved = await new RedisBasketRepository(NullLogger<RedisBasketRepository>.Instance, fresh).GetBasketAsync(buyerId);

            Assert.NotNull(saved);
            Assert.Equal(buyerId, saved.BuyerId);
            Assert.Equal(new[] { (42, 3), (43, 2) }, saved.Items.OrderBy(item => item.ProductId)
                .Select(item => (item.ProductId, item.Quantity)));
        }
        finally
        {
            await repository.DeleteBasketAsync(buyerId);
        }
    }

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

    private static ServerCallContext CreateContext(string buyerId)
    {
        var context = TestCallContext.Create();
        var httpContext = new DefaultHttpContext();
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", buyerId)]));
        context.SetUserState("__HttpContext", httpContext);
        return context;
    }

    private sealed class TestCallContext : ServerCallContext
    {
        private readonly Dictionary<object, object> _userState = [];
        protected override string MethodCore => "UpdateBasket";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "test";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => new();
        protected override Status StatusCore { get; set; }
        protected override WriteOptions WriteOptionsCore { get; set; } = null!;
        protected override AuthContext AuthContextCore => new(string.Empty, new Dictionary<string, List<AuthProperty>>());
        protected override IDictionary<object, object> UserStateCore => _userState;
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;

        public static TestCallContext Create() => new();
        public void SetUserState(object key, object value) => _userState[key] = value;
    }
}
