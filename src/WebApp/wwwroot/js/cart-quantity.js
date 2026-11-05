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

    var pendingQuantityUpdate = false;
    var queuedNavigation = null;

    function commit(input) {
        var form = input.closest('form');
        if (!form) {
            return;
        }

        var apply = form.querySelector('[data-cart-quantity-apply]');
        if (!apply) {
            return;
        }

        var value = Number(input.value);
        if (input.value.trim() === '' || !Number.isInteger(value) || value < 0) {
            input.setCustomValidity('Enter a whole number of zero or more.');
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

    var originalFetch = window.fetch;
    window.fetch = function (input, init) {
        var url = typeof input === 'string' ? input : input.url;
        var method = (init && init.method) || (typeof input !== 'string' && input.method) || 'GET';
        var isQuantityUpdate = method.toUpperCase() === 'POST' && new URL(url, window.location.href).pathname === '/cart';
        if (!isQuantityUpdate) {
            return originalFetch.apply(this, arguments);
        }

        pendingQuantityUpdate = true;
        return originalFetch.apply(this, arguments).then(function (response) {
            pendingQuantityUpdate = false;
            if (queuedNavigation) {
                var destination = queuedNavigation;
                queuedNavigation = null;
                if (response.ok) {
                    window.location.assign(destination);
                }
            }
            return response;
        }, function (error) {
            pendingQuantityUpdate = false;
            queuedNavigation = null;
            throw error;
        });
    };

    // Keep same-window navigation from cancelling an in-flight quantity update.
    document.addEventListener('click', function (e) {
        if (!pendingQuantityUpdate || e.defaultPrevented || e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) {
            return;
        }
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link || link.target && link.target !== '_self' || link.hasAttribute('download')) {
            return;
        }
        var destination = new URL(link.href, window.location.href);
        if (destination.origin !== window.location.origin) {
            return;
        }
        e.preventDefault();
        queuedNavigation = destination.href;
    }, true);

    // Enter should apply the typed value rather than trigger the first submit button
    // (which would be the decrement). Prevent the default submit and commit explicitly.
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
