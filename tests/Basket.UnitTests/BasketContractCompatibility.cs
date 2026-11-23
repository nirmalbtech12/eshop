using Google.Protobuf.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace eShop.Basket.UnitTests;

internal static class BasketContractCompatibility
{
    public static void AssertCompatible(FileDescriptorProto baseline, FileDescriptorProto provider)
    {
        if (baseline.Package != provider.Package)
        {
            Fail($"package changed from {baseline.Package} to {provider.Package}");
        }

        var providerServices = provider.Service.ToDictionary(service => service.Name);
        var providerMessages = provider.MessageType.ToDictionary(message => message.Name);

        foreach (var service in baseline.Service)
        {
            if (!providerServices.TryGetValue(service.Name, out var actualService))
            {
                Fail($"service {service.Name} is missing");
            }

            foreach (var method in service.Method)
            {
                var actualMethod = actualService.Method.FirstOrDefault(candidate => candidate.Name == method.Name);
                if (actualMethod is null)
                {
                    Fail($"RPC {service.Name}/{method.Name} is missing");
                }
                if (actualMethod.InputType != method.InputType || actualMethod.OutputType != method.OutputType ||
                    actualMethod.ClientStreaming != method.ClientStreaming || actualMethod.ServerStreaming != method.ServerStreaming)
                {
                    Fail($"RPC {service.Name}/{method.Name} signature changed");
                }
            }
        }

        foreach (var message in baseline.MessageType)
        {
            if (!providerMessages.TryGetValue(message.Name, out var actualMessage))
            {
                Fail($"message {message.Name} is missing");
            }

            foreach (var field in message.Field)
            {
                var actualField = actualMessage.Field.FirstOrDefault(candidate => candidate.Name == field.Name);
                if (actualField is null)
                {
                    Fail($"field {message.Name}.{field.Name} is missing");
                }
                if (actualField.Number != field.Number || actualField.Type != field.Type || actualField.TypeName != field.TypeName || actualField.Label != field.Label)
                {
                    Fail($"field {message.Name}.{field.Name} number, type or cardinality changed");
                }
            }
        }
    }

    [DoesNotReturn]
    private static void Fail(string message) => throw new InvalidOperationException(message);
}
