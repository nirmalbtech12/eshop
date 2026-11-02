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
    var pendingCommit = false;
    var deferredNavigation = null;

    document.addEventListener('submit', function (e) {
        if (e.target.matches('.cart-quantity')) {
            pendingCommit = true;
        }
    }, true);

    document.addEventListener('enhancedload', function () {
        document.querySelectorAll('[data-cart-quantity-input]').forEach(function (input) {
            input.removeAttribute('data-cart-quantity-pending');
        });
        if (!pendingCommit) {
            return;
        }
        pendingCommit = false;
        if (deferredNavigation) {
            var href = deferredNavigation;
            deferredNavigation = null;
            window.location.assign(href);
        }
    });

    document.addEventListener('click', function (e) {
        var link = e.target.closest && e.target.closest('a[href]');
        if (pendingCommit && link && link.classList.contains('cart-summary-link')) {
            e.preventDefault();
            deferredNavigation = link.href;
        }
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
        if (input.hasAttribute('data-cart-quantity-pending')) {
            return;
        }

        var value = parseInt(input.value, 10);
        if (!isFinite(value)) {
            return;
        }

        var error = form.querySelector('[data-cart-quantity-error]');
        if (value < 0) {
            if (error) {
                error.hidden = false;
            }
            return;
        }
        if (error) {
            error.hidden = true;
        }

        input.setAttribute('data-cart-quantity-pending', '');
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
