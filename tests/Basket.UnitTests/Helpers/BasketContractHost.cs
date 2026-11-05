#nullable enable
using System.Collections.Concurrent;
using System.Linq;
using eShop.Basket.API.Grpc;
using eShop.Basket.API.Model;
using eShop.Basket.API.Repositories;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace eShop.Basket.UnitTests.Helpers;

internal sealed class BasketContractHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly GrpcChannel _channel;

    private BasketContractHost(WebApplication app)
    {
        _app = app;
        _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = app.GetTestServer().CreateHandler()
        });
    }

    public MemoryBasketRepository Repository => _app.Services.GetRequiredService<MemoryBasketRepository>();

    public static async Task<BasketContractHost> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<MemoryBasketRepository>();
        builder.Services.AddSingleton<IBasketRepository>(services => services.GetRequiredService<MemoryBasketRepository>());
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("x-sub", out var subject))
            {
                context.User = new System.Security.Claims.ClaimsPrincipal(
                    new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("sub", subject.ToString())], "contract"));
            }
            await next();
        });
        app.MapGrpcService<BasketService>();
        await app.StartAsync();
        return new BasketContractHost(app);
    }

    public async Task<byte[]> InvokeAsync(string method, byte[] request, string? subject = null)
    {
        var marshaller = Marshallers.Create<byte[]>(value => value, data => data);
        var descriptor = new Method<byte[], byte[]>(MethodType.Unary, "BasketApi.Basket", method, marshaller, marshaller);
        var headers = subject is null ? null : new Metadata { { "x-sub", subject } };
        return await _channel.CreateCallInvoker().AsyncUnaryCall(descriptor, null,
            new CallOptions(headers: headers, deadline: DateTime.UtcNow.AddSeconds(10)), request).ResponseAsync;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
    }
}

internal sealed class MemoryBasketRepository : IBasketRepository
{
    private readonly ConcurrentDictionary<string, CustomerBasket> _baskets = new();
    public bool ReturnNullOnUpdate { get; set; }
    public ConcurrentQueue<string> Mutations { get; } = new();

    public void Seed(string buyer, (int Product, int Quantity)[] items) =>
        _baskets[buyer] = new CustomerBasket(buyer)
        {
            Items = items.Select(item => new eShop.Basket.API.Model.BasketItem { ProductId = item.Product, Quantity = item.Quantity }).ToList()
        };

    public Task<CustomerBasket> GetBasketAsync(string customerId) =>
        Task.FromResult(_baskets.TryGetValue(customerId, out var basket) ? Copy(basket) : null!);

    public Task<CustomerBasket> UpdateBasketAsync(CustomerBasket basket)
    {
        Mutations.Enqueue($"update:{basket.BuyerId}");
        if (ReturnNullOnUpdate) return Task.FromResult<CustomerBasket>(null!);
        _baskets[basket.BuyerId] = Copy(basket);
        return Task.FromResult(Copy(basket));
    }

    public Task<bool> DeleteBasketAsync(string id)
    {
        Mutations.Enqueue($"delete:{id}");
        return Task.FromResult(_baskets.TryRemove(id, out _));
    }

    private static CustomerBasket Copy(CustomerBasket basket) => new(basket.BuyerId)
    {
        Items = basket.Items.Select(item => new eShop.Basket.API.Model.BasketItem { ProductId = item.ProductId, Quantity = item.Quantity }).ToList()
    };
}
