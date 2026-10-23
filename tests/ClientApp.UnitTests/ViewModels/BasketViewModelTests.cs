using ClientApp.UnitTests.Mocks;
using eShop.ClientApp.Models.Basket;
using eShop.ClientApp.Services.Identity;

namespace ClientApp.UnitTests.ViewModels;

[TestClass]
public class BasketViewModelTests
{
    [TestMethod]
    public async Task UpdateQuantityPersistsAndRecalculatesTotals()
    {
        var (viewModel, basketService) = CreateViewModel();
        await viewModel.InitializeAsync();
        var item = viewModel.BasketItems[0];

        await viewModel.UpdateQuantityAsync(item, 4);

        Assert.AreEqual(4, item.Quantity);
        Assert.AreEqual(5, viewModel.BadgeCount);
        Assert.AreEqual(4 * item.UnitPrice + viewModel.BasketItems[1].UnitPrice, viewModel.Total);
        Assert.AreEqual(4, (await basketService.GetBasketAsync())!.Items[0].Quantity);
    }

    [TestMethod]
    public async Task ZeroQuantityRemovesLineFromDisplayedAndPersistedBasket()
    {
        var (viewModel, basketService) = CreateViewModel();
        await viewModel.InitializeAsync();
        var item = viewModel.BasketItems[0];
        var remainingTotal = viewModel.BasketItems[1].UnitPrice;

        await viewModel.UpdateQuantityAsync(item, 0);

        Assert.HasCount(1, viewModel.BasketItems);
        Assert.AreEqual(1, viewModel.BadgeCount);
        Assert.AreEqual(remainingTotal, viewModel.Total);
        Assert.IsFalse((await basketService.GetBasketAsync())!.Items.Any(x => x.ProductId == item.ProductId));
    }

    [TestMethod]
    public async Task NegativeQuantityIsRejectedWithoutChangingBasket()
    {
        var (viewModel, basketService) = CreateViewModel();
        await viewModel.InitializeAsync();
        var item = viewModel.BasketItems[0];

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => viewModel.UpdateQuantityAsync(item, -1));

        Assert.AreEqual(1, item.Quantity);
        Assert.AreEqual(1, (await basketService.GetBasketAsync())!.Items[0].Quantity);
    }

    private static (BasketViewModel ViewModel, IBasketService BasketService) CreateViewModel()
    {
        var basketService = new BasketMockService();
        var catalogService = new CatalogMockService();
        var orderService = new OrderMockService();
        var identityService = new IdentityMockService();
        var environment = new AppEnvironmentService(basketService, basketService, catalogService, catalogService,
            orderService, orderService, identityService, identityService);
        environment.UpdateDependencies(true);
        return (new BasketViewModel(environment, new MockNavigationService(), new MockSettingsService()), basketService);
    }
}
