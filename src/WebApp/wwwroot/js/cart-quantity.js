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
    var pendingWrite = null;
    var deferredDestination = null;

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
            input.setCustomValidity(value < 0
                ? 'Quantity cannot be negative. Use Remove to remove this item.'
                : 'Enter a quantity of at least 1.');
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

    document.addEventListener('input', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            input.setCustomValidity('');
        }
    });

    // Own cart form writes so a cart link clicked while a write is in flight waits
    // for the server response before leaving the page. This keeps the existing
    // authenticated form fields and antiforgery token in the request.
    document.addEventListener('submit', function (e) {
        var form = e.target;
        if (!form.matches || !form.matches('form.cart-quantity')) {
            return;
        }

        e.preventDefault();
        if (pendingWrite) {
            return;
        }

        var formData = new FormData(form, e.submitter);
        var body = new URLSearchParams();
        formData.forEach(function (value, key) { body.append(key, value); });
        var feedback = document.querySelector('[data-cart-update-error]');
        pendingWrite = fetch(form.action || window.location.href, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8' },
            body: body.toString()
        }).then(function (response) {
            if (!response.ok) {
                throw new Error('The basket update was rejected.');
            }
            return response.text();
        }).then(function (html) {
            var documentResponse = new DOMParser().parseFromString(html, 'text/html');
            var responseError = documentResponse.querySelector('[data-cart-update-error]');
            if (responseError && responseError.textContent.trim()) {
                throw new Error('The basket update could not be completed.');
            }
            return true;
        }).catch(function (error) {
            if (feedback) {
                feedback.textContent = error.message || 'Your basket could not be updated. Please try again.';
                feedback.hidden = false;
            }
            return false;
        }).then(function (succeeded) {
            if (succeeded) {
                if (deferredDestination) {
                    window.location.assign(deferredDestination);
                } else {
                    window.location.reload();
                }
            }
            return succeeded;
        }).finally(function () {
            pendingWrite = null;
            deferredDestination = null;
        });
    }, true);

    document.addEventListener('click', function (e) {
        if (!pendingWrite) {
            return;
        }
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link || link.origin !== window.location.origin) {
            return;
        }
        e.preventDefault();
        var destination = link.href;
        deferredDestination = destination;
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
