# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

## Checkout restart recovery

Keep the PostgreSQL, Redis, and RabbitMQ stores when restarting application services. Aspire waits for the configured dependency health checks before starting checkout consumers. Restart the affected API or WebApp and wait for its `/health` endpoint to report healthy before routing checkout traffic.

If a checkout response was lost or timed out, treat its outcome as ambiguous. Retry with the original `x-requestid`; the Ordering API uses that identifier to return the prior successful result without creating another order. Do not generate a new request ID while resolving an ambiguous checkout. Verify the order and its items in the retained ordering database before telling the customer the checkout failed.

This procedure covers application restarts while durable stores remain available. Do not delete database volumes as part of restart recovery; database loss requires the separate database backup and disaster-recovery process.

Run the repository checks with `dotnet build eShop.Web.slnf` and `dotnet test --solution eShop.Web.slnf --no-build --no-progress --output detailed`. Full local Aspire checks use the repository's HTTP test mode (`ESHOP_USE_HTTP_ENDPOINTS=1`) and still require Docker.
