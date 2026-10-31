namespace eShop.PaymentProcessor;

public sealed class ConfiguredPaymentGateway(IOptionsMonitor<PaymentOptions> options) : IPaymentGateway
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<bool>>> _charges = new();

    public Task<bool> ChargeAsync(int orderId, string idempotencyKey) =>
        _charges.GetOrAdd(
            idempotencyKey,
            _ => new Lazy<Task<bool>>(
                () => Task.FromResult(options.CurrentValue.PaymentSucceeded),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
}
