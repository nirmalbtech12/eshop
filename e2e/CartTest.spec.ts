import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart, removeFirstCartItem } from './cart-helpers';

test('add, update, and remove an item from the cart', async ({ page }) => {
  test.setTimeout(180_000);
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/item/4');
  await expect(page.getByRole('heading', { name: 'Expedition Backpack' })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart strong')).toHaveText('1');

  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
  await expect(page.getByText('Adventurer GPS Watch')).toBeVisible();

  const watchRow = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const backpackRow = page.locator('.cart-item').filter({ hasText: 'Expedition Backpack' });
  const quantity = watchRow.locator('[data-cart-quantity-input]');
  const quantityBadge = page.locator('.cart-summary-header .filter-badge');
  await expect(quantity).toHaveValue('1');
  await expect(backpackRow.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(quantityBadge).toHaveText('2');

  const updateRequest = page.waitForRequest(request =>
    request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([
    updateRequest,
    page.getByRole('button', { name: /^Increase quantity/ }).first().click(),
  ]);
  await expect(quantity).toHaveValue('2', { timeout: 15_000 });
  await expect(quantityBadge).toHaveText('3');
  await expect(watchRow.locator('.catalog-item-total')).toContainText('$399.98', { timeout: 15_000 });
  await expect(page.locator('.cart-summary-total')).toContainText('$529.97', { timeout: 15_000 });

  await watchRow.getByRole('button', { name: /^Decrease quantity/ }).click();
  await expect(quantity).toHaveValue('1', { timeout: 15_000 });
  await expect(quantityBadge).toHaveText('2');
  await expect(page.locator('.cart-summary-total')).toContainText('$329.98', { timeout: 15_000 });
  await watchRow.getByRole('button', { name: /^Increase quantity/ }).click();
  await expect(quantity).toHaveValue('2', { timeout: 15_000 });

  await quantity.fill('3');
  await quantity.press('Enter');
  await expect(quantity).toHaveValue('3', { timeout: 15_000 });
  await expect(quantityBadge).toHaveText('4');
  await page.goto('/');
  await page.goto('/cart');
  await expect(watchRow.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(backpackRow.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(quantityBadge).toHaveText('4');
  await page.reload();
  await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$729.96');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('4');

  let invalidPostCount = 0;
  const trackInvalidPost = request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/cart') invalidPostCount++;
  };
  page.on('request', trackInvalidPost);
  await watchRow.locator('[data-cart-quantity-input]').fill('-2');
  await watchRow.locator('[data-cart-quantity-input]').press('Enter');
  await page.waitForTimeout(500);
  page.off('request', trackInvalidPost);
  expect(invalidPostCount).toBe(0);
  const antiforgeryToken = await page.locator('input[name="__RequestVerificationToken"]').first().inputValue();
  const rejectedPost = await page.request.post(new URL('/cart', page.url()).toString(), {
    form: {
      __RequestVerificationToken: antiforgeryToken,
      _handler: 'update-cart',
      UpdateQuantityId: '99',
      UpdateQuantityValue: '-2',
    },
  });
  expect(rejectedPost.ok()).toBeTruthy();
  expect(await rejectedPost.text()).toContain('Enter a valid quantity. Use Remove to remove an item.');
  await page.reload();
  const reloadedWatch = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const reloadedBackpack = page.locator('.cart-item').filter({ hasText: 'Expedition Backpack' });
  await expect(page.locator('.cart-item')).toHaveCount(2);
  await expect(reloadedWatch.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(reloadedBackpack.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(reloadedWatch.locator('.catalog-item-total')).toContainText('$599.97');
  await expect(reloadedBackpack.locator('.catalog-item-total')).toContainText('$129.99');
  await expect(page.locator('.cart-summary-total')).toContainText('$729.96');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('4');

  await page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).getByRole('button', { name: /^Remove/ }).click();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item')).toContainText('Expedition Backpack');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$129.99');
  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('1');
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
  await page.reload();
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();

  await page.goto('/item/99');
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart strong')).toHaveText('1');
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart strong')).toHaveText('2');
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await emptyCart(page);
});
