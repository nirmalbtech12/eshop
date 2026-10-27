using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System.Text.Json;
using System.Threading;
using Asp.Versioning;
using Asp.Versioning.Http;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using eShop.Ordering.API.Application.Commands;
using eShop.Ordering.API.Application.Models;
using eShop.Ordering.API.Application.Queries;
using eShop.Ordering.Infrastructure;
using eShop.IntegrationEventLogEF;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace eShop.Ordering.FunctionalTests;

public sealed class OrderingApiTests : IClassFixture<OrderingApiFixture>
{
    private readonly WebApplicationFactory<Program> _webApplicationFactory;
    private readonly HttpClient _httpClient;

    public OrderingApiTests(OrderingApiFixture fixture)
    {
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
    public async Task AspireReadinessGatesConsumerUntilOrderingIsHealthy()
    {
        await AssertConsumerWaitsForHealthAsync("ordering-api", "webapp");
    }

    [Fact]
    public async Task AspireReadinessGatesWebAppUntilBasketIsHealthy()
    {
        await AssertConsumerWaitsForHealthAsync("basket-api", "webapp");
    }

    [Fact]
    public async Task AspireReadinessGatesWebhooksUntilIdentityIsHealthy()
    {
        await AssertConsumerWaitsForHealthAsync("identity-api", "webhooks-api");
    }

    private static async Task AssertConsumerWaitsForHealthAsync(string dependencyName, string consumerName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var repositoryRoot = FindRepositoryRoot();
        var readinessProbe = Path.Combine(repositoryRoot, "tests", "Aspire.ReadinessProbe", "Aspire.ReadinessProbe.csproj");
        var gateFile = Path.Combine(Path.GetTempPath(), $"{dependencyName}-ready-{Guid.NewGuid():N}");
        var orderingPort = GetFreePort();
        var consumerPort = GetFreePort();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            AssemblyName = typeof(OrderingApiTests).Assembly.FullName,
            DisableDashboard = true
        });
        var dependency = builder.AddExecutable(dependencyName, "dotnet", repositoryRoot,
                "run", "--project", readinessProbe, "--", "ordering", gateFile)
            .WithHttpEndpoint(port: orderingPort, env: "PORT")
            .WithHttpHealthCheck("/health");
        builder.AddExecutable(consumerName, "dotnet", repositoryRoot,
                "run", "--project", readinessProbe, "--", "consumer", gateFile)
            .WithHttpEndpoint(port: consumerPort, env: "PORT")
            .WithHttpHealthCheck("/health")
            .WaitFor(dependency);

        await using var app = builder.Build();
        try
        {
            var startTask = app.StartAsync(timeout.Token);

            using var dependencyClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{orderingPort}") };
            using var response = await WaitForUnhealthyHealthAsync(dependencyClient, timeout.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

            using var consumerClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{consumerPort}"), Timeout = TimeSpan.FromSeconds(2) };
            var consumerBlocked = false;
            try
            {
                using var blockedResponse = await consumerClient.GetAsync("/health", timeout.Token);
                consumerBlocked = blockedResponse.StatusCode != HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                consumerBlocked = true;
            }
            catch (TaskCanceledException)
            {
                consumerBlocked = true;
            }
            Assert.True(consumerBlocked, $"The dependent resource '{consumerName}' became ready while '{dependencyName}' was unhealthy.");

            await File.WriteAllTextAsync(gateFile, "ready", timeout.Token);
            await startTask;
            await app.ResourceNotifications.WaitForResourceHealthyAsync(dependencyName, timeout.Token);
            await app.ResourceNotifications.WaitForResourceHealthyAsync(consumerName, timeout.Token);

            using var readyResponse = await consumerClient.GetAsync("/health", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
        }
        finally
        {
            if (File.Exists(gateFile))
            {
                File.Delete(gateFile);
            }
        }
    }

    private static async Task<HttpResponseMessage> WaitForUnhealthyHealthAsync(HttpClient client, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var response = await client.GetAsync("/health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    return response;
                }
                response.Dispose();
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eShop.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the eShop repository root.");
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
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
        var response = await _httpClient.GetAsync("api/orders/1", TestContext.Current.CancellationToken);
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
    public async Task CheckoutRestartPreservesOrderAndRetryDoesNotDuplicate()
    {
        await using var isolatedFixture = new OrderingApiFixture();
        await isolatedFixture.InitializeAsync();
        var requestId = Guid.NewGuid();
        var item = new BasketItem { Id = "restart-test", ProductId = 12, ProductName = "Restart test", UnitPrice = 10, OldUnitPrice = 10, Quantity = 1 };
        var request = new CreateOrderRequest("restart-test-user", "Restart Test", "city", "street", "state", "country", "12345", "XXXXXXXXXXXX0005", "Restart Test", DateTime.UtcNow.AddYears(1), "123", 1, null, new List<BasketItem> { item });

        async Task<HttpResponseMessage> CheckoutAsync(HttpClient client, bool holdInFlight = false)
        {
            var content = new StringContent(JsonSerializer.Serialize(request), UTF8Encoding.UTF8, "application/json");
            content.Headers.Add("x-requestid", requestId.ToString());
            if (holdInFlight)
            {
                content.Headers.Add("x-test-hold-checkout", "true");
            }
            return await client.PostAsync("api/orders", content, TestContext.Current.CancellationToken);
        }

        async Task<(int Orders, int Outbox, int CheckoutEvents)> ReadPersistedStateAsync(WebApplicationFactory<Program> factory)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrderingContext>();
            var checkoutEvents = await db.Set<IntegrationEventLogEntry>()
                .Where(entry => entry.Content.Contains(request.UserId))
                .CountAsync();
            return (await db.Orders.CountAsync(), await db.Set<IntegrationEventLogEntry>().CountAsync(), checkoutEvents);
        }

        var requestGate = new OrderingApiFixture.CheckoutRequestGate();
        await using var firstFactory = isolatedFixture.CreateOrderingApiFactory(requestGate);
        var firstClient = firstFactory.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));
        var stateBeforeCheckout = await ReadPersistedStateAsync(firstFactory);
        var inFlightCheckout = CheckoutAsync(firstClient, holdInFlight: true);
        await requestGate.RequestCommitted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await firstFactory.DisposeAsync();
        firstClient.Dispose();
        var interruptedDuringRestart = false;
        try
        {
            using var interruptedResponse = await inFlightCheckout;
            interruptedDuringRestart = !interruptedResponse.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            // The server shutdown interrupted the response after checkout committed.
            interruptedDuringRestart = true;
        }
        catch (TaskCanceledException)
        {
            // The server shutdown interrupted the response after checkout committed.
            interruptedDuringRestart = true;
        }
        Assert.True(interruptedDuringRestart, "The buffered checkout response reached the client despite restarting the API before releasing it.");

        await using var restartedFactory = isolatedFixture.CreateOrderingApiFactory();
        using var restartedClient = restartedFactory.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));
        var stateAfterRestart = await ReadPersistedStateAsync(restartedFactory);
        Assert.Equal(stateBeforeCheckout.Orders + 1, stateAfterRestart.Orders);
        Assert.True(stateAfterRestart.Outbox > stateBeforeCheckout.Outbox);
        Assert.True(stateAfterRestart.CheckoutEvents > stateBeforeCheckout.CheckoutEvents);

        // Retry the interrupted request ID after restarting the API host.
        var secondResponse = await CheckoutAsync(restartedClient);
        secondResponse.EnsureSuccessStatusCode();
        var stateAfterRetry = await ReadPersistedStateAsync(restartedFactory);
        Assert.Equal(stateAfterRestart, stateAfterRetry);
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
