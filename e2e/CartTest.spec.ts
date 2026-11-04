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

  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('typed quantity survives immediate navigation and reload', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  let releasePost!: () => void;
  let postPaused!: () => void;
  const paused = new Promise<void>(resolve => { postPaused = resolve; });
  const release = new Promise<void>(resolve => { releasePost = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      postPaused();
      await release;
    }
    await route.continue();
  });

  await page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' }).fill('3');
  await page.getByRole('link', { name: /Continue shopping/ }).click();
  await paused;
  await expect(page).toHaveURL(/\/cart$/);
  releasePost();
  await expect(page).toHaveURL(/\/$/);
  await page.unroute('**/cart');

  await page.goto('/cart');
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveValue('3');
});

test('zero removes one line, negative is rejected, and totals follow accepted quantities', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'Alpine Fusion Goggles');
  await addProductToCart(page, 'Alpine Fusion Goggles');
  await page.goto('/cart');

  const firstQuantity = page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' });
  await firstQuantity.click();
  await firstQuantity.press('Control+A');
  await firstQuantity.pressSequentially('3');
  const quantityUpdate = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await firstQuantity.press('Enter');
  await quantityUpdate;
  await expect(firstQuantity).toHaveValue('3');

  const secondQuantity = page.getByRole('spinbutton', { name: 'Quantity of Alpine Fusion Goggles' });
  await secondQuantity.evaluate(input => input.removeAttribute('min'));
  await secondQuantity.click();
  await secondQuantity.press('Control+A');
  await secondQuantity.pressSequentially('-1');
  await secondQuantity.press('Enter');
  await expect(page.getByText('Quantity cannot be negative.')).toBeVisible();
  await expect(firstQuantity).toHaveValue('3');
  await page.reload();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveValue('3');
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Alpine Fusion Goggles' })).toHaveValue('2');

  const secondLineTotal = page.locator('.cart-item').filter({ hasText: 'Alpine Fusion Goggles' }).locator('.catalog-item-total');
  const secondUnitPriceText = await page.locator('.cart-item').filter({ hasText: 'Alpine Fusion Goggles' }).locator('.price').textContent();
  const unitPrice = Number(secondUnitPriceText!.replace(/[^0-9.]/g, ''));
  await expect(secondLineTotal).toHaveText(`$${(unitPrice * 2).toFixed(2)}`);

  const lineTotals = await page.locator('.catalog-item-total').allTextContents();
  const displayedLineTotal = lineTotals.reduce((sum, value) => sum + Number(value.replace(/[^0-9.]/g, '')), 0);
  const displayedSummaryTotal = Number((await page.locator('.cart-summary-total div').last().textContent())!.replace(/[^0-9.]/g, ''));
  expect(displayedSummaryTotal).toBe(displayedLineTotal);
  await expect(page.locator('.filter-badge')).toHaveText('5');

  await firstQuantity.click();
  await firstQuantity.press('Control+A');
  await firstQuantity.pressSequentially('0');
  await firstQuantity.press('Enter');
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveCount(0);
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Alpine Fusion Goggles' })).toHaveValue('2');
  await page.reload();
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Adventurer GPS Watch' })).toHaveCount(0);
  await expect(page.getByRole('spinbutton', { name: 'Quantity of Alpine Fusion Goggles' })).toHaveValue('2');
});
