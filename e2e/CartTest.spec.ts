import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart, removeFirstCartItem } from './cart-helpers';

const firstProduct = 'Adventurer GPS Watch';
const secondProduct = 'AeroLite Cycling Helmet';

test('AddRemoveAndSteppersRemainCorrect: add, stepper update, and remove retain the existing cart journey', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, firstProduct);
  await addProductToCart(page, firstProduct);
  await addProductToCart(page, secondProduct);

  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
  const rows = page.locator('.cart-item');
  await expect(rows).toHaveCount(2);
  await expect(rows.filter({ hasText: firstProduct }).locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(rows.filter({ hasText: secondProduct }).locator('[data-cart-quantity-input]')).toHaveValue('1');

  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([updateResponse, rows.filter({ hasText: secondProduct }).getByRole('button', { name: /^Increase quantity/ }).click()]);
  await expect(rows.filter({ hasText: secondProduct }).locator('[data-cart-quantity-input]')).toHaveValue('2');
  await expect(page.locator('.filter-badge')).toHaveText('4');

  await removeFirstCartItem(page);
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(secondProduct);
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
});

test('TypedQuantitySurvivesImmediateNavigation: typed quantity commits before navigation and survives a fresh read', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, firstProduct);
  await addProductToCart(page, secondProduct);
  await page.goto('/cart');

  let releaseRequest!: () => void;
  let markRequestHeld!: () => void;
  const heldRequest = new Promise<void>(resolve => { markRequestHeld = resolve; });
  const requestBarrier = new Promise<void>(resolve => { releaseRequest = resolve; });
  await page.route('**/cart', async route => {
    if (route.request().method() === 'POST') {
      markRequestHeld();
      await requestBarrier;
    }
    await route.continue();
  });

  try {
    await page.locator('.cart-item').filter({ hasText: firstProduct }).locator('[data-cart-quantity-input]').fill('3');
    const navigation = page.getByRole('link', { name: /Continue shopping/ }).click();
    await heldRequest;
    await expect(page).toHaveURL(/\/cart$/);
    releaseRequest();
    await navigation;
    await expect(page).toHaveURL(/\/$/);
  } finally {
    releaseRequest();
    await page.unroute('**/cart');
  }

  await page.goto('/cart');
  const rows = page.locator('.cart-item');
  await expect(rows).toHaveCount(2);
  await expect(rows.filter({ hasText: firstProduct }).locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(rows.filter({ hasText: secondProduct }).locator('[data-cart-quantity-input]')).toHaveValue('1');

  const firstLinePrice = Number((await rows.filter({ hasText: firstProduct }).locator('.price').innerText()).replace('$', ''));
  const secondLinePrice = Number((await rows.filter({ hasText: secondProduct }).locator('.price').innerText()).replace('$', ''));
  const enterResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await rows.filter({ hasText: secondProduct }).locator('[data-cart-quantity-input]').fill('3');
  await Promise.all([enterResponse, rows.filter({ hasText: secondProduct }).locator('[data-cart-quantity-input]').press('Enter')]);
  await expect(rows.filter({ hasText: secondProduct }).locator('.catalog-item-total')).toHaveText(`$${(secondLinePrice * 3).toFixed(2)}`);
  await page.reload();
  await expect(rows.filter({ hasText: firstProduct }).locator('[data-cart-quantity-input]')).toHaveValue('3');
  await expect(rows.filter({ hasText: secondProduct }).locator('[data-cart-quantity-input]')).toHaveValue('3');

  const lineTotals = await rows.locator('.catalog-item-total').allInnerTexts();
  expect(lineTotals.map(text => Number(text.replace('$', '')))).toEqual([firstLinePrice * 3, secondLinePrice * 3]);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${(firstLinePrice * 3 + secondLinePrice * 3).toFixed(2)}`);
  await expect(page.locator('.filter-badge')).toHaveText('6');
});

test('ExplicitZeroRemovesOnlySelectedLine: zero removes one line and final removal empties the basket', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, firstProduct);
  await addProductToCart(page, secondProduct);
  await page.goto('/cart');

  const firstLine = page.locator('.cart-item').filter({ hasText: firstProduct });
  const zeroResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await Promise.all([zeroResponse, firstLine.locator('[data-cart-quantity-input]').fill('0').then(() => firstLine.locator('[data-cart-quantity-input]').press('Enter'))]);
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await expect(page.locator('.cart-item').first()).toContainText(secondProduct);

  await page.reload();
  await expect(page.locator('.cart-item')).toHaveCount(1);
  await removeFirstCartItem(page);
  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
});

test('NegativeQuantitiesAreRejectedWithoutMutation: negative quantity leaves persisted lines unchanged', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, firstProduct);
  await addProductToCart(page, secondProduct);
  await page.goto('/cart');
  const rows = page.locator('.cart-item');
  const originalQuantities = await rows.locator('[data-cart-quantity-input]').evaluateAll(inputs => inputs.map(input => (input as HTMLInputElement).value));
  const originalTotals = await rows.locator('.catalog-item-total').allInnerTexts();
  const originalSummary = await page.locator('.cart-summary-total').innerText();
  const originalBadge = await page.locator('.filter-badge').innerText();
  const quantityInput = rows.first().locator('[data-cart-quantity-input]');
  await quantityInput.fill('-1');
  await quantityInput.press('Enter');
  expect(await quantityInput.evaluate((input: HTMLInputElement) => input.validity.valid)).toBe(false);
  const form = await rows.first().locator('form.cart-quantity').evaluate((element: HTMLFormElement) => {
    const data = new FormData(element);
    data.set('UpdateQuantityValue', '-1');
    return Array.from(data.entries());
  });

  const result = await page.evaluate(async entries => {
    const data = new FormData();
    for (const [name, value] of entries) data.append(name, value);
    const response = await fetch('/cart', { method: 'POST', body: data });
    return { status: response.status, body: await response.text() };
  }, form);
  expect(result.status).toBe(200);
  expect(result.body).toContain('Quantity cannot be negative.');
  await page.reload();

  await expect(rows).toHaveCount(2);
  expect(await rows.locator('[data-cart-quantity-input]').evaluateAll(inputs => inputs.map(input => (input as HTMLInputElement).value))).toEqual(originalQuantities);
  expect(await rows.locator('.catalog-item-total').allInnerTexts()).toEqual(originalTotals);
  await expect(page.locator('.cart-summary-total')).toHaveText(originalSummary);
  await expect(page.locator('.filter-badge')).toHaveText(originalBadge);
  await emptyCart(page);
});

test('BasketTotalsMatchCommittedQuantities: line totals, summary, and badge follow persisted quantities', async ({ page }) => {
  await emptyCart(page);
  await addProductToCart(page, firstProduct);
  await addProductToCart(page, secondProduct);
  await page.goto('/cart');
  const rows = page.locator('.cart-item');
  const firstRow = rows.filter({ hasText: firstProduct });
  const secondRow = rows.filter({ hasText: secondProduct });
  const firstPrice = Number((await firstRow.locator('.price').innerText()).replace('$', ''));
  const secondPrice = Number((await secondRow.locator('.price').innerText()).replace('$', ''));

  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');
  await firstRow.locator('[data-cart-quantity-input]').fill('3');
  await Promise.all([updateResponse, firstRow.locator('[data-cart-quantity-input]').press('Enter')]);
  await expect(firstRow.locator('.catalog-item-total')).toHaveText(`$${(firstPrice * 3).toFixed(2)}`);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${(firstPrice * 3 + secondPrice).toFixed(2)}`);
  await expect(page.locator('.filter-badge')).toHaveText('4');
  await page.reload();
  await expect(firstRow.locator('.catalog-item-total')).toHaveText(`$${(firstPrice * 3).toFixed(2)}`);
  await expect(secondRow.locator('.catalog-item-total')).toHaveText(`$${secondPrice.toFixed(2)}`);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${(firstPrice * 3 + secondPrice).toFixed(2)}`);
  await expect(page.locator('.filter-badge')).toHaveText('4');

  await removeFirstCartItem(page);
  await page.reload();
  const remainingRow = page.locator('.cart-item');
  await expect(remainingRow).toHaveCount(1);
  await expect(remainingRow).toContainText(secondProduct);
  await expect(remainingRow.locator('.catalog-item-total')).toHaveText(`$${secondPrice.toFixed(2)}`);
  await expect(page.locator('.cart-summary-total')).toContainText(`$${secondPrice.toFixed(2)}`);
  await expect(page.locator('.filter-badge')).toHaveText('1');
  await emptyCart(page);
});
