using System.Security.Claims;
using System.Net.Http;
using System.Linq;
using eShop.Basket.API.Grpc;
using eShop.Basket.API.Model;
using eShop.Basket.API.Repositories;
using eShop.Basket.UnitTests.Contracts;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ConsumerBasket = eShop.Basket.UnitTests.Contracts.Basket;

namespace eShop.Basket.UnitTests.Helpers;

internal sealed class BasketContractFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;
    private readonly HttpClient _httpClient;

    private BasketContractFixture(WebApplication app, GrpcChannel channel, HttpClient httpClient, ContractBasketRepository repository)
    {
        _app = app;
        _channel = channel;
        _httpClient = httpClient;
        Repository = repository;
        Client = new ConsumerBasket.BasketClient(channel);
    }

    public ConsumerBasket.BasketClient Client { get; }
    public ContractBasketRepository Repository { get; }

    public static async Task<BasketContractFixture> CreateAsync(CancellationToken cancellationToken)
    {
        var repository = new ContractBasketRepository();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<IBasketRepository>(repository);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Sub", out var subject))
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject.ToString())], "test"));
            }

            await next();
        });
        app.MapGrpcService<BasketService>();
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        await app.StartAsync(startupTimeout.Token);

        var client = app.GetTestClient();
        var httpClient = client;
        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpClient = httpClient });
        return new BasketContractFixture(app, channel, httpClient, repository);
    }

    public void AuthenticateAs(string subject) => _httpClient.DefaultRequestHeaders.Add("X-Test-Sub", subject);
    public void ClearAuthentication() => _httpClient.DefaultRequestHeaders.Remove("X-Test-Sub");

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        _httpClient.Dispose();
        return _app.DisposeAsync();
    }
}

internal sealed class ContractBasketRepository : IBasketRepository
{
    private readonly Dictionary<string, CustomerBasket> _baskets = new(StringComparer.Ordinal);

    public int UpdateCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public bool ReturnNullOnUpdate { get; set; }
    public string StoredBuyerId => _baskets.Keys.SingleOrDefault()!;

    public Task<CustomerBasket> GetBasketAsync(string customerId) =>
        Task.FromResult(_baskets.TryGetValue(customerId, out var basket) ? Copy(basket) : null!);

    public Task<CustomerBasket> UpdateBasketAsync(CustomerBasket basket)
    {
        UpdateCalls++;
        if (ReturnNullOnUpdate)
        {
            return Task.FromResult<CustomerBasket>(null!);
        }

        _baskets[basket.BuyerId] = Copy(basket);
        return Task.FromResult(Copy(basket));
    }

    public Task<bool> DeleteBasketAsync(string id)
    {
        DeleteCalls++;
        _baskets.Remove(id);
        return Task.FromResult(true);
    }

    private static CustomerBasket Copy(CustomerBasket basket) => new(basket.BuyerId)
    {
        Items = basket.Items.Select(item => new eShop.Basket.API.Model.BasketItem
        {
            ProductId = item.ProductId,
            Quantity = item.Quantity
        }).ToList()
    };
}
