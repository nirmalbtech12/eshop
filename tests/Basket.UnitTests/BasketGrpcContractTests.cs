using System.Linq;
using eShop.Basket.API.Grpc;
using eShop.Basket.UnitTests.Helpers;
using Google.Protobuf.Reflection;
using Grpc.Core;

namespace eShop.Basket.UnitTests;

[TestClass]
public class BasketGrpcContractTests
{
    [TestMethod]
    public async Task AddUpdateRemoveRoundTripPreservesConsumerContract()
    {
        await using var host = await BasketContractHost.StartAsync();
        host.Repository.Seed("buyer-2", [(99, 8)]);

        AssertBasket(await host.InvokeAsync("GetBasket", [], "buyer-1"));
        AssertBasket(await host.InvokeAsync("UpdateBasket", Update((42, 3)), "buyer-1"), (42, 3));
        AssertBasket(await host.InvokeAsync("GetBasket", [], "buyer-1"), (42, 3));
        AssertBasket(await host.InvokeAsync("UpdateBasket", Update((42, 5), (7, 1)), "buyer-1"), (42, 5), (7, 1));
        AssertBasket(await host.InvokeAsync("GetBasket", [], "buyer-1"), (42, 5), (7, 1));
        AssertBasket(await host.InvokeAsync("UpdateBasket", Update((7, 1)), "buyer-1"), (7, 1));
        AssertBasket(await host.InvokeAsync("DeleteBasket", [], "buyer-1"));
        AssertBasket(await host.InvokeAsync("GetBasket", [], "buyer-1"));
        AssertBasket(await host.InvokeAsync("GetBasket", [], "buyer-2"), (99, 8));
        CollectionAssert.AreEqual(new[] { "update:buyer-1", "update:buyer-1", "update:buyer-1", "delete:buyer-1" }, host.Repository.Mutations.ToArray());
    }

    [TestMethod]
    public async Task ExistingErrorContractsArePreserved()
    {
        await using var host = await BasketContractHost.StartAsync();
        var unauthenticated = await Assert.ThrowsAsync<RpcException>(() => host.InvokeAsync("UpdateBasket", Update((42, 3))));
        Assert.AreEqual(StatusCode.Unauthenticated, unauthenticated.StatusCode);
        var unauthenticatedDelete = await Assert.ThrowsAsync<RpcException>(() => host.InvokeAsync("DeleteBasket", []));
        Assert.AreEqual(StatusCode.Unauthenticated, unauthenticatedDelete.StatusCode);
        Assert.IsEmpty(host.Repository.Mutations);

        host.Repository.ReturnNullOnUpdate = true;
        var notFound = await Assert.ThrowsAsync<RpcException>(() => host.InvokeAsync("UpdateBasket", Update((42, 3)), "missing"));
        Assert.AreEqual(StatusCode.NotFound, notFound.StatusCode);
        CollectionAssert.AreEqual(new[] { "update:missing" }, host.Repository.Mutations.ToArray());
    }

    [TestMethod]
    public void ProviderDescriptorPreservesBaselineContract()
    {
        var responseItems = CustomerBasketResponse.Descriptor.FindFieldByName("items")!;
        var requestItems = UpdateBasketRequest.Descriptor.FindFieldByName("items")!;
        var product = eShop.Basket.API.Grpc.BasketItem.Descriptor.FindFieldByName("product_id")!;
        var quantity = eShop.Basket.API.Grpc.BasketItem.Descriptor.FindFieldByName("quantity")!;
        Assert.AreEqual(1, responseItems.FieldNumber);
        Assert.AreEqual(FieldType.Message, responseItems.FieldType);
        Assert.IsTrue(responseItems.IsRepeated);
        Assert.AreEqual(2, requestItems.FieldNumber);
        Assert.IsTrue(requestItems.IsRepeated);
        Assert.AreEqual(2, product.FieldNumber);
        Assert.AreEqual(FieldType.Int32, product.FieldType);
        Assert.AreEqual(6, quantity.FieldNumber);
        Assert.AreEqual(FieldType.Int32, quantity.FieldType);
        Assert.AreEqual("GetBasket", BasketReflection.Descriptor.Services.Single().Methods[0].Name);
        CollectionAssert.AreEquivalent(new[] { "GetBasket", "UpdateBasket", "DeleteBasket" },
            BasketReflection.Descriptor.Services.Single().Methods.Select(method => method.Name).ToArray());
    }

    [TestMethod]
    public async Task BasketContractRejectsBreakingChanges()
    {
        await using var host = await BasketContractHost.StartAsync();
        var absent = await Assert.ThrowsAsync<RpcException>(() => host.InvokeAsync("MissingBasket", []));
        Assert.AreEqual(StatusCode.Unimplemented, absent.StatusCode);

        var wrongTagResponse = await host.InvokeAsync("UpdateBasket", UpdateWithProductTagThree(42, 3), "buyer-1");
        Assert.Throws<AssertFailedException>(() => AssertBasket(wrongTagResponse, (42, 3)));
        var incorrectQuantityResponse = EncodeBasket((42, 4));
        Assert.Throws<AssertFailedException>(() => AssertBasket(incorrectQuantityResponse, (42, 3)));
        AssertBasket(await host.InvokeAsync("UpdateBasket", Update((42, 3)), "buyer-1"), (42, 3));
    }

    private static byte[] Update(params (int Product, int Quantity)[] items) =>
        items.SelectMany(item => LengthDelimited(2, EncodeBasketItem(item.Product, item.Quantity))).ToArray();

    private static byte[] UpdateWithProductTagThree(int product, int quantity) =>
        LengthDelimited(2, Scalar(3, product).Concat(Scalar(6, quantity)).ToArray()).ToArray();

    private static byte[] EncodeBasket(params (int Product, int Quantity)[] items) =>
        items.SelectMany(item => LengthDelimited(1, EncodeBasketItem(item.Product, item.Quantity))).ToArray();

    private static byte[] EncodeBasketItem(int product, int quantity) =>
        Scalar(2, product).Concat(Scalar(6, quantity)).ToArray();

    private static IEnumerable<byte> Scalar(int field, int value) => Varint(field << 3).Concat(Varint(value));

    private static IEnumerable<byte> LengthDelimited(int field, byte[] value) =>
        Varint((field << 3) | 2).Concat(Varint(value.Length)).Concat(value);

    private static byte[] Varint(int value)
    {
        var bytes = new List<byte>();
        uint remaining = (uint)value;
        while (remaining >= 0x80)
        {
            bytes.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }
        bytes.Add((byte)remaining);
        return [.. bytes];
    }

    private static void AssertBasket(byte[] response, params (int Product, int Quantity)[] expected)
    {
        var values = new List<(int Product, int Quantity)>();
        for (var index = 0; index < response.Length;)
        {
            var (tag, afterTag) = ReadVarint(response, index);
            Assert.AreEqual(10u, tag, "Response must contain repeated items at field 1.");
            var (length, afterLength) = ReadVarint(response, afterTag);
            var end = checked(afterLength + (int)length);
            int product = 0, quantity = 0;
            for (var itemIndex = afterLength; itemIndex < end;)
            {
                var (itemTag, itemAfterTag) = ReadVarint(response, itemIndex);
                var (value, itemAfterValue) = ReadVarint(response, itemAfterTag);
                if (itemTag == 16) product = (int)value;
                else if (itemTag == 48) quantity = (int)value;
                itemIndex = itemAfterValue;
            }
            values.Add((product, quantity));
            index = end;
        }
        CollectionAssert.AreEqual(expected, values.ToArray());
    }

    private static (uint Value, int Next) ReadVarint(byte[] data, int start)
    {
        uint value = 0;
        var shift = 0;
        while (true)
        {
            var current = data[start++];
            value |= (uint)(current & 0x7f) << shift;
            if ((current & 0x80) == 0) return (value, start);
            shift += 7;
        }
    }
}
