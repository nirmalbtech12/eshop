using eShop.PaymentProcessor.Gateway;
using eShop.PaymentProcessor.Payments;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddRabbitMqEventBus("EventBus")
    .AddSubscription<OrderStatusChangedToStockConfirmedIntegrationEvent, OrderStatusChangedToStockConfirmedIntegrationEventHandler>();

builder.Services.AddOptions<PaymentOptions>()
    .BindConfiguration(nameof(PaymentOptions))
    .Validate(o => o.MaxAttempts > 0 && o.AttemptTimeout > TimeSpan.Zero && o.InitialBackoff >= TimeSpan.Zero && o.MaxBackoff >= o.InitialBackoff && o.TimeoutCount >= 0, "Invalid payment retry settings.")
    .ValidateOnStart();

builder.AddNpgsqlDataSource("paymentdb");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulatedPaymentGateway>();
builder.Services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<SimulatedPaymentGateway>());
builder.Services.AddSingleton<PaymentService>();

var app = builder.Build();

await app.Services.GetRequiredService<SimulatedPaymentGateway>().InitializeAsync();

app.MapDefaultEndpoints();

await app.RunAsync();
