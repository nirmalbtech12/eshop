namespace eShop.Ordering.Domain.AggregatesModel.OrderAggregate;

public static class OrderStatusExtensions
{
    public static string ToDisplayName(this OrderStatus status) => status switch
    {
        OrderStatus.PaymentPending => "Payment pending",
        _ => status.ToString()
    };
}
