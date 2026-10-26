# eShop Tests

This directory contains a collection of unit and functional tests for validating the behavior of various components in the eShop application.

**NOTE:** Functional tests in this leverage the Aspire host to spin up test containers and require that Docker be running as a pre-requisite.

## Recovering checkout after a service restart

If Basket, Ordering, or the WebApp restarts while checkout is in progress, keep the basket and retry checkout from the same browser session. The WebApp assigns a request ID to each checkout submission and sends it to Ordering; Ordering stores handled request IDs with order data so a retry with that ID does not create a second order. The basket is cleared only after the order request succeeds.

To confirm recovery, check the customer's orders in the WebApp or Ordering API. If the order is present, the retry is safe and the basket should be cleared after the successful response. If no order is present, retry checkout while the basket remains available. Aspire waits for Basket and Ordering health checks before starting the WebApp, so wait for those resources to become healthy before retrying. Do not manually clear the basket to recover an uncertain checkout.
