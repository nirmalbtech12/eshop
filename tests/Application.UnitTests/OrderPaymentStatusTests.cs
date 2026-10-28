using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using eShop.WebApp.Components.Pages.User;

namespace eShop.Application.UnitTests;

[TestClass]
public class OrderPaymentStatusTests
{
    [TestMethod]
    public async Task RendersPendingConfirmationAndPreservesTerminalStatuses()
    {
        var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        foreach (var status in new[] { "StockConfirmed", "Paid", "Shipped", "Cancelled", "Other" })
        {
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<OrderPaymentStatus>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Status"] = status }))).ToHtmlString());
            if (status == "StockConfirmed")
            {
                StringAssert.Contains(html, "Payment confirmation pending. Confirmation may be delayed; please do not place another order.");
                Assert.IsFalse(html.Contains("visually-hidden", StringComparison.Ordinal));
                Assert.IsFalse(html.Contains("declined", StringComparison.OrdinalIgnoreCase));
            }
            else StringAssert.Contains(html, status);
        }
    }
}
