import { test, expect } from '@playwright/test';
import { addProductToCart } from './cart-helpers';

async function removeFirstCartItem(page: import('@playwright/test').Page) {
  const response = page.waitForResponse(candidate =>
    candidate.request().method() === 'POST' && new URL(candidate.url()).pathname === '/cart');
  await page.getByRole('button', { name: /^Remove/ }).first().click();
  const completed = await response;
  expect(completed.ok()).toBeTruthy();
}

async function clearBasket(page: import('@playwright/test').Page) {
  await page.goto('/cart');
  const quantities = page.locator('[data-cart-quantity-input]');
  while (await quantities.count() > 0) {
    const previousCount = await quantities.count();
    await removeFirstCartItem(page);
    await expect(quantities).toHaveCount(previousCount - 1, { timeout: 15_000 });
  }
  const emptyState = page.getByText('Your shopping bag is empty');
  if (!(await emptyState.isVisible())) {
    // The streamed form response can remove the last row before the empty-state
    // fragment is available. A fresh GET confirms the committed persisted state.
    await page.reload();
  }
  await expect(emptyState).toBeVisible({ timeout: 15_000 });
}

async function addSecondCatalogProduct(page: import('@playwright/test').Page) {
  await page.goto('/');
  await page.locator('.catalog-product').nth(1).click();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
}

test('add, update, and remove an item from the cart', async ({ page }) => {
  await clearBasket(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  const productPath = new URL(page.url()).pathname;
  const repeatedAdd = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === productPath);
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await repeatedAdd;
  await expect(page.locator('.in-cart')).toContainText('2');

  await page.goto('/cart');
  await expect(page.locator('.cart')).toBeVisible();
  await expect(page.getByText('Adventurer GPS Watch')).toBeVisible();

  const quantity = page.locator('[data-cart-quantity-input]');
  await expect(quantity).toHaveValue('2');

  const updateRequest = page.waitForRequest(request =>
    request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([
    updateRequest,
    page.getByRole('button', { name: /^Increase quantity/ }).first().click(),
  ]);
  await expect(quantity).toHaveValue('3', { timeout: 15_000 });
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97', { timeout: 15_000 });

  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('typed quantity commits before immediate navigation and survives reload', async ({ page }) => {
  await clearBasket(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]');
  await quantity.fill('3');
  await page.getByRole('link', { name: 'Continue shopping' }).click();
  await expect(page).toHaveURL(/\/$/);
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3', { timeout: 15_000 });
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3', { timeout: 15_000 });
});

test('negative quantity is rejected without changing the basket', async ({ page }) => {
  await clearBasket(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addSecondCatalogProduct(page);
  await page.goto('/cart');

  const quantity = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]');
  await quantity.fill('1');
  await quantity.press('Enter');
  await expect(quantity).toHaveValue('1');
  await quantity.evaluate((input: HTMLInputElement) => {
    input.min = '';
    input.setCustomValidity('');
    const forgedValue = document.createElement('input');
    forgedValue.type = 'hidden';
    forgedValue.name = 'UpdateQuantityValue';
    forgedValue.value = '-1';
    input.form!.append(forgedValue);
    input.form!.requestSubmit();
  });
  await expect(page.getByRole('alert')).toContainText('cannot be negative', { timeout: 15_000 });
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveCount(2);
  await expect(page.locator('[data-cart-quantity-input]').nth(0)).toHaveValue('1');
  await expect(page.locator('[data-cart-quantity-input]').nth(1)).toHaveValue('1');
});

test('multiple line totals and badge match committed quantities through removal', async ({ page }) => {
  await clearBasket(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/');
  const secondProduct = page.locator('.catalog-product').nth(1);
  const secondName = await secondProduct.locator('.name').innerText();
  await secondProduct.click();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
  await page.goto('/cart');

  await expect(page.locator('.cart-item')).toHaveCount(2);
  const firstLine = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const secondLine = page.locator('.cart-item').filter({ hasText: secondName });

  const arithmetic = async () => page.locator('.cart-item').evaluateAll(lines => {
    const cents = (value: string) => Math.round(Number(value.replace(/[^0-9.]/g, '')) * 100);
    return lines.map(line => {
      const unitPrice = cents(line.querySelector('.price')!.textContent!);
      const quantity = Number((line.querySelector('[data-cart-quantity-input]') as HTMLInputElement).value);
      const displayed = cents(line.querySelector('.catalog-item-total')!.textContent!);
      return { quantity, displayed, expected: unitPrice * quantity };
    });
  });
  const summaryCents = async () => page.locator('.cart-summary-total').last().innerText().then(text =>
    Math.round(Number(text.split('$').pop()!.replace(/[^0-9.]/g, '')) * 100));
  const assertDisplayedSnapshot = async () => {
    const rows = await arithmetic();
    expect(rows).toHaveLength(2);
    expect(rows.every(row => row.displayed === row.expected)).toBe(true);
    expect(await summaryCents()).toBe(rows.reduce((sum, row) => sum + row.expected, 0));
    await expect(page.locator('.filter-badge').last()).toHaveText(
      String(rows.reduce((sum, row) => sum + row.quantity, 0)));
  };

  await firstLine.locator('[data-cart-quantity-input]').fill('3');
  let releasePost!: () => void;
  const holdPost = new Promise<void>(resolve => { releasePost = resolve; });
  let signalPostStarted!: () => void;
  const postStarted = new Promise<void>(resolve => { signalPostStarted = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      signalPostStarted();
      await holdPost;
    }
    await route.continue();
  });
  const typedUpdate = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await firstLine.locator('[data-cart-quantity-input]').press('Enter');
  await postStarted;
  try {
    await expect(firstLine.locator('[data-cart-quantity-input]')).toHaveValue('3');
    await assertDisplayedSnapshot();
  } finally {
    releasePost();
  }
  await typedUpdate;
  await page.unroute('**/cart');
  await expect(firstLine.locator('[data-cart-quantity-input]')).toHaveValue('3');
  const increase = secondLine.getByRole('button', { name: /^Increase quantity/ });
  const steppedUpdate = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([steppedUpdate, increase.click()]);
  await expect(secondLine.locator('[data-cart-quantity-input]')).toHaveValue('2', { timeout: 15_000 });

  await expect.poll(async () => {
    const rows = await arithmetic();
    return rows.length === 2 && rows.every(row => row.displayed === row.expected);
  }).toBe(true);
  const rows = await arithmetic();
  const expectedTotal = rows.reduce((sum, row) => sum + row.expected, 0);
  const expectedQuantity = rows.reduce((sum, row) => sum + row.quantity, 0);
  expect(await summaryCents()).toBe(expectedTotal);
  await expect(page.locator('.filter-badge').last()).toHaveText(String(expectedQuantity));
  await page.reload();
  await expect.poll(summaryCents).toBe(expectedTotal);

  await removeFirstCartItem(page);
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
});
