extern alias webapp;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketStateTests
{
    [TestMethod]
    public async Task NegativeQuantityIsRejectedBeforeBasketAccess()
    {
        var state = new webapp::eShop.WebApp.Services.BasketState(null!, null!, null!, null!);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => state.SetQuantityAsync(42, -1));
    }
}
