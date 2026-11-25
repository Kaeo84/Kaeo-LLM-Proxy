/* Kaeo LLM Proxy dashboard login page. Handles first-run administrator setup and regular
   sign-in; on success the session cookie carries the browser into the main dashboard. */
(function ($) {
    'use strict';

    var THEME_STORAGE = 'kaeo.dashboard.theme';
    var setupMode = false;

    function applyStoredTheme() {
        var mode = 'auto';
        try { mode = localStorage.getItem(THEME_STORAGE) || 'auto'; } catch (e) { /* ignore */ }
        if (mode === 'light' || mode === 'dark') document.documentElement.setAttribute('data-theme', mode);
    }

    function showError(message) {
        $('#login-error').removeClass('hidden').text(message);
    }

    function enterSetupMode() {
        setupMode = true;
        $('#login-title').text('Create administrator account');
        $('#login-subtitle').text('No dashboard login exists yet. Create the first administrator account to secure this dashboard.');
        $('#field-name').removeClass('hidden');
        $('#field-confirm').removeClass('hidden');
        $('#login-password').attr('autocomplete', 'new-password');
        $('#login-submit').text('Create account');
        $('#login-note').text('Setup is only available from the local machine. Passwords must be at least 12 characters.');
    }

    function submit() {
        var username = $('#login-username').val().trim();
        var password = $('#login-password').val();
        var name = $('#login-name').val().trim();
        if (!username || !password) { showError('Enter a username and password.'); return; }

        if (setupMode) {
            if (!name) { showError('Enter a display name.'); return; }
            if (password.length < 12) { showError('Passwords must be at least 12 characters.'); return; }
            if (password !== $('#login-confirm').val()) { showError('Passwords do not match.'); return; }
        }

        $('#login-submit').prop('disabled', true);
        $('#login-error').addClass('hidden');
        var payload = setupMode
            ? { name: name, username: username, password: password }
            : { username: username, password: password };
        $.ajax({
            url: setupMode ? '/api/auth/setup' : '/api/auth/login',
            method: 'POST',
            contentType: 'application/json; charset=utf-8',
            dataType: 'json',
            data: JSON.stringify(payload)
        }).done(function () {
            window.location.replace('/');
        }).fail(function (xhr) {
            $('#login-submit').prop('disabled', false);
            var message = 'Request failed (' + xhr.status + ' ' + xhr.statusText + ').';
            try {
                var parsed = JSON.parse(xhr.responseText);
                if (parsed && parsed.error) message = parsed.error;
            } catch (e) { /* non-JSON error body */ }
            if (xhr.status === 429) message = 'Too many attempts. Wait a minute and try again.';
            showError(message);
        });
    }

    $(function () {
        applyStoredTheme();
        $('#login-form').on('submit', function (e) { e.preventDefault(); submit(); });
        $.ajax({ url: '/api/auth/status', method: 'GET', dataType: 'json' })
            .done(function (status) {
                if (status && status.setupRequired) enterSetupMode();
                $('#login-username').trigger('focus');
            })
            .fail(function () {
                showError('Cannot reach the dashboard API. Is the proxy running?');
            });
    });
})(jQuery);