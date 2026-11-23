import { expect, Page } from '@playwright/test';

export async function addProductToCart(page: Page, productName: string) {
  await page.goto('/');
  await page.getByRole('link', { name: productName }).click();
  await expect(page.getByRole('heading', { name: productName })).toBeVisible();
  const quantityInBag = page.locator('.in-cart strong');
  const currentQuantity = await quantityInBag.count() === 0 ? 0 : Number(await quantityInBag.innerText());
  const addResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === new URL(page.url()).pathname);
  await Promise.all([
    addResponse,
    page.getByRole('button', { name: 'Add to shopping bag' }).click(),
  ]);
  await expect(quantityInBag).toHaveText(String(currentQuantity + 1));
}

/** Remove the first cart line via its Remove control, waiting for the update to post. */
export async function removeFirstCartItem(page: Page) {
  const rows = page.locator('.cart-item');
  const remainingRows = await rows.count() - 1;
  const updateResponse = page.waitForResponse(response =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/cart');

  await Promise.all([
    updateResponse,
    page.getByRole('button', { name: /^Remove/ }).first().click(),
  ]);
  await expect(rows).toHaveCount(remainingRows);
}

export async function emptyCart(page: Page) {
  await page.goto('/cart');
  await expect(page.getByRole('heading', { name: 'Shopping bag', exact: true })).toBeVisible();

  const quantities = page.locator('[data-cart-quantity-input]');
  for (let remaining = await quantities.count(); remaining > 0; remaining = await quantities.count()) {
    if (remaining > 20) {
      throw new Error(`Refusing to remove an unexpected ${remaining} cart lines.`);
    }
    await removeFirstCartItem(page);
    await expect(quantities).toHaveCount(remaining - 1, { timeout: 15_000 });
  }

  await expect(page.getByText('Your shopping bag is empty')).toBeVisible();
}
