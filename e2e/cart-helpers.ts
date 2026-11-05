import { expect, Page } from '@playwright/test';

export async function addProductToCart(page: Page, productName: string) {
  await page.goto('/');
  await page.getByRole('link', { name: productName }).click();
  await expect(page.getByRole('heading', { name: productName })).toBeVisible();
  const productPath = new URL(page.url()).pathname;
  const addResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === productPath);
  await Promise.all([
    addResponse,
    page.getByRole('button', { name: 'Add to shopping bag' }).click(),
  ]);
  const response = await addResponse;
  if (!response.ok()) {
    throw new Error(`Adding ${productName} failed with status ${response.status()}`);
  }
}

/** Remove the first cart line via its Remove control, waiting for the update to post. */
export async function removeFirstCartItem(page: Page) {
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');

  await Promise.all([
    updateResponse,
    page.getByRole('button', { name: /^Remove/ }).first().click(),
  ]);
  const response = await updateResponse;
  if (!response.ok()) {
    throw new Error(`Cart removal failed with status ${response.status()}`);
  }
}

export async function emptyCart(page: Page) {
  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();

  const quantities = page.locator('[data-cart-quantity-input]');
  while (await quantities.count() > 0) {
    const previousCount = await quantities.count();
    await removeFirstCartItem(page);
    await expect(quantities).toHaveCount(previousCount - 1, { timeout: 15_000 });
  }

  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}
