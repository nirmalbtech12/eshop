var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisClient("redis");

builder.AddRabbitMqEventBus("EventBus")
    .AddSubscription<OrderStatusChangedToStockConfirmedIntegrationEvent, OrderStatusChangedToStockConfirmedIntegrationEventHandler>();

builder.Services.AddOptions<PaymentOptions>()
    .BindConfiguration(nameof(PaymentOptions));
builder.Services.AddSingleton<IPaymentResultStore, RedisPaymentResultStore>();
builder.Services.AddSingleton<IPaymentChargeProcessor, SimulatedPaymentChargeProcessor>();
builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddSingleton<IRetryDelay, RetryDelay>();

var app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync();
