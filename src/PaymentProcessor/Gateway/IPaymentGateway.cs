namespace eShop.PaymentProcessor.Gateway;

public record PaymentResult(Guid PaymentId, bool Succeeded);

public interface IPaymentGateway
{
    Task<PaymentResult> PayAsync(int orderId, string idempotencyKey, CancellationToken cancellationToken);
}
