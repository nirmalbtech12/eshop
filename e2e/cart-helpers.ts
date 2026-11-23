import { expect, Page } from '@playwright/test';

export async function addProductToCart(page: Page, productName: string) {
  await page.goto('/');
  await page.getByRole('link', { name: productName }).click();
  await expect(page.getByRole('heading', { name: productName })).toBeVisible();
  const itemPath = new URL(page.url()).pathname;
  const addResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === itemPath);
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await addResponse;
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
}

/** Remove the first cart line and wait for the committed result. */
export async function removeFirstCartItem(page: Page) {
  const previousCount = await page.locator('[data-cart-quantity-input]').count();
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');

  await Promise.all([
    updateResponse,
    page.getByRole('button', { name: /^Remove/ }).first().click(),
  ]);
  await expect(page.locator('[data-cart-quantity-input]')).toHaveCount(previousCount - 1, { timeout: 15_000 });
}

export async function emptyCart(page: Page) {
  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();

  const initialCount = await page.locator('[data-cart-quantity-input]').count();
  for (let remaining = initialCount; remaining > 0; remaining--) {
    await removeFirstCartItem(page);
  }

  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}
