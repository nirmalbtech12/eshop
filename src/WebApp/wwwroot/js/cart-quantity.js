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
    var pendingNavigation = null;
    var submissionPending = false;

    function finishSubmission() {
        submissionPending = false;
        if (pendingNavigation) {
            var destination = pendingNavigation;
            pendingNavigation = null;
            window.location.assign(destination);
        }
    }

    function formatCurrency(cents) {
        return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(cents / 100);
    }

    function updateDisplayedTotals() {
        var lines = Array.from(document.querySelectorAll('.cart-item'));
        var totalCents = 0;
        var totalQuantity = 0;

        for (var i = 0; i < lines.length; i++) {
            var line = lines[i];
            var input = line.querySelector('[data-cart-quantity-input]');
            var quantity = Number(input && input.value);
            if (!isFinite(quantity) || quantity < 0 || Math.floor(quantity) !== quantity) {
                return;
            }

            var priceText = line.querySelector('.price').textContent;
            var unitPriceCents = Math.round(Number(priceText.replace(/[^0-9.]/g, '')) * 100);
            var lineTotalCents = unitPriceCents * quantity;
            line.querySelector('.catalog-item-total').textContent = formatCurrency(lineTotalCents);
            totalCents += lineTotalCents;
            totalQuantity += quantity;
        }

        var summary = document.querySelector('.cart-summary-total > div:last-child');
        if (summary) {
            summary.textContent = formatCurrency(totalCents);
        }
        document.querySelectorAll('.filter-badge').forEach(function (badge) {
            badge.textContent = String(totalQuantity);
        });
    }

    var nativeFetch = window.fetch;
    window.fetch = function (request, options) {
        var url = typeof request === 'string' ? request : request.url;
        var method = (options && options.method) || (typeof request === 'object' && request.method) || 'GET';
        var isCartPost = method.toUpperCase() === 'POST' && new URL(url, window.location.href).pathname === '/cart';
        var result = nativeFetch.apply(this, arguments);
        if (isCartPost) {
            result.then(finishSubmission, finishSubmission);
        }
        return result;
    };

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
            input.setCustomValidity('Enter a quantity of zero or more.');
            input.reportValidity();
            input.value = input.defaultValue;
            updateDisplayedTotals();
            return;
        }
        input.setCustomValidity('');
        input.value = String(value);

        apply.value = String(value);
        submissionPending = true;
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

    document.addEventListener('input', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            updateDisplayedTotals();
        }
    });

    // A blur caused by clicking a navigation link fires change immediately before
    // click. Let the enhanced form commit finish before following that link.
    document.addEventListener('click', function (e) {
        var submitter = e.target.closest && e.target.closest('button[name="UpdateQuantityValue"]');
        if (submitter) {
            var form = submitter.closest('form');
            var input = form && form.querySelector('[data-cart-quantity-input]');
            var quantity = Number(submitter.value);
            if (input && isFinite(quantity) && quantity >= 0) {
                if (quantity === 0) {
                    input.min = '0';
                }
                input.value = String(quantity);
                updateDisplayedTotals();
            }
        }

        if (!submissionPending) {
            return;
        }
        var link = e.target.closest && e.target.closest('a[href]');
        if (link) {
            e.preventDefault();
            pendingNavigation = link.href;
        }
    }, true);

    document.addEventListener('enhancedload', function () {
        finishSubmission();
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
