// Commit typed quantities through the named cart form and hold same-tab navigation
// until Blazor reports that the enhanced form response has been applied.
(function () {
    'use strict';

    var updatePending = false;
    var pendingNavigation = null;
    var navigationTimer = null;

    function commit(input) {
        var form = input.closest('form');
        if (!form) {
            return;
        }

        var apply = form.querySelector('[data-cart-quantity-apply]');
        if (!apply) {
            return;
        }

        var value = input.valueAsNumber;
        if (!Number.isInteger(value)) {
            input.setCustomValidity('Enter a whole number quantity.');
            input.reportValidity();
            return;
        }
        if (value < 0) {
            input.setCustomValidity('Quantity cannot be negative.');
            input.reportValidity();
            return;
        }

        input.setCustomValidity('');
        apply.value = String(value);
        form.requestSubmit(apply);
    }

    function showNavigationError() {
        var error = document.querySelector('[data-cart-navigation-error]');
        if (error) {
            error.hidden = false;
        }
        pendingNavigation = null;
        updatePending = false;
    }

    document.addEventListener('input', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            input.setCustomValidity('');
        }
    });

    document.addEventListener('change', function (e) {
        var input = e.target.closest && e.target.closest('[data-cart-quantity-input]');
        if (input) {
            commit(input);
        }
    });

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

    document.addEventListener('submit', function (e) {
        if (e.target.matches('form.cart-quantity')) {
            updatePending = true;
        }
    }, true);

    document.addEventListener('click', function (e) {
        if (e.button !== 0 || e.metaKey || e.ctrlKey || e.shiftKey || e.altKey) {
            return;
        }
        var link = e.target.closest && e.target.closest('a[href]');
        if (!link || link.target || link.hasAttribute('download') || !updatePending) {
            return;
        }

        e.preventDefault();
        pendingNavigation = link.href;
        if (navigationTimer) {
            clearTimeout(navigationTimer);
        }
        navigationTimer = setTimeout(showNavigationError, 30000);
    }, true);

    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', function () {
            updatePending = false;
            if (pendingNavigation) {
                var destination = pendingNavigation;
                pendingNavigation = null;
                clearTimeout(navigationTimer);
                window.location.assign(destination);
            }
        });
    }
})();
