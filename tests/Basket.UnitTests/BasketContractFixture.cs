using System.Collections.Concurrent;
using System.Linq;
using System.Security.Claims;
using eShop.Basket.API.Grpc;
using eShop.Basket.API.Model;
using eShop.Basket.API.Repositories;
using Grpc.Net.Client;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelBasketItem = eShop.Basket.API.Model.BasketItem;

namespace eShop.Basket.UnitTests;

internal sealed class BasketContractFixture : IAsyncDisposable
{
    private readonly TestServer _server;
    private readonly GrpcChannel _channel;
    private readonly IHost _host;

    private BasketContractFixture(TestServer server, GrpcChannel channel, IHost host, ContractBasketRepository repository)
    {
        _server = server;
        _channel = channel;
        _host = host;
        Repository = repository;
        Client = new FrozenContract.Basket.BasketClient(channel);
    }

    public FrozenContract.Basket.BasketClient Client { get; }
    public ContractBasketRepository Repository { get; }

    public static async Task<BasketContractFixture> CreateAsync(CancellationToken cancellationToken)
    {
        var repository = new ContractBasketRepository();
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddGrpc();
                    services.AddSingleton<IBasketRepository>(repository);
                    services.AddSingleton<ILogger<BasketService>>(NullLogger<BasketService>.Instance);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue("X-Test-Subject", out var subject))
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [new Claim("sub", subject.ToString())], "contract-test"));
                        }
                        await next();
                    });
                    app.UseEndpoints(endpoints => endpoints.MapGrpcService<BasketService>());
                }))
            .Build();

        try
        {
            using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            await host.StartAsync(startupTimeout.Token);
            var server = host.GetTestServer();
            var channel = GrpcChannel.ForAddress(server.BaseAddress, new GrpcChannelOptions
            {
                HttpHandler = server.CreateHandler()
            });
            return new BasketContractFixture(server, channel, host, repository);
        }
        catch
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
            host.Dispose();
            throw;
        }
    }

    public Metadata Authenticated(string subject) => new() { { "X-Test-Subject", subject } };

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _server.Dispose();
        _host.Dispose();
    }
}

internal sealed class ContractBasketRepository : IBasketRepository
{
    private readonly ConcurrentDictionary<string, CustomerBasket> _baskets = new();
    private int _writeCount;
    private bool _returnNullOnUpdate;

    public int WriteCount => _writeCount;
    public bool ReturnNullOnUpdate { set => _returnNullOnUpdate = value; }

    public Task<CustomerBasket> GetBasketAsync(string customerId) =>
        Task.FromResult(_baskets.TryGetValue(customerId, out var basket) ? Clone(basket) : null!);

    public Task<CustomerBasket> UpdateBasketAsync(CustomerBasket basket)
    {
        Interlocked.Increment(ref _writeCount);
        if (_returnNullOnUpdate)
        {
            return Task.FromResult<CustomerBasket>(null!);
        }
        var clone = Clone(basket);
        _baskets[basket.BuyerId] = clone;
        return Task.FromResult(Clone(clone));
    }

    public Task<bool> DeleteBasketAsync(string id)
    {
        Interlocked.Increment(ref _writeCount);
        return Task.FromResult(_baskets.TryRemove(id, out _));
    }

    private static CustomerBasket Clone(CustomerBasket basket) => new(basket.BuyerId)
    {
        Items = basket.Items.Select(item => new ModelBasketItem { ProductId = item.ProductId, Quantity = item.Quantity }).ToList()
    };
}
