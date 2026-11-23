# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

## Basket API contracts

The [Basket API provider schema](../src/Basket.API/Proto/basket.proto) is checked against the independently generated [frozen consumer schema](Basket.UnitTests/Contracts/basket.consumer.proto). The [wire contract tests](Basket.UnitTests/BasketContractTests.cs) call the production `BasketService` through TestServer using the frozen client. Add and quantity changes both use `UpdateBasket` with a complete item list; removing an item replaces that list without the removed item. `DeleteBasket` clears the whole basket. The tests also cover buyer identity, anonymous mutation rejection, and the existing not-found response.

The [compatibility tests](Basket.UnitTests/BasketContractCompatibilityTests.cs) require the current provider schema to retain the frozen RPCs, message fields, field numbers, types, and cardinality. They reject incompatible descriptor mutations and allow additive fields. Update the frozen schema only when intentionally changing the consumer contract, and review the compatibility assertions and affected clients in the same change.

Run the basket tests locally with `dotnet test --project tests/Basket.UnitTests/Basket.UnitTests.csproj --no-progress --output detailed`. They are part of `eShop.Web.slnf`, which the pull request validation workflow builds and tests. These contract tests use TestServer and do not need Docker; the functional test suites above still require Docker for their Aspire-managed dependencies.
