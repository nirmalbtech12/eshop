using eShop.ClientApp.Models.Basket;
using eShop.ClientApp.Services;
using eShop.ClientApp.Services.AppEnvironment;
using eShop.ClientApp.Services.Settings;
using eShop.ClientApp.ViewModels.Base;

namespace eShop.ClientApp.ViewModels;

public partial class BasketViewModel : ViewModelBase
{
    private readonly IAppEnvironmentService _appEnvironmentService;
    private readonly ObservableCollectionEx<BasketItem> _basketItems = new();
    private readonly ISettingsService _settingsService;

    public BasketViewModel(
        IAppEnvironmentService appEnvironmentService,
        INavigationService navigationService, ISettingsService settingsService)
        : base(navigationService)
    {
        _appEnvironmentService = appEnvironmentService;
        _settingsService = settingsService;
    }

    public int BadgeCount => _basketItems?.Sum(basketItem => basketItem.Quantity) ?? 0;

    public decimal Total => _basketItems?.Sum(basketItem => basketItem.Quantity * basketItem.UnitPrice) ?? 0m;

    public IReadOnlyList<BasketItem> BasketItems => _basketItems;

    public override async Task InitializeAsync()
    {
        // Update Basket
        var basket = await _appEnvironmentService.BasketService.GetBasketAsync();

        if ((basket?.Items?.Count ?? 0) > 0)
        {
            await _basketItems.ReloadDataAsync(
                async innerList =>
                {
                    foreach (var basketItem in basket.Items.ToArray())
                    {
                        var catalogItem =
                            await _appEnvironmentService.CatalogService.GetCatalogItemAsync(basketItem.ProductId);
                        basketItem.PictureUrl = catalogItem.PictureUri;
                        basketItem.ProductName = catalogItem.Name;
                        basketItem.UnitPrice = catalogItem.Price;
                        innerList.Add(basketItem);
                    }
                });
            ReCalculateTotal();
        }
    }

    [RelayCommand]
    private Task AddAsync(BasketItem item)
    {
        return AddBasketItemAsync(item, _basketItems);
    }

    public async Task UpdateQuantityAsync(BasketItem item, int quantity)
    {
        if (quantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity));
        }

        var basket = await _appEnvironmentService.BasketService.GetBasketAsync();
        var displayedItem = _basketItems.FirstOrDefault(x => x.ProductId == item.ProductId);
        if (displayedItem is null || basket is null)
        {
            return;
        }

        if (quantity == 0)
        {
            _basketItems.Remove(displayedItem);
            basket.RemoveItemFromBasket(item);
        }
        else
        {
            displayedItem.Quantity = quantity;
            var storedItem = basket.Items.FirstOrDefault(x => x.ProductId == item.ProductId);
            if (storedItem is null) basket.AddItemToBasket(new BasketItem { ProductId = item.ProductId, Quantity = quantity });
            else storedItem.Quantity = quantity;
        }

        await _appEnvironmentService.BasketService.UpdateBasketAsync(basket);
        ReCalculateTotal();
    }

    [RelayCommand]
    private Task IncreaseQuantityAsync(BasketItem item) => UpdateQuantityAsync(item, item.Quantity + 1);

    [RelayCommand]
    private Task DecreaseQuantityAsync(BasketItem item) => UpdateQuantityAsync(item, item.Quantity - 1);

    private async Task AddBasketItemAsync(BasketItem item, IList<BasketItem> basketItems)
    {
        basketItems.Add(item);

        var basket = await _appEnvironmentService.BasketService.GetBasketAsync();

        if (basket != null)
        {
            basket.AddItemToBasket(item);
            await _appEnvironmentService.BasketService.UpdateBasketAsync(basket);
        }

        ReCalculateTotal();
    }

    [RelayCommand]
    private async Task DeleteAsync(BasketItem item)
    {
        _basketItems.Remove(item);

        var basket = await _appEnvironmentService.BasketService.GetBasketAsync();
        if (basket != null)
        {
            basket.RemoveItemFromBasket(item);
            await _appEnvironmentService.BasketService.UpdateBasketAsync(basket);
        }

        ReCalculateTotal();
    }

    public async Task ClearBasketItems()
    {
        _basketItems.Clear();
        
        await _appEnvironmentService.BasketService.ClearBasketAsync();

        ReCalculateTotal();
    }

    private void ReCalculateTotal()
    {
        OnPropertyChanged(nameof(BadgeCount));
        OnPropertyChanged(nameof(Total));
    }

    [RelayCommand]
    private async Task CheckoutAsync()
    {
        if (_basketItems?.Any() ?? false)
        {
            _appEnvironmentService.BasketService.LocalBasketItems = _basketItems;
            await NavigationService.NavigateToAsync("Checkout");
        }
    }
}
