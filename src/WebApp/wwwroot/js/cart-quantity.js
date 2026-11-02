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

    var pendingCommit = null;
    var navigationTarget = null;

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
        if (!isFinite(value) || value < 0) {
            input.setCustomValidity('Quantity cannot be negative.');
            input.reportValidity();
            return;
        }
        input.setCustomValidity('');
        if (value === 0) {
            input.setCustomValidity('Use Remove to remove this item.');
            input.reportValidity();
            return;
        }
        input.value = String(value);
        apply.value = String(value);
        var data = new FormData(form);
        data.set('UpdateQuantityValue', String(value));
        pendingCommit = fetch(form.action || window.location.href, {
            method: 'POST',
            body: data,
            credentials: 'same-origin'
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('Quantity update failed.');
            }
            input.setAttribute('data-cart-committed', String(value));
            return true;
        }).catch(function () {
            input.setCustomValidity('The quantity could not be saved. Please try again.');
            input.reportValidity();
            return false;
        }).then(function (saved) {
            pendingCommit = null;
            if (saved && navigationTarget) {
                window.location.assign(navigationTarget);
            } else if (saved) {
                window.location.reload();
            }
            return saved;
        });
        return pendingCommit;
    }

    document.addEventListener('click', function (e) {
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link) {
            return;
        }
        var input = document.querySelector('[data-cart-quantity-input]');
        if (!input) {
            return;
        }
        if (!pendingCommit && String(input.value) === input.getAttribute('data-cart-committed')) {
            return;
        }
        e.preventDefault();
        navigationTarget = link.href;
        if (!pendingCommit) {
            commit(input);
        }
    }, true);

    document.addEventListener('change', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            commit(input);
        }
    });

    document.addEventListener('input', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input && input.validity.valid) {
            input.setCustomValidity('');
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
