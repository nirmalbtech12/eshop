import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart, removeFirstCartItem } from './cart-helpers';

async function addHikingBootsToCart(page: import('@playwright/test').Page) {
  await page.goto('/item/1');
  await expect(page.getByRole('heading', { name: 'Wanderer Black Hiking Boots' })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
}

test('add, update, and remove an item from the cart', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addHikingBootsToCart(page);

  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
  await expect(page.getByText('Adventurer GPS Watch')).toBeVisible();

  const watchLine = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const bootsLine = page.locator('.cart-item').filter({ hasText: 'Wanderer Black Hiking Boots' });
  await expect(watchLine.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(bootsLine.locator('[data-cart-quantity-input]')).toHaveValue('1');

  const updateRequest = page.waitForRequest(request =>
    request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([
    updateRequest,
    watchLine.getByRole('button', { name: /^Increase quantity/ }).click(),
  ]);
  await expect(watchLine.locator('[data-cart-quantity-input]')).toHaveValue('3', { timeout: 15_000 });
  await expect(page.locator('.cart-summary-total')).toContainText('$709.96', { timeout: 15_000 });

  const removeWatch = page.waitForRequest(request =>
    request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([
    removeWatch,
    watchLine.getByRole('button', { name: /^Remove/ }).click(),
  ]);
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.getByText('Wanderer Black Hiking Boots')).toBeVisible();
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('typed quantity survives immediate continue shopping navigation', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]').first();
  await quantity.fill('3');
  await quantity.press('Enter');
  await page.getByRole('link', { name: 'Continue shopping' }).click();
  await page.goto('/cart');

  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97');

  const returnedQuantity = page.locator('[data-cart-quantity-input]').first();
  await returnedQuantity.fill('2');
  await returnedQuantity.press('Tab');
  await page.getByRole('link', { name: 'Continue shopping' }).click();
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await emptyCart(page);
});

test('negative typed quantity is rejected without changing the basket', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]').first();
  await quantity.fill('-1');
  await quantity.press('Enter');

  await expect(page.getByRole('alert')).toHaveText('Quantity cannot be negative.');
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await emptyCart(page);
});

test('basket totals reflect committed quantities across multiple products', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addHikingBootsToCart(page);
  await page.goto('/cart');

  const watch = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const boots = page.locator('.cart-item').filter({ hasText: 'Wanderer Black Hiking Boots' });
  await watch.locator('[data-cart-quantity-input]').fill('2');
  await watch.locator('[data-cart-quantity-input]').press('Enter');
  await expect(watch.locator('.catalog-item-total')).toContainText('$399.98');
  await expect(boots.locator('.catalog-item-total')).toContainText('$109.99');
  await expect(page.locator('.cart-summary-total')).toContainText('$509.97');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');

  await page.reload();
  await expect(page.locator('.cart-summary-total')).toContainText('$509.97');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');
  await emptyCart(page);
});
