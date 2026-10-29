using System.Net;
using System.Security.Claims;
using System.Threading;
using eShop.Ordering.Infrastructure;
using eShop.Ordering.Infrastructure.Idempotency;
using eShop.Ordering.API.Application.Commands;
using eShop.Ordering.API.Application.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace eShop.Ordering.FunctionalTests;

public sealed class CheckoutRestartTests(OrderingApiFixture fixture) : IClassFixture<OrderingApiFixture>
{
    [Fact]
    public async Task CheckoutSurvivesPrecommitAndCommittedRestart()
    {
        var checkoutIdentity = $"restart-{Guid.NewGuid():N}";
        var precommitIdentity = $"restart-precommit-{Guid.NewGuid():N}";
        var request = CreateRequest(checkoutIdentity);
        var precommitRequest = CreateRequest(precommitIdentity);
        var precommitRequestId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        await using (var firstApi = new RestartableOrderingApiFactory(fixture.PostgresConnectionString, fixture.IdentityEndpoint, fixture.RabbitMqConnectionString))
        {
            using var client = firstApi.CreateClient();
            await InstallOrderInsertFailure(fixture.PostgresConnectionString);
            try
            {
                using var failed = await PostOrder(client, precommitRequestId, precommitRequest);
                Assert.True(failed.StatusCode == HttpStatusCode.InternalServerError,
                    $"Expected injected transaction failure, got {(int)failed.StatusCode}: {await failed.Content.ReadAsStringAsync()}");
            }
            finally
            {
                await RemoveOrderInsertFailure(fixture.PostgresConnectionString);
            }
            await using (var failedScope = firstApi.Services.CreateAsyncScope())
            {
                var failedDb = failedScope.ServiceProvider.GetRequiredService<OrderingContext>();
                Assert.False(await failedDb.Orders.Include(order => order.Buyer).AnyAsync(order => order.Buyer.IdentityGuid == precommitIdentity));
                Assert.False(await failedDb.Set<ClientRequest>().AnyAsync(marker => marker.Id == precommitRequestId));
            }
            using var retried = await PostOrder(client, precommitRequestId, precommitRequest);
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
            using var responseLossClient = firstApi.CreateDefaultClient(new ResponseLossHandler(firstApi.Server.CreateHandler()));
            await Assert.ThrowsAsync<HttpRequestException>(() => PostOrder(responseLossClient, requestId, request));
        }

        OrderSnapshot committed;
        await using (var snapshotScope = fixture.Services.CreateAsyncScope())
        {
            committed = await CaptureSnapshot(snapshotScope.ServiceProvider.GetRequiredService<OrderingContext>(), requestId, checkoutIdentity);
        }

        await using var restartedApi = new RestartableOrderingApiFactory(fixture.PostgresConnectionString, fixture.IdentityEndpoint, fixture.RabbitMqConnectionString);
        using var restartedClient = restartedApi.CreateClient();
        using var retry = await PostOrder(restartedClient, requestId, request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        using var scope = restartedApi.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderingContext>();
        var afterRestart = await CaptureSnapshot(db, requestId, checkoutIdentity);
        Assert.Equal(committed, afterRestart);
        Assert.Equal(1, await db.Orders.CountAsync(order => order.Buyer.IdentityGuid == checkoutIdentity));
        Assert.Equal(1, await db.Set<ClientRequest>().CountAsync(marker => marker.Id == requestId));
    }

    private static CreateOrderRequest CreateRequest(string userId) => new(
        userId, "TestUser", "Seattle", "1 Main St", "WA", "USA", "98101", "1234567812340005", "Test User",
        DateTime.UtcNow.AddYears(1), "123", 1, "test buyer",
        [new BasketItem { Id = "1", ProductId = 12, ProductName = "Test", UnitPrice = 10, OldUnitPrice = 9, Quantity = 1 }]);

    private static async Task InstallOrderInsertFailure(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE OR REPLACE FUNCTION ordering.reject_checkout_order() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'Injected precommit checkout failure';
            END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_checkout_order BEFORE INSERT ON ordering.orders
            FOR EACH ROW EXECUTE FUNCTION ordering.reject_checkout_order();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveOrderInsertFailure(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            DROP TRIGGER IF EXISTS reject_checkout_order ON ordering.orders;
            DROP FUNCTION IF EXISTS ordering.reject_checkout_order();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<OrderSnapshot> CaptureSnapshot(OrderingContext db, Guid requestId, string checkoutIdentity)
    {
        var marker = await db.Set<ClientRequest>().SingleAsync(marker => marker.Id == requestId);
        Assert.Equal(nameof(CreateOrderCommand), marker.Name);
        var order = await db.Orders.Include(order => order.Buyer).Include(order => order.OrderItems)
            .SingleAsync(order => order.Buyer.IdentityGuid == checkoutIdentity);
        var eventEntries = await db.Set<eShop.IntegrationEventLogEF.IntegrationEventLogEntry>()
            .Where(entry => entry.EventTypeName == "eShop.Ordering.API.Application.IntegrationEvents.Events.OrderStartedIntegrationEvent")
            .ToListAsync();
        var events = eventEntries.Where(entry =>
        {
            using var content = System.Text.Json.JsonDocument.Parse(entry.Content);
            return content.RootElement.GetProperty("UserId").GetString() == checkoutIdentity;
        }).Select(entry => new EventSnapshot(entry.EventId, entry.EventTypeName, entry.Content, entry.TransactionId)).ToList();
        var item = Assert.Single(order.OrderItems);
        var eventSnapshot = Assert.Single(events);
        Assert.Equal(requestId, marker.Id);
        using (var eventContent = System.Text.Json.JsonDocument.Parse(eventSnapshot.Content))
        {
            Assert.Equal(checkoutIdentity, eventContent.RootElement.GetProperty("UserId").GetString());
        }
        Assert.Equal(checkoutIdentity, order.Buyer.IdentityGuid);
        Assert.NotEqual(Guid.Empty, eventSnapshot.TransactionId);
        return new OrderSnapshot(order.Id, order.BuyerId, order.Buyer.IdentityGuid, order.Address.Street, item.Id, item.ProductId, item.Units, item.UnitPrice, marker.Id, marker.Name, eventSnapshot);
    }

    private static Task<HttpResponseMessage> PostOrder(HttpClient client, Guid requestId, object request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "api/orders?api-version=1.0")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(request), System.Text.Encoding.UTF8, "application/json")
        };
        message.Headers.Add("x-requestid", requestId.ToString());
        return client.SendAsync(message);
    }

    private sealed class ResponseLossHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var response = await base.SendAsync(request, cancellationToken);
            _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            throw new HttpRequestException("Simulated response loss after the server committed the order.");
        }
    }

    private sealed record OrderSnapshot(int OrderId, int? BuyerId, string BuyerIdentity, string Street, int ItemId, int ProductId, int Units, decimal UnitPrice, Guid RequestId, string RequestName, EventSnapshot Event);
    private sealed record EventSnapshot(Guid EventId, string EventTypeName, string Content, Guid TransactionId);

    private sealed class RestartableOrderingApiFactory(string connectionString, string identityEndpoint, string rabbitMqConnectionString) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderingDB"] = connectionString,
                ["Identity:Url"] = identityEndpoint
                , ["ConnectionStrings:eventbus"] = rabbitMqConnectionString
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new TestAuthorizationFilter()));
            return base.CreateHost(builder);
        }
    }

    private sealed class TestAuthorizationFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var identity = new ClaimsIdentity("test");
                identity.AddClaim(new Claim("sub", AutoAuthorizeMiddleware.IDENTITY_ID));
                identity.AddClaim(new Claim("unique_name", AutoAuthorizeMiddleware.IDENTITY_ID));
                identity.AddClaim(new Claim(ClaimTypes.Name, AutoAuthorizeMiddleware.IDENTITY_ID));
                context.User.AddIdentity(identity);
                await continuation();
            });
            next(app);
        };
    }
}
