using eShop.Basket.API.Grpc;
using Google.Protobuf.Reflection;

namespace eShop.Basket.FunctionalTests;

public class BasketApiContractTests
{
    [Fact]
    public void Basket_service_exposes_get_update_and_delete_contracts()
    {
        var service = BasketReflection.Descriptor.Services.Single(x => x.Name == "Basket");

        Assert.Equal(new[] { "GetBasket", "UpdateBasket", "DeleteBasket" },
            service.Methods.Select(x => x.Name));
        Assert.Equal("GetBasketRequest", service.Methods[0].InputType.Name);
        Assert.Equal("CustomerBasketResponse", service.Methods[0].OutputType.Name);
        Assert.Equal("UpdateBasketRequest", service.Methods[1].InputType.Name);
        Assert.Equal("CustomerBasketResponse", service.Methods[1].OutputType.Name);
        Assert.Equal("DeleteBasketRequest", service.Methods[2].InputType.Name);
        Assert.Equal("DeleteBasketResponse", service.Methods[2].OutputType.Name);
    }

    [Fact]
    public void Basket_messages_preserve_field_names_and_numbers()
    {
        AssertField(CustomerBasketResponse.Descriptor, "items", 1);
        AssertField(eShop.Basket.API.Grpc.BasketItem.Descriptor, "product_id", 2);
        AssertField(eShop.Basket.API.Grpc.BasketItem.Descriptor, "quantity", 6);
        AssertField(UpdateBasketRequest.Descriptor, "items", 2);
    }

    private static void AssertField(MessageDescriptor message, string name, int number)
    {
        var field = message.FindFieldByName(name);
        Assert.NotNull(field);
        Assert.Equal(number, field.FieldNumber);
    }
}
