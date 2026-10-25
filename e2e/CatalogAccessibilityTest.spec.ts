import { test, expect } from '@playwright/test';

test('catalog controls support keyboard and expose accessible state', async ({ page }) => {
  await page.addInitScript(() => localStorage.removeItem('aw.catalog.sort.v1'));
  await page.goto('/');

  const filters = page.locator('.catalog-search-summary');
  await filters.focus();
  await expect(page.locator('.catalog-search-details')).toHaveAttribute('open', '');
  await page.keyboard.press('Enter');
  await expect(page.locator('.catalog-search-details')).not.toHaveAttribute('open', '');
  await page.keyboard.press('Enter');
  await expect(page.locator('.catalog-search-details')).toHaveAttribute('open', '');

  const brandChoice = page.locator('.catalog-search-group').nth(0).locator('a.catalog-search-tag').nth(1);
  const brandHref = await brandChoice.getAttribute('href');
  await brandChoice.focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(new RegExp(`brand=${new URL(brandHref!, 'http://localhost').searchParams.get('brand')}`));

  const sort = page.getByRole('combobox', { name: 'Sort products' });
  await expect(sort).toBeVisible();
  await sort.focus();
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-asc');
  await page.keyboard.press('Escape');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).toBeFocused();
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).toContainText('Name: Z to A');
  await expect(page.locator('#catalog-sort-opt-name-desc')).toHaveAttribute('aria-selected', 'true');

  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await page.keyboard.press('Tab');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('#catalog-sort-opt-price-asc')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.catalog-product').first()).toBeFocused();

  expect(await page.locator('.catalog-product').count()).toBeGreaterThan(0);
  const firstProduct = page.locator('.catalog-product').first();
  await expect(firstProduct).toHaveAccessibleName(/.+/);
  await expect(firstProduct.locator('img')).toHaveAttribute('alt', /.+/);

  await page.goto('/');
  await expect(page.getByRole('navigation', { name: 'Catalog pages' })).toBeVisible();
  const pageTwo = page.getByRole('link', { name: '2', exact: true });
  await expect(pageTwo).toBeVisible();
  await pageTwo.focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\?page=2$/);
  await expect(page.locator('.active-page')).toHaveText('2');

  await page.goto('/');
  const brandIds = await page.locator('.catalog-search-group').nth(0).locator('a').evaluateAll(links =>
    links.map(link => new URL((link as HTMLAnchorElement).href).searchParams.get('brand')).filter((id): id is string => !!id));
  const typeIds = await page.locator('.catalog-search-group').nth(1).locator('a').evaluateAll(links =>
    links.map(link => new URL((link as HTMLAnchorElement).href).searchParams.get('type')).filter((id): id is string => !!id));

  let unavailableFacetFound = false;
  for (const brandId of brandIds) {
    for (const typeId of typeIds) {
      await page.goto(`/?brand=${brandId}&type=${typeId}`);
      const unavailableFacet = page.locator('.catalog-search-tag.is-empty').first();
      if (await unavailableFacet.count()) {
        await expect(unavailableFacet).toHaveAttribute('aria-disabled', 'true');
        await expect(unavailableFacet).toHaveAttribute('role', 'group');
        await expect(unavailableFacet).toHaveAttribute('aria-label', /, unavailable$/);
        await expect(unavailableFacet).not.toHaveAttribute('href', /.+/);
        await expect(page.getByRole('link', { name: /unavailable/ })).toHaveCount(0);
        await expect(page.getByRole('group', { name: /, unavailable$/ }).first()).toBeVisible();
        unavailableFacetFound = true;
        break;
      }
    }
    if (unavailableFacetFound) break;
  }
  expect(unavailableFacetFound, 'seeded facets should include an unavailable choice for a brand/type combination').toBe(true);
});

test('catalog keeps its visual baseline and layout', async ({ page }) => {
  await page.addInitScript(() => localStorage.removeItem('aw.catalog.sort.v1'));
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await expect(page.locator('.catalog-items')).toHaveCSS('display', 'grid');
  await expect(page.locator('.catalog-search')).toHaveCSS('width', '384px');
  await expect(page.locator('.catalog-sort-trigger')).toHaveCSS('border-radius', '999px');
  await expect(page.locator('.catalog')).toHaveScreenshot('catalog.png', {
    animations: 'disabled',
    maxDiffPixelRatio: 0.01,
  });
});
