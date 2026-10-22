# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application. The Basket functional tests also pin the Basket gRPC API contract: RPC names, request and response types, and protobuf field names and numbers. Run them with `dotnet test tests/Basket.FunctionalTests/Basket.FunctionalTests.csproj`.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.