namespace eShop.PaymentProcessor;

public interface IPaymentChargeStore
{
    Task<bool> GetOrCreateChargeAsync(int orderId, string idempotencyKey, bool paymentSucceeded);
}
