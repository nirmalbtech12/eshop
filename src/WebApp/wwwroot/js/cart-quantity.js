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

    var pendingForms = new Set();
    var deferredNavigation = null;

    function finishPendingSubmissions() {
        pendingForms.clear();
        if (deferredNavigation) {
            var target = deferredNavigation;
            deferredNavigation = null;
            window.location.assign(target);
        }
    }

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (form.matches && form.matches('form[data-enhance].cart-quantity')) {
            pendingForms.add(form);
        }
    }, true);

    (function hookEnhancedLoad() {
        if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
            window.Blazor.addEventListener('enhancedload', finishPendingSubmissions);
        } else {
            setTimeout(hookEnhancedLoad, 150);
        }
    })();

    document.addEventListener('click', function (e) {
        if (pendingForms.size === 0) {
            return;
        }
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link || link.target || link.hasAttribute('download')) {
            return;
        }
        var target = new URL(link.href, window.location.href);
        if (target.origin !== window.location.origin) {
            return;
        }
        e.preventDefault();
        deferredNavigation = target.href;
    }, true);

    function commit(input) {
        var form = input.closest('form');
        if (!form) {
            return;
        }

        var apply = form.querySelector('[data-cart-quantity-apply]');
        if (!apply) {
            return;
        }

        var error = form.querySelector('[data-cart-quantity-error]');
        var value = input.value.trim() === '' ? NaN : Number(input.value);
        if (!Number.isInteger(value) || value < 0) {
            if (error) {
                error.textContent = value < 0 ? 'Quantity cannot be negative.' : 'Enter a whole number of zero or more.';
            }
            input.setAttribute('aria-invalid', 'true');
            return;
        }
        if (error) error.textContent = '';
        input.removeAttribute('aria-invalid');
        input.value = String(value);

        if (pendingForms.has(form)) {
            return;
        }

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
