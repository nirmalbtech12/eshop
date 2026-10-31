import { test, expect } from '@playwright/test';

function contrastRatio(foreground: string, background: string): number {
  const luminance = (color: string) => {
    const channels = color.match(/[\d.]+/g)?.slice(0, 3).map(Number) ?? [];
    const linear = channels.map(channel => {
      const value = channel / 255;
      return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
    });
    return 0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2];
  };
  const values = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
  return (values[0] + 0.05) / (values[1] + 0.05);
}

test('catalog accessibility keyboard names contrast and visual regression', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto('/');
  await expect(page.locator('.catalog-product').first()).toBeVisible();

  const sort = page.locator('#catalog-sort-trigger');
  await expect(sort).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Sort Name: A to Z' })).toBeVisible();
  await sort.focus();
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-asc');

  const contrastSamples = await page.evaluate(() => {
    const selectors = [
      '#catalog-sort-label',
      '#catalog-sort-trigger',
      '.catalog-sort-option.is-active',
      '.catalog-search-tag:not(.is-empty) > span:first-child',
      '.catalog-product-content .name',
      '.catalog-product-content .price',
    ];
    const viewportWidth = document.documentElement.clientWidth;
    const viewportHeight = document.documentElement.clientHeight;
    const samples: { text: string; foreground: string; background: string; state: string }[] = [];

    for (const element of document.querySelectorAll(selectors.join(','))) {
      const rect = element.getBoundingClientRect();
      const style = getComputedStyle(element);
      const intersectsViewport = rect.width > 0 && rect.height > 0
        && rect.right > 0 && rect.bottom > 0
        && rect.left < viewportWidth && rect.top < viewportHeight;
      if (!intersectsViewport || style.display === 'none' || style.visibility === 'hidden') continue;

      let background: string | null = null;
      for (let ancestor: Element | null = element; ancestor; ancestor = ancestor.parentElement) {
        const ancestorStyle = getComputedStyle(ancestor);
        if (ancestorStyle.backgroundImage !== 'none') {
          throw new Error(`Cannot audit ${element.className || element.id}: background image/gradient on ${ancestor.className || ancestor.tagName}`);
        }
        const color = ancestorStyle.backgroundColor;
        const channels = color.match(/[\d.]+/g)?.map(Number) ?? [];
        const alpha = channels.length > 3 ? channels[3] : 1;
        if (alpha === 1) {
          background = color;
          break;
        }
        if (alpha !== 0) {
          throw new Error(`Cannot audit ${element.className || element.id}: translucent background ${color}`);
        }
      }
      if (!background) throw new Error(`No opaque background found for ${element.className || element.id}`);
      samples.push({
        text: element.textContent?.trim() ?? '',
        foreground: style.color,
        background,
        state: element === document.activeElement ? 'keyboard-focused' : element.classList.contains('is-active') ? 'keyboard-active option' : 'default',
      });
    }
    return samples;
  });
  expect(contrastSamples.length).toBeGreaterThan(0);
  for (const sample of contrastSamples) {
    expect(contrastRatio(sample.foreground, sample.background), `${sample.state} ${sample.text}: ${sample.foreground} on ${sample.background}`).toBeGreaterThanOrEqual(4.5);
  }

  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-desc');
  await page.keyboard.press('Enter');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).not.toHaveAttribute('aria-activedescendant');
  await expect(sort).toHaveText('Name: Z to A');
  await expect(page.locator('[data-value="name-desc"]')).toHaveAttribute('aria-selected', 'true');

  const controls = [
    page.getByRole('link', { name: 'All', exact: false }).first(),
    page.getByRole('link', { name: /Adventurer GPS Watch/ }),
    page.getByRole('navigation', { name: 'Catalog pages' }).getByRole('link', { name: '2', exact: true }),
  ];
  for (const control of controls) await expect(control).toHaveAccessibleName(/\S/);
  await expect(page.locator('.catalog-product img').first()).toHaveAttribute('alt', /\S/);

  await expect(page.locator('.catalog')).toHaveScreenshot('catalog.png', { animations: 'disabled' });
});
