import { test, expect } from '@playwright/test';

test('catalog controls support keyboard use and have accessible names', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => localStorage.removeItem('aw.catalog.sort.v1'));
  await page.reload();

  const sort = page.getByRole('combobox', { name: 'Sort products' });
  await expect(sort).toBeVisible();
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  const originalGrid = await page.locator('.catalog-items').boundingBox();
  await expect(page.locator('.catalog')).toHaveScreenshot('catalog-accessibility.png', { animations: 'disabled' });

  const contrastPairs = await page.evaluate(() => {
    const luminance = (color: string) => {
      const normalized = color.startsWith('#') ? `rgb(${color.slice(1).match(/../g)!.map(value => parseInt(value, 16)).join(',')})` : color;
      const channels = normalized.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(value => {
        const channel = value / 255;
        return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
      });
      return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    };
    const backgroundOf = (element: Element) => {
      for (let current: Element | null = element; current; current = current.parentElement) {
        const color = getComputedStyle(current).backgroundColor;
        if (Number(color.match(/rgba\([^,]+,[^,]+,[^,]+,\s*([\d.]+)/)?.[1] ?? 1) > 0) return color;
      }
      return 'rgb(255, 255, 255)';
    };
    const ratio = (foreground: string, background: string) => {
      const a = luminance(foreground), b = luminance(background);
      return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
    };
    const pairs = Array.from(document.querySelectorAll('.catalog-count, .catalog-sort-label, .catalog-sort-trigger, .catalog-search-group h3, .catalog-search-tag, .catalog-search-tag.active, .catalog-pagination a, .active-page'))
      .map(element => ({ selector: element.className.toString(), ratio: ratio(getComputedStyle(element).color, backgroundOf(element)) }));
    const style = getComputedStyle(document.documentElement);
    for (const foreground of ['--neutral-900', '--neutral-800', '--neutral-600']) {
      for (const background of ['--color-surface', '--color-surface-2']) {
        pairs.push({ selector: `${foreground} on ${background}`, ratio: ratio(style.getPropertyValue(foreground).trim(), style.getPropertyValue(background).trim()) });
      }
    }
    return pairs;
  });
  for (const pair of contrastPairs) {
    expect(pair.ratio, `${pair.selector} contrast`).toBeGreaterThanOrEqual(4.5);
  }

  await sort.focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  await page.waitForTimeout(250);
  const focusContrast = await sort.evaluate(element => {
    const parse = (color: string) => color.match(/[\d.]+/g)!.slice(0, 3).map(Number).map(value => {
      const channel = value / 255;
      return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
    }).reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
    const style = getComputedStyle(element);
    const foreground = parse(style.borderTopColor);
    const background = parse(style.backgroundColor);
    return (Math.max(foreground, background) + 0.05) / (Math.min(foreground, background) + 0.05);
  });
  expect(focusContrast, 'sort keyboard focus border contrast').toBeGreaterThanOrEqual(3);
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await expect(page.getByRole('option', { name: 'Name: A to Z' })).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-desc');
  await page.keyboard.press('Enter');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).toBeFocused();
  await expect(page.locator('#catalog-sort-opt-name-desc')).toHaveAttribute('aria-selected', 'true');
  await page.keyboard.press('ArrowDown');
  await page.keyboard.press('Escape');
  await expect(sort).toBeFocused();

  await expect(page.getByRole('group', { name: 'Brand' })).toBeVisible();
  await expect(page.getByRole('group', { name: 'Type' })).toBeVisible();
  const brandAll = page.getByRole('group', { name: 'Brand' }).getByRole('link', { name: /^All/ });
  await brandAll.focus();
  await expect(brandAll).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('group', { name: 'Brand' })).toBeVisible();

  const productLink = page.getByRole('link', { name: /Adventurer GPS Watch/ });
  await expect(productLink).toBeVisible();
  const productImage = page.locator('.catalog-product img').first();
  const imageName = await productImage.getAttribute('alt');
  expect(imageName).toBeTruthy();
  await expect(page.getByRole('img', { name: imageName!, exact: true })).toBeVisible();
  await expect(page.locator('.catalog').getByRole('img')).toHaveCount(await page.locator('.catalog-product img').count());
  await expect(page.locator('.catalog img[role="presentation"]')).toHaveCount(3);
  await expect(page.getByRole('img', { name: 'Filters' })).toHaveCount(0);
  await expect(page.getByRole('img', { name: 'Previous page' })).toHaveCount(0);
  await expect(page.getByRole('img', { name: 'Next page' })).toHaveCount(0);

  const nextPage = page.getByRole('navigation', { name: 'Catalog pages' }).getByRole('link', { name: 'Next page' });
  await nextPage.focus();
  await expect(nextPage).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\?page=2$/);
  await expect(page.locator('[aria-current="page"]')).toHaveText('2');
  const previousPage = page.getByRole('navigation', { name: 'Catalog pages' }).getByRole('link', { name: 'Previous page' });
  await previousPage.focus();
  await expect(previousPage).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/$/);

  const currentPage = page.getByText('1', { exact: true });
  await expect(currentPage).toHaveAttribute('aria-current', 'page');
  const newGrid = await page.locator('.catalog-items').boundingBox();
  expect(newGrid?.x).toBe(originalGrid?.x);
  expect(newGrid?.width).toBe(originalGrid?.width);
});
