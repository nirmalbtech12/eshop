using eShop.Basket.UnitTests.FrozenContract;
using Grpc.Core;

namespace eShop.Basket.UnitTests;

[TestClass]
public sealed class BasketContractTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task FrozenConsumerCanAddUpdateRemoveAndDeleteBasket()
    {
        await using var fixture = await BasketContractFixture.CreateAsync(TestContext.CancellationToken);
        var buyerA = fixture.Authenticated("buyer-a");
        var buyerB = fixture.Authenticated("buyer-b");

        var empty = await fixture.Client.GetBasketAsync(new GetBasketRequest(), buyerA, Deadline());
        Assert.IsEmpty(empty.Items);

        var added = await fixture.Client.UpdateBasketAsync(new UpdateBasketRequest
        {
            Items = { new BasketItem { ProductId = 42, Quantity = 1 }, new BasketItem { ProductId = 7, Quantity = 2 } }
        }, buyerA, Deadline());
        AssertItems(added, (42, 1), (7, 2));
        Assert.AreEqual("buyer-a", (await fixture.Repository.GetBasketAsync("buyer-a")).BuyerId);

        var buyerBAdded = await fixture.Client.UpdateBasketAsync(new UpdateBasketRequest
        {
            Items = { new BasketItem { ProductId = 99, Quantity = 4 } }
        }, buyerB, Deadline());
        AssertItems(buyerBAdded, (99, 4));

        var updated = await fixture.Client.UpdateBasketAsync(new UpdateBasketRequest
        {
            Items = { new BasketItem { ProductId = 42, Quantity = 3 }, new BasketItem { ProductId = 7, Quantity = 2 } }
        }, buyerA, Deadline());
        AssertItems(updated, (42, 3), (7, 2));

        var removed = await fixture.Client.UpdateBasketAsync(new UpdateBasketRequest
        {
            Items = { new BasketItem { ProductId = 7, Quantity = 2 } }
        }, buyerA, Deadline());
        AssertItems(removed, (7, 2));
        AssertItems(await fixture.Client.GetBasketAsync(new GetBasketRequest(), buyerB, Deadline()), (99, 4));

        await fixture.Client.DeleteBasketAsync(new DeleteBasketRequest(), buyerA, Deadline());
        Assert.IsEmpty((await fixture.Client.GetBasketAsync(new GetBasketRequest(), buyerA, Deadline())).Items);
        await fixture.Client.DeleteBasketAsync(new DeleteBasketRequest(), buyerA, Deadline());
    }

    [TestMethod]
    public async Task FrozenConsumerPreservesIdentityAndErrorContracts()
    {
        await using var fixture = await BasketContractFixture.CreateAsync(TestContext.CancellationToken);
        await fixture.Client.UpdateBasketAsync(new UpdateBasketRequest
        {
            Items = { new BasketItem { ProductId = 12, Quantity = 1 } }
        }, fixture.Authenticated("buyer-a"), Deadline());
        var initialWrites = fixture.Repository.WriteCount;

        Assert.IsEmpty((await fixture.Client.GetBasketAsync(new GetBasketRequest(), deadline: Deadline())).Items);
        var updateError = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.Client.UpdateBasketAsync(new UpdateBasketRequest(), deadline: Deadline()).ResponseAsync);
        Assert.AreEqual(StatusCode.Unauthenticated, updateError.StatusCode);
        var deleteError = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.Client.DeleteBasketAsync(new DeleteBasketRequest(), deadline: Deadline()).ResponseAsync);
        Assert.AreEqual(StatusCode.Unauthenticated, deleteError.StatusCode);
        Assert.AreEqual(initialWrites, fixture.Repository.WriteCount);
        AssertItems(await fixture.Client.GetBasketAsync(new GetBasketRequest(), fixture.Authenticated("buyer-a"), Deadline()), (12, 1));

        fixture.Repository.ReturnNullOnUpdate = true;
        var missingError = await Assert.ThrowsAsync<RpcException>(() =>
            fixture.Client.UpdateBasketAsync(new UpdateBasketRequest
            {
                Items = { new BasketItem { ProductId = 50, Quantity = 2 } }
            }, fixture.Authenticated("missing"), Deadline()).ResponseAsync);
        Assert.AreEqual(StatusCode.NotFound, missingError.StatusCode);
    }

    private DateTime Deadline() => DateTime.UtcNow.AddSeconds(10);

    private static void AssertItems(CustomerBasketResponse response, params (int ProductId, int Quantity)[] expected)
    {
        Assert.AreEqual(expected.Length, response.Items.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].ProductId, response.Items[i].ProductId);
            Assert.AreEqual(expected[i].Quantity, response.Items[i].Quantity);
        }
    }
}
