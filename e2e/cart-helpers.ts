import { expect, Page } from '@playwright/test';

export async function addProductToCart(page: Page, productName: string) {
  await page.goto('/');
  await page.getByRole('link', { name: productName }).click();
  await expect(page.getByRole('heading', { name: productName })).toBeVisible();
  const productPath = new URL(page.url()).pathname;
  const addResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === productPath && response.ok());
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  await addResponse;
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
}

/** Remove the first cart line via its Remove control, waiting for the update to post. */
export async function removeFirstCartItem(page: Page) {
  const lineCount = await page.locator('.cart-item').count();
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart' && response.ok());

  await Promise.all([
    updateResponse,
    page.getByRole('button', { name: /^Remove/ }).first().click(),
  ]);
  await expect(page.locator('.cart-item')).toHaveCount(lineCount - 1, { timeout: 15_000 });
  if (lineCount === 1) {
    await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
  } else {
    await expect(page.locator('[data-cart-quantity-input]').first()).toBeVisible();
  }
}

export async function emptyCart(page: Page) {
  await page.goto('/cart');
  await expect(page.locator('.cart')).toBeVisible({ timeout: 15_000 });

  const quantities = page.locator('[data-cart-quantity-input]');
  while (await quantities.count() > 0) {
    const previousCount = await quantities.count();
    await removeFirstCartItem(page);
    await expect(quantities).toHaveCount(previousCount - 1, { timeout: 15_000 });
  }

  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}
