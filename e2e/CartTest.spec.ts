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

  let releaseSuccess!: () => void;
  let signalSuccess!: () => void;
  const successRequestSeen = new Promise<void>(resolve => { signalSuccess = resolve; });
  const successGate = new Promise<void>(resolve => { releaseSuccess = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      signalSuccess();
      await successGate;
    }
    await route.continue();
  });
  await quantity.fill('4');
  const successfulNavigation = page.getByRole('link', { name: /Continue shopping/ }).click();
  try {
    await successRequestSeen;
    await expect(page).toHaveURL('/cart');
    releaseSuccess();
    await successfulNavigation;
  } finally {
    releaseSuccess();
    await page.unroute('**/cart');
  }
  await expect(page).toHaveURL('/');
  await page.goto('/cart');
  await expect(page.locator('[data-cart-quantity-input]')).toHaveValue('4', { timeout: 15_000 });

  let releaseFailure!: () => void;
  let signalFailure!: () => void;
  const failureRequestSeen = new Promise<void>(resolve => { signalFailure = resolve; });
  const failureGate = new Promise<void>(resolve => { releaseFailure = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      signalFailure();
      await failureGate;
      await route.abort('failed');
      return;
    }
    await route.continue();
  });
  await page.locator('[data-cart-quantity-input]').fill('6');
  const failedNavigation = page.getByRole('link', { name: /Continue shopping/ }).click();
  try {
    await failureRequestSeen;
    await expect(page).toHaveURL('/cart');
    releaseFailure();
    await failedNavigation;
    await expect(page.locator('[data-cart-quantity-error]')).toContainText('Quantity update failed');
    await expect(page).toHaveURL('/cart');
  } finally {
    releaseFailure();
    await page.unroute('**/cart');
  }

  const updateRequest = page.waitForRequest(request =>
    request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([
    updateRequest,
    page.getByRole('button', { name: /^Increase quantity/ }).first().click(),
  ]);
  await expect(quantity).toHaveValue('5', { timeout: 15_000 });
  await expect(page.locator('.cart-summary-total')).toContainText('$999.95', { timeout: 15_000 });

  await quantity.fill('0');
  const zeroRequest = page.waitForRequest(request => request.method() === 'POST' && new URL(request.url()).pathname === '/cart');
  await Promise.all([zeroRequest, quantity.press('Enter')]);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });

  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  const input = page.locator('[data-cart-quantity-input]');
  await input.fill('-1');
  await input.dispatchEvent('change');
  await expect(input).toHaveAttribute('aria-invalid', 'true');
  await expect(input).toHaveValue('-1');
  await expect(page.locator('.cart-summary-total')).toContainText('$199.99');

  await input.fill('1');
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible({ timeout: 15_000 });

  await addProductToCart(page, 'Adventurer GPS Watch');
  await addProductToCart(page, 'AeroLite Cycling Helmet');
  await page.goto('/cart');
  const assertTotals = async () => {
    const rows = page.locator('.cart-item');
    await expect(rows).toHaveCount(2);
    let expectedTotal = 0;
    for (const row of await rows.all()) {
      const unitPrice = Number((await row.locator('.price').innerText()).replace('$', ''));
      const count = Number(await row.locator('[data-cart-quantity-input]').inputValue());
      const lineTotal = Number((await row.locator('.catalog-item-total').innerText()).replace('$', ''));
      expect(lineTotal).toBeCloseTo(unitPrice * count, 2);
      expectedTotal += lineTotal;
    }
    const displayedTotal = Number((await page.locator('.cart-summary-total').innerText()).split('$').at(-1));
    expect(displayedTotal).toBeCloseTo(expectedTotal, 2);
    await expect(page.locator('.cart-summary-header .filter-badge')).toHaveText('2');
  };
  await assertTotals();
  await page.reload();
  await assertTotals();
});
