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

test('cart quantities persist across navigation with consistent totals and explicit removal', async ({ page }) => {
  test.setTimeout(120_000);
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/item/1');
  await expect(page.getByRole('heading', { name: 'Wanderer Black Hiking Boots' })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
  await page.goto('/cart');

  const watch = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const boots = page.locator('.cart-item').filter({ hasText: 'Wanderer Black Hiking Boots' });
  const watchId = await watch.getAttribute('data-product-id');
  const bootsId = await boots.getAttribute('data-product-id');
  const watchPrice = Number(await watch.getAttribute('data-unit-price'));
  const bootsPrice = Number(await boots.getAttribute('data-unit-price'));
  expect(watchId).toBeTruthy();
  expect(bootsId).toBeTruthy();

  const watchInput = watch.locator('[data-cart-quantity-input]');
  await watchInput.fill('3');
  const enterPost = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await watchInput.press('Enter');
  const enterResponse = await enterPost;
  expect(enterResponse.ok()).toBeTruthy();
  expect(new URLSearchParams(enterResponse.request().postData() ?? '').getAll('UpdateQuantityValue')).toEqual(['3']);
  await expect(watchInput).toHaveValue('3');
  await expect(watch.locator('.catalog-item-total')).toHaveText(`$${(3 * watchPrice).toFixed(2)}`);
  await expect(boots.locator('.catalog-item-total')).toHaveText(`$${bootsPrice.toFixed(2)}`);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${(3 * watchPrice + bootsPrice).toFixed(2)}`);
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('4');

  await page.getByRole('link', { name: 'Continue shopping' }).click();
  await page.goto('/cart');
  await expect(page.locator(`.cart-item[data-product-id="${watchId}"] [data-cart-quantity-input]`)).toHaveValue('3');
  await expect(page.locator(`.cart-item[data-product-id="${bootsId}"] [data-cart-quantity-input]`)).toHaveValue('1');

  const refreshedWatch = page.locator(`.cart-item[data-product-id="${watchId}"]`);
  const refreshedInput = refreshedWatch.locator('[data-cart-quantity-input]');
  await refreshedInput.fill('2');
  const blurPost = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await refreshedInput.press('Tab');
  const blurResponse = await blurPost;
  expect(blurResponse.ok()).toBeTruthy();
  expect(new URLSearchParams(blurResponse.request().postData() ?? '').getAll('UpdateQuantityValue')).toEqual(['2']);
  await expect(refreshedInput).toHaveValue('2');
  await expect(refreshedWatch.locator('.catalog-item-total')).toHaveText(`$${(2 * watchPrice).toFixed(2)}`);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${(2 * watchPrice + bootsPrice).toFixed(2)}`);
  await page.goto('/');
  await page.goto('/cart');
  await expect(page.locator(`.cart-item[data-product-id="${watchId}"] [data-cart-quantity-input]`)).toHaveValue('2');
  await expect(page.locator(`.cart-item[data-product-id="${bootsId}"] [data-cart-quantity-input]`)).toHaveValue('1');
  await expect(page.locator(`.cart-item[data-product-id="${watchId}"] .catalog-item-total`)).toHaveText(`$${(2 * watchPrice).toFixed(2)}`);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${(2 * watchPrice + bootsPrice).toFixed(2)}`);
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');

  const typedNegative = page.locator(`.cart-item[data-product-id="${watchId}"] [data-cart-quantity-input]`);
  await typedNegative.fill('-1');
  await typedNegative.press('Tab');
  expect(await typedNegative.evaluate((input: HTMLInputElement) => input.checkValidity())).toBe(false);
  await page.goto('/cart');
  await expect(page.locator(`.cart-item[data-product-id="${watchId}"] [data-cart-quantity-input]`)).toHaveValue('2');
  await expect(page.locator(`.cart-item[data-product-id="${bootsId}"] [data-cart-quantity-input]`)).toHaveValue('1');

  const negativeForm = page.locator(`.cart-item[data-product-id="${watchId}"] form`);
  await negativeForm.locator('[data-cart-quantity-input]').evaluate((input: HTMLInputElement) => {
    input.value = '-1';
    const form = input.form!;
    HTMLFormElement.prototype.submit.call(form);
  });
  await expect(page.getByRole('alert')).toContainText('whole quantity', { timeout: 15_000 });
  await page.goto('/cart');
  await expect(page.locator(`.cart-item[data-product-id="${watchId}"] [data-cart-quantity-input]`)).toHaveValue('2');
  await expect(page.locator(`.cart-item[data-product-id="${bootsId}"] [data-cart-quantity-input]`)).toHaveValue('1');

  const zeroInput = page.locator(`.cart-item[data-product-id="${watchId}"] [data-cart-quantity-input]`);
  await zeroInput.fill('0');
  const zeroPost = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await zeroInput.press('Enter');
  expect((await zeroPost).ok()).toBeTruthy();
  await expect(page.locator(`.cart-item[data-product-id="${watchId}"]`)).toHaveCount(0);
  await expect(page.locator(`.cart-item[data-product-id="${bootsId}"] [data-cart-quantity-input]`)).toHaveValue('1');
  await page.goto('/');
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator(`.cart-item[data-product-id="${bootsId}"] [data-cart-quantity-input]`)).toHaveValue('1');
  await expect(page.locator('.cart-summary-total')).toContainText(`$${bootsPrice.toFixed(2)}`);
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
});
