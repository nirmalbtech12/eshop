import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart, removeFirstCartItem } from './cart-helpers';

async function addProductById(page: import('@playwright/test').Page, productId: number, productName: string) {
  await page.goto(`/item/${productId}`);
  await expect(page.getByRole('heading', { name: productName })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
}

test('quantity persists through navigation and zero removes only that line', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductById(page, 100, 'EcoLite Trekking Poles');

  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();

  const watch = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const poles = page.locator('.cart-item').filter({ hasText: 'EcoLite Trekking Poles' });
  await expect(watch.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(poles.locator('[data-cart-quantity-input]')).toHaveValue('1');

  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([
    updateResponse,
    watch.getByRole('button', { name: /^Increase quantity/ }).click(),
  ]);
  await expect(watch.locator('[data-cart-quantity-input]')).toHaveValue('2');

  await page.goto('/');
  await page.goto('/cart');
  const persistedWatch = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const persistedPoles = page.locator('.cart-item').filter({ hasText: 'EcoLite Trekking Poles' });
  await expect(persistedWatch.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(persistedPoles.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(persistedWatch.locator('.catalog-item-total')).toContainText('$399.98');
  await expect(persistedPoles.locator('.catalog-item-total')).toContainText('$79.99');
  await expect(page.locator('.filter-badge')).toHaveText('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$479.97');

  const removeResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([
    removeResponse,
    persistedWatch.getByRole('button', { name: /^Remove/ }).click(),
  ]);
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item')).toContainText('EcoLite Trekking Poles');
  await expect(page.locator('.cart-item')).not.toContainText('Adventurer GPS Watch');
  await expect(poles.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.filter-badge')).toHaveText('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$79.99');

  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
});

test('negative quantity is rejected without changing the basket', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]');
  await expect(quantity).toHaveValue('1');
  const rejectedResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');

  await page.locator('.cart-quantity').evaluate(form => {
    const submitter = form.querySelector<HTMLButtonElement>('[data-cart-quantity-apply]')!;
    submitter.value = '-1';
    form.requestSubmit(submitter);
  });
  await rejectedResponse;

  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.cart-item')).toContainText('Adventurer GPS Watch');
  await expect(page.locator('.cart-summary-total')).toContainText('$199.99');

  const validResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([
    validResponse,
    page.getByRole('button', { name: /^Increase quantity/ }).click(),
  ]);
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(page.locator('.cart-summary-total')).toContainText('$399.98');
});
