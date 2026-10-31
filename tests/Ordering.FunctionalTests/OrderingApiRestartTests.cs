using System.Net;
using System.Threading;
using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Asp.Versioning.Http;
using eShop.Ordering.API.Application.Commands;
using eShop.Ordering.API.Application.Models;
using eShop.Ordering.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace eShop.Ordering.FunctionalTests;

public sealed partial class OrderingApiTests
{
    [Fact]
    public async Task OrderSurvivesOrderingApiRestart()
    {
        await using var fixture = new OrderingApiFixture();
        await fixture.InitializeAsync();

        var productId = 731;
        var requestId = Guid.NewGuid();
        var request = new CreateOrderRequest(
            Guid.NewGuid().ToString(), "Restart Test", "Seattle", "1 Main Street", "WA", "US", "98101",
            "XXXXXXXXXXXX0005", "Restart Test", DateTime.UtcNow.AddYears(1), "123", 1, null,
            [new BasketItem
            {
                Id = Guid.NewGuid().ToString(),
                ProductId = productId,
                ProductName = "Restart test product",
                UnitPrice = 17.25m,
                OldUnitPrice = 18m,
                Quantity = 3,
                PictureUrl = "restart-test.png"
            }]);
        var body = JsonSerializer.Serialize(request);
        OrderSnapshot beforeRestart;
        await using (var firstHost = fixture.CreateApiHost())
        {
            using var client = firstHost.CreateDefaultClient(
                new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)),
                new LoseCheckoutResponseHandler());
            using var content = CreateCheckoutContent(body, requestId);
            await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("api/orders", content, TestContext.Current.CancellationToken));
            beforeRestart = await ReadOrderByProduct(firstHost, productId);
        }

        await using var secondHost = fixture.CreateApiHost();
        using var restartedClient = secondHost.CreateClient();
        using var health = await restartedClient.GetAsync("alive", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var retryClient = secondHost.CreateDefaultClient(new ApiVersionHandler(new QueryStringApiVersionWriter(), new ApiVersion(1.0)));
        using var retryContent = CreateCheckoutContent(body, requestId);
        using var retryResponse = await retryClient.PostAsync("api/orders", retryContent, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);

        var afterRestart = await ReadOrderByProduct(secondHost, productId);
        Assert.Equal(beforeRestart, afterRestart);
        Assert.Equal(1, await CountOrdersByProduct(secondHost, productId));
    }

    private static StringContent CreateCheckoutContent(string body, Guid requestId)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        content.Headers.Add("x-requestid", requestId.ToString());
        return content;
    }

    private static async Task<OrderSnapshot> ReadOrderByProduct(WebApplicationFactory<Program> host, int productId)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderingContext>();
        var order = await context.Orders
            .Include(candidate => candidate.OrderItems)
            .OrderByDescending(candidate => candidate.Id)
            .FirstAsync(candidate => candidate.OrderItems.Any(item => item.ProductId == productId));
        var item = Assert.Single(order.OrderItems, candidate => candidate.ProductId == productId);
        return new OrderSnapshot(order.Id, item.Id, item.ProductId, item.ProductName, item.Units, item.UnitPrice, item.Discount);
    }

    private static async Task<int> CountOrdersByProduct(WebApplicationFactory<Program> host, int productId)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderingContext>();
        return await context.Orders.CountAsync(order => order.OrderItems.Any(item => item.ProductId == productId));
    }

    private sealed class LoseCheckoutResponseHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            response.Dispose();
            throw new HttpRequestException("Simulated connection loss after the checkout committed.");
        }
    }

    private sealed record OrderSnapshot(int OrderId, int ItemId, int ProductId, string ProductName, int Units, decimal UnitPrice, decimal Discount);
}
