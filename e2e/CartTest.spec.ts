import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart } from './cart-helpers';

async function removeAndWaitForCartUpdate(page: import('@playwright/test').Page) {
  const response = page.waitForResponse(result =>
    result.request().method() === 'POST' && new URL(result.url()).pathname === '/cart');

  await Promise.all([
    response,
    page.getByRole('button', { name: /^Remove/ }).first().click(),
  ]);
}

async function withCartCleanup(page: import('@playwright/test').Page, scenario: () => Promise<void>) {
  await emptyCart(page);
  let scenarioError: unknown;
  try {
    await scenario();
  } catch (error) {
    scenarioError = error;
    throw error;
  } finally {
    try {
      await emptyCart(page);
    } catch (cleanupError) {
      if (scenarioError === undefined) throw cleanupError;
    }
  }
}

async function removeNamedItemAndWait(page: import('@playwright/test').Page, productName: string) {
  const row = page.locator('.cart-item').filter({ hasText: productName });
  const response = page.waitForResponse(result =>
    result.request().method() === 'POST' && new URL(result.url()).pathname === '/cart');

  await Promise.all([
    response,
    row.getByRole('button', { name: /^Remove/ }).click(),
  ]);
}

test('add, update, and remove an item from the cart', async ({ page }) => withCartCleanup(page, async () => {
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

  await removeAndWaitForCartUpdate(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
}));

test('typed quantity survives immediate navigation and reload', async ({ page }) => withCartCleanup(page, async () => {
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  const quantity = page.locator('[data-cart-quantity-input]');
  await quantity.fill('3');
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await page.getByRole('link', { name: /Continue shopping/ }).click();
  const response = await updateResponse;
  expect(response.ok()).toBeTruthy();
  await expect(page).toHaveURL(/\/$/);
  await page.goto('/cart');
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(page.locator('.cart-item .catalog-item-total')).toContainText('$599.97');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');

  const enterQuantity = page.locator('[data-cart-quantity-input]');
  await enterQuantity.fill('2');
  await enterQuantity.press('Enter');
  await expect(enterQuantity).toHaveValue('2');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('2');
  await removeAndWaitForCartUpdate(page);
}));

test('zero removes only the selected line and totals remain consistent', async ({ page }) => withCartCleanup(page, async () => {
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/?type=4');
  await page.getByRole('link', { name: 'Explorer 45L Backpack' }).click();
  await expect(page.getByRole('heading', { name: 'Explorer 45L Backpack' })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
  await page.goto('/cart');
  const rows = page.locator('.cart-item');
  const retainedName = 'Explorer 45L Backpack';
  const quantityRow = rows.filter({ hasText: 'Adventurer GPS Watch' });
  const incrementResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([
    incrementResponse,
    quantityRow.getByRole('button', { name: /^Increase quantity/ }).click(),
  ]);
  await page.reload();
  await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('.catalog-item-total')).toContainText('$399.98');
  await expect(page.locator('.cart-summary-total')).toContainText('$549.97');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');

  const retainedRow = rows.filter({ hasText: retainedName });
  const retainedPrice = Number((await retainedRow.locator('.price').innerText()).replace(/[^\d.]/g, ''));
  await removeNamedItemAndWait(page, 'Adventurer GPS Watch');
  await page.reload();
  await expect(rows).toHaveCount(1);
  await expect(page.getByText(retainedName)).toBeVisible();
  await expect(retainedRow.locator('[data-cart-quantity-input]')).toHaveValue('1');
  const total = `$${retainedPrice.toFixed(2)}`;
  await expect(retainedRow.locator('.catalog-item-total')).toContainText(total);
  await expect(page.locator('.cart-summary-total')).toContainText(total);
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('1');
  await removeAndWaitForCartUpdate(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}));

test('invalid quantity submissions preserve the basket', async ({ page }) => withCartCleanup(page, async () => {
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  for (const value of ['', 'not-a-number', '-2']) {
    await page.locator('[data-cart-quantity-input]').evaluate((element, nextValue) => {
      const input = element as HTMLInputElement;
      input.type = 'text';
      input.name = 'UpdateQuantityInput';
      input.value = nextValue as string;
      input.form!.requestSubmit();
    }, value);
    await expect(page.getByRole('alert')).toContainText('Enter a whole number');
    await page.reload();
    await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
    await expect(page.locator('.cart-summary-total')).toContainText('$199.99');
  }
}));
