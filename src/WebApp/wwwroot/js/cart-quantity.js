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

    function commit(input) {
        var form = input.closest('form');
        if (!form) {
            return;
        }

        var apply = form.querySelector('[data-cart-quantity-apply]');
        if (!apply) {
            return;
        }

        var value = input.value.trim();
        if (!/^\d+$/.test(value) || Number(value) < 1) {
            input.setCustomValidity('Enter a quantity greater than zero. Use Remove to remove an item.');
            input.reportValidity();
            return;
        }
        input.setCustomValidity('');

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

    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form.matches('.cart-quantity')) return;
        var submitter = e.submitter;
        var target = form.querySelector('[data-cart-quantity-value]');
        if (submitter && submitter.hasAttribute('data-cart-quantity')) {
            target.value = submitter.getAttribute('data-cart-quantity');
        } else {
            var input = form.querySelector('[data-cart-quantity-input]');
            if (!input || !/^\d+$/.test(input.value.trim()) || Number(input.value) < 1) {
                e.preventDefault();
                if (input) {
                    input.setCustomValidity('Enter a quantity greater than zero. Use Remove to remove an item.');
                    input.reportValidity();
                }
                return;
            }
            target.value = input.value.trim();
        }
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
