# Catalog accessibility audit

## Scope and method

The audit covers the rendered catalog page at the desktop Playwright viewport (1440 × 1000): the sort label and combobox while keyboard-focused, the keyboard-active listbox option, representative nonempty filter labels, product names and prices, product image alternatives, pagination names, and the catalog screenshot. It uses the existing Playwright Chromium setup and AppHost. For contrast, the test samples only text intersecting the viewport whose effective background resolves to a solid opaque computed color. It walks the element and ancestor backgrounds and fails if it encounters a gradient, background image, or translucent layer instead of substituting a guessed color. The WCAG 2.1 AA normal-text threshold is 4.5:1. These targeted checks are not a complete WCAG conformance assessment.

## Findings

- The sort control supports ArrowDown navigation, selection with Enter, and returns to its collapsed state. Its accessible name includes the visible Sort label and current value.
- Representative filter, product, and pagination links have accessible names; product images have nonempty alternative text.
- The focused sort label and control, keyboard-active sort option, visible filter labels, product names, and prices meet the threshold above on their resolved solid backgrounds.
- No source correction was indicated by the rendered audit. Existing markup, styles, and keyboard behavior passed the targeted assertions.
- The catalog screenshot is checked against the committed Playwright baseline at `e2e/CatalogAccessibilityTest.spec.ts-snapshots/catalog-e2e-tests-without-logged-in-linux.png`.

## Verification and limitations

The `catalog accessibility keyboard names contrast and visual regression` Playwright test exercises the rendered page, computed styles, accessible names, keyboard state, and screenshot. It checks viewport intersection and resolves each sample's actual solid background through its ancestors. The tested interactive states are the keyboard-focused sort control and keyboard-active listbox option. The existing Playwright project uses one worker and starts the AppHost from `playwright.config.ts`; `.github/workflows/playwright.yml` provisions Chromium and runs the E2E suite. This audit does not evaluate every page state, viewport, browser, assistive technology, focus-ring non-text contrast, or WCAG success criterion.
