import { test, expect } from '@playwright/test';

test('catalog controls support keyboard use and expose accessible names', async ({ page }) => {
  await page.goto('/');

  const sort = page.getByRole('combobox', { name: 'Sort' });
  await expect(sort).toBeVisible();
  await expect(sort).toHaveAttribute('aria-valuetext', 'Name: A to Z');
  await expect(page.locator('.catalog-product')).toHaveCount(9);
  const initialBounds = await page.locator('.catalog-items').boundingBox();
  await sort.focus();
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-asc');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Enter');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).toHaveAttribute('aria-valuetext', 'Name: Z to A');

  await expect(page.getByRole('link', { name: /All brands, \d+ products/ })).toBeVisible();
  await expect(page.getByRole('link', { name: /All types, \d+ products/ })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Adventurer GPS Watch' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Catalog pages' })).toBeVisible();
  const afterBounds = await page.locator('.catalog-items').boundingBox();
  expect(afterBounds?.x).toBe(initialBounds?.x);
  expect(afterBounds?.width).toBe(initialBounds?.width);
  await expect(page.locator('.catalog-product')).toHaveCount(9);
});

test('catalog default and filtered states match their visual baselines', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.catalog-product')).toHaveCount(9);
  await expect(page.locator('.catalog')).toHaveScreenshot('catalog-default.png', {
    animations: 'disabled',
    maxDiffPixelRatio: 0.01,
  });

  const brand = page.locator('.catalog-search-group').filter({ has: page.getByRole('heading', { name: 'Brand' }) })
    .locator('a.catalog-search-tag:not([aria-label^="All"])').first();
  await brand.click();
  await expect(page).toHaveURL(/brand=/);
  await expect(page.locator('.catalog-product').first()).toBeVisible();
  await expect(page.locator('.catalog')).toHaveScreenshot('catalog-filtered.png', {
    animations: 'disabled',
    maxDiffPixelRatio: 0.01,
  });
});
