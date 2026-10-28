using System.Collections.Concurrent;

namespace eShop.PaymentProcessor;

public sealed class SimulatedPaymentGateway(IOptionsMonitor<PaymentOptions> options) : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, Lazy<bool>> _charges = new();
    public int ChargeCount => _charges.Count;

    public Task<bool> ChargeAsync(int orderId, string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _charges.GetOrAdd(idempotencyKey,
            _ => new Lazy<bool>(() => options.CurrentValue.PaymentSucceeded, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        return Task.FromResult(result);
    }
}
