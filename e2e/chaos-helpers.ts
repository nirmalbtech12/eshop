import { execFile, spawn } from 'node:child_process';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);

export async function restartOrderingApi(): Promise<void> {
  await stopOrderingApi();
  await startOrderingApi();
}

export async function stopOrderingApi(): Promise<void> {
  await execFileAsync('aspire', [
    'resource', 'ordering-api', 'stop', '--apphost', 'src/eShop.AppHost', '--non-interactive',
  ], { timeout: 90_000, cwd: process.cwd() });
}

export async function startOrderingApi(): Promise<void> {
  await execFileAsync('aspire', [
    'resource', 'ordering-api', 'start', '--apphost', 'src/eShop.AppHost', '--non-interactive',
  ], { timeout: 90_000, cwd: process.cwd() });
  await execFileAsync('aspire', [
    'wait', 'ordering-api', '--apphost', 'src/eShop.AppHost', '--timeout', '90', '--non-interactive',
  ], { timeout: 95_000, cwd: process.cwd() });
}

export async function stopOrderingApiDuringBlockedCheckout(releaseLock: () => Promise<void>): Promise<void> {
  const stop = execFileAsync('aspire', [
    'resource', 'ordering-api', 'stop', '--apphost', 'src/eShop.AppHost', '--non-interactive',
  ], { timeout: 90_000, cwd: process.cwd() }).then(() => null, error => error);

  const deadline = Date.now() + 12_000;
  let stoppingObserved = false;
  while (Date.now() < deadline) {
    try {
      const { stdout } = await execFileAsync('aspire', [
        'describe', 'ordering-api', '--apphost', 'src/eShop.AppHost', '--format', 'Json', '--non-interactive',
      ], { timeout: 5_000, cwd: process.cwd() });
      const jsonStart = stdout.indexOf('{');
      const description = JSON.parse(stdout.slice(jsonStart));
      const serialized = JSON.stringify(description);
      if (/ordering-api/i.test(serialized) && /"state"\s*:\s*"Stopping"/i.test(serialized)) {
        stoppingObserved = true;
        break;
      }
    } catch {
      // Retry resource observation until its finite deadline.
    }
    await new Promise(resolve => setTimeout(resolve, 200));
  }

  await releaseLock();
  if (!stoppingObserved) {
    throw new Error('Aspire did not report ordering-api stopping after the in-flight checkout was observed.');
  }

  const stopError = await stop;
  if (stopError) throw stopError;
}

let postgresContainer: string | undefined;

async function describeResource(resourceName: string): Promise<any> {
  const { stdout } = await execFileAsync('aspire', [
    'describe', resourceName, '--apphost', 'src/eShop.AppHost', '--format', 'Json', '--non-interactive',
  ], { timeout: 10_000, cwd: process.cwd() });
  const jsonStart = stdout.indexOf('{');
  if (jsonStart < 0) throw new Error(`Aspire did not return resource details for ${resourceName}.`);
  const details = JSON.parse(stdout.slice(jsonStart));
  const resources = details.resources?.filter((resource: { displayName?: string }) => resource.displayName === resourceName) ?? [];
  if (resources.length !== 1) throw new Error(`Expected one ${resourceName} resource from the current AppHost; found ${resources.length}.`);
  return resources[0];
}

async function getPostgresContainer(): Promise<string> {
  if (postgresContainer) return postgresContainer;

  const resource = await describeResource('postgres');
  const containerId = resource.properties?.['container.id'];
  const containerName = resource.name;
  if (resource.resourceType !== 'Container' || !containerName?.startsWith('postgres-') || !/^[0-9a-f]{64}$/i.test(containerId ?? '')) {
    throw new Error('The current Aspire AppHost did not identify its PostgreSQL container.');
  }
  const labels = await execFileAsync('docker', [
    'inspect', '--format', '{{.Id}} {{index .Config.Labels "com.microsoft.developer.usvc-dev.name"}} {{.Config.Image}}', containerId,
  ], { timeout: 10_000 });
  const [inspectedId, inspectedName, inspectedImage] = labels.stdout.trim().split(' ');
  if (inspectedId !== containerId || inspectedName !== containerName || !inspectedImage.includes('ankane/pgvector')) {
    throw new Error(`Aspire PostgreSQL resource ${containerName} did not match its Docker container identity.`);
  }

  postgresContainer = containerId;
  return postgresContainer;
}

export async function orderingApiHttpOrigin(): Promise<string> {
  const resource = await describeResource('ordering-api');
  const endpoint = resource.urls?.find((entry: { url?: string }) => entry.url?.startsWith('http://'))?.url;
  if (!endpoint) throw new Error('The current Aspire ordering-api resource has no HTTP endpoint.');
  return new URL(endpoint).origin;
}

export async function orderingApiHealthStatuses(): Promise<{ health: number; alive: number }> {
  const origin = await orderingApiHttpOrigin();
  const [health, alive] = await Promise.all([
    fetch(`${origin}/health`, { signal: AbortSignal.timeout(5_000) }),
    fetch(`${origin}/alive`, { signal: AbortSignal.timeout(5_000) }),
  ]);
  return { health: health.status, alive: alive.status };
}

async function setOrderingDatabaseConnections(allowConnections: boolean): Promise<void> {
  const container = await getPostgresContainer();
  const sql = `ALTER DATABASE orderingdb ALLOW_CONNECTIONS ${allowConnections ? 'true' : 'false'}`;
  await execFileAsync('docker', [
    'exec', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres -c "$1"', 'psql', sql,
  ], { timeout: 15_000 });
}

export async function disableOrderingDatabase(): Promise<void> {
  await setOrderingDatabaseConnections(false);
}

export async function restoreOrderingDatabase(): Promise<void> {
  await setOrderingDatabaseConnections(true);
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    try {
      await execFileAsync('docker', [
        'exec', await getPostgresContainer(), 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orderingdb -At -c "SELECT 1"',
      ], { timeout: 5_000 });
      return;
    } catch {
      await new Promise(resolve => setTimeout(resolve, 250));
    }
  }
  throw new Error('Ordering database did not accept connections within 30 seconds after recovery.');
}

export async function countRequestMarkers(requestId: string): Promise<number> {
  if (!/^[0-9a-f-]{36}$/i.test(requestId)) throw new Error('Invalid checkout request ID.');
  const container = await getPostgresContainer();
  const { stdout } = await execFileAsync('docker', [
    'exec', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orderingdb -At -c "$1"', 'psql',
    `SELECT count(*) FROM ordering.requests WHERE "Id" = '${requestId}'::uuid`,
  ], { timeout: 10_000 });
  return Number.parseInt(stdout.trim(), 10);
}

export async function checkoutEffectCounts(since: string): Promise<{ requests: number; orders: number; items: number; events: number }> {
  const timestamp = Date.parse(since);
  if (!Number.isFinite(timestamp)) throw new Error('Invalid checkout observation time.');
  const container = await getPostgresContainer();
  const sql = `WITH recent_orders AS (
    SELECT "Id" FROM ordering.orders WHERE "OrderDate" >= '${new Date(timestamp).toISOString()}'::timestamptz
  ) SELECT json_build_object(
    'requests', (SELECT count(*) FROM ordering.requests WHERE "Time" >= '${new Date(timestamp).toISOString()}'::timestamptz),
    'orders', (SELECT count(*) FROM recent_orders),
    'items', (SELECT count(*) FROM ordering."orderItems" WHERE "OrderId" IN (SELECT "Id" FROM recent_orders)),
    'events', (SELECT count(*) FROM ordering."IntegrationEventLog" WHERE "CreationTime" >= '${new Date(timestamp).toISOString()}'::timestamptz AND "EventTypeName" LIKE '%OrderStartedIntegrationEvent')
  )::text`;
  const { stdout } = await execFileAsync('docker', [
    'exec', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orderingdb -At -c "$1"', 'psql', sql,
  ], { timeout: 10_000 });
  return JSON.parse(stdout.trim());
}

export async function inspectCheckout(requestId: string, orderNumber: number, since: string): Promise<string> {
  if (!/^[0-9a-f-]{36}$/i.test(requestId) || !Number.isSafeInteger(orderNumber) || !Number.isFinite(Date.parse(since))) {
    throw new Error('Invalid checkout identity for PostgreSQL inspection.');
  }

  const sql = `WITH target_order AS (
    SELECT o."Id", o."Address_Street", o."Address_City", o."Address_State", o."Address_Country", o."Address_ZipCode", b."IdentityGuid"
    FROM ordering.orders o LEFT JOIN ordering.buyers b ON b."Id" = o."BuyerId" WHERE o."Id" = ${orderNumber}
  )
  SELECT json_build_object(
    'request', (SELECT json_build_object('id', r."Id", 'name', r."Name", 'time', r."Time") FROM ordering.requests r WHERE r."Id" = '${requestId}'::uuid),
    'order', (SELECT json_build_object(
      'id', t."Id", 'userId', t."IdentityGuid", 'street', t."Address_Street", 'city', t."Address_City", 'state', t."Address_State", 'country', t."Address_Country", 'zipCode', t."Address_ZipCode",
      'items', (SELECT json_agg(json_build_object('productName', i."ProductName", 'units', i."Units", 'unitPrice', i."UnitPrice", 'discount', i."Discount") ORDER BY i."Id") FROM ordering."orderItems" i WHERE i."OrderId" = t."Id")
    ) FROM target_order t),
    'events', (SELECT coalesce(json_agg(json_build_object('eventId', e."EventId", 'transactionId', e."TransactionId", 'eventType', e."EventTypeName", 'creationTime', e."CreationTime", 'userId', e."Content"::jsonb ->> 'UserId') ORDER BY e."EventId"), '[]'::json)
      FROM ordering."IntegrationEventLog" e, target_order t
      WHERE e."EventTypeName" LIKE '%OrderStartedIntegrationEvent' AND e."CreationTime" >= '${new Date(since).toISOString()}'::timestamptz
        AND e."Content"::jsonb ->> 'UserId' = t."IdentityGuid")
  )::text;`;
  const container = await getPostgresContainer();
  const { stdout } = await execFileAsync('docker', [
    'exec', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orderingdb -At -c "$1"', 'psql', sql,
  ], { timeout: 15_000 });
  return stdout.trim();
}

export async function lockOrdersTable(): Promise<() => Promise<void>> {
  const container = await getPostgresContainer();
  const psql = spawn('docker', [
    'exec', '-i', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d orderingdb -Atq',
  ], { stdio: 'pipe' });
  let output = '';
  let error = '';
  psql.stdout.on('data', chunk => { output += chunk.toString(); });
  psql.stderr.on('data', chunk => { error += chunk.toString(); });
  psql.stdin.write('BEGIN;\nLOCK TABLE ordering.orders IN ACCESS EXCLUSIVE MODE;\nSELECT \'BEN50_LOCKED\';\n');

  const lockDeadline = Date.now() + 15_000;
  while (!output.includes('BEN50_LOCKED') && Date.now() < lockDeadline) {
    if (psql.exitCode !== null) throw new Error(`PostgreSQL lock fixture exited early: ${error}`);
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!output.includes('BEN50_LOCKED')) {
    psql.kill();
    throw new Error(`PostgreSQL lock fixture did not acquire the order table: ${error}`);
  }

  let released = false;
  return async () => {
    if (released) return;
    released = true;
    psql.stdin.end('ROLLBACK;\n\\q\n');
    await new Promise<void>((resolve, reject) => {
      psql.once('exit', code => code === 0 ? resolve() : reject(new Error(`PostgreSQL lock fixture exited with ${code}: ${error}`)));
    });
  };
}

export async function waitForBlockedCheckout(): Promise<void> {
  const container = await getPostgresContainer();
  const query = `SELECT count(*) FROM pg_stat_activity WHERE datname = 'orderingdb' AND wait_event_type = 'Lock' AND state = 'active' AND pid <> pg_backend_pid() AND query ~* '(orders|orderItems)'`;
  const deadline = Date.now() + 30_000;
  while (Date.now() < deadline) {
    try {
      const result = await execFileAsync('docker', [
        'exec', container, 'sh', '-c', 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U "$POSTGRES_USER" -d orderingdb -At -c "$1"', 'psql', query,
      ], { timeout: 5_000 });
      if (Number.parseInt(result.stdout.trim(), 10) > 0) return;
    } catch {
      // The bounded deadline below reports failure if PostgreSQL cannot observe the blocked request.
    }
    await new Promise(resolve => setTimeout(resolve, 300));
  }
  throw new Error('No checkout query waiting on the order-table lock was observed within 30 seconds.');
}
