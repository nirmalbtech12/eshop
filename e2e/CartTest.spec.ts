import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart, removeFirstCartItem } from './cart-helpers';

test('add, update, and remove an item from the cart', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'Adventurer GPS Watch');

  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
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

test('typed quantity survives immediate navigation and reload', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' });
  await quantity.fill('3');
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await quantity.press('Enter');
  await updateResponse;
  await expect(quantity).toHaveValue('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97');

  await page.reload();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveValue('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97');
});

test('quantity update completes before immediate continue shopping navigation', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  let releasePost!: () => void;
  const postBarrier = new Promise<void>(resolve => { releasePost = resolve; });
  let postSeen!: () => void;
  const observedPost = new Promise<void>(resolve => { postSeen = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      postSeen();
      await postBarrier;
    }
    await route.continue();
  });

  try {
    const quantity = page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' });
    await quantity.fill('4');
    await quantity.press('Enter');
    await observedPost;
    const navigation = page.waitForNavigation();
    await page.getByRole('link', { name: 'Continue shopping' }).click();
    releasePost();
    await navigation;
    await expect(page.getByRole('heading', { name: 'Ready for a new adventure?' })).toBeVisible();
    await page.goto('/cart');
    await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveValue('4', { timeout: 15_000 });
  } finally {
    releasePost();
    if (!page.isClosed()) {
      await page.unroute('**/cart');
    }
  }

  await page.reload();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveValue('4');
  await expect(page.locator('.cart-summary-total')).toContainText('$799.96');
});

test('zero removes one line and negative quantities are rejected without changing the basket', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'Wanderer Black Hiking Boots');
  await page.goto('/cart');

  const watchQuantity = page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' });
  const bootsQuantity = page.getByRole('spinbutton', { name: 'Quantity of Wanderer Black Hiking Boots' });
  await expect(watchQuantity).toHaveValue('2');
  await expect(bootsQuantity).toHaveValue('1');
  await expect(page.locator('.cart-item')).toHaveCount(2);
  await expect(page.locator('.cart-item').nth(0).locator('.catalog-item-total')).toContainText('$399.98');
  await expect(page.locator('.cart-item').nth(1).locator('.catalog-item-total')).toContainText('$109.99');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$509.97');

  await watchQuantity.fill('0');
  const zeroResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await watchQuantity.press('Enter');
  expect((await zeroResponse).ok()).toBeTruthy();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveCount(0);
  await page.reload();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveCount(0);
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Wanderer Black Hiking Boots' })).toHaveValue('1');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$109.99');

  const negativeValidity = await bootsQuantity.evaluate((input: HTMLInputElement) => {
    input.value = '-1';
    return input.validity.valid;
  });
  expect(negativeValidity).toBe(false);

  const rejected = await bootsQuantity.evaluate(async input => {
    const form = input.closest('form')!;
    const data = new FormData(form);
    data.set('UpdateQuantityValue', '-1');
    const response = await fetch(form.action, { method: 'POST', body: data });
    return { status: response.status, html: await response.text() };
  });
  expect(rejected.status).toBe(200);
  expect(rejected.html).toContain('Enter a whole number of zero or more.');
  await page.reload();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Wanderer Black Hiking Boots' })).toHaveValue('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$109.99');

  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
});
