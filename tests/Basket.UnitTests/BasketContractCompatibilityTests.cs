using System.Linq;
using Google.Protobuf.Reflection;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketContractCompatibilityTests
{
    [TestMethod]
    public void ProviderSatisfiesFrozenConsumerContract()
    {
        AssertConsumerCompatible(ProviderDescriptor(), ConsumerDescriptor());
    }

    [TestMethod]
    public void BreakingDescriptorChangesAreRejected()
    {
        var provider = ProviderDescriptor();
        var consumer = ConsumerDescriptor();
        AssertConsumerCompatible(provider, consumer);

        Reject(provider, consumer, mutated => mutated.Package = "OtherPackage");
        Reject(provider, consumer, mutated => mutated.Service[0].Name = "OtherBasket");
        Reject(provider, consumer, mutated => mutated.Service[0].Method.RemoveAt(0));
        Reject(provider, consumer, mutated => mutated.Service[0].Method[0].InputType = ".BasketApi.OtherRequest");
        Reject(provider, consumer, mutated => mutated.Service[0].Method[0].OutputType = ".BasketApi.OtherResponse");
        Reject(provider, consumer, mutated => mutated.Service[0].Method[0].ClientStreaming = true);
        Reject(provider, consumer, mutated => mutated.Service[0].Method[0].ServerStreaming = true);
        Reject(provider, consumer, mutated => mutated.MessageType.Single(message => message.Name == "BasketItem").Field.Single(field => field.Name == "product_id").Number = 9);
        Reject(provider, consumer, mutated => mutated.MessageType.Single(message => message.Name == "BasketItem").Field.Single(field => field.Name == "product_id").Type = FieldDescriptorProto.Types.Type.String);
        Reject(provider, consumer, mutated => mutated.MessageType.Single(message => message.Name == "UpdateBasketRequest").Field.Single().Label = FieldDescriptorProto.Types.Label.Optional);
        Reject(provider, consumer, mutated => mutated.MessageType.Remove(mutated.MessageType.Single(message => message.Name == "BasketItem")));

        var additive = provider.Clone();
        additive.MessageType.Single(message => message.Name == "BasketItem").Field.Add(new FieldDescriptorProto
        {
            Name = "display_name",
            Number = 12,
            Type = FieldDescriptorProto.Types.Type.String,
            Label = FieldDescriptorProto.Types.Label.Optional
        });
        AssertConsumerCompatible(additive, consumer);
    }

    private static FileDescriptorProto ProviderDescriptor() =>
        eShop.Basket.API.Grpc.GetBasketRequest.Descriptor.File.ToProto();

    private static FileDescriptorProto ConsumerDescriptor() =>
        Contracts.GetBasketRequest.Descriptor.File.ToProto();

    private static void Reject(FileDescriptorProto provider, FileDescriptorProto consumer, Action<FileDescriptorProto> mutate)
    {
        var incompatible = provider.Clone();
        mutate(incompatible);
        Assert.ThrowsExactly<InvalidOperationException>(() => AssertConsumerCompatible(incompatible, consumer));
    }

    private static void AssertConsumerCompatible(FileDescriptorProto provider, FileDescriptorProto consumer)
    {
        if (provider.Package != consumer.Package)
        {
            throw new InvalidOperationException($"Package differs: expected {consumer.Package}, actual {provider.Package}.");
        }

        foreach (var expectedService in consumer.Service)
        {
            var actualService = provider.Service.SingleOrDefault(service => service.Name == expectedService.Name)
                ?? throw new InvalidOperationException($"Missing service {expectedService.Name}.");
            foreach (var expectedMethod in expectedService.Method)
            {
                var actualMethod = actualService.Method.SingleOrDefault(method => method.Name == expectedMethod.Name)
                    ?? throw new InvalidOperationException($"Missing method {expectedService.Name}.{expectedMethod.Name}.");
                Require(actualMethod.InputType == expectedMethod.InputType, $"{expectedMethod.Name} request type");
                Require(actualMethod.OutputType == expectedMethod.OutputType, $"{expectedMethod.Name} response type");
                Require(actualMethod.ClientStreaming == expectedMethod.ClientStreaming, $"{expectedMethod.Name} client streaming");
                Require(actualMethod.ServerStreaming == expectedMethod.ServerStreaming, $"{expectedMethod.Name} server streaming");
            }
        }

        foreach (var expectedMessage in consumer.MessageType)
        {
            var actualMessage = provider.MessageType.SingleOrDefault(message => message.Name == expectedMessage.Name)
                ?? throw new InvalidOperationException($"Missing message {expectedMessage.Name}.");
            foreach (var expectedField in expectedMessage.Field)
            {
                var actualField = actualMessage.Field.SingleOrDefault(field => field.Number == expectedField.Number)
                    ?? throw new InvalidOperationException($"Missing field {expectedMessage.Name}.{expectedField.Name} tag {expectedField.Number}.");
                Require(actualField.Name == expectedField.Name, $"{expectedMessage.Name} tag {expectedField.Number} name");
                Require(actualField.Type == expectedField.Type, $"{expectedMessage.Name}.{expectedField.Name} type");
                Require(actualField.Label == expectedField.Label, $"{expectedMessage.Name}.{expectedField.Name} cardinality");
                Require(actualField.TypeName == expectedField.TypeName, $"{expectedMessage.Name}.{expectedField.Name} message type");
            }
        }
    }

    private static void Require(bool condition, string member)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Incompatible consumer member: {member}.");
        }
    }
}
