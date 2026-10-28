// Keep typed quantity edits alive when the shopper immediately follows a cart link.
(function () {
    'use strict';

    var submittedValue = null;
    var pendingNavigation = null;
    var navigationTimer = null;
    var lastSubmissionSucceeded = false;

    function commit(input) {
        var form = input.closest('form');
        var value = Number(input.value);
        var apply = form && form.querySelector('[data-cart-quantity-apply]');
        if (!apply || !Number.isInteger(value) || value < 1 || submittedValue === value) {
            return;
        }

        submittedValue = value;
        lastSubmissionSucceeded = false;
        apply.value = String(value);
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit(apply);
        } else {
            apply.click();
        }
    }

    function completeNavigationIfReady() {
        var cart = document.querySelector('[data-cart-update-success="true"]');
        if (!pendingNavigation || !cart) return;

        var destination = pendingNavigation;
        pendingNavigation = null;
        clearTimeout(navigationTimer);
        lastSubmissionSucceeded = true;
        window.location.assign(destination);
    }

    document.addEventListener('change', function (event) {
        var input = event.target.closest && event.target.closest('[data-cart-quantity-input]');
        if (input) commit(input);
    });

    document.addEventListener('input', function (event) {
        var input = event.target.closest && event.target.closest('[data-cart-quantity-input]');
        if (input && Number(input.value) !== submittedValue) submittedValue = null;
    });

    document.addEventListener('keydown', function (event) {
        var input = event.target.closest && event.target.closest('[data-cart-quantity-input]');
        if (input && event.key === 'Enter') {
            event.preventDefault();
            commit(input);
        }
    });

    document.addEventListener('click', function (event) {
        var link = event.target.closest && event.target.closest('.cart-summary-link');
        if (!link) return;

        var input = document.querySelector('[data-cart-quantity-input]');
        if (!input || !Number.isInteger(Number(input.value)) || Number(input.value) < 1 ||
            input.value === input.defaultValue) return;

        event.preventDefault();
        pendingNavigation = new URL(link.getAttribute('href') || '/', window.location.href).href;
        commit(input);
        if (lastSubmissionSucceeded) {
            var completedDestination = pendingNavigation;
            pendingNavigation = null;
            window.location.assign(completedDestination);
            return;
        }
        clearTimeout(navigationTimer);
        navigationTimer = setTimeout(function () { pendingNavigation = null; }, 15000);
    }, true);

    window.Blazor.addEventListener('enhancedload', function () {
        var cart = document.querySelector('[data-cart-update-success]');
        lastSubmissionSucceeded = !!cart && cart.dataset.cartUpdateSuccess === 'true';
        completeNavigationIfReady();
    });

    new MutationObserver(completeNavigationIfReady).observe(document.documentElement, {
        attributes: true,
        attributeFilter: ['data-cart-update-success'],
        childList: true,
        subtree: true
    });
})();
