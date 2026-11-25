/* Kaeo LLM Proxy dashboard. jQuery + AJAX only — no frameworks, no build step, no postbacks.
   Every interaction is a JSON call to the /api surface; polling is plain setInterval. */
(function ($) {
    'use strict';

    // ------------------------------------------------------------------ utilities
    var KEY_STORAGE = 'kaeo.dashboard.key';
    var THEME_STORAGE = 'kaeo.dashboard.theme';
    var MAX_LOG_ROWS = 500;

    function esc(value) {
        if (value === null || value === undefined) return '';
        return String(value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function fmt(n) {
        if (n === null || n === undefined) return '—';
        return Number(n).toLocaleString();
    }

    function getPath(obj, path) {
        return path.split('.').reduce(function (o, k) { return o == null ? undefined : o[k]; }, obj);
    }

    function setPath(obj, path, value) {
        var parts = path.split('.');
        var cur = obj;
        for (var i = 0; i < parts.length - 1; i++) {
            if (cur[parts[i]] == null || typeof cur[parts[i]] !== 'object') cur[parts[i]] = {};
            cur = cur[parts[i]];
        }
        cur[parts[parts.length - 1]] = value;
    }

    function prettyJson(text) {
        if (!text) return '';
        try { return JSON.stringify(JSON.parse(text), null, 2); } catch (e) { return text; }
    }

    function getKey() {
        try { return sessionStorage.getItem(KEY_STORAGE) || ''; } catch (e) { return ''; }
    }

    function setKey(key) {
        try {
            if (key) sessionStorage.setItem(KEY_STORAGE, key);
            else sessionStorage.removeItem(KEY_STORAGE);
        } catch (e) { /* storage unavailable — key lives for this page only */ }
    }

    // ------------------------------------------------------------------ toasts
    function toast(message, isError) {
        var $t = $('<div class="toast">').toggleClass('error', !!isError).text(message);
        $('#toasts').append($t);
        setTimeout(function () { $t.fadeOut(300, function () { $t.remove(); }); }, isError ? 8000 : 4000);
    }

    // ------------------------------------------------------------------ modal
    var modalOpen = false;

    function openModal(title, $body, buttons, wide) {
        $('#modal-title').text(title);
        $('#modal-body').empty().append($body);
        $('#modal-error').addClass('hidden').text('');
        var $btns = $('#modal-buttons').empty();
        (buttons || []).forEach(function (b) {
            var $btn = $('<button type="button">').addClass(b.cls || '').text(b.label);
            $btn.on('click', function () {
                if (b.onClick) b.onClick();
                else closeModal();
            });
            $btns.append($btn);
        });
        $('#modal').toggleClass('wide', !!wide);
        $('#modal-backdrop').removeClass('hidden');
        modalOpen = true;
        $('#modal-body').find('input,select,textarea').first().trigger('focus');
    }

    function closeModal() {
        $('#modal-backdrop').addClass('hidden');
        $('#modal-body').empty();
        modalOpen = false;
    }

    function modalError(message) {
        $('#modal-error').removeClass('hidden').text(message);
    }

    function confirmDialog(title, message, onConfirm) {
        openModal(title, $('<p>').text(message), [
            { label: 'Cancel' },
            { label: 'Delete', cls: 'danger', onClick: function () { closeModal(); onConfirm(); } }
        ]);
    }

    // ------------------------------------------------------------------ API key prompt
    var keyPromptQueue = null; // callbacks waiting on the open key modal

    function promptForKey(onOk, onCancel) {
        if (keyPromptQueue) {
            keyPromptQueue.push({ ok: onOk, cancel: onCancel });
            return;
        }
        keyPromptQueue = [{ ok: onOk, cancel: onCancel }];

        var $body = $('<div>')
            .append($('<p class="muted">').text('This dashboard requires an API key. Enter the key configured in WebDashboard.ApiKey.'))
            .append($('<div class="field">').append($('<label>').text('API key'))
                .append($('<input type="password" id="key-input" autocomplete="off">').val(getKey())));

        function finish(save) {
            var queue = keyPromptQueue || [];
            keyPromptQueue = null;
            if (save) setKey($('#key-input').val().trim());
            closeModal();
            queue.forEach(function (q) { (save ? q.ok : q.cancel)(); });
        }

        openModal('Dashboard API key', $body, [
            { label: 'Clear key', onClick: function () { setKey(''); $('#key-input').val(''); } },
            { label: 'Cancel', onClick: function () { finish(false); } },
            { label: 'Save', cls: 'primary', onClick: function () { finish(true); } }
        ]);
        $('#key-input').on('keydown', function (e) { if (e.key === 'Enter') finish(true); });
    }

    // ------------------------------------------------------------------ API helper
    function apiError(xhr) {
        var message = 'Request failed (' + xhr.status + ' ' + xhr.statusText + ')';
        try {
            var parsed = JSON.parse(xhr.responseText);
            if (parsed && parsed.error) message = parsed.error;
        } catch (e) { /* non-JSON error body */ }
        var err = new Error(message);
        err.status = xhr.status;
        return err;
    }

    function api(method, path, body, opts) {
        opts = opts || {};
        return new Promise(function (resolve, reject) {
            function attempt(retried) {
                var headers = {};
                var key = getKey();
                if (key) headers['X-Dashboard-Api-Key'] = key;
                $.ajax({
                    url: path,
                    method: method,
                    headers: headers,
                    contentType: 'application/json; charset=utf-8',
                    data: body === undefined ? undefined : JSON.stringify(body),
                    dataType: 'text' // several endpoints return an empty 200 body
                }).then(function (text) {
                    var parsed = null;
                    if (text) {
                        try { parsed = JSON.parse(text); } catch (e) { parsed = text; }
                    }
                    resolve(parsed);
                }, function (xhr) {
                    if (xhr.status === 401 && !retried) {
                        setConn('key');
                        promptForKey(function () { attempt(true); }, function () { reject(apiError(xhr)); });
                        return;
                    }
                    var err = apiError(xhr);
                    if (!opts.silent) toast(err.message, true);
                    reject(err);
                });
            }
            attempt(false);
        });
    }

    function setConn(state) {
        var $c = $('#conn-state');
        $c.removeClass('ok err warn');
        if (state === 'ok') $c.addClass('ok').text('connected');
        else if (state === 'key') $c.addClass('warn').text('key required');
        else $c.addClass('err').text('offline');
    }

    // ------------------------------------------------------------------ theme
    function applyTheme(mode) {
        if (mode === 'light' || mode === 'dark') document.documentElement.setAttribute('data-theme', mode);
        else document.documentElement.removeAttribute('data-theme');
        $('#btn-theme').text(mode.charAt(0).toUpperCase() + mode.slice(1));
    }

    function cycleTheme() {
        var current = 'auto';
        try { current = localStorage.getItem(THEME_STORAGE) || 'auto'; } catch (e) { /* ignore */ }
        var next = current === 'auto' ? 'light' : current === 'light' ? 'dark' : 'auto';
        try { localStorage.setItem(THEME_STORAGE, next); } catch (e) { /* ignore */ }
        applyTheme(next);
    }

    // ------------------------------------------------------------------ tabs
    var activeTab = 'dashboard';
    var loaders = {};

    function activateTab(name) {
        if (!loaders[name]) name = 'dashboard';
        activeTab = name;
        $('#tabs a').removeClass('active').filter('[data-tab="' + name + '"]').addClass('active');
        $('.tab').removeClass('active');
        $('#tab-' + name).addClass('active');
        loaders[name]();
    }

    // ------------------------------------------------------------------ dashboard tab
    function pill(running, labelOn, labelOff) {
        return $('<span class="pill">').addClass(running ? 'ok' : '').text(running ? labelOn : labelOff);
    }

    function kv(k, v) {
        return $('<div class="kv">').append($('<span class="k">').text(k)).append($('<span class="v">').text(v));
    }

    function refreshDashboard() {
        Promise.all([api('GET', '/api/status', undefined, { silent: true }), api('GET', '/api/stats', undefined, { silent: true })])
            .then(function (results) {
                setConn('ok');
                renderStatus(results[0]);
                renderStats(results[1]);
            })
            .catch(function (err) { setConn(err.status === 401 ? 'key' : 'err'); });
    }

    function renderStatus(s) {
        var $cards = $('#status-cards').empty();

        var $proxy = $('<div class="card">').append($('<h3>').text('Proxy ').append(pill(s.proxy.isRunning, 'running', 'stopped')))
            .append(kv('Listen', s.proxy.listenAddress + ':' + s.proxy.listenPort))
            .append(kv('Version', s.version || '—'));
        var $proxyActions = $('<div class="actions">');
        $('<button type="button" class="primary">').text('Start').prop('disabled', s.proxy.isRunning)
            .on('click', function () { api('POST', '/api/proxy/start').then(refreshDashboard); }).appendTo($proxyActions);
        $('<button type="button" class="danger">').text('Stop').prop('disabled', !s.proxy.isRunning)
            .on('click', function () { api('POST', '/api/proxy/stop').then(refreshDashboard); }).appendTo($proxyActions);
        $proxy.append($proxyActions);
        $cards.append($proxy);

        var $mcp = $('<div class="card">').append($('<h3>').text('MCP server ').append(pill(s.mcp.isRunning, 'running', 'stopped')))
            .append(kv('Status', s.mcp.status || '—'))
            .append(kv('Endpoint', s.mcp.endpointUrl || '—'));
        $cards.append($mcp);

        var enabled = (s.modules || []).filter(function (m) { return m.isEnabled; }).length;
        var running = (s.modules || []).filter(function (m) { return m.isRunning; }).length;
        $cards.append($('<div class="card">').append($('<h3>').text('Modules'))
            .append(kv('Registered', (s.modules || []).length))
            .append(kv('Enabled', enabled))
            .append(kv('Running', running)));

        $cards.append($('<div class="card">').append($('<h3>').text('Dashboard'))
            .append(kv('Auth mode', s.dashboard.authMode))
            .append(kv('Scheme', s.dashboard.scheme)));
    }

    function renderStats(stats) {
        var $wrap = $('#stats-tables').empty();
        var sources = [['proxy', 'Proxy'], ['mcp', 'MCP'], ['nonProxied', 'Non-proxied']];
        var $table = $('<table>').append($('<thead>').append($('<tr>')
            .append('<th>Source</th><th>Requests</th><th>Errors</th><th>Prompt tokens</th><th>Completion tokens</th><th>req/s</th>')));
        var $tbody = $('<tbody>');
        sources.forEach(function (pair) {
            var st = stats[pair[0]] || {};
            $tbody.append($('<tr>')
                .append($('<td>').text(pair[1]))
                .append($('<td class="num">').text(fmt(st.totalRequests)))
                .append($('<td class="num">').text(fmt(st.totalErrors)))
                .append($('<td class="num">').text(fmt(st.totalPromptTokens)))
                .append($('<td class="num">').text(fmt(st.totalCompletionTokens)))
                .append($('<td class="num">').text((st.requestsPerSecond || 0).toFixed(2))));
        });
        $wrap.append($('<div class="table-wrap">').append($table.append($tbody)));
    }

    loaders.dashboard = refreshDashboard;

    // ------------------------------------------------------------------ requests tab
    var logCursors = { proxy: 0, mcp: 0, nonproxied: 0 };
    var logsPaused = false;
    var logBusy = false;
    var STATUS_NAMES = ['Success', 'Error', 'Cancelled'];

    function currentSource() { return $('#log-source').val(); }

    function resetLogsView() {
        logCursors[currentSource()] = 0;
        $('#log-table tbody').empty();
        updateLogCount();
        pollLogs(true);
    }

    function updateLogCount() {
        $('#log-count').text($('#log-table tbody tr').length + ' rows');
    }

    function pollLogs(initial) {
        if (logBusy) return;
        logBusy = true;
        var source = currentSource();
        var url = '/api/logs?source=' + encodeURIComponent(source) + '&after=' + logCursors[source] + '&limit=' + (initial ? 1000 : 200);
        api('GET', url, undefined, { silent: true })
            .then(function (data) {
                logCursors[source] = data.nextAfter || logCursors[source];
                var $tbody = $('#log-table tbody');
                (data.entries || []).forEach(function (entry) { $tbody.append(logRow(entry)); });
                while ($tbody.children().length > MAX_LOG_ROWS) $tbody.children().first().remove();
                updateLogCount();
            })
            .catch(function () { /* transient poll failure — next tick retries */ })
            .then(function () { logBusy = false; });
    }

    function logRow(e) {
        var statusName = STATUS_NAMES[e.status] || String(e.status);
        var $row = $('<tr>')
            .append($('<td>').text(new Date(e.timestamp).toLocaleTimeString()))
            .append($('<td>').text(e.method))
            .append($('<td>').text(e.ollamaPath))
            .append($('<td>').text(e.model || '—'))
            .append($('<td>').append($('<span class="pill">')
                .addClass(e.status === 0 ? 'ok' : e.status === 1 ? 'err' : 'warn')
                .text(e.statusCode + ' ' + statusName)))
            .append($('<td class="num">').text((e.durationMs || 0).toFixed(0)))
            .append($('<td class="num">').text(fmt(e.promptTokens)))
            .append($('<td class="num">').text(fmt(e.completionTokens)))
            .append($('<td class="num">').text((e.tokensPerSecond || 0).toFixed(1)));
        $row.on('click', function () { logDetail(e); });
        return $row;
    }

    function logDetail(e) {
        var $body = $('<div>');
        var $grid = $('<div class="field-grid">');
        [
            ['Request ID', e.requestId], ['Time', new Date(e.timestamp).toLocaleString()],
            ['Method', e.method], ['Path', e.ollamaPath], ['Upstream path', e.upstreamPath],
            ['Model', e.model], ['Original model', e.originalModel],
            ['Status', e.statusCode + ' ' + (STATUS_NAMES[e.status] || e.status)],
            ['Duration', (e.durationMs || 0).toFixed(1) + ' ms'], ['Streaming', e.streaming ? 'yes' : 'no'],
            ['Prompt tokens', fmt(e.promptTokens)], ['Completion tokens', fmt(e.completionTokens)],
            ['Cached prompt', fmt(e.cachedPromptTokens)], ['Reasoning tokens', fmt(e.reasoningTokens)],
            ['Tokens/s', (e.tokensPerSecond || 0).toFixed(2)], ['Stop reason', e.stopReason],
            ['Request bytes', fmt(e.requestBytes)], ['Response bytes', e.responseBytes < 0 ? 'unknown' : fmt(e.responseBytes)],
            ['Client', e.clientAddress], ['User agent', e.userAgent]
        ].forEach(function (pair) {
            if (pair[1] === null || pair[1] === undefined || pair[1] === '') return;
            $grid.append($('<div class="field">').append($('<label>').text(pair[0])).append($('<div>').text(pair[1])));
        });
        $body.append($grid);

        if (e.errorMessage) $body.append($('<h4>').text('Error')).append($('<pre>').text(e.errorMessage));
        if (e.debugSummary) $body.append($('<h4>').text('Proxy overrides')).append($('<pre>').text(e.debugSummary));

        [
            ['Request body', e.requestBody], ['Upstream request body', e.upstreamRequestBody],
            ['Response body', e.responseBody], ['Upstream response body', e.upstreamResponseBody],
            ['Request headers', e.requestHeaders], ['Response headers', e.responseHeaders]
        ].forEach(function (pair) {
            if (!pair[1]) return;
            var $details = $('<details>').append($('<summary>').text(pair[0]));
            $details.append($('<pre>').text(pair[0].indexOf('headers') >= 0 ? pair[1] : prettyJson(pair[1])));
            $body.append($details);
        });

        openModal('Request detail', $body, [{ label: 'Close' }], true);
    }

    loaders.requests = function () { resetLogsView(); };

    // ------------------------------------------------------------------ models tab
    var UPSTREAM_TYPES = [[1, 'OpenAI'], [2, 'Ollama'], [3, 'Anthropic'], [0, 'LlamaCpp (legacy)']];

    function upstreamTypeName(value) {
        for (var i = 0; i < UPSTREAM_TYPES.length; i++) if (UPSTREAM_TYPES[i][0] === value) return UPSTREAM_TYPES[i][1];
        return String(value);
    }

    function loadMappings() {
        api('GET', '/api/mappings').then(function (list) {
            var $tbody = $('#mapping-table tbody').empty();
            if (!list.length) {
                $tbody.append($('<tr>').append($('<td colspan="8" class="muted">').text('No model mappings yet.')));
                return;
            }
            list.forEach(function (m) {
                var $actions = $('<div class="row-actions">');
                $('<button type="button">').text('Edit').on('click', function () { mappingDialog(m); }).appendTo($actions);
                $('<button type="button" class="danger">').text('Delete').on('click', function () {
                    confirmDialog('Delete model', 'Delete model "' + (m.OllamaName || m.id) + '"? This cannot be undone.', function () {
                        api('DELETE', '/api/mappings/' + m.id).then(function () { toast('Model deleted.'); loadMappings(); });
                    });
                }).appendTo($actions);

                $tbody.append($('<tr>')
                    .append($('<td>').append($('<span class="pill">').addClass(m.isEnabled ? 'ok' : '').text(m.isEnabled ? 'yes' : 'no')))
                    .append($('<td>').text(m.OllamaName))
                    .append($('<td>').text(m.LlamaCppName))
                    .append($('<td>').text(m.upstreamUrl))
                    .append($('<td>').text(upstreamTypeName(m.upstreamType)))
                    .append($('<td>').text(m.credentialName || '—'))
                    .append($('<td>').text(m.hidden ? 'yes' : 'no'))
                    .append($('<td>').append($actions)));
            });
        });
    }

    function mappingDialog(existing) {
        var base = existing ? JSON.parse(JSON.stringify(existing)) : {};
        var $body = $('<div>');

        function field(label, $input, hint) {
            var $f = $('<div class="field">').append($('<label>').text(label)).append($input);
            if (hint) $f.append($('<span class="hint">').text(hint));
            $body.append($f);
            return $input;
        }

        var $proxyName = field('Proxy name (client-facing)', $('<input type="text">').val(base.OllamaName || ''), 'The name clients use, e.g. "llama3".');
        var $modelName = field('Upstream model name', $('<input type="text">').val(base.LlamaCppName || ''), 'The model requested from the upstream server.');
        var $url = field('Upstream URL', $('<input type="text">').val(base.upstreamUrl || ''), 'e.g. http://192.168.1.10:8080');
        var $type = field('Upstream type', $('<select>'));
        UPSTREAM_TYPES.forEach(function (pair) {
            $('<option>').val(pair[0]).text(pair[1]).appendTo($type);
        });
        $type.val(existing ? String(base.upstreamType) : '1');
        var $credential = field('Credential name', $('<input type="text" list="credential-names">').val(base.credentialName || ''), 'Optional stored credential used as the bearer key.');
        var $timeout = field('Upstream timeout (seconds)', $('<input type="number" min="1">').val(base.upstreamTimeoutSeconds || 300));
        var $enabled = field('Enabled', $('<input type="checkbox">').prop('checked', base.isEnabled !== false));
        var $hidden = field('Hidden from discovery', $('<input type="checkbox">').prop('checked', !!base.hidden));

        var $advanced = $('<textarea rows="12">').val(JSON.stringify(base, null, 2));
        $body.append($('<details>').append($('<summary>').text('All settings (JSON) — advanced fields preserved on save')).append($advanced));
        $body.append($('<datalist id="credential-names">'));
        api('GET', '/api/credentials', undefined, { silent: true }).then(function (creds) {
            (creds || []).forEach(function (c) { $('<option>').val(c.name).appendTo('#credential-names'); });
        }).catch(function () { /* datalist is a convenience only */ });

        function save() {
            var merged;
            try { merged = JSON.parse($advanced.val() || '{}'); }
            catch (e) { modalError('Advanced JSON is invalid: ' + e.message); return; }
            if (!merged || typeof merged !== 'object' || Array.isArray(merged)) { modalError('Advanced JSON must be an object.'); return; }

            merged.OllamaName = $proxyName.val().trim();
            merged.LlamaCppName = $modelName.val().trim();
            merged.upstreamUrl = $url.val().trim();
            merged.upstreamType = parseInt($type.val(), 10);
            merged.credentialName = $credential.val().trim() || null;
            merged.upstreamTimeoutSeconds = parseInt($timeout.val(), 10) || 300;
            merged.isEnabled = $enabled.prop('checked');
            merged.hidden = $hidden.prop('checked');

            if (!merged.OllamaName) { modalError('Proxy name is required.'); return; }
            if (!merged.LlamaCppName) { modalError('Upstream model name is required.'); return; }
            if (!merged.upstreamUrl) { modalError('Upstream URL is required.'); return; }

            var request = existing
                ? api('PUT', '/api/mappings/' + existing.id, merged)
                : api('POST', '/api/mappings', merged);
            request.then(function () {
                closeModal();
                toast(existing ? 'Model updated.' : 'Model added.');
                loadMappings();
            }).catch(function (err) { modalError(err.message); });
        }

        openModal(existing ? 'Edit model' : 'Add model', $body, [
            { label: 'Cancel' },
            { label: 'Save', cls: 'primary', onClick: save }
        ], true);
    }

    loaders.models = loadMappings;

    // ------------------------------------------------------------------ credentials tab
    function loadCredentials() {
        api('GET', '/api/credentials').then(function (list) {
            var $tbody = $('#credential-table tbody').empty();
            if (!list.length) {
                $tbody.append($('<tr>').append($('<td colspan="5" class="muted">').text('No credentials stored.')));
                return;
            }
            list.forEach(function (c) {
                var $del = $('<button type="button" class="danger">').text('Delete').on('click', function () {
                    confirmDialog('Delete credential', 'Delete credential "' + c.name + '"? Mappings referencing it will lose authentication.', function () {
                        api('DELETE', '/api/credentials/' + encodeURIComponent(c.name)).then(function () { toast('Credential deleted.'); loadCredentials(); });
                    });
                });
                $tbody.append($('<tr>')
                    .append($('<td>').text(c.name))
                    .append($('<td>').text(c.description || '—'))
                    .append($('<td>').text(c.username || '—'))
                    .append($('<td>').append($('<span class="pill">').addClass(c.hasSecretMaterial ? 'ok' : '').text(c.hasSecretMaterial ? 'yes' : 'no')))
                    .append($('<td>').append($del)));
            });
        });
    }

    function credentialDialog() {
        var $body = $('<div>');
        function field(label, $input, hint) {
            var $f = $('<div class="field">').append($('<label>').text(label)).append($input);
            if (hint) $f.append($('<span class="hint">').text(hint));
            $body.append($f);
            return $input;
        }
        var $name = field('Name', $('<input type="text">'), 'Saving with an existing name replaces that credential.');
        var $description = field('Description', $('<input type="text">'));
        var $username = field('Username', $('<input type="text">'));
        var $secret = field('Secret / API key', $('<input type="password" autocomplete="new-password">'), 'Write-only: never returned by the API.');
        var $privateKey = field('Private key (PEM, optional)', $('<textarea rows="4">'));
        var $certificate = field('Certificate (PEM, optional)', $('<textarea rows="4">'));

        function save() {
            var name = $name.val().trim();
            if (!name) { modalError('Credential name is required.'); return; }
            api('POST', '/api/credentials', {
                name: name,
                description: $description.val().trim() || null,
                username: $username.val().trim() || null,
                secret: $secret.val() || null,
                privateKey: $privateKey.val().trim() || null,
                certificate: $certificate.val().trim() || null
            }).then(function () {
                closeModal();
                toast('Credential saved.');
                loadCredentials();
            }).catch(function (err) { modalError(err.message); });
        }

        openModal('Add credential', $body, [
            { label: 'Cancel' },
            { label: 'Save', cls: 'primary', onClick: save }
        ]);
    }

    loaders.credentials = loadCredentials;

    // ------------------------------------------------------------------ modules tab
    function loadModules() {
        api('GET', '/api/modules').then(function (list) {
            var $cards = $('#module-cards').empty();
            if (!list.length) {
                $cards.append($('<p class="muted">').text('No modules registered.'));
                return;
            }
            list.forEach(function (m) {
                var $card = $('<div class="card">')
                    .append($('<h3>').text(m.name + ' ')
                        .append($('<span class="pill">').addClass(m.isRunning ? 'ok' : '').text(m.isRunning ? 'running' : 'stopped'))
                        .append(' ')
                        .append(signatureBadge(m)));
                $card.append(kv('Module ID', m.moduleId))
                    .append(kv('Version', m.version))
                    .append(kv('Enabled', m.isEnabled ? 'yes' : 'no'))
                    .append(kv('Registered', m.registeredUtc ? new Date(m.registeredUtc).toLocaleString() : '—'));
                if (m.signatureState === 'Signed' && m.signerKeyId) $card.append(kv('Signer key', m.signerKeyId));
                if (m.lastError) $card.append($('<div class="banner error">').text(m.lastError));

                var $actions = $('<div class="actions">');
                $('<button type="button">').text(m.isEnabled ? 'Disable' : 'Enable').on('click', function () {
                    api('POST', '/api/modules/' + m.id + '/toggle', { enabled: !m.isEnabled })
                        .then(function () { toast('Module ' + (m.isEnabled ? 'disabled' : 'enabled') + '.'); loadModules(); });
                }).appendTo($actions);
                $('<button type="button">').text('Configure').on('click', function () { moduleConfigDialog(m); }).appendTo($actions);
                $card.append($actions);
                $cards.append($card);
            });
        });
    }

    function signatureBadge(m) {
        var state = m.signatureState || 'Unsigned';
        var cls = state === 'Signed' ? 'ok' : (state === 'Invalid' ? 'err' : 'warn');
        var label = state === 'Signed' ? 'signed' : (state === 'Invalid' ? 'invalid signature' : 'unsigned');
        return $('<span class="pill">').addClass(cls).text(label);
    }

    function uploadModulePackage(file) {
        var form = new FormData();
        form.append('package', file, file.name);
        var headers = {};
        var key = getKey();
        if (key) headers['X-Dashboard-Api-Key'] = key;
        toast('Uploading ' + file.name + '…');
        $.ajax({
            url: '/api/modules/upload',
            method: 'POST',
            headers: headers,
            data: form,
            processData: false,
            contentType: false,
            dataType: 'text'
        }).then(function (text) {
            var result = null;
            try { result = JSON.parse(text); } catch (e) { /* empty body */ }
            toast('Module ' + ((result && result.name) || file.name) + ' imported (' +
                ((result && result.signatureState) || 'Unsigned') + ').');
            loadModules();
        }, function (xhr) {
            if (xhr.status === 401) {
                setConn('key');
                promptForKey(function () { uploadModulePackage(file); }, function () { toast(apiError(xhr).message, true); });
                return;
            }
            toast(apiError(xhr).message, true);
        });
    }

    function moduleConfigDialog(m) {
        api('GET', '/api/modules/' + m.id + '/config', undefined, { silent: true })
            .then(function (config) {
                var $body = $('<div>')
                    .append($('<p class="muted">').text('Edit the module settings as JSON. Invalid JSON or values are rejected by the module.'))
                    .append($('<textarea rows="16">').val(typeof config === 'string' ? config : JSON.stringify(config, null, 2)));
                var $editor = $body.find('textarea');

                function save() {
                    var parsed;
                    try { parsed = JSON.parse($editor.val()); }
                    catch (e) { modalError('Invalid JSON: ' + e.message); return; }
                    api('PUT', '/api/modules/' + m.id + '/config', parsed)
                        .then(function () { closeModal(); toast('Module settings saved.'); })
                        .catch(function (err) { modalError(err.message); });
                }

                openModal('Configure ' + m.name, $body, [
                    { label: 'Cancel' },
                    { label: 'Save', cls: 'primary', onClick: save }
                ], true);
            })
            .catch(function (err) {
                if (err.status === 501) toast('This module does not expose JSON settings yet.', false);
                else toast(err.message, true);
            });
    }

    loaders.modules = loadModules;

    // ------------------------------------------------------------------ users tab
    function loadUsers() {
        api('GET', '/api/users').then(function (list) {
            var $cards = $('#user-cards').empty();
            if (!list.length) {
                $cards.append($('<p class="muted">').text('No users yet. Add a user, then create an API key for them.'));
                return;
            }
            list.forEach(function (u) {
                var $card = $('<div class="card">')
                    .append($('<h3>').text(u.name + ' ')
                        .append($('<span class="pill">').addClass(u.isEnabled ? 'ok' : '').text(u.isEnabled ? 'enabled' : 'disabled')));
                $card.append(kv('Email', u.email || '—'))
                    .append(kv('Created', u.createdUtc ? new Date(u.createdUtc).toLocaleString() : '—'));

                var $keys = $('<div class="user-keys">');
                if (!(u.keys || []).length) {
                    $keys.append($('<p class="muted">').text('No API keys.'));
                } else {
                    u.keys.forEach(function (k) {
                        var $row = $('<div class="kv key-row">');
                        var scope = (k.allowedModels && k.allowedModels.length) ? k.allowedModels.join(', ') : 'all models';
                        $row.append($('<span class="k">').text((k.label || '(unlabeled)') + ' · ' + k.keyPrefix + '…'));
                        $row.append($('<span class="v">').text(scope
                            + ' · last used ' + (k.lastUsedUtc ? new Date(k.lastUsedUtc).toLocaleString() : 'never')));
                        var $keyActions = $('<span class="actions">');
                        $('<button type="button" class="ghost">').text('Edit').on('click', function () {
                            keyDialog(u, k);
                        }).appendTo($keyActions);
                        $('<button type="button" class="ghost danger">').text('Delete').on('click', function () {
                            confirmDialog('Delete API key', 'Delete key "' + (k.label || k.keyPrefix) + '" for ' + u.name + '? Clients using it lose access immediately.', function () {
                                api('DELETE', '/api/keys/' + k.id).then(function () { toast('Key deleted.'); loadUsers(); });
                            });
                        }).appendTo($keyActions);
                        $row.append($keyActions);
                        $keys.append($row);
                    });
                }
                $card.append($keys);

                var $actions = $('<div class="actions">');
                $('<button type="button" class="primary">').text('Create key').on('click', function () { keyDialog(u, null); }).appendTo($actions);
                $('<button type="button">').text('Edit').on('click', function () { userDialog(u); }).appendTo($actions);
                $('<button type="button">').text(u.isEnabled ? 'Disable' : 'Enable').on('click', function () {
                    api('PUT', '/api/users/' + u.id, { isEnabled: !u.isEnabled })
                        .then(function () { toast('User ' + (u.isEnabled ? 'disabled' : 'enabled') + '.'); loadUsers(); });
                }).appendTo($actions);
                $('<button type="button" class="danger">').text('Delete').on('click', function () {
                    confirmDialog('Delete user', 'Delete user "' + u.name + '" and all of their API keys? This cannot be undone.', function () {
                        api('DELETE', '/api/users/' + u.id).then(function () { toast('User deleted.'); loadUsers(); });
                    });
                }).appendTo($actions);
                $card.append($actions);
                $cards.append($card);
            });
        });
    }

    function userDialog(existing) {
        var $body = $('<div>');
        function field(label, $input, hint) {
            var $f = $('<div class="field">').append($('<label>').text(label)).append($input);
            if (hint) $f.append($('<span class="hint">').text(hint));
            $body.append($f);
            return $input;
        }
        var $name = field('Name', $('<input type="text">').val(existing ? existing.name : ''), 'Required — who this account belongs to.');
        var $email = field('Email', $('<input type="email">').val(existing ? existing.email : ''), 'Contact address (optional).');

        function save() {
            var name = $name.val().trim();
            if (!name) { modalError('User name is required.'); return; }
            var payload = { name: name, email: $email.val().trim() };
            var request = existing
                ? api('PUT', '/api/users/' + existing.id, payload)
                : api('POST', '/api/users', payload);
            request.then(function () {
                closeModal();
                toast(existing ? 'User updated.' : 'User added.');
                loadUsers();
            }).catch(function (err) { modalError(err.message); });
        }

        openModal(existing ? 'Edit user' : 'Add user', $body, [
            { label: 'Cancel' },
            { label: 'Save', cls: 'primary', onClick: save }
        ]);
    }

    function keyDialog(user, existing) {
        api('GET', '/api/mappings', undefined, { silent: true }).then(function (mappings) {
            var models = (mappings || [])
                .map(function (m) { return m.OllamaName; })
                .filter(function (n) { return !!n; })
                .sort(function (a, b) { return a.toLowerCase().localeCompare(b.toLowerCase()); });
            var allowed = existing ? (existing.allowedModels || []) : [];

            var $body = $('<div>');
            var $label = $('<input type="text">').val(existing ? existing.label : '');
            $body.append($('<div class="field">').append($('<label>').text('Label')).append($label)
                .append($('<span class="hint">').text('What this key is for, e.g. "laptop" or "CI pipeline".')));

            $body.append($('<div class="field">').append($('<label>').text('Model access'))
                .append($('<span class="hint">').text('Check the models this key may call. Leave everything unchecked for access to all models.')));
            var $models = $('<div class="model-checks">');
            if (!models.length) {
                $models.append($('<p class="muted">').text('No models configured — the key will allow all models.'));
            }
            models.forEach(function (name) {
                var checked = allowed.some(function (a) { return a.toLowerCase() === name.toLowerCase(); });
                var $cb = $('<input type="checkbox">').val(name).prop('checked', checked);
                $models.append($('<label class="check">').append($cb).append($('<span>').text(name)));
            });
            $body.append($models);

            function selectedModels() {
                return $models.find('input:checked').map(function () { return $(this).val(); }).get();
            }

            function save() {
                var payload = { label: $label.val().trim(), allowedModels: selectedModels() };
                if (existing) {
                    api('PUT', '/api/keys/' + existing.id, payload).then(function () {
                        closeModal();
                        toast('Key updated.');
                        loadUsers();
                    }).catch(function (err) { modalError(err.message); });
                } else {
                    api('POST', '/api/users/' + user.id + '/keys', payload).then(function (result) {
                        closeModal();
                        loadUsers();
                        showNewKeyDialog(user, result.plainText);
                    }).catch(function (err) { modalError(err.message); });
                }
            }

            openModal(existing ? 'Edit key' : 'Create API key for ' + user.name, $body, [
                { label: 'Cancel' },
                { label: existing ? 'Save' : 'Create', cls: 'primary', onClick: save }
            ]);
        }).catch(function (err) { toast(err.message, true); });
    }

    function showNewKeyDialog(user, plainText) {
        var $input = $('<input type="text" readonly>').val(plainText).on('focus', function () { this.select(); });
        var $body = $('<div>')
            .append($('<p>').text('Copy this key now — it is stored hashed and cannot be shown again. Send it as "Authorization: Bearer <key>" or "x-api-key".'))
            .append($('<div class="field">').append($('<label>').text('API key for ' + user.name)).append($input));
        openModal('New API key', $body, [
            {
                label: 'Copy', cls: 'primary', onClick: function () {
                    function fallback() {
                        $input.trigger('focus').trigger('select');
                        try { document.execCommand('copy'); toast('Key copied to clipboard.'); }
                        catch (e) { toast('Copy failed — select the text and copy manually.', true); }
                    }
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(plainText).then(function () { toast('Key copied to clipboard.'); }, fallback);
                    } else {
                        fallback();
                    }
                }
            },
            { label: 'Done' }
        ]);
    }

    loaders.users = loadUsers;

    // ------------------------------------------------------------------ settings tab
    var SETTINGS_SCHEMA = [
        {
            title: 'Proxy', fields: [
                { path: 'listenPort', label: 'Listen port', type: 'number', hint: '1–65535. Applies after proxy restart.' },
                { path: 'listenAddress', label: 'Listen address', type: 'text', hint: 'localhost, 0.0.0.0, or a specific IP.' },
                { path: 'autoStartProxy', label: 'Auto-start proxy on launch', type: 'bool' },
                { path: 'maxConcurrentRequests', label: 'Max concurrent requests', type: 'number' },
                { path: 'maxRequestBodyBytes', label: 'Max request body (bytes)', type: 'number' },
                { path: 'enableCors', label: 'Enable CORS', type: 'bool' }
            ]
        },
        {
            title: 'Logging & capture', fields: [
                { path: 'maxLogEntries', label: 'Max in-memory log entries', type: 'number' },
                { path: 'collectRequestDetails', label: 'Collect request details', type: 'bool' },
                { path: 'collectResponseDetails', label: 'Collect response details', type: 'bool' },
                { path: 'debugMode', label: 'Debug mode (raw upstream capture)', type: 'bool' },
                { path: 'hideHeartbeatLogs', label: 'Hide heartbeat logs', type: 'bool' },
                { path: 'logging.logRetentionHours', label: 'Log retention (hours)', type: 'number', readonly: true },
                { path: 'logging.maxLogEntriesWithBodies', label: 'Max entries with bodies', type: 'number', readonly: true }
            ]
        },
        {
            title: 'Streaming & health', fields: [
                { path: 'enableSseKeepAlive', label: 'SSE keep-alive', type: 'bool' },
                { path: 'sseKeepAliveIntervalSeconds', label: 'SSE keep-alive interval (s)', type: 'number' },
                { path: 'heartbeatIntervalSeconds', label: 'Heartbeat interval (s)', type: 'number' }
            ]
        },
        {
            title: 'Diagnostics', fields: [
                { path: 'enablePerformanceSampling', label: 'Performance sampling', type: 'bool' },
                { path: 'enableApiExplorer', label: 'API explorer', type: 'bool' }
            ]
        },
        {
            title: 'Proxy access security', fields: [
                { path: 'proxySecurity.requireApiKey', label: 'Require API key on proxy requests', type: 'bool', hint: 'Every model request must present a valid user API key (Users tab). Health and version probes stay public.' }
            ]
        },
        {
            title: 'Web dashboard (applies after restart)', fields: [
                { path: 'webDashboard.enabled', label: 'Dashboard enabled', type: 'bool' },
                { path: 'webDashboard.port', label: 'Port (0 = proxy port + 1)', type: 'number' },
                { path: 'webDashboard.listenAddress', label: 'Listen address', type: 'text' },
                { path: 'webDashboard.requireApiKey', label: 'Require API key even on loopback', type: 'bool' },
                { path: 'webDashboard.scheme', label: 'Scheme', type: 'select', options: ['Http', 'Https'] },
                { path: 'webDashboard.certificatePath', label: 'HTTPS certificate path (PFX)', type: 'text' },
                { path: 'webDashboard.apiKey', label: 'API key', type: 'password', writeOnly: true, hasPath: 'webDashboard.hasApiKey' },
                { path: 'webDashboard.certificatePassword', label: 'Certificate password', type: 'password', writeOnly: true, hasPath: 'webDashboard.hasCertificatePassword' }
            ]
        }
    ];

    function renderSettings(view) {
        var $form = $('#settings-form').empty();
        SETTINGS_SCHEMA.forEach(function (group) {
            var $group = $('<fieldset class="group">').append($('<legend>').text(group.title));
            var $grid = $('<div class="field-grid">');
            group.fields.forEach(function (f) {
                var value = getPath(view, f.path);
                var $input;
                if (f.type === 'bool') {
                    $input = $('<input type="checkbox">').prop('checked', !!value);
                    var $boolField = $('<div class="field bool">').append($input).append($('<label>').text(f.label));
                    $input.attr('data-path', f.path).prop('disabled', !!f.readonly);
                    $grid.append($boolField);
                    return;
                }
                if (f.type === 'select') {
                    $input = $('<select>');
                    f.options.forEach(function (o) { $('<option>').val(o).text(o).appendTo($input); });
                    $input.val(value || f.options[0]);
                } else if (f.type === 'password') {
                    var has = !!getPath(view, f.hasPath);
                    $input = $('<input type="password" autocomplete="new-password">')
                        .attr('placeholder', has ? '(set — leave blank to keep)' : '(not set)');
                } else {
                    $input = $('<input>').attr('type', f.type === 'number' ? 'number' : 'text')
                        .val(value === null || value === undefined ? '' : value);
                }
                $input.attr('data-path', f.path).prop('disabled', !!f.readonly);
                var $field = $('<div class="field">').append($('<label>').text(f.label)).append($input);
                if (f.hint) $field.append($('<span class="hint">').text(f.hint));
                if (f.readonly) $field.append($('<span class="hint">').text('Read-only here.'));
                $grid.append($field);
            });
            $group.append($grid);
            $form.append($group);
        });
    }

    function loadSettings() {
        $('#settings-error').addClass('hidden');
        api('GET', '/api/settings').then(renderSettings);
    }

    function saveSettings() {
        var payload = {};
        var valid = true;
        $('#settings-form [data-path]').each(function () {
            var $input = $(this);
            if ($input.prop('disabled')) return;
            var path = $input.attr('data-path');
            var field = null;
            SETTINGS_SCHEMA.forEach(function (g) {
                g.fields.forEach(function (f) { if (f.path === path) field = f; });
            });
            if (!field) return;

            if (field.type === 'bool') {
                setPath(payload, path, $input.prop('checked'));
            } else if (field.type === 'password') {
                var secret = $input.val();
                if (secret) setPath(payload, path, secret); // blank = keep existing
            } else if (field.type === 'number') {
                var raw = $input.val();
                if (raw === '') return;
                var num = Number(raw);
                if (!isFinite(num)) { valid = false; return; }
                setPath(payload, path, num);
            } else {
                setPath(payload, path, $input.val());
            }
        });

        if (!valid) {
            $('#settings-error').removeClass('hidden').text('Numeric fields must contain valid numbers.');
            return;
        }

        api('PUT', '/api/settings', payload, { silent: true })
            .then(function (view) {
                $('#settings-error').addClass('hidden');
                toast('Settings saved.');
                renderSettings(view);
            })
            .catch(function (err) { $('#settings-error').removeClass('hidden').text(err.message); });
    }

    loaders.settings = loadSettings;

    // ------------------------------------------------------------------ MCP tab
    function loadMcp() {
        api('GET', '/api/mcp').then(function (m) {
            var $view = $('#mcp-view').empty();
            var $card = $('<div class="card">')
                .append($('<h3>').text('MCP server ').append(pill(m.isRunning, 'running', 'stopped')))
                .append(kv('Status', m.status || '—'))
                .append(kv('Endpoint', m.endpointUrl || '—'))
                .append(kv('Listen', m.listenAddress + ':' + m.listenPort));
            var s = m.settings || {};
            $card.append(kv('Enabled', s.enabled ? 'yes' : 'no'))
                .append(kv('API explorer', s.enableApiExplorer ? 'yes' : 'no'))
                .append(kv('Auth credential', s.authCredentialName || 'none'))
                .append(kv('Collect request details', s.collectRequestDetails ? 'yes' : 'no'))
                .append(kv('Collect response details', s.collectResponseDetails ? 'yes' : 'no'));
            $view.append($('<div class="cards">').append($card));

            $view.append($('<h2>').text('Tool modules'));
            var $tools = $('<div class="cards">');
            if (!(m.toolModules || []).length) {
                $tools.append($('<p class="muted">').text('No loaded module exposes MCP tools.'));
            } else {
                m.toolModules.forEach(function (t) {
                    $tools.append($('<div class="card">').append($('<h3>').text(t.name)).append(kv('Registry ID', t.id)));
                });
            }
            $view.append($tools);
        });
    }

    loaders.mcp = loadMcp;

    // ------------------------------------------------------------------ polling
    var lastStatusPoll = 0;
    var lastLogPoll = 0;

    setInterval(function () {
        if (document.hidden) return;
        var now = Date.now();
        if (activeTab === 'dashboard' && now - lastStatusPoll >= 3000) {
            lastStatusPoll = now;
            refreshDashboard();
        }
        if (activeTab === 'requests' && !logsPaused && now - lastLogPoll >= 2000) {
            lastLogPoll = now;
            pollLogs(false);
        }
    }, 500);

    // ------------------------------------------------------------------ wiring
    $(function () {
        var savedTheme = 'auto';
        try { savedTheme = localStorage.getItem(THEME_STORAGE) || 'auto'; } catch (e) { /* ignore */ }
        applyTheme(savedTheme);

        $('#tabs a').on('click', function (e) {
            e.preventDefault();
            var name = $(this).data('tab');
            if (location.hash !== '#' + name) location.hash = name;
            else activateTab(name);
        });
        $(window).on('hashchange', function () { activateTab(location.hash.replace('#', '')); });

        $('#btn-theme').on('click', cycleTheme);
        $('#btn-key').on('click', function () { promptForKey(function () { toast('API key saved.'); loaders[activeTab](); }, function () { }); });

        $('#modal-close').on('click', closeModal);
        $('#modal-backdrop').on('click', function (e) { if (e.target === this) closeModal(); });
        $(document).on('keydown', function (e) { if (e.key === 'Escape' && modalOpen) closeModal(); });

        $('#log-source').on('change', resetLogsView);
        $('#btn-pause').on('click', function () {
            logsPaused = !logsPaused;
            $(this).text(logsPaused ? 'Resume' : 'Pause');
        });
        $('#btn-clear-logs').on('click', function () { $('#log-table tbody').empty(); updateLogCount(); });

        $('#btn-add-mapping').on('click', function () { mappingDialog(null); });
        $('#btn-refresh-mappings').on('click', loadMappings);
        $('#btn-add-credential').on('click', credentialDialog);
        $('#btn-refresh-credentials').on('click', loadCredentials);
        $('#btn-refresh-modules').on('click', loadModules);
        $('#btn-upload-module').on('click', function () { $('#module-package-input').trigger('click'); });
        $('#module-package-input').on('change', function () {
            var file = this.files && this.files[0];
            this.value = '';
            if (file) uploadModulePackage(file);
        });
        $('#btn-add-user').on('click', function () { userDialog(null); });
        $('#btn-refresh-users').on('click', loadUsers);
        $('#btn-save-settings').on('click', saveSettings);
        $('#btn-reload-settings').on('click', loadSettings);
        $('#settings-form').on('submit', function (e) { e.preventDefault(); saveSettings(); });
        $('#btn-refresh-mcp').on('click', loadMcp);

        activateTab(location.hash.replace('#', '') || 'dashboard');
    });
})(jQuery);