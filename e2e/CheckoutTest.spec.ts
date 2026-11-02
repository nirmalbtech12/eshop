import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart } from './cart-helpers';
import {
  checkoutEffectCounts,
  countRequestMarkers,
  disableOrderingDatabase,
  inspectCheckout,
  orderingApiHttpOrigin,
  orderingApiHealthStatuses,
  lockOrdersTable,
  restartOrderingApi,
  startOrderingApi,
  restoreOrderingDatabase,
  stopOrderingApiDuringBlockedCheckout,
  waitForBlockedCheckout,
} from './chaos-helpers';

async function readCheckoutSnapshot(page: import('@playwright/test').Page, requestId: string, since: string) {
  const orderText = await page.locator('.order-number').last().innerText();
  const orderNumber = Number.parseInt(orderText, 10);
  const snapshot = JSON.parse(await inspectCheckout(requestId, orderNumber, since));

  expect(snapshot.request).toMatchObject({ id: requestId, name: 'CreateOrderCommand' });
  expect(snapshot.order.id).toBe(orderNumber);
  expect(snapshot.order.street).toBe('15703 NE 61st Ct');
  expect(snapshot.order.city).toBe('Redmond');
  expect(snapshot.order.items).toHaveLength(1);
  expect(snapshot.order.items[0].productName).toBe('Adventurer GPS Watch');
  expect(snapshot.order.items[0].units).toBe(1);
  expect(snapshot.order.items[0].unitPrice).toBeGreaterThan(0);
  expect(snapshot.events).toHaveLength(1);
  expect(snapshot.events[0].transactionId).toMatch(/^[0-9a-f-]{36}$/i);
  expect(snapshot.events[0].userId).toBe(snapshot.order.userId);
  return snapshot;
}

test('committed checkout survives ordering restart', async ({ page }) => {
  await emptyCart(page);

  await page.goto('/user/orders');
  await expect(page.getByRole('heading', { name: 'Orders', exact: true })).toBeVisible();
  const existingOrderCount = await page.locator('.order-number').count();

  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
  await page.getByRole('link', { name: 'Check out' }).click();

  await expect(page.getByRole('heading', { name: 'Checkout' })).toBeVisible();
  await expect(page.getByLabel('Address')).toHaveValue('15703 NE 61st Ct');
  await expect(page.getByLabel('City')).toHaveValue('Redmond');
  const requestId = await page.locator('input[name="Info.RequestId"]').inputValue();
  const checkoutStarted = new Date().toISOString();
  await page.getByRole('button', { name: 'Place order' }).click();

  await expect(page).toHaveURL(/\/user\/orders$/);
  await expect(page.getByRole('heading', { name: 'Orders', exact: true })).toBeVisible();
  await expect(page.locator('.order-number')).toHaveCount(existingOrderCount + 1);
  const orderNumber = await page.locator('.order-number').last().innerText();
  const beforeRestart = await readCheckoutSnapshot(page, requestId, checkoutStarted);

  await restartOrderingApi();
  await page.reload();
  await expect(page.locator('.order-number').last()).toHaveText(orderNumber);
  const afterRestart = await readCheckoutSnapshot(page, requestId, checkoutStarted);
  expect(afterRestart).toEqual(beforeRestart);
});

test('checkout remains retryable during a database outage', async ({ page }) => {
  await emptyCart(page);
  await page.goto('/user/orders');
  const existingOrderCount = await page.locator('.order-number').count();
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  await page.getByRole('link', { name: 'Check out' }).click();
  await expect(page.getByRole('heading', { name: 'Checkout' })).toBeVisible();

  const requestIdInput = page.locator('input[name="Info.RequestId"]');
  const requestId = await requestIdInput.inputValue();
  const checkoutStarted = new Date().toISOString();
  const baselineEffects = await checkoutEffectCounts(checkoutStarted);
  const apiProbe = async (origin: string) => page.request.post(`${origin}/api/orders/?api-version=1.0`, {
    headers: { 'x-requestid': crypto.randomUUID() },
    data: {
      userId: 'readiness-probe', userName: 'readiness-probe', city: 'Redmond', street: 'Test', state: 'WA', country: 'US', zipCode: '98052',
      cardNumber: '1111222233334444', cardHolderName: 'Readiness Probe', cardExpiration: '2030-01-01T00:00:00Z', cardSecurityNumber: '123',
      cardTypeId: 1, buyer: 'readiness-probe', items: [],
    },
  });
  try {
    expect(await orderingApiHealthStatuses()).toEqual({ health: 200, alive: 200 });
    const apiOrigin = await orderingApiHttpOrigin();
    const healthyProbe = await apiProbe(apiOrigin);
    expect(healthyProbe.status()).not.toBe(503);
    await disableOrderingDatabase();
    await expect.poll(orderingApiHealthStatuses, { timeout: 30_000 }).toEqual({ health: 503, alive: 200 });
    expect((await apiProbe(apiOrigin)).status()).toBe(503);
    await page.getByRole('button', { name: 'Place order' }).click();
    await expect(page.getByRole('alert')).toContainText('Your basket is still available', { timeout: 30_000 });
    await expect(page).toHaveURL(/\/checkout$/);
    await expect(requestIdInput).toHaveValue(requestId);
  } finally {
    await restoreOrderingDatabase();
  }

  await expect.poll(orderingApiHealthStatuses, { timeout: 30_000 }).toEqual({ health: 200, alive: 200 });
  expect((await apiProbe(await orderingApiHttpOrigin())).status()).toBe(401);
  await expect(requestIdInput).toHaveValue(requestId);
  expect(await countRequestMarkers(requestId)).toBe(0);
  expect(await checkoutEffectCounts(checkoutStarted)).toEqual(baselineEffects);
  await page.getByRole('button', { name: 'Place order' }).click();
  await expect(page).toHaveURL(/\/user\/orders$/);
  await expect(page.locator('.order-number')).toHaveCount(existingOrderCount + 1);
  await readCheckoutSnapshot(page, requestId, checkoutStarted);
  expect(await checkoutEffectCounts(checkoutStarted)).toEqual({
    requests: baselineEffects.requests + 1,
    orders: baselineEffects.orders + 1,
    items: baselineEffects.items + 1,
    events: baselineEffects.events + 1,
  });
});

test('checkout recovers when ordering restarts during a blocked write', async ({ page }) => {
  await emptyCart(page);
  await page.goto('/user/orders');
  const existingOrderCount = await page.locator('.order-number').count();
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  await page.getByRole('link', { name: 'Check out' }).click();
  await expect(page.getByRole('heading', { name: 'Checkout' })).toBeVisible();

  const requestIdInput = page.locator('input[name="Info.RequestId"]');
  const requestId = await requestIdInput.inputValue();
  const checkoutStarted = new Date().toISOString();
  const releaseLock = await lockOrdersTable();
  let orderingStopAttempted = false;
  const submit = page.getByRole('button', { name: 'Place order' }).click({ timeout: 90_000 })
    .then(() => null, error => error);
  try {
    await waitForBlockedCheckout();
    orderingStopAttempted = true;
    await stopOrderingApiDuringBlockedCheckout(releaseLock);
  } finally {
    await releaseLock();
    if (orderingStopAttempted) await startOrderingApi();
  }

  await submit;
  await expect.poll(async () =>
    (await page.getByRole('alert').count()) > 0 || /\/user\/orders$/.test(page.url()),
    { timeout: 30_000 },
  ).toBe(true);
  if (/\/checkout$/.test(page.url())) {
    await expect(page.getByRole('alert')).toContainText('Your basket is still available');
    await expect(requestIdInput).toHaveValue(requestId);
    await page.getByRole('button', { name: 'Place order' }).click();
  }
  await expect(page).toHaveURL(/\/user\/orders$/);
  await expect(page.locator('.order-number')).toHaveCount(existingOrderCount + 1);
  await readCheckoutSnapshot(page, requestId, checkoutStarted);
});
