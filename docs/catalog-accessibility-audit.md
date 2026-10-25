# Catalog accessibility audit

Scope: web catalog filters, product cards, sorting, results, and pagination.

## Findings and changes

- The sort widget is a custom select-only combobox. It now has the explicit accessible name “Sort products”; its listbox options expose selected state, and keyboard focus is included in the shared visible focus ring.
- Brand and Type filters are named groups. Filter links retain visible text, including each option count; product links include product names and product images use the item name as alternative text. Filter and pagination icons are decorative.
- Loading results announce “Loading products”; the empty state has a heading and a text-labeled clear action. Current pagination uses `aria-current="page"`.

## Contrast and visual review

Measured token pairs against white (#fff): neutral 900 (#141414) is 18.42:1, neutral 800 (#3d3d3d) is 11.25:1, and neutral 600 (#6b6b6b) is 5.33:1. All exceed WCAG AA's 4.5:1 normal-text threshold. The keyboard focus outline uses neutral 900 against the light catalog surface (#fff), for 18.42:1. The browser test also measures the rendered product-count text against the rendered page background and asserts at least 4.5:1. Interactive options and pagination retain textual labels and non-color state indicators. A Playwright screenshot comparison of the catalog is checked against a checked-in baseline; its viewport and layout remain unchanged during keyboard sorting.

## Verification

`CatalogAccessibilityTest.spec.ts` checks the combobox name, expanded/selected/active state, keyboard selection and Escape focus restoration, named filter groups, product link naming, current-page state, rendered contrast, stable grid geometry, and screenshot baseline. This automated check does not replace manual screen-reader testing or checks of every component state.
