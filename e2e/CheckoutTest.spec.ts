import { test, expect } from '@playwright/test';
import { addProductToCart, emptyCart } from './cart-helpers';

test('place an order through checkout', async ({ page }) => {
  test.setTimeout(240_000);
  await emptyCart(page);

  await page.goto('/user/orders');
  await expect(page.getByRole('heading', { name: 'Orders', exact: true })).toBeVisible();
  const oldNumbers = await page.locator('.order-number').allTextContents();
  const existingOrderCount = oldNumbers.length;

  await addProductToCart(page, 'Adventurer GPS Watch');
  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();
  const expectedTotal = (await page.locator('.cart-summary-total div').last().innerText()).trim();
  await page.getByRole('link', { name: 'Check out' }).click();

  await expect(page.getByRole('heading', { name: 'Checkout' })).toBeVisible();
  await expect(page.getByLabel('Address')).toHaveValue('15703 NE 61st Ct');
  await expect(page.getByLabel('City')).toHaveValue('Redmond');
  await page.getByRole('button', { name: 'Place order' }).click();

  await expect(page).toHaveURL(/\/user\/orders$/);
  await expect(page.getByRole('heading', { name: 'Orders', exact: true })).toBeVisible();
  await expect(page.locator('.order-number')).toHaveCount(existingOrderCount + 1);
  const numbers = await page.locator('.order-number').allTextContents();
  const newNumber = numbers.find(number => !oldNumbers.includes(number));
  expect(newNumber).toBeTruthy();
  const order = page.locator('.orders-item').filter({ has: page.locator('.order-number', { hasText: newNumber!.trim() }) });
  const total = await order.locator('.order-total').innerText();
  expect(total.trim()).toBe(expectedTotal);
  if (process.env.PAYMENT_SCENARIO === 'retry') {
    await expect(order.getByText('Payment pending')).toBeVisible({ timeout: 180_000 });
    await expect(order.getByText(/Please do not place another order/)).toBeVisible();
  }
  const finalStatus = process.env.PAYMENT_SCENARIO === 'declined' ? 'Cancelled' : 'Paid';
  await expect(order.locator('.status')).toHaveText(finalStatus, { timeout: 180_000 });
  await page.reload();
  await expect(order.locator('.status')).toHaveText(finalStatus);
  await expect(order.locator('.order-total')).toHaveText(total);
});
