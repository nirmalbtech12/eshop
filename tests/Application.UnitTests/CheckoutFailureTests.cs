using System.Net;
using eShop.WebApp.Services;

namespace eShop.Application.UnitTests;

[TestClass]
public sealed class CheckoutFailureTests
{
    [TestMethod]
    public async Task FailedOrderingResponsePreservesBasketAndRequestId()
    {
        var requestId = Guid.NewGuid();
        Guid? observedRequestId = null;
        using var client = new HttpClient(new StubHandler(request =>
        {
            observedRequestId = Guid.Parse(request.Headers.GetValues("x-requestid").Single());
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        })) { BaseAddress = new Uri("http://localhost") };
        var ordering = new OrderingService(client);
        var request = new CreateOrderRequest("user", "name", "city", "street", "state", "country", "zip", "1111", "name", DateTime.UtcNow, "111", 1, "buyer", []);

        await Assert.ThrowsAsync<HttpRequestException>(() => ordering.CreateOrder(request, requestId));

        Assert.AreEqual(requestId, observedRequestId);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response(request));
    }
}
