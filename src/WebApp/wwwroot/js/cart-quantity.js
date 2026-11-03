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
    var lastEditedInput = null;
    var pendingInput = null;
    var queuedNavigation = null;
    var failureMessage = null;
    var failureObserver = null;
    var originalFetch = window.fetch.bind(window);

    window.fetch = function (input, init) {
        var requestUrl = typeof input === 'string' ? input : input.url;
        var method = (init && init.method) || input.method || 'GET';
        return originalFetch(input, init).then(function (response) {
            if (method.toUpperCase() === 'POST' && new URL(requestUrl, location.href).pathname === '/cart') {
                return response.clone().text().then(function () {
                    pendingInput = null;
                    if (!response.ok) {
                        queuedNavigation = null;
                        window.setTimeout(showUpdateFailure, 0);
                        return response;
                    }
                    window.setTimeout(function () {
                        if (queuedNavigation) {
                            var destination = queuedNavigation;
                            queuedNavigation = null;
                            location.assign(destination);
                        }
                    }, 0);
                    return response;
                });
            }
            return response;
        }).catch(function (error) {
            if (method.toUpperCase() === 'POST' && new URL(requestUrl, location.href).pathname === '/cart') {
                pendingInput = null;
                queuedNavigation = null;
                window.setTimeout(showUpdateFailure, 0);
            }
            throw error;
        });
    };

    var originalXhrOpen = XMLHttpRequest.prototype.open;
    var originalXhrSend = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function (method, url) {
        this.__cartQuantityMethod = method;
        this.__cartQuantityUrl = url;
        return originalXhrOpen.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function () {
        var xhr = this;
        if (xhr.__cartQuantityMethod?.toUpperCase() === 'POST' &&
            new URL(xhr.__cartQuantityUrl, location.href).pathname === '/cart') {
            xhr.addEventListener('loadend', function () {
                pendingInput = null;
                if (xhr.status < 200 || xhr.status >= 300) {
                    queuedNavigation = null;
                    showUpdateFailure();
                } else if (queuedNavigation) {
                    var destination = queuedNavigation;
                    queuedNavigation = null;
                    location.assign(destination);
                }
            }, { once: true });
        }
        return originalXhrSend.apply(this, arguments);
    };

    function showUpdateFailure() {
        if (!failureMessage) {
            failureMessage = document.createElement('p');
            failureMessage.className = 'validation-message';
            failureMessage.setAttribute('role', 'alert');
            failureMessage.setAttribute('data-cart-quantity-error', '');
        }
        failureMessage.textContent = 'Quantity update failed. Please try again.';
        if (!failureMessage.isConnected) document.body.append(failureMessage);
        if (!failureObserver) {
            failureObserver = new MutationObserver(function () {
                if (!failureMessage.isConnected) document.body.append(failureMessage);
            });
            failureObserver.observe(document.body, { childList: true, subtree: true });
        }
    }

    function commit(input) {
        var form = input.closest('form');
        if (!form) {
            return;
        }

        var apply = form.querySelector('[data-cart-quantity-apply]');
        if (!apply) {
            return false;
        }

        var value = Number(input.value);
        if (input.value.trim() === '' || !Number.isInteger(value) || value < 0) {
            input.setCustomValidity('Enter a whole quantity of zero or more.');
            input.setAttribute('aria-invalid', 'true');
            input.reportValidity();
            return false;
        }
        input.setCustomValidity('');
        input.removeAttribute('aria-invalid');
        input.value = String(value);

        apply.value = String(value);
        pendingInput = input;
        if (lastEditedInput === input) lastEditedInput = null;
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit(apply);
        } else {
            apply.click();
        }
        return true;
    }

    document.addEventListener('change', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            commit(input);
        }
    }, true);

    document.addEventListener('input', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            lastEditedInput = input;
            input.setCustomValidity('');
            input.removeAttribute('aria-invalid');
        }
    });

    // A link click blurs the number input before its change event. Submit the edit
    // during capture so the form request starts before the browser follows the link.
    document.addEventListener('click', function (e) {
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link) return;
        var focusedInput = pendingInput || (document.activeElement && document.activeElement.closest
            ? document.activeElement.closest('[data-cart-quantity-input]') : null);
        focusedInput = focusedInput || lastEditedInput;
        if (focusedInput) {
            e.preventDefault();
            queuedNavigation = link.href;
            if (pendingInput !== focusedInput && !commit(focusedInput)) return;
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
