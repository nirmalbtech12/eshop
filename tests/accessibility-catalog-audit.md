# Catalog Accessibility Audit

## Scope

This audit covers the catalog page, including filter groups and facet links, product links, sorting, pagination, and the visible shared shell. Automated checks use Playwright with Chromium and axe-core against WCAG 2.0, 2.1, and 2.2 A/AA tags. Axe audits the first page, page two, a selected brand, a valid no-results brand/type combination, and open sorting at desktop and mobile viewport sizes (1280 x 900 and 390 x 844).

## Findings and fixes

- Inactive facets with zero results retained an `href`, so keyboard users could activate them despite `aria-disabled`. Their `href` is now omitted while selected facets remain removable.
- Inactive facets with zero results also remained in the tab sequence. They now have `tabindex="-1"`, no `href`, and `aria-disabled="true"`; the keyboard test confirms Enter cannot activate them.
- Brand and Type filter groups did not expose their visible headings as group names. The groups now reference those headings; the contextual All links expose `aria-current` when selected.
- Product links included duplicate image alt text and mismatched visible text in their accessible names. The image is decorative, and the link name now follows its visible brand, product name, and price text.
- The previous-page link's explicit accessible name omitted its visible “Prev” text. Its name now includes that text.

No color, spacing, typography, or layout rules were changed. Accessibility changes are limited to element relationships, link activation, and accessible naming.

## Results

The focused Playwright suite passed all four cases: keyboard navigation, WCAG AA audit, accessible names and states, and desktop/mobile screenshot comparisons. Keyboard checks exercise visible focus with Tab and Shift+Tab, disclosure toggling, facet URL changes, disabled facets, sort ArrowDown/Home/End/Enter/Escape/Tab behavior, persisted sort order across filter/page/reload, and Enter navigation through pagination and a product link. Axe reported no violations for the audited states at both viewports.

The four browser test identities are linked into the existing `tests/eShop.AppHost.UnitTests` project, which is already included in the authoritative solution filter. The .NET PR workflow now sets up Node, runs `npm ci`, and installs Chromium before the solution build and test commands. The GitHub-hosted `ubuntu-latest` runner image documents Node/npm and Docker Engine; AppHost container tests also exercised the Docker-dependent lifecycle.

Six desktop/mobile Playwright baselines (home, selected brand, and open sorting) were rendered from supplied base commit `9886b2fbbc4b8715a1334132b9b95d5ccab7115a` in a temporary source archive using the same Chromium runtime and test harness. The modified tree matches the base captures within the configured one-percent pixel tolerance. The source diff contains no visual styling changes.

The complete Playwright suite passed all eight cases. `dotnet build eShop.Web.slnf --no-restore` succeeded with nine warnings and no errors. The exact solution test command passed 126/126 tests, including the four linked catalog browser identities. Commands and complete output are retained under ignored `artifacts/ben49/` and `artifacts/catalog-accessibility-tests/` files.

Manual screen-reader testing was not performed. Automated names, roles, state, keyboard behavior, and axe checks do not replace testing with assistive technologies or certify every browser/screen-reader combination. The Playwright lifecycle uses the repository's Aspire AppHost and Docker-backed dependencies; both active browser-consuming workflows provision Chromium and npm dependencies before their test commands.
