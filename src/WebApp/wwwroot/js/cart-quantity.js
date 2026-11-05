// Cart quantity: commit a directly-typed quantity immediately, without an Update
// button. The [-] / [+] / Remove buttons already submit on their own (they are real
// submit buttons inside the enhanced-navigation form), so this only enhances typing:
// on change (or Enter) we copy the typed value into the hidden apply submitter and
// request a submit, letting Blazor's enhanced form post update the basket in place.
//
// Delegated from document and safe to load once; no per-element wiring, so it keeps
// working after enhanced-navigation morphs the DOM.
(function () {
    'use strict';
    var pendingForms = new Map();

    function resolvePendingSubmissions() {
        pendingForms.forEach(function (resolve, form) {
            pendingForms.delete(form);
            resolve();
        });
    }

    (function hookEnhancedLoad() {
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', resolvePendingSubmissions);
        } else {
            setTimeout(hookEnhancedLoad, 150);
        }
    })();

    function commit(input) {
        var form = input.closest('form');
        if (!form) {
            return;
        }

        var apply = form.querySelector('[data-cart-quantity-apply]');
        if (!apply) {
            return;
        }

        var value = parseInt(input.value, 10);
        if (!isFinite(value) || value < 1) {
            input.setCustomValidity('Enter a quantity greater than zero. Use Remove to remove an item.');
            input.reportValidity();
            return;
        }
        input.setCustomValidity('');
        input.value = String(value);

        apply.value = String(value);
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit(apply);
        } else {
            apply.click();
        }
    }

    document.addEventListener('change', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            commit(input);
        }
    });

    // Enhanced form submissions finish on Blazor's enhancedload event. Keep a
    // navigation click from disposing the page while its cart mutation is pending.
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form.matches || !form.matches('form.cart-quantity')) {
            return;
        }
        var resolveSubmission;
        var submission = new Promise(function (resolve) { resolveSubmission = resolve; });
        pendingForms.set(form, resolveSubmission);
    }, true);

    document.addEventListener('click', function (e) {
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link || link.target || link.hasAttribute('download')) {
            return;
        }
        var pending = Array.from(document.querySelectorAll('form.cart-quantity'))
            .map(function (form) { return pendingForms.get(form); })
            .filter(Boolean);
        if (pending.length === 0) {
            return;
        }
        e.preventDefault();
        var href = link.href;
        Promise.all(pending).then(function () { window.location.assign(href); });
    }, true);

    // Enter should apply the typed value rather than trigger the first submit button
    // (which would be the decrement). Prevent the default submit and blur to commit.
    document.addEventListener('keydown', function (e) {
        if (e.key !== 'Enter') {
            return;
        }
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            e.preventDefault();
            commit(input);
        }
    });
})();
