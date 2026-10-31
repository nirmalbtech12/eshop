using System.Linq;
using Google.Protobuf.Reflection;
using eShop.Basket.API.Grpc;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketApiContractTests
{
    [TestMethod]
    public void ContractDescriptorsMatchExpectedBasketApi()
    {
        var service = BasketReflection.Descriptor.Services.Single();
        Assert.AreEqual("Basket", service.Name);
        Assert.HasCount(3, service.Methods);
        AssertCollectionEquals(
            ["DeleteBasket", "GetBasket", "UpdateBasket"],
            service.Methods.Select(method => method.Name).Order().ToArray());

        AssertMethod("GetBasket", "GetBasketRequest", "CustomerBasketResponse");
        AssertMethod("UpdateBasket", "UpdateBasketRequest", "CustomerBasketResponse");
        AssertMethod("DeleteBasket", "DeleteBasketRequest", "DeleteBasketResponse");

        AssertFields(CustomerBasketResponse.Descriptor,
            ("items", 1, FieldType.Message, true));
        AssertFields(BasketItem.Descriptor,
            ("product_id", 2, FieldType.Int32, false),
            ("quantity", 6, FieldType.Int32, false));
        AssertFields(UpdateBasketRequest.Descriptor,
            ("items", 2, FieldType.Message, true));
        AssertFields(GetBasketRequest.Descriptor);
        AssertFields(DeleteBasketRequest.Descriptor);
        AssertFields(DeleteBasketResponse.Descriptor);
    }

    private static void AssertMethod(string name, string inputType, string outputType)
    {
        var method = BasketReflection.Descriptor.Services.Single().Methods.Single(item => item.Name == name);
        Assert.AreEqual(inputType, method.InputType.Name);
        Assert.AreEqual(outputType, method.OutputType.Name);
    }

    private static void AssertFields(MessageDescriptor message, params (string Name, int Number, FieldType Type, bool Repeated)[] expected)
    {
        var fields = message.Fields.InFieldNumberOrder();
        Assert.HasCount(expected.Length, fields);
        foreach (var (name, number, type, repeated) in expected)
        {
            var field = fields.SingleOrDefault(item => item.Name == name);
            Assert.IsNotNull(field, $"Missing {message.Name}.{name}");
            Assert.AreEqual(number, field.FieldNumber);
            Assert.AreEqual(type, field.FieldType);
            Assert.AreEqual(repeated, field.IsRepeated);
        }
    }

    private static void AssertCollectionEquals<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
    }
}
