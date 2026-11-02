import { test, expect } from '@playwright/test';

async function ensureEmptyCart(page: import('@playwright/test').Page) {
  await page.goto('/cart');
  await expect.poll(async () =>
    (await page.getByText('Your shopping bag is empty').isVisible()) ||
    (await page.locator('.cart-item').count()) > 0).toBe(true);

  while (await page.locator('.cart-item').count() > 0) {
    const previousCount = await page.locator('.cart-item').count();
    const response = page.waitForResponse(result =>
      result.request().method() === 'POST' && new URL(result.url()).pathname === '/cart');
    await Promise.all([response, page.getByRole('button', { name: /^Remove/ }).first().click()]);
    await expect(page.locator('.cart-item')).toHaveCount(previousCount - 1);
  }
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}

async function removeLineAndWaitForResponse(page: import('@playwright/test').Page) {
  const response = page.waitForResponse(result =>
    result.request().method() === 'POST' && new URL(result.url()).pathname === '/cart');
  await Promise.all([response, page.getByRole('button', { name: /^Remove/ }).first().click()]);
}

async function addProductAndWait(page: import('@playwright/test').Page, name: string, total: string) {
  await page.goto('/');
  await page.getByRole('link', { name }).click();
  await expect(page.getByRole('heading', { name })).toBeVisible();
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await expect(page.getByRole('link', { name: new RegExp(`Shopping bag, ${total} item`) })).toBeVisible();
}

async function removeProductAndWait(page: import('@playwright/test').Page, name: string) {
  const response = page.waitForResponse(result =>
    result.request().method() === 'POST' && new URL(result.url()).pathname === '/cart');
  await Promise.all([
    response,
    page.locator('.cart-item').filter({ hasText: name }).getByRole('button', { name: /^Remove/ }).click(),
  ]);
}

test('add, update, and remove an item from the cart', async ({ page }) => {
  await ensureEmptyCart(page);
  await addProductAndWait(page, 'Adventurer GPS Watch', '1');

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

  await quantity.fill('3');
  await page.getByRole('link', { name: /Continue shopping/ }).click();
  await expect(page).toHaveURL(/\/$/);
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('3', { timeout: 15_000 });
  await expect(page.locator('.cart-summary-total')).toContainText('$599.97', { timeout: 15_000 });

  await removeLineAndWaitForResponse(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('basket totals and identities remain consistent across add, update, remove, and reload', async ({ page }) => {
  await ensureEmptyCart(page);
  await page.goto('/');
  const availableNames = await page.locator('a.catalog-product .name').allTextContents();
  expect(availableNames.length).toBeGreaterThanOrEqual(2);
  const firstProduct = availableNames[0].trim();
  const secondProduct = availableNames[1].trim();
  await addProductAndWait(page, firstProduct, '1');
  await addProductAndWait(page, secondProduct, '2');
  await addProductAndWait(page, firstProduct, '3');
  await page.goto('/cart');

  const watch = page.locator('.cart-item').filter({ hasText: firstProduct });
  const boots = page.locator('.cart-item').filter({ hasText: secondProduct });
  await expect(watch).toHaveCount(1);
  await expect(boots).toHaveCount(1);
  await expect(watch.locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(boots.locator('[data-cart-quantity-input]')).toHaveValue('1');

  const watchPrice = Number((await watch.locator('.price').innerText()).replace(/[^0-9.]/g, ''));
  const bootsPrice = Number((await boots.locator('.price').innerText()).replace(/[^0-9.]/g, ''));
  const total = (watchPrice * 2) + bootsPrice;
  await expect(watch.locator('.catalog-item-total')).toContainText((watchPrice * 2).toFixed(2));
  await expect(boots.locator('.catalog-item-total')).toContainText(bootsPrice.toFixed(2));
  await expect(page.locator('.cart-summary-total')).toContainText(total.toFixed(2));
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('3');

  await removeProductAndWait(page, firstProduct);
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await page.goto('/cart');
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item').filter({ hasText: secondProduct })).toHaveCount(1);
  await expect(page.locator('.cart-summary-total')).toContainText(bootsPrice.toFixed(2));
  await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('1');
  await removeLineAndWaitForResponse(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });
});

test('negative cart quantity is rejected without changing persisted basket', async ({ page }) => {
  await ensureEmptyCart(page);
  await addProductAndWait(page, 'Adventurer GPS Watch', '1');
  await page.goto('/cart');

  const quantity = page.locator('[data-cart-quantity-input]');
  await expect(quantity).toHaveValue('1');
  await quantity.fill('-1');
  await expect.poll(() => quantity.evaluate(input => input.validationMessage)).toContain('Value must be greater than or equal to 1.');
  await expect.poll(() => quantity.evaluate(input => input.validity.rangeUnderflow)).toBe(true);
  await expect(quantity).toHaveValue('-1');
  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item').getByText('Adventurer GPS Watch')).toBeVisible();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$199.99');
});

test('forged negative cart form is rejected without persistence', async ({ page }) => {
  await ensureEmptyCart(page);
  await addProductAndWait(page, 'Adventurer GPS Watch', '1');
  await page.goto('/cart');

  const result = await page.locator('.cart-quantity').evaluate(async form => {
    const data = new FormData(form);
    data.set('UpdateQuantityValue', '-1');
    const response = await fetch(form.action || window.location.href, {
      method: 'POST', body: data, credentials: 'same-origin'
    });
    return { status: response.status, html: await response.text() };
  });

  expect(result.status).toBe(200);
  expect(result.html).toContain('Quantity cannot be negative.');
  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item').getByText('Adventurer GPS Watch')).toBeVisible();
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('1');
  await expect(page.locator('.cart-summary-total')).toContainText('$199.99');
});
