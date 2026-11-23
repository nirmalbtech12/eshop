using eShop.Basket.API.Grpc;
using eShop.Basket.UnitTests.FrozenContract;
using Google.Protobuf.Reflection;
using System.Linq;

namespace eShop.Basket.UnitTests;

[TestClass]
public sealed class BasketContractCompatibilityTests
{
    private static readonly FileDescriptorProto Baseline = FrozenContract.BasketConsumerReflection.Descriptor.ToProto();
    private static readonly FileDescriptorProto Provider = eShop.Basket.API.Grpc.BasketReflection.Descriptor.ToProto();

    [TestMethod]
    public void ProviderMatchesFrozenConsumerContract() =>
        BasketContractCompatibility.AssertCompatible(Baseline, Provider);

    [TestMethod]
    public void BreakingDescriptorChangesAreRejected()
    {
        AssertRejected("package changed", proto => proto.Package = "BasketApiV2");
        AssertRejected("RPC Basket/GetBasket is missing", proto => proto.Service[0].Method.RemoveAt(0));
        AssertRejected("RPC Basket/GetBasket is missing", proto => proto.Service[0].Method[0].Name = "FetchBasket");
        AssertRejected("RPC Basket/GetBasket signature changed", proto => proto.Service[0].Method[0].InputType = ".BasketApi.UpdateBasketRequest");
        AssertRejected("RPC Basket/GetBasket signature changed", proto => proto.Service[0].Method[0].OutputType = ".BasketApi.DeleteBasketResponse");
        AssertRejected("field BasketItem.product_id is missing", proto => proto.MessageType.Single(message => message.Name == "BasketItem").Field.RemoveAt(0));
        AssertRejected("field BasketItem.product_id number, type or cardinality changed", proto =>
            proto.MessageType.Single(message => message.Name == "BasketItem").Field.Single(field => field.Name == "product_id").Number = 9);
        AssertRejected("field BasketItem.product_id number, type or cardinality changed", proto =>
            proto.MessageType.Single(message => message.Name == "BasketItem").Field.Single(field => field.Name == "product_id").Type = FieldDescriptorProto.Types.Type.Int64);
        AssertRejected("field CustomerBasketResponse.items number, type or cardinality changed", proto =>
            proto.MessageType.Single(message => message.Name == "CustomerBasketResponse").Field.Single().Label = FieldDescriptorProto.Types.Label.Optional);
    }

    [TestMethod]
    public void AdditiveDescriptorChangeRemainsCompatible()
    {
        var additive = Provider.Clone();
        additive.MessageType.Single(message => message.Name == "BasketItem").Field.Add(new FieldDescriptorProto
        {
            Name = "note",
            Number = 100,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional
        });

        BasketContractCompatibility.AssertCompatible(Baseline, additive);
    }

    private static void AssertRejected(string expectedMessage, Action<FileDescriptorProto> mutate)
    {
        var broken = Provider.Clone();
        mutate(broken);
        var exception = Assert.Throws<InvalidOperationException>(() => BasketContractCompatibility.AssertCompatible(Baseline, broken));
        Assert.Contains(expectedMessage, exception.Message);
    }
}
