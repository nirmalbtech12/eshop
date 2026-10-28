namespace eShop.PaymentProcessor;

public interface IPaymentGateway
{
    Task<bool> ChargeAsync(int orderId, string idempotencyKey, CancellationToken cancellationToken);
}
