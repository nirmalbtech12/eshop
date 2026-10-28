import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const wcagTags = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];
const viewports = [
  { name: 'desktop', width: 1280, height: 900 },
  { name: 'mobile', width: 390, height: 844 },
];

async function waitForCatalog(page: Page, empty = false) {
  if (empty) {
    await expect(page.locator('.catalog-empty')).toBeVisible({ timeout: 60_000 });
    return;
  }

  await expect(page.locator('.catalog-product').first()).toBeVisible({ timeout: 60_000 });
  await page.evaluate(() => document.fonts.ready);
  await page.locator('.catalog-product img').evaluateAll(images => Promise.all(images.map(image => {
    const img = image as HTMLImageElement;
    return img.decode().catch(() => undefined);
  })));
}

async function expectVisibleFocus(page: Page) {
  const style = await page.locator(':focus').evaluate(element => {
    const computed = getComputedStyle(element);
    return { outlineStyle: computed.outlineStyle, outlineWidth: computed.outlineWidth };
  });
  expect(style.outlineStyle).not.toBe('none');
  expect(Number.parseFloat(style.outlineWidth)).toBeGreaterThan(0);
}

async function expectNoAxeViolations(page: Page, state: string) {
  const results = await new AxeBuilder({ page }).withTags(wcagTags).analyze();
  expect(results.violations.map(violation => ({
    id: violation.id,
    targets: violation.nodes.map(node => ({ target: node.target, html: node.html })),
    summary: violation.nodes.map(node => node.failureSummary),
  })), state).toEqual([]);
}

test('catalog keyboard navigation', async ({ page }) => {
  await page.goto('/');
  await waitForCatalog(page);

  const summary = page.locator('.catalog-search-summary');
  await page.getByRole('group', { name: 'Brand' }).getByRole('link', { name: /^All \d+ brands$/ }).focus();
  await page.keyboard.press('Shift+Tab');
  await expect(summary).toBeFocused();
  await expect(summary).toBeVisible();
  await expectVisibleFocus(page);
  await page.keyboard.press('Enter');
  await expect(page.locator('.catalog-search-details')).not.toHaveAttribute('open', '');
  await page.keyboard.press('Enter');
  await expect(page.locator('.catalog-search-details')).toHaveAttribute('open', '');

  await page.getByRole('group', { name: 'Brand' }).getByRole('link', { name: 'Daybird 10' }).focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/brand=1/);
  await waitForCatalog(page);

  await page.goto('/?brand=1&type=2');
  await waitForCatalog(page, true);
  const disabledFacet = page.locator('.catalog-search-tag[aria-disabled="true"]').first();
  await expect(disabledFacet).toBeVisible();
  await expect(disabledFacet).not.toHaveAttribute('href', /.+/);
  await expect(disabledFacet).toHaveJSProperty('tabIndex', -1);
  await disabledFacet.evaluate(element => (element as HTMLElement).focus());
  const emptyStateUrl = page.url();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(emptyStateUrl);

  await page.goto('/');
  await waitForCatalog(page);
  const sort = page.locator('[data-catalog-sort-trigger]');
  await sort.focus();
  await page.keyboard.press('Enter');
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await expect(sort).toHaveAttribute('aria-controls', 'catalog-sort-listbox');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-asc');
  await page.keyboard.press('ArrowDown');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-desc');
  await page.keyboard.press('Home');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-asc');
  await page.keyboard.press('End');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-price-desc');
  await page.keyboard.press('Enter');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('#catalog-sort-opt-price-desc')).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('[data-catalog-sort-value]')).toHaveText('Price: High to Low');
  await expect.poll(() => page.evaluate(() => localStorage.getItem('aw.catalog.sort.v1'))).toBe('price-desc');
  const prices = await page.locator('.catalog-item').evaluateAll(items => items.map(item => Number(item.getAttribute('data-sort-price'))));
  expect(prices[0]).toBeGreaterThanOrEqual(prices.at(-1)!);

  await page.goto('/?brand=1');
  await waitForCatalog(page);
  await expect(page.locator('[data-catalog-sort-value]')).toHaveText('Price: High to Low');
  await page.goto('/?page=2');
  await waitForCatalog(page);
  await expect(page.locator('[data-catalog-sort-value]')).toHaveText('Price: High to Low');
  await page.reload();
  await waitForCatalog(page);
  await expect(page.locator('[data-catalog-sort-value]')).toHaveText('Price: High to Low');

  await sort.focus();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Escape');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).toBeFocused();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Tab');
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('.catalog-product').first()).toBeFocused();
  await expectVisibleFocus(page);

  await page.goto('/');
  await waitForCatalog(page);
  const pageTwo = page.getByRole('link', { name: '2', exact: true });
  await pageTwo.focus();
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/page=2/);
  await waitForCatalog(page);
  await page.goto('/');
  await waitForCatalog(page);
  await page.getByRole('link', { name: /Adventurer GPS Watch/ }).focus();
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Adventurer GPS Watch' })).toBeVisible();
});

test('catalog WCAG AA audit', async ({ page }) => {
  const states = [
    { name: 'page one', route: '/', empty: false },
    { name: 'page two', route: '/?page=2', empty: false },
    { name: 'selected brand', route: '/?brand=1', empty: false },
    { name: 'empty results', route: '/?brand=1&type=2', empty: true },
  ];

  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    for (const state of states) {
      await page.goto(state.route);
      await waitForCatalog(page, state.empty);
      await expectNoAxeViolations(page, `${viewport.name}: ${state.name}`);
    }

    await page.goto('/');
    await waitForCatalog(page);
    await page.locator('[data-catalog-sort-trigger]').click();
    await expectNoAxeViolations(page, `${viewport.name}: open sorting`);
  }
});

test('catalog accessible names and states', async ({ page }) => {
  await page.goto('/');
  await waitForCatalog(page);
  await expect(page.getByRole('link', { name: 'AdventureWorks home' }).first()).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Primary' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Shopping bag' }).first()).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ask the assistant' })).toBeVisible();

  const brandGroup = page.getByRole('group', { name: 'Brand' });
  const typeGroup = page.getByRole('group', { name: 'Type' });
  await expect(brandGroup).toBeVisible();
  await expect(typeGroup).toBeVisible();
  await expect(brandGroup.getByRole('link', { name: /^All \d+ brands$/ })).toHaveAttribute('aria-current', 'true');
  await expect(typeGroup.getByRole('link', { name: /^All \d+ types$/ })).toHaveAttribute('aria-current', 'true');

  const product = page.locator('.catalog-product').first();
  const productName = await product.locator('.name').innerText();
  const brand = await product.locator('.catalog-product-brand').count()
    ? (await product.locator('.catalog-product-brand').textContent())?.trim() ?? ''
    : '';
  const price = await product.locator('.price').innerText();
  await expect(product).toHaveAccessibleName([brand, productName, price].filter(Boolean).join(' '));
  await expect(product.locator('img')).toHaveAttribute('alt', '');

  await expect(page.getByRole('navigation', { name: 'Catalog pages' })).toBeVisible();
  await expect(page.locator('.active-page')).toHaveAttribute('aria-current', 'page');
  const sort = page.getByRole('combobox', { name: /Sort/ });
  await expect(sort).toHaveAttribute('aria-expanded', 'false');
  await expect(sort).toHaveAttribute('aria-controls', 'catalog-sort-listbox');
  await sort.click();
  await expect(sort).toHaveAttribute('aria-expanded', 'true');
  await expect(sort).toHaveAttribute('aria-activedescendant', 'catalog-sort-opt-name-asc');
  await expect(page.getByRole('listbox', { name: 'Sort' })).toBeVisible();
  await expect(page.getByRole('option', { name: 'Name: A to Z' })).toHaveAttribute('aria-selected', 'true');

  await page.goto('/?brand=1');
  await waitForCatalog(page);
  await expect(page.getByRole('group', { name: 'Brand' }).getByRole('link', { name: /Daybird/ })).toHaveAttribute('aria-current', 'true');
  await expect(page.getByRole('group', { name: 'Brand' }).getByRole('link', { name: /^All \d+ brands$/ })).not.toHaveAttribute('aria-current', 'true');
});

test('catalog visual regression', async ({ page }) => {
  for (const viewport of viewports) {
    await page.setViewportSize({ width: viewport.width, height: viewport.height });
    for (const state of [
      { name: 'home', route: '/', openSort: false },
      { name: 'selected-brand', route: '/?brand=1', openSort: false },
      { name: 'open-sort', route: '/', openSort: true },
    ]) {
      await page.goto(state.route);
      await waitForCatalog(page);
      if (state.openSort) {
        await page.locator('[data-catalog-sort-trigger]').click();
      }
      await expect(page).toHaveScreenshot(`catalog-${viewport.name}-${state.name}-${viewport.width}x${viewport.height}.png`, {
        fullPage: true, animations: 'disabled', caret: 'hide', maxDiffPixelRatio: 0.01,
      });
    }
  }
});
