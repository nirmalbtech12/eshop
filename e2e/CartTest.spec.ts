import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart, removeFirstCartItem } from './cart-helpers';

test('add, update, and remove an item from the cart', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');

  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
  await expect(page.getByText('Adventurer GPS Watch')).toBeVisible();

  const quantity = page.locator('[data-cart-quantity-input]');
  await expect(quantity).toHaveValue('1');

  const updateRequest = page.waitForRequest(request =>
    request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([
    updateRequest,
    page.getByRole('button', { name: /^Increase quantity/ }).first().click(),
  ]);
  await expect(quantity).toHaveValue('2', { timeout: 15_000 });
  await expect(page.locator('.cart-summary-total')).toContainText('$399.98', { timeout: 15_000 });

  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await addProductToCart(page, 'AeroLite Cycling Helmet');
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(2);
  await removeFirstCartItem(page);
  await expect(page.getByText('AeroLite Cycling Helmet')).toBeVisible({ timeout: 15_000 });
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await page.reload();
  await expect(page.getByText('AeroLite Cycling Helmet')).toBeVisible();
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('typed quantity persists across immediate navigation', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]');
  await quantity.fill('3');
  const firstWrite = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  const freshCart = page.waitForRequest(request =>
    request.method() === 'GET' && new URL(request.url()).pathname === '/cart');
  await quantity.press('Enter');
  await firstWrite;
  await freshCart;
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3', { timeout: 15_000 });

  let releasePost!: () => void;
  const postBarrier = new Promise<void>(resolve => { releasePost = resolve; });
  let enteredPost!: () => void;
  const postStarted = new Promise<void>(resolve => { enteredPost = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      enteredPost();
      await postBarrier;
    }
    await route.continue();
  });

  await quantity.fill('4');
  const post = quantity.press('Enter');
  try {
    await postStarted;
    await page.getByRole('link', { name: 'Continue shopping' }).click();
    releasePost();
    await post;
    await expect(page).toHaveURL(/\/$/);
  } finally {
    releasePost();
    await page.unroute('**/cart');
  }

  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('4', { timeout: 15_000 });
  await addProductToCart(page, 'AeroLite Cycling Helmet');
  await page.goto('/cart');
  const rows = page.locator('.cart-item');
  await expect(rows).toHaveCount(2);
  const lineAmounts = await rows.evaluateAll(items => items.map(row => {
    const unitPrice = Number(row.querySelector('.catalog-item-content .price')!.textContent!.replace(/[^\d.]/g, ''));
    const quantity = Number((row.querySelector('[data-cart-quantity-input]') as HTMLInputElement).value);
    const total = Number(row.querySelector('.catalog-item-total')!.textContent!.replace(/[^\d.]/g, ''));
    return { unitPrice, quantity, total };
  }));
  for (const line of lineAmounts) {
    expect(Math.round(line.unitPrice * 100) * line.quantity).toBe(Math.round(line.total * 100));
  }
  const expectedTotal = lineAmounts.reduce((sum, line) => sum + Math.round(line.total * 100), 0);
  const visibleTotal = Number((await page.locator('.cart-summary-total').innerText()).replace(/[^\d.]/g, ''));
  expect(Math.round(visibleTotal * 100)).toBe(expectedTotal);
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('5');
});

test('negative quantity is rejected without changing the basket', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  const quantity = page.locator('[data-cart-quantity-input]');
  await quantity.fill('-1');
  await quantity.press('Enter');
  await expect(quantity).toHaveJSProperty('validity.valid', false);
  await expect(quantity).toHaveValue('-1');
  const rejectedPost = await page.locator('form.cart-quantity').evaluate(async form => {
    const body = new URLSearchParams();
    new FormData(form).forEach((value, key) => body.append(key, value));
    body.set('UpdateQuantityId', form.querySelector<HTMLInputElement>('[name="UpdateQuantityId"]')!.value);
    body.set('UpdateQuantityValue', '-1');
    const response = await fetch(form.getAttribute('action') || window.location.href, {
      method: 'POST', credentials: 'same-origin',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' },
      body: body.toString()
    });
    return { ok: response.ok, html: await response.text() };
  });
  expect(rejectedPost.ok).toBeTruthy();
  expect(rejectedPost.html).toContain('Quantity cannot be negative');
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$199.99');
});
