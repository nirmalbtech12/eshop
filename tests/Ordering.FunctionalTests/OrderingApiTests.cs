using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Ordering.API.Application.Commands;
using eShop.Ordering.API.Application.Models;
using eShop.Ordering.API.Application.Queries;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace eShop.Ordering.FunctionalTests;

public sealed class OrderingApiTests : IClassFixture<OrderingApiFixture>
{
    private readonly OrderingApiFixture _fixture;

    public OrderingApiTests(OrderingApiFixture fixture)
    {
        var handler = new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0));

        _fixture = fixture;
        _httpClient = fixture.Api.CreateDefaultClient(handler);
    }

    private HttpClient _httpClient;

    [Fact]
    public async Task CheckoutRetryAfterApiRestartKeepsSingleOrderAndHealth()
    {
        var requestId = Guid.NewGuid();
        var userId = requestId.ToString("N");
        var street = $"restart-{userId}";
        var orderRequest = new CreateOrderRequest(userId, "TestUser", "Seattle", street, "WA", "US", "98101", "XXXXXXXXXXXX0005", "Test User", DateTime.UtcNow.AddYears(1), "123", 1, "test buyer",
            [new BasketItem { Id = "restart-test", ProductId = 12, ProductName = "Restart Test", UnitPrice = 173.29m, OldUnitPrice = 150, Quantity = 1 }]);

        async Task<HttpResponseMessage> SubmitCheckout() => await _httpClient.PostAsync("api/orders", new StringContent(JsonSerializer.Serialize(orderRequest), UTF8Encoding.UTF8, "application/json")
        {
            Headers = { { "x-requestid", requestId.ToString() } }
        }, TestContext.Current.CancellationToken);

        await using var database = new NpgsqlConnection(_fixture.PostgresConnectionString);
        await database.OpenAsync(TestContext.Current.CancellationToken);
        _fixture.CheckoutRestart.Enabled = true;
        var interruptedCheckout = SubmitCheckout();
        await _fixture.CheckoutRestart.OrderAdded.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await _fixture.RestartApiAsync();
        try
        {
            using var interruptedResponse = await interruptedCheckout.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, interruptedResponse.StatusCode);
        }
        catch (TaskCanceledException)
        {
        }

        Assert.Equal(0, await CountRequestAsync(database, requestId, TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountOrdersAsync(database, street, TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountUserEventsAsync(database, userId, TestContext.Current.CancellationToken));
        _httpClient.Dispose();
        _httpClient = _fixture.Api.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));

        var healthResponse = await _httpClient.GetAsync("health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        var retryResponse = await SubmitCheckout();
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

        Assert.Equal(1, await CountRequestAsync(database, requestId, TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountOrdersAsync(database, street, TestContext.Current.CancellationToken));
        Assert.True(await CountDurableEventsAsync(database, userId, TestContext.Current.CancellationToken) >= 1);
    }

    private static async Task<int> CountRequestAsync(NpgsqlConnection database, Guid requestId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM ordering.requests WHERE \"Id\" = @id;", database);
        command.Parameters.AddWithValue("id", requestId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountOrdersAsync(NpgsqlConnection database, string street, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM ordering.orders WHERE \"Address_Street\" = @street;", database);
        command.Parameters.AddWithValue("street", street);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountDurableEventsAsync(NpgsqlConnection database, string userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM ordering.\"IntegrationEventLog\" WHERE \"Content\" LIKE @userId AND \"State\" = 2;", database);
        command.Parameters.AddWithValue("userId", $"%{userId}%");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int> CountUserEventsAsync(NpgsqlConnection database, string userId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM ordering.\"IntegrationEventLog\" WHERE \"Content\" LIKE @userId;", database);
        command.Parameters.AddWithValue("userId", $"%{userId}%");
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
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
        var response = await _httpClient.GetAsync($"api/orders/{int.MaxValue}", TestContext.Current.CancellationToken);
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
