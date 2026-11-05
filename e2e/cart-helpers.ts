import { expect, Page } from '@playwright/test';

export async function addProductToCart(page: Page, productName: string) {
  await page.goto('/');
  const productLink = page.getByRole('link', { name: productName });
  for (let catalogPage = 1; catalogPage <= 12 && await productLink.count() === 0; catalogPage++) {
    await page.goto(catalogPage === 1 ? '/' : `/?page=${catalogPage}`);
  }
  await productLink.click();
  await expect(page.getByRole('heading', { name: productName })).toBeVisible();
  const addResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname.startsWith('/item/'));
  await page.getByRole('button', { name: 'Add to shopping bag' }).click();
  expect((await addResponse).ok()).toBeTruthy();
  await expect(page.locator('.in-cart').getByRole('link', { name: 'shopping bag' })).toBeVisible();
}

/** Remove the first cart line via its Remove control and wait for the server response. */
export async function removeFirstCartItem(page: Page) {
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');

  await page.getByRole('button', { name: /^Remove/ }).first().click();
  const response = await updateResponse;
  expect(response.ok()).toBeTruthy();
}

export async function emptyCart(page: Page) {
  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();

  const quantities = page.locator('[data-cart-quantity-input]');
  const initialCount = await quantities.count();
  for (let removed = 0; removed < initialCount; removed++) {
    const previousCount = await quantities.count();
    await removeFirstCartItem(page);
    await expect(quantities).toHaveCount(previousCount - 1, { timeout: 15_000 });
  }

  await expect(quantities).toHaveCount(0);

  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}
