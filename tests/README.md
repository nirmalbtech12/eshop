# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

Basket API protobuf contracts are covered by `BasketApiContractTests` in `Basket.FunctionalTests`. The tests pin the gRPC service methods and protobuf message field numbers and types defined in `src/Basket.API/Proto/basket.proto`. Run them with `dotnet test tests/Basket.FunctionalTests/Basket.FunctionalTests.csproj`.
