# Catalog accessibility audit

Scope: the web catalog at `/`, including filter disclosure and facets, sort
control, product cards, and pagination. Review standard: WCAG 2.2 AA text
contrast (4.5:1 for normal text); keyboard operation; meaningful names and
selected/disabled states.

## Findings and changes

- Filter disclosure uses native `summary` keyboard behavior. Filter links remain
  links when usable; active choices expose `aria-current="true"`. Zero-result
  choices are noninteractive groups named “<facet>, unavailable,” expose the
  disabled state, and retain readable contrast.
- Sort is a labeled combobox. The existing arrow, Enter/Space, Home/End,
  Escape, Tab, and type-ahead behavior exposes expanded, active-descendant, and
  selected state. The trigger name is “Sort products.”
- Product links are named by their visible product title. Product images have
  product-name alt text. Pagination has a named navigation landmark, named
  previous/next links, and a current-page marker.
- Catalog layout geometry is unchanged. The only intentional visual change is
  replacing opacity-faded disabled labels with the existing muted text color.

## Contrast review

Ratios use the sRGB relative-luminance formula and the source palette in
`src/WebApp/wwwroot/css/app.css`:

| Catalog text / indicator | Foreground | Background | Ratio | Result |
| --- | --- | --- | ---: | --- |
| Main text, options, and focus outline | `#141414` | `#ffffff` | 18.4:1 | Pass |
| Secondary text and disabled facet labels | `#6b6b6b` | `#ffffff` | 5.3:1 | Pass |
| Secondary ink on light count badge | `#3d3d3d` | `#ededea` | 8.9:1 | Pass |
| Selected filter text | `#ffffff` | `#141414` | 18.4:1 | Pass |

The sort active-row fill is decorative; option text remains `#141414` on
`#f6f6f4` (15.4:1). Ratios are rounded to one decimal place.

## Regression coverage

`e2e/CatalogAccessibilityTest.spec.ts` checks keyboard disclosure, filter-link
navigation, sort Escape dismissal and Tab selection, unavailable facet names and
disabled state, product image names, and pagination navigation. It also compares
a 1440 × 1000 catalog screenshot against
`e2e/CatalogAccessibilityTest.spec.ts-snapshots/catalog-e2e-tests-without-logged-in-linux.png`
and asserts the catalog grid, filter width, and sort control corner radius.

The repository Playwright workflow runs the test through `npm run test:e2e`.
Run it locally with `npx playwright test e2e/CatalogAccessibilityTest.spec.ts --project='e2e tests without logged in'`.
