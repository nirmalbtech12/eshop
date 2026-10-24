using ClientApp.UnitTests.Mocks;
using eShop.ClientApp.Services.Identity;

namespace ClientApp.UnitTests.ViewModels;

[TestClass]
public class BasketViewModelTests
{
    [TestMethod]
    public async Task QuantityUpdatePersistsAndZeroRemovesAfterReload()
    {
        var basketService = new BasketMockService();
        var catalog = new CatalogMockService();
        var environment = new AppEnvironmentService(basketService, basketService, catalog, catalog,
            new OrderMockService(), new OrderMockService(), new IdentityMockService(), new IdentityMockService());
        environment.UpdateDependencies(true);
        var viewModel = new BasketViewModel(environment, new MockNavigationService(), new MockSettingsService());
        await viewModel.InitializeAsync();
        var item = viewModel.BasketItems[0];

        await viewModel.UpdateQuantityAsync(item, 4);
        Assert.AreEqual(4, viewModel.BasketItems[0].Quantity);
        Assert.AreEqual(5, viewModel.BadgeCount);
        Assert.AreEqual(viewModel.BasketItems.Sum(basketItem => basketItem.Quantity * basketItem.UnitPrice), viewModel.Total);

        var reloaded = new BasketViewModel(environment, new MockNavigationService(), new MockSettingsService());
        await reloaded.InitializeAsync();
        Assert.AreEqual(4, reloaded.BasketItems[0].Quantity);
        await reloaded.UpdateQuantityAsync(reloaded.BasketItems[0], 0);
        Assert.HasCount(1, reloaded.BasketItems);
        Assert.AreEqual(1, reloaded.BadgeCount);

        var afterRemoval = new BasketViewModel(environment, new MockNavigationService(), new MockSettingsService());
        await afterRemoval.InitializeAsync();
        Assert.IsFalse(afterRemoval.BasketItems.Any(basketItem => basketItem.ProductId == item.ProductId));
    }
}
