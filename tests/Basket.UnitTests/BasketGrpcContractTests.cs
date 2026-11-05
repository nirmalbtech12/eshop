using System.Linq;
using eShop.Basket.UnitTests.Helpers;
using eShop.Basket.UnitTests.Contracts;
using Grpc.Core;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketGrpcContractTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task AddUpdateRemoveRoundTrip()
    {
        await using var fixture = await BasketContractFixture.CreateAsync(TestContext.CancellationToken);
        fixture.AuthenticateAs("buyer-1");
        var options = Options();

        var added = new UpdateBasketRequest();
        added.Items.Add(new BasketItem { ProductId = 42, Quantity = 3 });
        var addResponse = await fixture.Client.UpdateBasketAsync(added, options).ResponseAsync;
        AssertItems(addResponse.Items, (42, 3));
        var storedAdd = await fixture.Client.GetBasketAsync(new GetBasketRequest(), options).ResponseAsync;
        AssertItems(storedAdd.Items, (42, 3));

        var replacement = new UpdateBasketRequest();
        replacement.Items.Add(new BasketItem { ProductId = 42, Quantity = 5 });
        replacement.Items.Add(new BasketItem { ProductId = 77, Quantity = 2 });
        var updateResponse = await fixture.Client.UpdateBasketAsync(replacement, options).ResponseAsync;
        AssertItems(updateResponse.Items, (42, 5), (77, 2));
        var storedUpdate = await fixture.Client.GetBasketAsync(new GetBasketRequest(), options).ResponseAsync;
        AssertItems(storedUpdate.Items, (42, 5), (77, 2));
        Assert.AreEqual("buyer-1", fixture.Repository.StoredBuyerId);

        var deleteResponse = await fixture.Client.DeleteBasketAsync(new DeleteBasketRequest(), options).ResponseAsync;
        Assert.IsNotNull(deleteResponse);
        Assert.IsEmpty((await fixture.Client.GetBasketAsync(new GetBasketRequest(), options).ResponseAsync).Items);
        Assert.IsNotNull(await fixture.Client.DeleteBasketAsync(new DeleteBasketRequest(), options).ResponseAsync);
        Assert.AreEqual(2, fixture.Repository.DeleteCalls);
    }

    [TestMethod]
    public async Task AnonymousCallsPreserveContract()
    {
        await using var fixture = await BasketContractFixture.CreateAsync(TestContext.CancellationToken);
        Assert.IsEmpty((await fixture.Client.GetBasketAsync(new GetBasketRequest(), Options()).ResponseAsync).Items);

        var updateError = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.UpdateBasketAsync(new UpdateBasketRequest(), Options()).ResponseAsync);
        Assert.AreEqual(StatusCode.Unauthenticated, updateError.StatusCode);
        var deleteError = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.DeleteBasketAsync(new DeleteBasketRequest(), Options()).ResponseAsync);
        Assert.AreEqual(StatusCode.Unauthenticated, deleteError.StatusCode);
        Assert.AreEqual(0, fixture.Repository.UpdateCalls);
        Assert.AreEqual(0, fixture.Repository.DeleteCalls);
    }

    [TestMethod]
    public async Task FailedUpdatePreservesNotFoundContract()
    {
        await using var fixture = await BasketContractFixture.CreateAsync(TestContext.CancellationToken);
        fixture.AuthenticateAs("missing-buyer");
        fixture.Repository.ReturnNullOnUpdate = true;
        var request = new UpdateBasketRequest();
        request.Items.Add(new BasketItem { ProductId = 8, Quantity = 1 });

        var error = await Assert.ThrowsAsync<RpcException>(async () =>
            await fixture.Client.UpdateBasketAsync(request, Options()).ResponseAsync);

        Assert.AreEqual(StatusCode.NotFound, error.StatusCode);
    }

    private CallOptions Options() => new(
        deadline: DateTime.UtcNow.AddSeconds(30),
        cancellationToken: TestContext.CancellationToken);

    private static void AssertItems(IEnumerable<BasketItem> actual, params (int ProductId, int Quantity)[] expected)
    {
        Assert.AreEqual(expected.Length, actual.Count());
        CollectionAssert.AreEqual(expected, actual.Select(item => (item.ProductId, item.Quantity)).ToArray());
    }
}
