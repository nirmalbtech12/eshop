using System.Net;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Ordering.API.Application.Commands;
using eShop.Ordering.API.Application.Models;
using eShop.Ordering.API.Application.Queries;
using eShop.Ordering.Infrastructure;
using eShop.Ordering.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;

namespace eShop.Ordering.FunctionalTests;

public sealed class OrderingApiTests : IClassFixture<OrderingApiFixture>
{
    private readonly OrderingApiFixture _fixture;
    private readonly WebApplicationFactory<Program> _webApplicationFactory;
    private readonly HttpClient _httpClient;

    public OrderingApiTests(OrderingApiFixture fixture)
    {
        _fixture = fixture;
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0));

        _webApplicationFactory = fixture;
        _httpClient = _webApplicationFactory.CreateDefaultClient(handler);
    }

    [Fact]
    public async Task GetAllStoredOrdersWorks()
    {
        // Act
        var response = await _httpClient.GetAsync("api/orders", TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CancelWithEmptyGuidFails()
    {
        // Act
        var content = new StringContent(BuildOrder(), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.Empty.ToString() } }
        };
        var response = await _httpClient.PutAsync("/api/orders/cancel", content, TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CancelNonExistentOrderFails()
    {
        // Act
        var content = new StringContent(BuildOrder(), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.NewGuid().ToString() } }
        };
        var response = await _httpClient.PutAsync("api/orders/cancel", content, TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task ShipWithEmptyGuidFails()
    {
        // Act
        var content = new StringContent(BuildOrder(), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.Empty.ToString() } }
        };
        var response = await _httpClient.PutAsync("api/orders/ship", content, TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ShipNonExistentOrderFails()
    {
        // Act
        var content = new StringContent(BuildOrder(), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.NewGuid().ToString() } }
        };
        var response = await _httpClient.PutAsync("api/orders/ship", content, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task GetAllOrdersCardType()
    {
        // Act 1
        var response = await _httpClient.GetAsync("api/orders/cardtypes", TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetStoredOrdersWithOrderId()
    {
        // Act
        var response = await _httpClient.GetAsync("api/orders/2147483647", TestContext.Current.CancellationToken);
        var responseStatus = response.StatusCode;

        // Assert
        Assert.Equal("NotFound", responseStatus.ToString());
    }

    [Fact]
    public async Task AddNewEmptyOrder()
    {
        // Act
        var content = new StringContent(JsonSerializer.Serialize(new Order()), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.Empty.ToString() } }
        };
        var response = await _httpClient.PostAsync("api/orders", content, TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AddNewOrder()
    {
        // Act
        var item = new BasketItem
        {
            Id = "1",
            ProductId = 12,
            ProductName = "Test",
            UnitPrice = 10,
            OldUnitPrice = 9,
            Quantity = 1,
            PictureUrl = null
        };
        var cardExpirationDate = Convert.ToDateTime("2023-12-22T12:34:24.334Z");
        var OrderRequest = new CreateOrderRequest("1", "TestUser", null, null, null, null, null, "XXXXXXXXXXXX0005", "Test User", cardExpirationDate, "test buyer", 1, null, new List<BasketItem> { item });
        var content = new StringContent(JsonSerializer.Serialize(OrderRequest), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.NewGuid().ToString() } }
        };
        var response = await _httpClient.PostAsync("api/orders", content, TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RetryingOrderAfterOrderingRestartKeepsOnePersistedOrder()
    {
        var item = new BasketItem
        {
            Id = "1",
            ProductId = 12,
            ProductName = "Restart test",
            UnitPrice = 10,
            OldUnitPrice = 9,
            Quantity = 1,
            PictureUrl = null
        };
        var request = new CreateOrderRequest("restart-test-" + Guid.NewGuid(), "Restart Test", "Seattle", "1 Main St", "WA", "USA", "98101",
            "XXXXXXXXXXXX0005", "Restart Test", DateTime.UtcNow.AddYears(1), "123", 1, null, new List<BasketItem> { item });
        var requestId = Guid.NewGuid();

        var heldResponse = new HoldOrderResponseStartupFilter();
        var restartableFactory = _fixture.CreateOrderingApiFactory(heldResponse);
        var restartableClient = restartableFactory.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));
        var restartableFactoryDisposed = false;
        HttpRequestMessage CreateOrderRequest()
        {
            var content = new StringContent(JsonSerializer.Serialize(request), UTF8Encoding.UTF8, "application/json");
            content.Headers.Add("x-requestid", requestId.ToString());
            return new HttpRequestMessage(HttpMethod.Post, "api/orders") { Content = content };
        }

        try
        {
            using var submissionCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using var initialRequest = CreateOrderRequest();
            var submission = restartableClient.SendAsync(initialRequest, submissionCancellation.Token);
            await heldResponse.ResponseHeld.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            await using (var scope = restartableFactory.Services.CreateAsyncScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<OrderingContext>();
                Assert.Equal(1, await context.Orders.CountAsync(
                    order => order.Buyer.IdentityGuid == request.UserId, TestContext.Current.CancellationToken));
                Assert.Equal(1, await context.Set<ClientRequest>().CountAsync(
                    clientRequest => clientRequest.Id == requestId, TestContext.Current.CancellationToken));
            }

            Assert.False(submission.IsCompleted);

            // The order is committed, but the checkout caller has not received its
            // response. Stop Ordering at this boundary to simulate a lost response.
            await restartableFactory.DisposeAsync();
            restartableFactoryDisposed = true;
            heldResponse.Release();
            await Assert.ThrowsAnyAsync<Exception>(() => submission);
        }
        finally
        {
            heldResponse.Release();
            restartableClient.Dispose();
            if (!restartableFactoryDisposed)
            {
                await restartableFactory.DisposeAsync();
            }
        }

        await using var restartedFactory = _fixture.CreateOrderingApiFactory();
        using var restartedClient = restartedFactory.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));
        using var retryRequest = CreateOrderRequest();
        var retryResponse = await restartedClient.SendAsync(retryRequest, TestContext.Current.CancellationToken);
        retryResponse.EnsureSuccessStatusCode();

        await using var restartedScope = restartedFactory.Services.CreateAsyncScope();
        var restartedContext = restartedScope.ServiceProvider.GetRequiredService<OrderingContext>();
        Assert.Equal(1, await restartedContext.Orders.CountAsync(
            order => order.Buyer.IdentityGuid == request.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await restartedContext.Set<ClientRequest>().CountAsync(
            clientRequest => clientRequest.Id == requestId, TestContext.Current.CancellationToken));
    }

    private sealed class HoldOrderResponseStartupFilter : IStartupFilter
    {
        private readonly TaskCompletionSource _responseHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ResponseHeld => _responseHeld.Task;
        public void Release() => _release.TrySetResult();

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/orders"))
                {
                    var originalBody = context.Response.Body;
                    await using var bufferedBody = new MemoryStream();
                    context.Response.Body = bufferedBody;
                    try
                    {
                        await nextMiddleware(context);
                        _responseHeld.TrySetResult();
                        await _release.Task.WaitAsync(context.RequestAborted);
                        if (!context.RequestAborted.IsCancellationRequested)
                        {
                            bufferedBody.Position = 0;
                            await bufferedBody.CopyToAsync(originalBody, context.RequestAborted);
                        }
                    }
                    finally
                    {
                        context.Response.Body = originalBody;
                    }
                }
                else
                {
                    await nextMiddleware(context);
                }
            });
            next(app);
        };
    }

    [Fact]
    public async Task PostDraftOrder()
    {
        // Act
        var item = new BasketItem
        {
            Id = "1",
            ProductId = 12,
            ProductName = "Test",
            UnitPrice = 10,
            OldUnitPrice = 9,
            Quantity = 1,
            PictureUrl = null
        };
        var bodyContent = new CustomerBasket("1", new List<BasketItem> { item });
        var content = new StringContent(JsonSerializer.Serialize(bodyContent), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.NewGuid().ToString() } }
        };
        var response = await _httpClient.PostAsync("api/orders/draft", content, TestContext.Current.CancellationToken);
        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateOrderDraftSucceeds()
    {
        var payload = FakeOrderDraftCommand();
        var content = new StringContent(JsonSerializer.Serialize(FakeOrderDraftCommand()), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", Guid.NewGuid().ToString() } }
        };
        var response = await _httpClient.PostAsync("api/orders/draft", content, TestContext.Current.CancellationToken);

        var s = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var responseData = JsonSerializer.Deserialize<OrderDraftDTO>(s, new JsonSerializerOptions() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(payload.Items.Count(), responseData.OrderItems.Count());
        Assert.Equal(payload.Items.Sum(o => o.Quantity * o.UnitPrice), responseData.Total);
        AssertThatOrderItemsAreTheSameAsRequestPayloadItems(payload, responseData);
    }

    private CreateOrderDraftCommand FakeOrderDraftCommand()
    {
        return new CreateOrderDraftCommand(
            BuyerId: Guid.NewGuid().ToString(),
            new List<BasketItem>()
            {
                new BasketItem()
                {
                    Id = Guid.NewGuid().ToString(),
                    ProductId = 1,
                    ProductName = "Test Product 1",
                    UnitPrice = 10.2m,
                    OldUnitPrice = 9.8m,
                    Quantity = 2,
                    PictureUrl = Guid.NewGuid().ToString(),
                }
            });
    }

    private static void AssertThatOrderItemsAreTheSameAsRequestPayloadItems(CreateOrderDraftCommand payload, OrderDraftDTO responseData)
    {
        // check that OrderItems contain all product Ids from the payload
        var payloadItemsProductIds = payload.Items.Select(x => x.ProductId);
        var orderItemsProductIds = responseData.OrderItems.Select(x => x.ProductId);
        Assert.All(orderItemsProductIds, orderItemProdId => payloadItemsProductIds.Contains(orderItemProdId));
        // TODO: might need to add more asserts in here
    }

    string BuildOrder()
    {
        var order = new
        {
            OrderNumber = "-1"
        };
        return JsonSerializer.Serialize(order);
    }
}
