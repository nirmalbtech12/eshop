# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

## Basket API contracts

The [Basket contract tests](Basket.UnitTests/BasketContractTests.cs) protect the consumer-facing protobuf definitions in [basket.proto](../src/Basket.API/Proto/basket.proto), including RPC signatures, field numbers, wire bytes, authentication statuses, and basket operations. `UpdateBasket` replaces the caller's complete item collection, so adding items, changing quantities, and removing individual items use that operation; `DeleteBasket` removes the whole basket. The tests use local service contexts and substitutes and do not require Docker or a contract broker.

Treat changes to these contracts as compatibility changes: review consumer impact and update the explicit descriptor and wire fixtures deliberately. Do not regenerate expected values automatically as part of a routine test run.

Run the focused contracts with `dotnet test --project tests/Basket.UnitTests/Basket.UnitTests.csproj --no-progress --output normal`. The existing `.github/workflows/pr-validation.yml` workflow sets up .NET and tests `eShop.Web.slnf`, which includes `Basket.UnitTests`; the solution command discovers these tests without a separate CI step. The broader solution tests also include Aspire functional tests and retain their Docker prerequisite.
