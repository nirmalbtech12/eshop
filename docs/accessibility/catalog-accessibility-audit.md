# Catalog accessibility audit

Scope: the web catalog at `/`, including filter facets, sorting, product links, and pagination.

## Findings and changes

- The sort combobox referenced its own trigger in `aria-labelledby`. It now has the concise name “Sort” and exposes the selected choice through `aria-valuetext`.
- Brand and type filter links now have explicit names that include the facet, product count, and selected or unavailable state. Product image alternatives and pagination labels were already present.
- Unavailable facets previously used 40% opacity, which reduced text contrast. They retain their dashed unavailable treatment and now use the muted text color without opacity.
- Sort, filter, and clear-filter borders use the muted color so interactive boundaries contrast against the white surface.

## Contrast review

The web palette defines white surfaces (`#ffffff`), ink (`#141414`), secondary ink (`#3d3d3d`), muted text (`#6b6b6b`), and the previous strong line (`#c9c8c3`). WCAG contrast calculations give ink on white 18.4:1, secondary ink 11.0:1, and muted text 5.3:1, exceeding the 4.5:1 normal-text requirement. The previous strong line was about 1.7:1 against white; muted borders are about 5.3:1, exceeding the 3:1 non-text control boundary requirement. Focus uses ink with the shared surface ring.

## Verification

- `dotnet build src/WebApp/WebApp.csproj` — passed with 0 warnings and 0 errors.
- `npx playwright test e2e/CatalogAccessibilityTest.spec.ts --project='e2e tests without logged in'` — passed. The browser test exercises keyboard opening, movement and selection; accessible names for sort, facets, product links, and pagination; and grid width and product count after the interaction.
- The browser test compares default and filtered catalog screenshots against checked-in Playwright baselines (`e2e/CatalogAccessibilityTest.spec.ts-snapshots/`). Both states passed with a 1% pixel-difference threshold.
- `CatalogAccessibilityBrowserTests.CatalogBrowserAccessibilityAndVisualChecksPass` runs that Playwright test from the selected .NET application unit-test project, so the host validation scope executes the browser and visual checks.
