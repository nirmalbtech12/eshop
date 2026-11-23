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

        var error = form.parentElement.querySelector('[data-cart-quantity-error]');
        var value = input.valueAsNumber;
        if (!Number.isInteger(value) || value < 0) {
            input.setCustomValidity('Enter zero or a positive whole number.');
            input.reportValidity();
            if (error) {
                error.textContent = 'Enter zero or a positive whole number.';
            }
            return;
        }
        input.setCustomValidity('');
        if (error) error.textContent = '';
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

    document.addEventListener('input', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            input.setCustomValidity('');
            var error = input.closest('form').parentElement.querySelector('[data-cart-quantity-error]');
            if (error) error.textContent = '';
        }
    });

    var updateInFlight = false;
    var deferredNavigation = null;
    document.addEventListener('submit', function (e) {
        if (e.target.matches('.cart-quantity')) {
            updateInFlight = true;
        }
    }, true);

    document.addEventListener('click', function (e) {
        if (!updateInFlight) return;
        var link = e.target.closest && e.target.closest('a[href]');
        if (link && link.origin === window.location.origin) {
            e.preventDefault();
            deferredNavigation = link.href;
        }
    }, true);

    var originalFetch = window.fetch;
    window.fetch = function (input, init) {
        var request = input instanceof Request ? input : null;
        var url = new URL(request ? request.url : input, window.location.href);
        var method = (init && init.method) || (request && request.method) || 'GET';
        if (method.toUpperCase() !== 'POST' || url.pathname !== '/cart') {
            return originalFetch.apply(this, arguments);
        }

        updateInFlight = true;
        return originalFetch.apply(this, arguments).then(function (response) {
            return response.clone().text().then(function (html) {
                var committed = response.ok && /data-cart-update-status=["']committed["']/.test(html);
                updateInFlight = false;
                if (committed && deferredNavigation) {
                    var destination = deferredNavigation;
                    deferredNavigation = null;
                    window.location.assign(destination);
                } else {
                    deferredNavigation = null;
                }
                return response;
            });
        }, function (error) {
            updateInFlight = false;
            deferredNavigation = null;
            throw error;
        });
    };
})();
