namespace eShop.PaymentProcessor;

public sealed class SimulatedPaymentGateway(
    IPaymentChargeStore chargeStore,
    IOptionsMonitor<PaymentOptions> options) : IPaymentGateway
{
    public Task<bool> ChargeAsync(int orderId, string idempotencyKey) =>
        chargeStore.GetOrCreateChargeAsync(orderId, idempotencyKey, options.CurrentValue.PaymentSucceeded);
}
