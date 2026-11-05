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
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97');

  await page.goto('/item/4');
  await expect(page.getByRole('heading', { name: 'Expedition Backpack' })).toBeVisible();
  const backpackAdd = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/item/4' && response.ok());
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await backpackAdd;
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(2);
  await expect(page.locator('.cart-summary-total')).toContainText('$729.96');
  await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('.catalog-item-total')).toHaveText('$599.97');
  await page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' })
    .getByRole('button', { name: /^Remove/ }).click();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item')).toContainText('Expedition Backpack');
  await page.reload();
  await expect(page.locator('.cart-item')).toContainText('Expedition Backpack');
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('typed quantity survives immediate navigation and reload with consistent totals', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]').first();
  await quantity.fill('3');
  await quantity.press('Enter');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97', { timeout: 15_000 });
  await page.getByRole('link', { name: /Continue shopping/ }).click();
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97');

  let releaseResponse!: () => void;
  const responseGate = new Promise<void>(resolve => { releaseResponse = resolve; });
  let submitted!: () => void;
  const postObserved = new Promise<void>(resolve => { submitted = resolve; });
  let submittedPayload = '';
  const productId = await page.locator('form.cart-quantity input[name="UpdateQuantityId"]').first().inputValue();
  const postResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await page.route('**/cart', async route => {
    if (route.request().method() !== 'POST') {
      await route.continue();
      return;
    }
    submittedPayload = route.request().postData() ?? '';
    submitted();
    await responseGate;
    await route.continue();
  });
  try {
    await page.locator('[data-cart-quantity-input]').first().fill('4');
    await page.locator('[data-cart-quantity-input]').first().press('Enter');
    await postObserved;
    expect(submittedPayload).toContain(`UpdateQuantityId=${productId}`);
    expect(submittedPayload).toContain('UpdateQuantityValue=4');
    await page.getByRole('link', { name: 'Check out' }).evaluate((link: HTMLAnchorElement) => link.click());
    expect(new URL(page.url()).pathname).toBe('/cart');
    releaseResponse();
    await expect.poll(() => new URL(page.url()).pathname, { timeout: 15_000 }).toBe('/checkout');
  } finally {
    releaseResponse();
  }
  await postResponse;
  await page.unroute('**/cart');
  expect(new URL(page.url()).pathname).toBe('/checkout');
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('4');
  await expect(page.locator('.cart-summary-total')).toContainText('$799.96');

  const input = page.locator('[data-cart-quantity-input]').first();
  await input.fill('-1');
  await input.press('Enter');
  await expect(input).toHaveJSProperty('validity.valid', false);
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('4');
  await expect(page.locator('.cart-summary-total')).toContainText('$799.96');

  const forgedResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await page.locator('[data-cart-quantity-apply]').evaluate((button: HTMLButtonElement) => {
    button.value = '-1';
    button.form!.requestSubmit(button);
  });
  await forgedResponse;
  await expect(page.getByRole('alert')).toContainText('Use Remove');
  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('4');
  await expect(page.locator('.cart-summary-total')).toContainText('$799.96');
  await removeFirstCartItem(page);
});
