using Google.Protobuf.Reflection;
using eShop.Basket.API.Grpc;

namespace eShop.Basket.FunctionalTests;

public sealed class BasketApiContractTests
{
    [Fact]
    public void BasketServiceRetainsAddUpdateAndRemoveOperations()
    {
        var service = BasketReflection.Descriptor.Services.Single();

        Assert.Equal("Basket", service.Name);
        Assert.Equal(new[] { "GetBasket", "UpdateBasket", "DeleteBasket" },
            service.Methods.Select(method => method.Name));
        Assert.Equal("GetBasketRequest", service.Methods[0].InputType.Name);
        Assert.Equal("CustomerBasketResponse", service.Methods[0].OutputType.Name);
        Assert.Equal("UpdateBasketRequest", service.Methods[1].InputType.Name);
        Assert.Equal("CustomerBasketResponse", service.Methods[1].OutputType.Name);
        Assert.Equal("DeleteBasketRequest", service.Methods[2].InputType.Name);
        Assert.Equal("DeleteBasketResponse", service.Methods[2].OutputType.Name);
    }

    [Fact]
    public void BasketMessagesRetainTheirWireFields()
    {
        AssertField("CustomerBasketResponse", "items", 1, FieldType.Message, repeated: true, "BasketItem");
        AssertField("BasketItem", "product_id", 2, FieldType.Int32);
        AssertField("BasketItem", "quantity", 6, FieldType.Int32);
        AssertField("UpdateBasketRequest", "items", 2, FieldType.Message, repeated: true, "BasketItem");
    }

    private static void AssertField(string messageName, string name, int number, FieldType type,
        bool repeated = false, string? messageType = null)
    {
        var message = BasketReflection.Descriptor.MessageTypes.Single(item => item.Name == messageName);
        var descriptor = message.FindFieldByName(name);
        Assert.NotNull(descriptor);
        Assert.Equal(number, descriptor!.FieldNumber);
        Assert.Equal(type, descriptor.FieldType);
        Assert.Equal(repeated, descriptor.IsRepeated);
        if (messageType is not null)
            Assert.Equal(messageType, descriptor.MessageType.Name);
    }
}
