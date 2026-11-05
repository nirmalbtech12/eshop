# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

## Basket API contracts

The gRPC contract tests in [BasketGrpcContractTests](Basket.UnitTests/BasketGrpcContractTests.cs) call the real service through an in-process TestServer. They use the provider definition in [basket.proto](../src/Basket.API/Proto/basket.proto) and the independently maintained [basket.consumer.proto](Basket.UnitTests/Contracts/basket.consumer.proto). The [compatibility checks](Basket.UnitTests/BasketContractCompatibilityTests.cs) compare the provider descriptors with that frozen consumer definition and include breaking-change controls.

`UpdateBasket` covers both the initial add and later replacement of a buyer's items. `DeleteBasket` covers removal. The round-trip checks include item IDs, quantities, cardinality, buyer association and the response after deletion. Anonymous mutation and missing-basket errors are covered as well.

Run the basket unit and contract tests with .NET SDK 10:

```sh
dotnet test --project tests/Basket.UnitTests/Basket.UnitTests.csproj --no-progress --output detailed
```

These contracts use TestServer and do not require Docker. The project is included in `eShop.Web.slnf` and the existing PR validation workflow. Update the frozen consumer proto only after reviewing the provider change for compatibility with existing consumers; do not regenerate it automatically from the provider during tests.
