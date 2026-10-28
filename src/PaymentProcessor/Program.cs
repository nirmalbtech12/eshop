var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddRabbitMqEventBus("EventBus")
    .AddSubscription<OrderStatusChangedToStockConfirmedIntegrationEvent, OrderStatusChangedToStockConfirmedIntegrationEventHandler>();

builder.Services.AddOptions<PaymentOptions>()
    .BindConfiguration(nameof(PaymentOptions))
    .Validate(o => o.MaxAttempts is >= 1 and <= 5 && o.AttemptTimeoutMilliseconds > 0 && o.InitialBackoffMilliseconds > 0, "Payment retry settings are invalid.")
    .ValidateOnStart();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IPaymentGateway, SimulatedPaymentGateway>();
builder.Services.AddSingleton<PaymentService>();

var app = builder.Build();

app.MapDefaultEndpoints();

await app.RunAsync();
