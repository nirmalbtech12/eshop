# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

## Basket gRPC contracts

The wire contract is defined in [`Basket API proto`](../src/Basket.API/Proto/basket.proto) and exercised independently in [`BasketGrpcContractTests`](Basket.UnitTests/BasketGrpcContractTests.cs). The contract tests use literal gRPC method paths and protobuf field tags against the production `BasketService` in an isolated TestServer; they do not build expected payloads from provider generated messages.

Adding an item and changing quantities both use `UpdateBasket`, which replaces the submitted item list. Removing one item therefore sends the remaining list; clearing the whole basket uses `DeleteBasket`. The tests also pin the existing authentication and missing basket statuses. When changing the proto, update the literal compatibility assertions and keep the negative controls meaningful so incompatible paths, tags, or response values remain detectable.

Run the contracts and the existing Basket unit tests with `dotnet test --project tests/Basket.UnitTests/Basket.UnitTests.csproj`. The main PR/push CI runs `dotnet test --solution eShop.Web.slnf`; this includes Basket.UnitTests. These TestServer contracts need no Docker. The functional test suites still use Aspire containers and require Docker.
