# Local checkout recovery

This procedure covers recovery from a graceful `ordering-api` restart while the
local Aspire application and its PostgreSQL resource remain running. It does not
cover database disaster recovery or guarantee exactly-once delivery by RabbitMQ.

## Check service health

In the Aspire dashboard, inspect `ordering-api`, `postgres`, and `eventbus` and
review the ordering API logs for the failed request. The Development `/health`
endpoint reports readiness, including PostgreSQL connectivity; `/alive` reports
process liveness only. A live process can be unready. Do not reset the PostgreSQL
resource or its data as a first recovery step.

## Restart and confirm recovery

From the repository root, use the Aspire CLI against this AppHost:

```sh
aspire resource ordering-api restart --apphost src/eShop.AppHost
aspire wait ordering-api --apphost src/eShop.AppHost
```

Wait for the resource to become healthy and confirm `/health` succeeds. Keep the
existing `postgres` resource and `orderingdb` database in place. Check the
dashboard logs for migration or connection failures before retrying checkout.

## Resolve an uncertain checkout

Return to the checkout page. If the order was not confirmed, the basket remains
available and the page shows a retry message. Submit again using the same form
request identity; the API uses that identity to safely recognize a request that
may already have committed. After confirmation, open **Orders** and verify the
order and its items. If it is still unavailable or health does not recover,
preserve the database, collect service logs without credentials, and escalate to
the service operator.

Avoid deleting or recreating persistent resources during this procedure. A
database restore or broker delivery guarantee requires a separate recovery plan.
