#nullable enable
using System.Linq;
using System.Security.Claims;
using eShop.Basket.API.Grpc;
using eShop.Basket.API.Model;
using eShop.Basket.API.Repositories;
using eShop.Basket.UnitTests.Helpers;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using BasketItemModel = eShop.Basket.API.Model.BasketItem;
using BasketGrpc = eShop.Basket.API.Grpc;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketContractTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RpcAndMessageDescriptorsPreserveConsumerContract()
    {
        var file = CustomerBasketResponse.Descriptor.File;
        Assert.AreEqual("BasketApi", file.Package);
        AssertRequiredServiceContract(file.Services.Select(ToServiceContract), new ServiceContract("Basket",
            [
                new MethodContract("GetBasket", "GetBasketRequest", "CustomerBasketResponse", false, false),
                new MethodContract("UpdateBasket", "UpdateBasketRequest", "CustomerBasketResponse", false, false),
                new MethodContract("DeleteBasket", "DeleteBasketRequest", "DeleteBasketResponse", false, false)
            ]));
        AssertField<CustomerBasketResponse>("items", 1, FieldType.Message, true);
        AssertField<UpdateBasketRequest>("items", 2, FieldType.Message, true);
        AssertField<BasketGrpc.BasketItem>("product_id", 2, FieldType.Int32, false);
        AssertField<BasketGrpc.BasketItem>("quantity", 6, FieldType.Int32, false);
        Assert.AreEqual(0, new GetBasketRequest().CalculateSize());
        Assert.AreEqual(0, new DeleteBasketRequest().CalculateSize());
        Assert.AreEqual(0, new DeleteBasketResponse().CalculateSize());
    }

    [TestMethod]
    public void FixedWireFixturesPreserveConsumerContract()
    {
        byte[] itemBytes = [0x10, 0x2A, 0x30, 0x03];
        byte[] updateBytes = [0x12, 0x04, 0x10, 0x2A, 0x30, 0x03];
        byte[] responseBytes = [0x0A, 0x04, 0x10, 0x2A, 0x30, 0x03];

        var item = BasketGrpc.BasketItem.Parser.ParseFrom(itemBytes);
        Assert.AreEqual(42, item.ProductId);
        Assert.AreEqual(3, item.Quantity);
        AssertPayloadContract(itemBytes, item.ToByteArray());

        var update = UpdateBasketRequest.Parser.ParseFrom(updateBytes);
        Assert.AreEqual(42, update.Items.Single().ProductId);
        Assert.AreEqual(3, update.Items.Single().Quantity);
        AssertPayloadContract(updateBytes, update.ToByteArray());

        var response = CustomerBasketResponse.Parser.ParseFrom(responseBytes);
        Assert.AreEqual(42, response.Items.Single().ProductId);
        Assert.AreEqual(3, response.Items.Single().Quantity);
        AssertPayloadContract(responseBytes, response.ToByteArray());
        Assert.IsEmpty(new GetBasketRequest().ToByteArray());
        Assert.IsEmpty(new DeleteBasketRequest().ToByteArray());
        Assert.IsEmpty(new DeleteBasketResponse().ToByteArray());
    }

    [TestMethod]
    public void ContractAssertionsRejectBreakingDescriptorsAndPayloads()
    {
        var expected = new MethodContract("UpdateBasket", "UpdateBasketRequest", "CustomerBasketResponse", false, false);
        AssertMethodContract(expected, expected);
        var basketService = new ServiceContract("Basket",
            [
                new MethodContract("GetBasket", "GetBasketRequest", "CustomerBasketResponse", false, false),
                expected,
                new MethodContract("DeleteBasket", "DeleteBasketRequest", "DeleteBasketResponse", false, false),
                new MethodContract("ListBasketSummaries", "ListBasketSummariesRequest", "ListBasketSummariesResponse", false, false)
            ]);
        var descriptorFixture = new[]
        {
            basketService,
            new ServiceContract("BasketAnalytics", [new MethodContract("GetSummary", "SummaryRequest", "SummaryResponse", false, false)])
        };
        AssertRequiredServiceContract(descriptorFixture, new ServiceContract("Basket",
            [
                new MethodContract("GetBasket", "GetBasketRequest", "CustomerBasketResponse", false, false),
                expected,
                new MethodContract("DeleteBasket", "DeleteBasketRequest", "DeleteBasketResponse", false, false)
            ]));
        Assert.Throws<AssertFailedException>(() => AssertMethodContract(expected, expected with { Name = "ReplaceBasket" }));
        Assert.Throws<AssertFailedException>(() => AssertMethodContract(expected, expected with { Input = "GetBasketRequest" }));
        Assert.Throws<AssertFailedException>(() => AssertMethodContract(expected, expected with { Output = "DeleteBasketResponse" }));
        Assert.Throws<AssertFailedException>(() => AssertMethodContract(expected, expected with { ClientStreaming = true }));
        Assert.Throws<AssertFailedException>(() => AssertMethodContract(expected, expected with { ServerStreaming = true }));
        AssertFieldContract(2, FieldType.Int32, true, 2, FieldType.Int32, true);
        Assert.Throws<AssertFailedException>(() => AssertFieldContract(2, FieldType.Int32, true, 3, FieldType.Int32, true));
        Assert.Throws<AssertFailedException>(() => AssertFieldContract(2, FieldType.Int32, true, 2, FieldType.Int64, true));
        Assert.Throws<AssertFailedException>(() => AssertFieldContract(2, FieldType.Int32, true, 2, FieldType.Int32, false));
        byte[] fixture = [0x10, 0x2A, 0x30, 0x03];
        AssertPayloadContract(fixture, fixture);
        var alteredPayload = fixture.ToArray();
        alteredPayload[^1] = 0x04;
        Assert.Throws<AssertFailedException>(() => AssertPayloadContract(fixture, alteredPayload));
    }

    [TestMethod]
    public async Task AddUpdateAndRemoveItemsPreserveConsumerContract()
    {
        CustomerBasket? state = null;
        var repository = Substitute.For<IBasketRepository>();
        repository.UpdateBasketAsync(Arg.Any<CustomerBasket>()).Returns(call =>
        {
            state = Clone(call.Arg<CustomerBasket>());
            return Task.FromResult(state);
        });
        repository.GetBasketAsync("buyer-1").Returns(_ => Task.FromResult(state));
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var context = CreateContext("buyer-1");

        var request = new UpdateBasketRequest();
        request.Items.Add(new BasketGrpc.BasketItem { ProductId = 42, Quantity = 3 });
        request.Items.Add(new BasketGrpc.BasketItem { ProductId = 7, Quantity = 1 });
        var response = await service.UpdateBasket(request, context);
        AssertBasket(response, (42, 3), (7, 1));
        await repository.Received(1).UpdateBasketAsync(Arg.Is<CustomerBasket>(b => b.BuyerId == "buyer-1" && b.Items.Count == 2));

        request = new UpdateBasketRequest();
        request.Items.Add(new BasketGrpc.BasketItem { ProductId = 42, Quantity = 5 });
        request.Items.Add(new BasketGrpc.BasketItem { ProductId = 7, Quantity = 1 });
        response = await service.UpdateBasket(request, context);
        AssertBasket(response, (42, 5), (7, 1));

        request = new UpdateBasketRequest();
        request.Items.Add(new BasketGrpc.BasketItem { ProductId = 7, Quantity = 1 });
        response = await service.UpdateBasket(request, context);
        AssertBasket(response, (7, 1));
        AssertBasket(await service.GetBasket(new GetBasketRequest(), context), (7, 1));

        response = await service.UpdateBasket(new UpdateBasketRequest(), context);
        Assert.IsEmpty(response.Items);
        Assert.IsEmpty((await service.GetBasket(new GetBasketRequest(), context)).Items);
        await repository.Received(4).UpdateBasketAsync(Arg.Is<CustomerBasket>(b => b.BuyerId == "buyer-1"));
    }

    [TestMethod]
    public async Task DeleteBasketPreservesConsumerContract()
    {
        CustomerBasket? state = new() { BuyerId = "buyer-1", Items = [new BasketItemModel { ProductId = 42, Quantity = 3 }] };
        var repository = Substitute.For<IBasketRepository>();
        repository.GetBasketAsync("buyer-1").Returns(_ => Task.FromResult(state));
        repository.DeleteBasketAsync("buyer-1").Returns(_ => { state = null; return Task.FromResult(true); });
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);
        var context = CreateContext("buyer-1");

        var response = await service.DeleteBasket(new DeleteBasketRequest(), context);
        Assert.IsInstanceOfType<DeleteBasketResponse>(response);
        Assert.IsEmpty(response.ToByteArray());
        Assert.IsEmpty((await service.GetBasket(new GetBasketRequest(), context)).Items);
        await service.DeleteBasket(new DeleteBasketRequest(), context);
        await repository.Received(2).DeleteBasketAsync("buyer-1");
    }

    [TestMethod]
    public async Task AuthenticationAndFailureStatusesPreserveConsumerContract()
    {
        var repository = Substitute.For<IBasketRepository>();
        repository.GetBasketAsync("missing").Returns(Task.FromResult<CustomerBasket>(null!));
        var service = new BasketService(repository, NullLogger<BasketService>.Instance);

        Assert.IsEmpty((await service.GetBasket(new GetBasketRequest(), CreateContext(null))).Items);
        await repository.DidNotReceive().GetBasketAsync(Arg.Any<string>());
        var updateError = await Assert.ThrowsAsync<RpcException>(() => service.UpdateBasket(new UpdateBasketRequest(), CreateContext(null)));
        Assert.AreEqual(StatusCode.Unauthenticated, updateError.StatusCode);
        var deleteError = await Assert.ThrowsAsync<RpcException>(() => service.DeleteBasket(new DeleteBasketRequest(), CreateContext(null)));
        Assert.AreEqual(StatusCode.Unauthenticated, deleteError.StatusCode);
        await repository.DidNotReceive().UpdateBasketAsync(Arg.Any<CustomerBasket>());
        await repository.DidNotReceive().DeleteBasketAsync(Arg.Any<string>());
        Assert.IsEmpty((await service.GetBasket(new GetBasketRequest(), CreateContext("missing"))).Items);

        repository.UpdateBasketAsync(Arg.Any<CustomerBasket>()).Returns(Task.FromResult<CustomerBasket>(null!));
        var missing = await Assert.ThrowsAsync<RpcException>(() => service.UpdateBasket(new UpdateBasketRequest(), CreateContext("missing")));
        Assert.AreEqual(StatusCode.NotFound, missing.StatusCode);
    }

    private static ServiceContract ToServiceContract(ServiceDescriptor service)
    {
        return new ServiceContract(service.Name, service.Methods.Select(method =>
            new MethodContract(method.Name, method.InputType.Name, method.OutputType.Name, method.IsClientStreaming, method.IsServerStreaming)).ToArray());
    }

    private static void AssertRequiredServiceContract(IEnumerable<ServiceContract> actual, ServiceContract required)
    {
        var service = actual.SingleOrDefault(candidate => candidate.Name == required.Name);
        Assert.IsNotNull(service, $"Required service {required.Name} was not found.");
        AssertRequiredMethodContracts(service.Methods, required.Methods);
    }

    private static void AssertRequiredMethodContracts(IEnumerable<MethodContract> actual, IEnumerable<MethodContract> required)
    {
        foreach (var expected in required)
        {
            var method = actual.SingleOrDefault(candidate => candidate.Name == expected.Name);
            Assert.IsNotNull(method, $"Required RPC {expected.Name} was not found.");
            AssertMethodContract(method, expected);
        }
    }

    private static void AssertMethodContract(MethodContract actual, MethodContract expected)
    {
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Input, actual.Input);
        Assert.AreEqual(expected.Output, actual.Output);
        Assert.AreEqual(expected.ClientStreaming, actual.ClientStreaming);
        Assert.AreEqual(expected.ServerStreaming, actual.ServerStreaming);
    }

    private static void AssertPayloadContract(byte[] expectedBaseline, byte[] actualPayload)
    {
        Assert.IsTrue(expectedBaseline.SequenceEqual(actualPayload), "Serialized payload differs from the fixed contract baseline.");
    }

    private static void AssertField<T>(string name, int number, FieldType type, bool repeated) where T : IMessage<T>
    {
        var field = typeof(T) == typeof(CustomerBasketResponse) ? CustomerBasketResponse.Descriptor.FindFieldByName(name) :
            typeof(T) == typeof(UpdateBasketRequest) ? UpdateBasketRequest.Descriptor.FindFieldByName(name) :
            BasketGrpc.BasketItem.Descriptor.FindFieldByName(name);
        Assert.IsNotNull(field);
        AssertFieldContract(number, type, repeated, field.FieldNumber, field.FieldType, field.IsRepeated);
    }

    private static void AssertFieldContract(int expectedNumber, FieldType expectedType, bool expectedRepeated, int number, FieldType type, bool repeated)
    {
        Assert.AreEqual(expectedNumber, number);
        Assert.AreEqual(expectedType, type);
        Assert.AreEqual(expectedRepeated, repeated);
    }

    private static CustomerBasket Clone(CustomerBasket basket) => new()
    {
        BuyerId = basket.BuyerId,
        Items = basket.Items.Select(i => new BasketItemModel { ProductId = i.ProductId, Quantity = i.Quantity }).ToList()
    };

    private static void AssertBasket(CustomerBasketResponse response, params (int ProductId, int Quantity)[] expected)
    {
        Assert.AreEqual(expected.Length, response.Items.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.AreEqual(expected[i].ProductId, response.Items[i].ProductId);
            Assert.AreEqual(expected[i].Quantity, response.Items[i].Quantity);
        }
    }

    private TestServerCallContext CreateContext(string? userId)
    {
        var context = TestServerCallContext.Create(cancellationToken: TestContext.CancellationToken);
        var httpContext = new DefaultHttpContext();
        if (userId is not null) httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId)]));
        context.SetUserState("__HttpContext", httpContext);
        return context;
    }

    private sealed record MethodContract(string Name, string Input, string Output, bool ClientStreaming, bool ServerStreaming);
    private sealed record ServiceContract(string Name, MethodContract[] Methods);
}
