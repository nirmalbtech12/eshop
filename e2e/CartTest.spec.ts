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

test('typed quantity persists when navigating immediately', async ({ page }) => {
  test.setTimeout(90_000);
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/');
  const otherProduct = page.locator('.catalog-product').nth(1);
  const otherName = (await otherProduct.locator('.name').innerText()).trim();
  expect(otherName).not.toBe('Adventurer GPS Watch');
  await addProductToCart(page, otherName);
  await page.goto('/cart');

  const targetLine = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  const otherLine = page.locator('.cart-item').filter({ hasText: otherName });
  await expect(page.locator('.cart-item')).toHaveCount(2);
  const input = targetLine.locator('[data-cart-quantity-input]');
  const targetProductId = await targetLine.locator('input[name="UpdateQuantityId"]').inputValue();
  await input.fill('3');

  let releasePost!: () => void;
  const postGate = new Promise<void>(resolve => { releasePost = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      await postGate;
    }
    await route.continue();
  });

  try {
    const postIntercepted = page.waitForRequest(request =>
      request.method() === 'POST' && new URL(request.url()).pathname === '/cart', { timeout: 15_000 });
    const navigationClick = page.getByRole('link', { name: 'Continue shopping' }).click();
    const submittedRequest = await postIntercepted;
    const postedBody = submittedRequest.postData() ?? '';
    const submitted = new URLSearchParams(postedBody);
    expect(submitted.get('UpdateQuantityValue')).toBe('3');
    expect(submitted.get('UpdateQuantityId')).toBe(targetProductId);
    await navigationClick;
    await expect(page).toHaveURL(/\/cart$/);

    releasePost();
    await expect(page).toHaveURL(/\/$/);
    await page.goto('/cart');
    await expect(page.locator('.cart-item')).toHaveCount(2);
    await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('3');
    await expect(page.locator('.cart-item').filter({ hasText: otherName }).locator('[data-cart-quantity-input]')).toHaveValue('1');
    await page.reload();
    await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('3');
    await expect(page.locator('.cart-item').filter({ hasText: otherName }).locator('[data-cart-quantity-input]')).toHaveValue('1');

    const freshPage = await page.context().newPage();
    try {
      await freshPage.goto('/cart');
      await expect(freshPage.locator('.cart-item')).toHaveCount(2);
      await expect(freshPage.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('3');
      await expect(freshPage.locator('.cart-item').filter({ hasText: otherName }).locator('[data-cart-quantity-input]')).toHaveValue('1');
    } finally {
      await freshPage.close();
    }

    const updateResponse = page.waitForResponse(response =>
      response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
    await page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]').fill('4');
    await page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]').press('Enter');
    const enterResult = await updateResponse;
    expect(new URLSearchParams(enterResult.request().postData() ?? '').get('UpdateQuantityValue')).toBe('4');
    expect(new URLSearchParams(enterResult.request().postData() ?? '').get('UpdateQuantityId')).toBe(targetProductId);
    expect(await enterResult.text()).toContain('data-cart-update-status="committed"');
    await page.reload();
    await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('4');

    const stepperResponse = page.waitForResponse(response =>
      response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
    await page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).getByRole('button', { name: /^Increase quantity/ }).click();
    const stepperResult = await stepperResponse;
    expect(new URLSearchParams(stepperResult.request().postData() ?? '').get('UpdateQuantityValue')).toBe('5');
    expect(new URLSearchParams(stepperResult.request().postData() ?? '').get('UpdateQuantityId')).toBe(targetProductId);
    expect(stepperResult.ok()).toBeTruthy();
    expect(await stepperResult.text()).toContain('data-cart-update-status="committed"');
    await page.reload();
    await expect(page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' }).locator('[data-cart-quantity-input]')).toHaveValue('5');
    await expect(page.locator('.cart-item').filter({ hasText: otherName }).locator('[data-cart-quantity-input]')).toHaveValue('1');
  } finally {
    releasePost();
    await page.unroute('**/cart');
    await emptyCart(page);
  }
});

test('zero removes one line and add remove and totals stay consistent', async ({ page }) => {
  test.setTimeout(120_000);
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/');
  const secondProduct = page.locator('.catalog-product').nth(1);
  const secondName = (await secondProduct.locator('.name').innerText()).trim();
  expect(secondName).not.toBe('Adventurer GPS Watch');
  await secondProduct.click();
  await expect(page.getByRole('heading', { name: secondName, exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.locator('.in-cart')).toContainText('1 in your');
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(2);
  await assertCartTotals(page);

  const firstLine = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  await firstLine.locator('[data-cart-quantity-input]').fill('0');
  const zeroResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await firstLine.locator('[data-cart-quantity-input]').press('Enter');
  await zeroResponse;
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item')).toContainText(secondName);
  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await assertCartTotals(page);

  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  const gpsLine = page.locator('.cart-item').filter({ hasText: 'Adventurer GPS Watch' });
  await expect(gpsLine.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(page.locator('.cart-item')).toHaveCount(2);
  await assertCartTotals(page);

  const removeResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await gpsLine.getByRole('button', { name: /^Remove/ }).click();
  await removeResponse;
  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item')).toContainText(secondName);
  await assertCartTotals(page);

  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
  await expect(page.locator('.cart-summary')).toHaveCount(0);
});

test('negative quantity is rejected by input and server without changing the basket', async ({ page }) => {
  test.setTimeout(90_000);
  await emptyCart(page);
  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  const input = page.locator('[data-cart-quantity-input]').first();
  const originalQuantity = await input.inputValue();
  const line = page.locator('.cart-item').first();
  const originalTotal = await line.locator('.catalog-item-total').innerText();

  await input.fill('-1');
  await input.press('Enter');
  await expect(line.locator('[data-cart-quantity-error]')).toContainText('zero or a positive whole number');
  await expect(input).toHaveValue('-1');

  const form = line.locator('form.cart-quantity');
  const token = await form.locator('input[name="__RequestVerificationToken"]').inputValue();
  const productId = await form.locator('input[name="UpdateQuantityId"]').inputValue();
  const response = await page.request.post('/cart', {
    form: {
      _handler: 'update-cart',
      __RequestVerificationToken: token,
      UpdateQuantityId: productId,
      UpdateQuantityValue: '-1',
    },
  });
  expect(response.ok()).toBeTruthy();
  expect(await response.text()).toContain('Quantity cannot be negative.');

  await page.reload();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue(originalQuantity);
  await expect(line.locator('.catalog-item-total')).toHaveText(originalTotal);
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await emptyCart(page);
});

async function assertCartTotals(page: import('@playwright/test').Page) {
  const rows = page.locator('.cart-item');
  let expectedTotal = 0;
  let expectedQuantity = 0;
  for (let index = 0; index < await rows.count(); index++) {
    const row = rows.nth(index);
    const quantity = Number(await row.locator('[data-cart-quantity-input]').inputValue());
    const unitPrice = Number((await row.locator('.catalog-item-content .price').innerText()).replace(/[^\d.]/g, ''));
    const rowTotal = Number((await row.locator('.catalog-item-total').innerText()).replace(/[^\d.]/g, ''));
    expect(rowTotal).toBeCloseTo(unitPrice * quantity, 2);
    expectedTotal += unitPrice * quantity;
    expectedQuantity += quantity;
  }

  const summaryTotal = Number((await page.locator('.cart-summary-total > div:last-child').innerText()).replace(/[^\d.]/g, ''));
  const summaryQuantity = Number(await page.locator('.cart-summary-header .filter-badge').innerText());
  expect(summaryTotal).toBeCloseTo(expectedTotal, 2);
  expect(summaryQuantity).toBe(expectedQuantity);
}
