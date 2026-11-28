/* Kaeo LLM Proxy dashboard. jQuery + AJAX only — no frameworks, no build step, no postbacks.
   Every interaction is a JSON call to the /api surface; polling is plain setInterval. */
(function ($) {
    'use strict';

    // ------------------------------------------------------------------ utilities
    var THEME_STORAGE = 'kaeo.dashboard.theme';
    var MAX_LOG_ROWS = 500;

    // Both log views default to manual refresh; the chosen interval is remembered per view so a
    // reload of the page keeps the cadence the user picked.
    var REFRESH_STORAGE = { requests: 'kaeo.dashboard.refresh.requests', syslog: 'kaeo.dashboard.refresh.syslog' };

    function readRefreshSeconds(key) {
        try {
            var value = parseInt(localStorage.getItem(key), 10);
            return isFinite(value) && value > 0 ? value : 0;
        } catch (e) { return 0; }
    }

    function writeRefreshSeconds(key, seconds) {
        try { localStorage.setItem(key, String(seconds)); } catch (e) { /* ignore */ }
    }

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

    // ------------------------------------------------------------------ toasts
    function toast(message, isError) {
        var $t = $('<div class="toast">').toggleClass('error', !!isError).text(message);
        $('#toasts').append($t);
        setTimeout(function () { $t.fadeOut(300, function () { $t.remove(); }); }, isError ? 8000 : 4000);
    }

    // ------------------------------------------------------------------ modal
    var modalOpen = false;
    var modalOnClose = null;

    function openModal(title, $body, buttons, wide, onClose) {
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
        modalOnClose = onClose || null;
        $('#modal-body').find('input,select,textarea').first().trigger('focus');
    }

    function closeModal() {
        $('#modal-backdrop').addClass('hidden');
        $('#modal-body').empty();
        modalOpen = false;
        // Runs for every close path (button, ×, backdrop, Escape) so callers can undo side effects
        // such as the selected-row highlight.
        var onClose = modalOnClose;
        modalOnClose = null;
        if (onClose) onClose();
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
            $.ajax({
                url: path,
                method: method,
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
                if (xhr.status === 401) {
                    // Session expired or revoked — the whole GUI lives behind the login page.
                    window.location.replace('/login.html');
                    reject(apiError(xhr));
                    return;
                }
                var err = apiError(xhr);
                if (!opts.silent) toast(err.message, true);
                reject(err);
            });
        });
    }

    function setConn(state) {
        var $c = $('#conn-state');
        $c.removeClass('ok err warn');
        if (state === 'ok') $c.addClass('ok').text('connected');
        else if (state === 'key') $c.addClass('warn').text('sign-in required');
        else if (state === 'signedin') $c.addClass('warn').text('signed in');
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
    var capabilities = { Admin: true };
    var currentIdentity = null;
    // Which API documents this instance publishes, from /api/auth/me. Refreshed after a settings
    // save because the two toggles live on the Settings tab.
    var explorers = {};
    var loaders = {};

    // Tabs are gated by capability, not by a single role: a user may hold several roles at once and
    // the server decides what the union allows. The list is ordered as the nav shows it.
    var TAB_CAPABILITY = {
        dashboard: 'ViewDashboard',
        requests: 'ViewLogs',
        syslog: 'Admin',
        usage: 'ViewUsage',
        models: ['ManageModels', 'ViewModels', 'ViewOwnModels'],
        credentials: 'ManageSettings',
        users: 'ManageUsers',
        settings: 'ManageSettings',
        mcp: 'ViewDashboard',
        testconsole: 'TestConsole',
        // Help is always reachable; its own topics are filtered by capability instead.
        help: null
    };

    function can(capability) { return !!capabilities[capability]; }

    // A tab may list several acceptable capabilities; holding any one of them shows the tab.
    function canAny(needed) {
        if (!needed) return true;
        if (typeof needed === 'string') return can(needed);
        return needed.some(can);
    }

        // Mirrors DashboardRoles.All on the server. /api/auth/roles replaces this at boot; the copy is
            // only a fallback so the user editor still renders if that call fails.
            var DASHBOARD_ROLES = [
                { key: 'admin', label: 'Admin', description: 'Everything, including all models and MCP tools.' },
                { key: 'readOnly', label: 'Read Only', description: 'View request logs and usage.' },
                { key: 'modelsReadOnly', label: 'Models Read Only', description: 'Read Only plus the Models tab in read-only form, listing every model.' },
                { key: 'ownModelsReadOnly', label: 'Own Models Read Only', description: 'Read Only plus the Models tab in read-only form, limited to the models granted to this user.' },
                { key: 'dashboardReadOnly', label: 'Dashboard Read Only', description: 'View the dashboard and status.' },
                { key: 'dashboardReadWrite', label: 'Dashboard Read-Write', description: 'View the dashboard and use the start/stop/restart controls.' },
                { key: 'userAdmin', label: 'User Admin', description: 'Read Only plus add, update and remove users and permissions.' },
                { key: 'modelAdmin', label: 'Model Admin', description: 'Read Only plus add, update and remove models.' },
                { key: 'testConsole', label: 'Test Console', description: 'Use the Test Console with the models this user can access.' },
                { key: 'registrationReadOnly', label: 'Registration Read Only', description: 'The model registration API in read-only form, for a service account API key. No dashboard tabs.' },
                { key: 'registrationReadWrite', label: 'Registration Read-Write', description: 'Registration Read Only plus registering, updating and removing models by API key. No dashboard tabs.' }
            ];

    function setIdentity(identity) {
        currentIdentity = identity || null;
        capabilities = {};
        ((currentIdentity && currentIdentity.capabilities) || []).forEach(function (name) { capabilities[name] = true; });
        explorers = (currentIdentity && currentIdentity.explorers) || {};
        applyDashboardRole();
        var loginRequired = !currentIdentity || currentIdentity.loginRequired !== false;
        $('#btn-signin').toggle(loginRequired)
            .text(currentIdentity && currentIdentity.username ? 'Sign out' : 'Sign in')
            .attr('title', currentIdentity && currentIdentity.username ? 'Signed in as ' + currentIdentity.username : 'Sign in to the dashboard');

        // Connection state comes from /api/status, which needs Dashboard Read Only. Without that role
        // the pill says so instead of sitting on a placeholder forever.
        $('#conn-state').attr('title', can('ViewDashboard')
            ? 'API connection state'
            : 'Signed in. Connection state needs the Dashboard Read Only role.');
        if (!can('ViewDashboard')) setConn('signedin');
    }

    function refreshIdentity() {
        return api('GET', '/api/auth/me', undefined, { silent: true })
            .then(setIdentity)
            .catch(function () { /* keep the identity already on screen */ });
    }

    function applyDashboardRole() {
        applyLogSourceRole();

        var allowed = [];
        $('#tabs a').each(function () {
            var $a = $(this);
            var needed = TAB_CAPABILITY[$a.attr('data-tab')];
            var visible = canAny(needed);
            $a.toggle(visible);
            if (visible) allowed.push($a.attr('data-tab'));
        });
        $('#topbar').toggleClass('read-only', !can('DashboardControl'));

        // Model writes belong to the Model Admin; the read-only model roles see the table alone.
        $('#btn-add-mapping, #btn-remove-mapping, #btn-duplicate-mapping, #btn-configure-mapping, ' +
            '#btn-toggle-mapping, #btn-discover-models').toggle(can('ManageModels'));

        if (allowed.length && allowed.indexOf(activeTab) < 0) {
            var next = allowed.indexOf('requests') >= 0 ? 'requests' : allowed[0];
            location.hash = '#' + next;
            activateTab(next);
        }
    }

    function activateTab(name) {
        if (!canAny(TAB_CAPABILITY[name])) {
            name = can('ViewLogs') ? 'requests' : 'dashboard';
        }
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

    // The proxy and the MCP server listen on their own ports, so their explorers need absolute
    // URLs. A wildcard listen address means every interface; the host this page was reached on is
    // the one that will answer, so it is used instead.
    function listenHost(address) {
        var host = (address || '').trim();
        if (!host || host === '0.0.0.0' || host === '::' || host === '[::]' || host === '*') {
            host = location.hostname || 'localhost';
        }
        if (host.indexOf(':') >= 0 && host.charAt(0) !== '[') host = '[' + host + ']';
        return host;
    }

    function explorerUrl(explorer, path) {
        if (!explorer || !explorer.listenPort) return '';
        return 'http://' + listenHost(explorer.listenAddress) + ':' + explorer.listenPort + (path || '');
    }

    function explorerLink(href, text, live) {
        if (!href) return $();
        if (live) {
            return $('<a>').attr('href', href).attr('target', '_blank').attr('rel', 'noopener noreferrer').text(text);
        }
        return $('<span class="explorer-off">').text(text + ' (server stopped)');
    }

    // A Help link either names a fixed href or one of the published API documents, in which case
    // it renders only while that document is actually being served.
    function helpLinkHref(link) {
        if (!link.explorer) return link.href || null;
        var explorer = explorers[link.explorer] || {};
        if (!explorer.enabled) return null;
        if (link.explorer === 'registration') return explorer.path || null;
        return explorerUrl(explorer, link.path || '') || null;
    }

    function refreshDashboard() {
        Promise.all([api('GET', '/api/status', undefined, { silent: true }), api('GET', '/api/stats', undefined, { silent: true })])
            .then(function (results) {
                setConn('ok');
                renderStatus(results[0]);
                renderStats(results[1]);
            })
            .catch(function (err) {
                // 401 = no session; 403 = signed in but the role cannot read status, which is not
                // the same thing as the proxy being unreachable.
                setConn(err.status === 401 ? 'key' : err.status === 403 ? 'signedin' : 'err');
            });
    }

    function renderStatus(s) {
        // The status payload repeats the API-document state so a toggle flipped in the Windows app
        // reaches this page on the next poll instead of needing a reload.
        if (s.explorers) explorers = s.explorers;

        var $cards = $('#status-cards').empty();

        var $proxy = $('<div class="card">').append($('<h3>').text('Proxy ').append(pill(s.proxy.isRunning, 'running', 'stopped')))
            .append(kv('Listen', s.proxy.listenAddress + ':' + s.proxy.listenPort))
            .append(kv('Version', s.version || '—'));
        var $proxyActions = $('<div class="actions">');
        $('<button type="button" class="primary">').text('Start').prop('disabled', s.proxy.isRunning)
            .on('click', function () { api('POST', '/api/proxy/start').then(refreshDashboard); }).appendTo($proxyActions);
        $('<button type="button" class="danger">').text('Stop').prop('disabled', !s.proxy.isRunning)
            .on('click', function () { api('POST', '/api/proxy/stop').then(refreshDashboard); }).appendTo($proxyActions);
        $('<button type="button">').text('Restart').prop('disabled', !s.proxy.isRunning)
            .on('click', function () { api('POST', '/api/proxy/restart').then(refreshDashboard); }).appendTo($proxyActions);
        if (s.proxy.error) $proxy.append($('<p class="card-error">').text(s.proxy.error));
        var proxyExplorer = explorers.proxy || {};
        if (proxyExplorer.enabled) {
            $proxy.append($('<p class="card-links">')
                .append(explorerLink(explorerUrl(proxyExplorer, proxyExplorer.scalarPath), 'API explorer', s.proxy.isRunning))
                .append(explorerLink(explorerUrl(proxyExplorer, proxyExplorer.specPath), 'OpenAPI spec', s.proxy.isRunning)));
        }
        if (can('DashboardControl')) $proxy.append($proxyActions);
        $cards.append($proxy);

        var $mcp = $('<div class="card">').append($('<h3>').text('MCP server ').append(pill(s.mcp.isRunning, 'running', 'stopped')))
            .append(kv('Status', s.mcp.status || '—'))
            .append(kv('Endpoint', s.mcp.endpointUrl || '—'));
        var $mcpActions = $('<div class="actions">');
        $('<button type="button" class="primary">').text('Start').prop('disabled', s.mcp.isRunning)
            .on('click', function () { api('POST', '/api/mcp/start').then(refreshDashboard); }).appendTo($mcpActions);
        $('<button type="button" class="danger">').text('Stop').prop('disabled', !s.mcp.isRunning)
            .on('click', function () { api('POST', '/api/mcp/stop').then(refreshDashboard); }).appendTo($mcpActions);
        $('<button type="button">').text('Restart').prop('disabled', !s.mcp.isRunning)
            .on('click', function () { api('POST', '/api/mcp/restart').then(refreshDashboard); }).appendTo($mcpActions);
        var mcpExplorer = explorers.mcp || {};
        if (mcpExplorer.enabled) {
            $mcp.append($('<p class="card-links">')
                .append(explorerLink(explorerUrl(mcpExplorer, mcpExplorer.scalarPath), 'API explorer', s.mcp.isRunning))
                .append(explorerLink(explorerUrl(mcpExplorer, mcpExplorer.specPath), 'OpenAPI spec', s.mcp.isRunning)));
        }
        if (can('DashboardControl')) $mcp.append($mcpActions);
        $cards.append($mcp);

        // The dashboard owns this listener, so Stop and Restart take this page down with them.
        var web = s.dashboard || {};
        var $web = $('<div class="card">').append($('<h3>').text('Web server ').append(pill(web.isRunning, 'running', 'stopped')))
            .append(kv('Status', web.status || '—'))
            .append(kv('URL', web.url || '—'))
            .append(kv('Listen', (web.listenAddress || 'localhost') + ':' + (web.listenPort || '—')))
            .append(kv('Certificate', web.certificateCredentialName || '—'));
        var $webActions = $('<div class="actions">');
        $('<button type="button" class="primary">').text('Start').prop('disabled', web.isRunning || !web.canControl)
            .on('click', function () { api('POST', '/api/webdashboard/start').then(refreshDashboard); }).appendTo($webActions);
        $('<button type="button" class="danger">').text('Stop').prop('disabled', !web.isRunning)
            .on('click', function () {
                if (!window.confirm('Stop the web server? This page will stop responding and must be restarted from the Windows app or the headless console.')) return;
                api('POST', '/api/webdashboard/stop')
                    .then(function () { toast('Web server stopping…'); })
                    .catch(function () { /* the listener drops before the reply is read */ });
            }).appendTo($webActions);
        $('<button type="button">').text('Restart').prop('disabled', !web.isRunning || !web.canControl)
            .on('click', function () {
                api('POST', '/api/webdashboard/restart')
                    .then(function () { toast('Web server restarting…'); })
                    .catch(function () { /* the listener drops before the reply is read */ });
            }).appendTo($webActions);
        if (can('DashboardControl')) $web.append($webActions);
        if (web.error) $web.append($('<p class="card-error">').text(web.error));
        $cards.append($web);

        // The registration document is served by this dashboard, so its link is a plain relative
        // path. It is only reported for roles that can read it.
        var registration = explorers.registration || {};
        if (registration.enabled) {
            $cards.append($('<div class="card">').append($('<h3>').text('Registration API'))
                .append(kv('Document', registration.path))
                .append($('<p class="card-links">').append(explorerLink(registration.path, 'OpenAPI document', true)))
                .append($('<p class="muted">').text('Service-account keys drive /api/mappings from a script. Switch the document off on the Settings tab.')));
        }

        var perf = s.performance || {};
        $cards.append($('<div class="card">').append($('<h3>').text('Performance'))
            .append(kv('CPU', (perf.cpuPercent || 0).toFixed(1) + ' %'))
            .append(kv('Memory', (perf.memoryMb || 0).toFixed(1) + ' MB'))
            .append(kv('GC gen2', fmt(perf.gen2Collections)))
            .append(kv('Large object heap', (perf.lohMb || 0).toFixed(1) + ' MB')));

        var enabled = (s.modules || []).filter(function (m) { return m.isEnabled; }).length;
        var running = (s.modules || []).filter(function (m) { return m.isRunning; }).length;
        $cards.append($('<div class="card">').append($('<h3>').text('Modules'))
            .append(kv('Registered', (s.modules || []).length))
            .append(kv('Enabled', enabled))
            .append(kv('Running', running)));

        $cards.append($('<div class="card">').append($('<h3>').text('Dashboard'))
            .append(kv('Auth mode', s.dashboard.authMode))
            .append(kv('Scheme', s.dashboard.scheme))
            .append(kv('Database', s.databasePath || '—')));
        if (s.databaseDegraded) {
            $cards.append($('<p class="card-error">')
                .text('The database could not be opened, so this instance is running against a private in-memory store. Changes will not survive a restart.'));
        }
    }

    function renderStats(stats) {
        var $wrap = $('#stats-tables').empty();
        var sources = [['proxy', 'Proxy'], ['mcp', 'MCP'], ['web', 'Web'], ['nonProxied', 'Non-proxied']];
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
    var logCursors = { proxy: 0, mcp: 0, web: 0, nonproxied: 0 };
    var logLoaded = { proxy: false, mcp: false, web: false, nonproxied: false };
    var renderedLogSource = null;
    var logGeneration = 0;
    var logsPaused = false;
    var logBusy = false;
    var STATUS_NAMES = ['Success', 'Error', 'Cancelled'];

    function currentSource() { return $('#log-source').val(); }

    // Non-Admin sessions only see their own proxy requests; MCP/non-proxied traffic is not
    // attributed to API-key users, so those sources stay Admin-only.
    function applyLogSourceRole() {
        var $source = $('#log-source');
        var keep = $source.val() || 'proxy';
        $source.empty();
        [['proxy', 'Proxy'], ['mcp', 'MCP'], ['web', 'Web'], ['nonproxied', 'Non-proxied']].forEach(function (s) {
            if (!can('Admin') && s[0] !== 'proxy') return;
            $source.append($('<option>').val(s[0]).text(s[1]));
        });
        $source.val(keep);
        if (!$source.val()) $source.val('proxy');
    }

    function resetLogsView() {
        var source = currentSource();
        renderedLogSource = source;
        logGeneration++;
        logCursors[source] = 0;
        logLoaded[source] = false;
        $('#log-table tbody').empty();
        updateLogCount();
        pollLogs(true);
    }

    function loadRequestsView() {
        var source = currentSource();
        if (renderedLogSource !== source) resetLogsView();
        else if (logLoaded[source]) pollLogs(false);
        else pollLogs(true);
        loadStreams();
    }

    function updateLogCount() {
        $('#log-count').text($('#log-table tbody tr:not(.hidden)').length + ' rows');
    }

    // The client filter hides loaded rows rather than re-querying: the tag is derived from the
    // User-Agent at serialization time, so no server-side filter exists to ask for.
    function clientFilter() { return $('#log-client').val() || ''; }

    function applyClientFilter() {
        var wanted = clientFilter();
        $('#log-table tbody tr').each(function () {
            var tag = $(this).attr('data-client') || '';
            $(this).toggleClass('hidden', !!wanted && tag !== wanted);
        });
        updateLogCount();
    }

    function pollLogs(initial) {
        if (logBusy) return;
        logBusy = true;
        var source = currentSource();
        var generation = logGeneration;
        // Fresh load omits `after`: the server answers newest-first from the database. Polls send
        // the cursor and preserve the already-rendered rows.
        var url = '/api/logs?source=' + encodeURIComponent(source) + '&limit=' + (initial ? MAX_LOG_ROWS : 200);
        if (!initial) url += '&after=' + logCursors[source];
        api('GET', url, undefined, { silent: true })
            .then(function (data) {
                if (generation !== logGeneration || source !== currentSource()) return;
                logCursors[source] = data.nextAfter || logCursors[source];
                var $tbody = $('#log-table tbody');
                var entries = data.entries || [];
                if (initial) {
                    entries.forEach(function (entry) { $tbody.append(logRow(entry)); });
                    logLoaded[source] = true;
                } else {
                    // Polls answer oldest-first. Prepending them in a reverse loop left each batch in
                    // that same ascending order while stacking every newer batch on top, so the table
                    // alternated between time blocks instead of reading newest-to-oldest. Merge the
                    // batch and re-sort by date and time instead of relying on arrival order.
                    entries.forEach(function (entry) { $tbody.append(logRow(entry)); });
                    sortLogRows($tbody);
                }
                while ($tbody.children().length > MAX_LOG_ROWS) $tbody.children().last().remove();
                updateLogCount();
            })
            .catch(function () { /* transient poll failure — next tick retries */ })
            .then(function () {
                logBusy = false;
                var activeSource = currentSource();
                if (activeTab === 'requests' && !logLoaded[activeSource]
                    && (source !== activeSource || generation !== logGeneration))
                    pollLogs(true);
            });
    }

    function logRow(e) {
        var statusName = STATUS_NAMES[e.status] || String(e.status);
        var timestamp = new Date(e.timestamp);
        var dateText = (timestamp.getMonth() + 1) + '/' + String(timestamp.getDate()).padStart(2, '0');
        var $row = $('<tr>')
            .append($('<td>').text(dateText))
            .append($('<td>').text(timestamp.toLocaleTimeString()))
            .append($('<td>').text(e.requestor || ''))
            .append($('<td>').text(e.method))
            .append($('<td>').text(e.ollamaPath))
            .append($('<td class="truncate log-model">').text(e.model || '—'))
            .append($('<td>').append($('<span class="pill">')
                .addClass(e.status === 0 ? 'ok' : e.status === 1 ? 'err' : 'warn')
                .text(e.statusCode + ' ' + statusName)))
            .append($('<td class="num">').text((e.durationMs || 0).toFixed(0)))
            .append($('<td class="num">').text(fmt(e.promptTokens)))
            .append($('<td class="num">').text(fmt(e.completionTokens)))
            .append($('<td class="num">').text((e.tokensPerSecond || 0).toFixed(1)));
        $row.attr('data-client', e.clientTag || '');
        // Sort keys for sortLogRows: epoch milliseconds, plus the request id the server breaks ties on.
        $row.attr('data-ticks', String(timestamp.getTime()));
        $row.attr('data-req', e.requestId || '');
        if (clientFilter() && clientFilter() !== (e.clientTag || '')) $row.addClass('hidden');
        $row.on('click', function () { logDetail(e); });
        return $row;
    }

    // Newest first by date and time, then by request id, matching the server's own ordering so a
    // polled batch can be merged into already-rendered rows without scrambling the sequence.
    function sortLogRows($tbody) {
        var rows = $tbody.children().toArray();
        rows.sort(function (a, b) {
            var diff = (+b.getAttribute('data-ticks')) - (+a.getAttribute('data-ticks'));
            if (diff !== 0) return diff;
            var left = a.getAttribute('data-req') || '', right = b.getAttribute('data-req') || '';
            return left < right ? 1 : left > right ? -1 : 0;
        });
        $tbody.append(rows);
    }

    function logDetail(e) {
        // List rows are summaries; bodies/headers live in the store and load on demand.
        openModal('Request detail', $('<p class="muted">').text('Loading full entry\u2026'), [{ label: 'Close' }], true);
        var url = '/api/logs/detail?source=' + encodeURIComponent(currentSource()) + '&timestamp=' + encodeURIComponent(e.timestamp);
        api('GET', url, undefined, { silent: true })
            .then(function (full) { renderLogDetail(full && full.requestId ? full : e); })
            .catch(function () { renderLogDetail(e); });
    }

    function renderLogDetail(e) {
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
            ['Client', e.clientAddress], ['Client app', e.clientTag], ['User agent', e.userAgent]
        ].forEach(function (pair) {
            if (pair[1] === null || pair[1] === undefined || pair[1] === '') return;
            $grid.append($('<div class="field">').append($('<label>').text(pair[0])).append($('<div>').text(pair[1])));
        });
        $body.append($grid);

        if (e.errorMessage) $body.append($('<h4>').text('Error')).append($('<pre>').text(e.errorMessage));
        // Not only overrides: the same summary carries transformation notes (system-message merge,
        // dropped undeclared tool calls), so the heading names the whole audit trail.
        if (e.debugSummary) $body.append($('<h4>').text('Proxy notes')).append($('<pre>').text(e.debugSummary));

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

        $('#modal-body').empty().append($body);
    }

    loaders.requests = loadRequestsView;

        // ------------------------------------------------------------------ live streams
        var streamCache = [];
        var streamsBusy = false;
        var selectedStreamId = null;

        function streamStateClass(state) {
            return state === 'Streaming' ? 'ok' : state === 'Waiting' ? 'warn' : '';
        }

        function loadStreams() {
            if (streamsBusy) return;
            streamsBusy = true;
            api('GET', '/api/streams', undefined, { silent: true })
                .then(function (data) {
                    var live = (data && data.live) || [];
                    var history = (data && data.history) || [];
                    streamCache = live.concat(history);
                    renderStreams(live.length, history.length);
                })
                .catch(function () { /* transient poll failure — next tick retries */ })
                .then(function () { streamsBusy = false; });
        }

        function renderStreams(liveCount, historyCount) {
            var $tbody = $('#stream-table tbody').empty();
            $('#stream-count').text(streamCache.length
                ? liveCount + ' live, ' + historyCount + ' recent'
                : 'none');
            if (!streamCache.length) {
                $tbody.append($('<tr>').append($('<td colspan="9" class="muted">')
                    .text('No streams in flight and none finished in the last 15 minutes.')));
                return;
            }
            streamCache.forEach(function (s) {
                $tbody.append($('<tr>')
                    .attr('data-id', s.requestId)
                    .toggleClass('selected', s.requestId === selectedStreamId)
                    .append($('<td>').append($('<span class="pill">').addClass(streamStateClass(s.state)).text(s.state)))
                    .append($('<td>').text(new Date(s.startedUtc).toLocaleTimeString()))
                    .append($('<td>').text(s.kind))
                    .append($('<td>').text(s.method))
                    .append($('<td>').text(s.path))
                    .append($('<td>').text(s.model || '\u2014'))
                    .append($('<td class="num">').text(fmt(s.upstreamBytes)))
                    .append($('<td class="num">').text(fmt(s.clientBytes)))
                    .append($('<td class="num">').text(fmt(s.frameCount))));
            });
        }

        // The list omits the captured tails (up to 64 KB per stream); they load in the dialog on row click.
        function openStreamDetail(id) {
            selectedStreamId = id;
            $('#stream-table tbody tr[data-id]').each(function () {
                $(this).toggleClass('selected', $(this).attr('data-id') === id);
            });

            var stream = null;
            for (var i = 0; i < streamCache.length; i++) if (streamCache[i].requestId === id) { stream = streamCache[i]; break; }

            var $raw = $('<pre>').text('Loading\u2026');
            var $forwarded = $('<pre>').text('Loading\u2026');
            var $body = $('<div class="stream-panes">')
                .append($('<div>').append($('<h4>').text('Raw upstream tail')).append($raw))
                .append($('<div>').append($('<h4>').text('Forwarded to client')).append($forwarded));
            if (stream) {
                $body.prepend($('<p class="muted">').text(
                    stream.kind + ' \u00b7 ' + stream.method + ' ' + stream.path + ' \u00b7 ' + (stream.model || 'no model')));
            }

            openModal('Stream detail', $body, [{ label: 'Close' }], true, function () {
                // Clear the highlight however the dialog was dismissed, then repaint the rows.
                selectedStreamId = null;
                $('#stream-table tbody tr[data-id]').removeClass('selected');
            });

            api('GET', '/api/streams/detail?id=' + encodeURIComponent(id), undefined, { silent: true })
                .then(function (s) {
                    $raw.text(s.rawUpstreamText || '(no upstream bytes captured yet)');
                    $forwarded.text(s.forwardedText || '(nothing forwarded to the client yet)');
                })
                .catch(function (err) {
                    $raw.text('');
                    $forwarded.text('');
                    modalError(err.message);
                });
        }

        // ------------------------------------------------------------------ sys log tab
        var SYSLOG_LEVELS = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'];
        var SYSLOG_MAX_ROWS = 1000;
        var syslogBusy = false;

        function selectedSyslogLevels() {
            return $('#syslog-levels input:checked').map(function () { return this.value; }).get();
        }

        function loadSyslog() {
            if (syslogBusy) return;
            var levels = selectedSyslogLevels();
            if (!levels.length) {
                $('#syslog-table tbody').empty();
                $('#syslog-count').text('0 rows');
                $('#syslog-health').text('No levels selected — tick at least one level to load entries.');
                return;
            }
            syslogBusy = true;
            var url = '/api/syslog?limit=' + SYSLOG_MAX_ROWS
                + '&hideHeartbeats=' + ($('#syslog-hide-heartbeats').prop('checked') ? 'true' : 'false');
            // Sending every level would be the same as no filter; only narrow when the user narrowed.
            if (levels.length < SYSLOG_LEVELS.length) url += '&level=' + encodeURIComponent(levels.join(','));
            var search = $.trim($('#syslog-search').val() || '');
            if (search) url += '&search=' + encodeURIComponent(search);

            api('GET', url, undefined, { silent: true })
                .then(function (data) {
                    var entries = (data && data.entries) || [];
                    var $body = $('#syslog-table tbody').empty();
                    entries.forEach(function (e) { $body.append(syslogRow(e)); });
                    $('#syslog-count').text(entries.length + (entries.length === 1 ? ' row' : ' rows'));
                    $('#syslog-health').text(data && data.dbHealthy === false
                        ? 'The database sink is offline, so new entries are going to the fallback log file and will not appear here.'
                        : '');
                })
                .catch(function (err) { $('#syslog-health').text(err.message); })
                .then(function () { syslogBusy = false; });
        }

        function syslogRow(e) {
            var $row = $('<tr>')
                .append($('<td>').text(new Date(e.timestamp).toLocaleTimeString()))
                .append($('<td>').append($('<span class="pill syslog-level">')
                    .addClass((e.level || '').toLowerCase()).text(e.level)))
                .append($('<td class="msg">').text(e.message))
                .append($('<td class="muted">').text(shortSource(e.sourceContext)));
            $row.on('click', function () { syslogDetail(e); });
            return $row;
        }

        function shortSource(context) {
            if (!context) return '';
            var parts = String(context).split('.');
            return parts[parts.length - 1];
        }

        function syslogDetail(e) {
            var $body = $('<div>');
            var $grid = $('<div class="field-grid">');
            [
                ['Time', new Date(e.timestamp).toLocaleString()],
                ['Level', e.level],
                ['Source', e.sourceContext],
                ['Heartbeat', e.isHeartbeat ? 'yes' : 'no']
            ].forEach(function (pair) {
                if (!pair[1]) return;
                $grid.append($('<div class="field">').append($('<label>').text(pair[0])).append($('<div>').text(pair[1])));
            });
            $body.append($grid).append($('<h4>').text('Message')).append($('<pre>').text(e.message || ''));
            if (e.exception) $body.append($('<h4>').text('Exception')).append($('<pre>').text(e.exception));
            openModal('Log entry', $body, [{ label: 'Close' }], true);
        }

        loaders.syslog = loadSyslog;

        // ------------------------------------------------------------------ models tab
    var UPSTREAM_TYPES = [[1, 'OpenAI'], [2, 'Ollama'], [3, 'Anthropic'], [0, 'LlamaCpp (legacy)']];
    var UPSTREAM_MODEL_MAX_CHARS = 48;

    function upstreamTypeName(value) {
        for (var i = 0; i < UPSTREAM_TYPES.length; i++) if (UPSTREAM_TYPES[i][0] === value) return UPSTREAM_TYPES[i][1];
        return String(value);
    }

    function truncateText(text, max) {
        var value = text === null || text === undefined ? '' : String(text);
        return value.length <= max ? value : value.slice(0, max - 1) + '\u2026';
    }

    // The grid is a projection of the last /api/mappings response; toolbar actions and the
    // row click/double-click handlers all resolve the mapping by Id from this cache.
    var mappingCache = [];
    var selectedMappingId = null;

    function findMapping(id) {
        if (id === null || id === undefined) return null;
        for (var i = 0; i < mappingCache.length; i++) if (mappingCache[i].id === id) return mappingCache[i];
        return null;
    }

    function mappingName(m) {
        return m && m.OllamaName ? m.OllamaName : 'mapping ' + (m ? m.id : '');
    }

    function updateMappingButtons() {
        $('#btn-remove-mapping, #btn-duplicate-mapping, #btn-configure-mapping, #btn-toggle-mapping')
            .prop('disabled', !findMapping(selectedMappingId));
    }

    function selectMapping(id) {
        selectedMappingId = findMapping(id) ? id : null;
        $('#mapping-table tbody tr[data-id]').each(function () {
            $(this).toggleClass('selected', Number($(this).attr('data-id')) === selectedMappingId);
        });
        updateMappingButtons();
    }

    function loadMappings() {
        api('GET', '/api/mappings').then(function (list) {
            mappingCache = list || [];
            if (!findMapping(selectedMappingId)) selectedMappingId = null;

            var $tbody = $('#mapping-table tbody').empty();
            if (!mappingCache.length) {
                $tbody.append($('<tr>').append($('<td colspan="8" class="muted">').text('No model mappings yet.')));
                updateMappingButtons();
                return;
            }
            mappingCache.forEach(function (m) {
                var model = m.LlamaCppName || '';
                var $auto = $('<td>');
                if (m.autoManaged) {
                    // The title carries why discovery turned it off, which is otherwise invisible.
                    $auto.append($('<span class="pill">')
                        .addClass(m.autoDisabledUtc ? 'warn' : 'ok')
                        .attr('title', m.autoDisabledReason || 'Registered and managed by model auto-discovery.')
                        .text(m.autoDisabledUtc ? 'off' : 'yes'));
                } else {
                    $auto.text('\u2014');
                }
                $tbody.append($('<tr>')
                    .attr('data-id', m.id)
                    .toggleClass('selected', m.id === selectedMappingId)
                    .append($('<td>').append($('<span class="pill">').addClass(m.isEnabled ? 'ok' : '').text(m.isEnabled ? 'yes' : 'no')))
                    .append($('<td>').text(m.OllamaName))
                    .append($('<td class="truncate">').attr('title', model).text(truncateText(model, UPSTREAM_MODEL_MAX_CHARS)))
                    .append($('<td>').text(m.instructionSetName || '\u2014'))
                    .append($('<td>').text(upstreamTypeName(m.upstreamType)))
                    .append($('<td>').text(m.credentialName || '\u2014'))
                    .append($('<td>').text(m.hidden ? 'yes' : 'no'))
                    .append($auto));
            });
            updateMappingButtons();
        });
        loadDiscoveryStatus();
    }

    function describeDiscovery(result) {
        if (!result) return '';
        var parts = [];
        if (result.registered) parts.push(result.registered + ' registered');
        if (result.reactivated) parts.push(result.reactivated + ' re-enabled');
        if (result.disabled) parts.push(result.disabled + ' disabled');
        if (result.failedSources) parts.push(result.failedSources + ' unreachable');
        return parts.length ? parts.join(', ') : 'no changes';
    }

    function loadDiscoveryStatus() {
        api('GET', '/api/mappings/discovery', undefined, { silent: true }).then(function (d) {
            var text;
            if (!d || !d.enabled) {
                text = 'Auto-discovery is off.';
            } else if (!d.lastScanUtc) {
                text = 'Auto-discovery every ' + d.intervalSeconds + 's; no scan yet.';
            } else {
                text = 'Last scan ' + new Date(d.lastScanUtc).toLocaleTimeString() +
                    ': ' + describeDiscovery(d.lastResult) + '.';
            }
            $('#discovery-status').text(text).attr('title',
                d && d.lastResult && d.lastResult.messages ? d.lastResult.messages.join('\n') : '');
        }).catch(function () { /* the status line is informational only */ });
    }

    function discoverModelsNow() {
        var $btn = $('#btn-discover-models').prop('disabled', true);
        api('POST', '/api/mappings/discover', {}).then(function (result) {
            var summary = describeDiscovery(result);
            toast('Discovery: ' + summary + '.');
            if (result && result.messages && result.messages.length) {
                var $body = $('<div>');
                $body.append($('<p>').text('Scanned ' + result.sources + ' upstream(s): ' + summary + '.'));
                $body.append($('<pre>').text(result.messages.join('\n')));
                openModal('Model discovery', $body, [{ label: 'Close' }], true);
            }
            loadMappings();
        }).catch(function (err) {
            toast('Discovery failed: ' + err.message, true);
        }).then(function () {
            $btn.prop('disabled', false);
        });
    }

    function configureSelectedMapping() {
        var m = findMapping(selectedMappingId);
        if (m) mappingDialog(m);
    }

    function removeSelectedMapping() {
        var m = findMapping(selectedMappingId);
        if (!m) return;
        confirmDialog('Delete model', 'Delete model "' + mappingName(m) + '"? This cannot be undone.', function () {
            api('DELETE', '/api/mappings/' + m.id).then(function () {
                selectedMappingId = null;
                toast('Model deleted.');
                loadMappings();
            });
        });
    }

    function toggleSelectedMapping() {
        var m = findMapping(selectedMappingId);
        if (!m) return;
        var updated = JSON.parse(JSON.stringify(m));
        updated.isEnabled = !updated.isEnabled;
        api('PUT', '/api/mappings/' + m.id, updated).then(function () {
            toast(updated.isEnabled ? 'Model enabled.' : 'Model disabled.');
            loadMappings();
        });
    }

    // Mirrors the Windows app: "name - Copy", then "name - Copy 2", and so on.
    function uniqueProxyName(baseName) {
        var base = baseName || 'Model';
        var used = {};
        mappingCache.forEach(function (m) { used[String(m.OllamaName || '').toLowerCase()] = true; });
        var candidate = base + ' - Copy';
        if (!used[candidate.toLowerCase()]) return candidate;
        for (var n = 2; ; n++) {
            candidate = base + ' - Copy ' + n;
            if (!used[candidate.toLowerCase()]) return candidate;
        }
    }

    function duplicateSelectedMapping() {
        var source = findMapping(selectedMappingId);
        if (!source) return;
        var copy = JSON.parse(JSON.stringify(source));
        delete copy.id; // the POST endpoint assigns the next Id
        copy.OllamaName = uniqueProxyName(source.OllamaName);
        api('POST', '/api/mappings', copy).then(function (created) {
            selectedMappingId = created && created.id ? created.id : null;
            toast('Model duplicated.');
            loadMappings();
        });
    }

    function mappingDialog(existing) {
        // Read-only model roles get the table but not the editor.
        if (!can('ManageModels')) return;
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
        var $instructionSet = field('Instruction set', $('<input type="text" list="instruction-set-names">').val(base.instructionSetName || ''), 'Optional named instruction set prepended to this model\'s requests.');
        var $timeout = field('Upstream timeout (seconds)', $('<input type="number" min="1">').val(base.upstreamTimeoutSeconds || 300));
        var $enabled = field('Enabled', $('<input type="checkbox">').prop('checked', base.isEnabled !== false));
        var $hidden = field('Hidden from discovery', $('<input type="checkbox">').prop('checked', !!base.hidden));
        var $autoManaged = field('Auto-managed by upstream', $('<input type="checkbox">').prop('checked', !!base.autoManaged),
            'Registers every model this upstream lists, and disables a model once it has been missing for the grace period. Saving here clears any discovery-applied disable.');
        if (base.autoDisabledReason) {
            $body.append($('<p class="hint">').text('Discovery disabled this model: ' + base.autoDisabledReason));
        }

        var $advanced = $('<textarea rows="12">').val(JSON.stringify(base, null, 2));
        $body.append($('<details>').append($('<summary>').text('All settings (JSON) — advanced fields preserved on save')).append($advanced));
        $body.append($('<datalist id="credential-names">'));
        $body.append($('<datalist id="instruction-set-names">'));
        api('GET', '/api/credentials', undefined, { silent: true }).then(function (creds) {
            (creds || []).forEach(function (c) { $('<option>').val(c.name).appendTo('#credential-names'); });
        }).catch(function () { /* datalist is a convenience only */ });
        api('GET', '/api/settings', undefined, { silent: true }).then(function (settings) {
            ((settings && settings.instructionSets) || []).forEach(function (set) {
                $('<option>').val(set.name).text(set.description || '').appendTo('#instruction-set-names');
            });
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
            merged.instructionSetName = $instructionSet.val().trim() || null;
            merged.upstreamTimeoutSeconds = parseInt($timeout.val(), 10) || 300;
            merged.isEnabled = $enabled.prop('checked');
            merged.hidden = $hidden.prop('checked');
            merged.autoManaged = $autoManaged.prop('checked');

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
                var $edit = $('<button type="button">').text('Edit').on('click', function () { credentialDialog(c); });
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
                    .append($('<td>').append($edit).append(' ').append($del)));
            });
        });
    }

    // `existing` comes from the credentials list, which never returns secret material: the secret,
    // private key, and certificate start blank and are only sent when the operator types a
    // replacement. A blank field leaves the stored value untouched (the API treats null as "keep").
    function credentialDialog(existing) {
        var $body = $('<div>');
        function field(label, $input, hint) {
            var $f = $('<div class="field">').append($('<label>').text(label)).append($input);
            if (hint) $f.append($('<span class="hint">').text(hint));
            $body.append($f);
            return $input;
        }
        var editing = !!existing;
        var $name = field('Name', $('<input type="text">').val(editing ? existing.name : ''),
            editing ? 'The name identifies the credential and cannot be changed here.' : 'Saving with an existing name replaces that credential.');
        if (editing) $name.prop('readonly', true);
        var $description = field('Description', $('<input type="text">').val(editing ? (existing.description || '') : ''));
        var $username = field('Username', $('<input type="text">').val(editing ? (existing.username || '') : ''));
        var $secret = field('Secret / API key', $('<input type="password" autocomplete="new-password">'),
            editing ? 'Write-only: never returned by the API. Leave blank to keep the stored value.' : 'Write-only: never returned by the API.');
        var $privateKey = field('Private key (PEM, optional)', $('<textarea rows="4">'),
            editing ? 'Leave blank to keep the stored key.' : null);
        var $certificate = field('Certificate (PFX as base64 or PEM, optional)', $('<textarea rows="4">'),
            editing ? 'Leave blank to keep the stored certificate; paste a new one to replace it (used for the HTTPS dashboard).' : 'Used for the HTTPS dashboard when selected in Settings.');

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

        openModal(editing ? 'Edit credential' : 'Add credential', $body, [
            { label: 'Cancel' },
            { label: 'Save', cls: 'primary', onClick: save }
        ]);
    }

    loaders.credentials = loadCredentials;

    // ------------------------------------------------------------------ modules (rendered inside the MCP tab)
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
        toast('Uploading ' + file.name + '…');
        $.ajax({
            url: '/api/modules/upload',
            method: 'POST',
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
            if (xhr.status === 401) { window.location.replace('/login.html'); return; }
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

    // ------------------------------------------------------------------ users tab

        // Every per-row action lives behind a single 3-dot button so a card stays readable no matter
        // how many keys it holds. The popup is a single shared element, positioned against the button
        // that opened it, so nothing is clipped by the card's overflow.
        var $kebabMenu = null;

        function closeKebabMenu() {
            if (!$kebabMenu) return;
            $($kebabMenu.data('owner')).attr('aria-expanded', 'false');
            $kebabMenu.remove();
            $kebabMenu = null;
        }

        function kebabButton(items) {
            var $btn = $('<button type="button" class="kebab" aria-haspopup="menu" aria-label="More options">')
                .append('<span></span><span></span><span></span>');
            $btn.on('click', function (e) {
                e.stopPropagation();
                var reopen = !($kebabMenu && $kebabMenu.data('owner') === $btn[0]);
                closeKebabMenu();
                if (!reopen) return;

                var $menu = $('<div class="kebab-menu" role="menu">');
                items.forEach(function (item) {
                    if (item.separator) { $menu.append($('<div class="kebab-sep">')); return; }
                    $('<button type="button" role="menuitem">')
                        .addClass(item.danger ? 'danger' : '')
                        .text(item.label)
                        .on('click', function (ev) {
                            ev.stopPropagation();
                            closeKebabMenu();
                            item.action();
                        })
                        .appendTo($menu);
                });

                $('body').append($menu);
                var rect = $btn[0].getBoundingClientRect();
                var width = $menu.outerWidth();
                var height = $menu.outerHeight();
                var left = Math.max(8, Math.min(rect.right - width, window.innerWidth - width - 8));
                var top = rect.bottom + 4;
                if (top + height > window.innerHeight - 8) top = Math.max(8, rect.top - height - 4);
                $menu.css({ left: left + 'px', top: top + 'px' }).data('owner', $btn[0]);
                $btn.attr('aria-expanded', 'true');
                $kebabMenu = $menu;
            });
            return $btn;
        }

        $(document).on('click', function () { closeKebabMenu(); });
        $(document).on('keydown', function (e) { if (e.key === 'Escape') closeKebabMenu(); });
        $(window).on('resize scroll', function () { closeKebabMenu(); });

        function roleLabel(key) {
            var definition = DASHBOARD_ROLES.filter(function (r) { return r.key === key; })[0];
            return definition ? definition.label : key;
        }

        // Roles are pills rather than one comma-joined string: a card is only 280px wide, and a
        // multi-role value used to break mid-word ("Test Co / nsole").
        function rolesRow(user) {
            var $value = $('<span class="v role-list">');
            var roles = (user.dashboardRoles || []).slice();
            if (!roles.length && user.dashboardRole) roles = [user.dashboardRole];
            if (!user.dashboardLoginEnabled) {
                $value.text('—');
            } else if (!roles.length) {
                $value.text('No dashboard access');
            } else {
                roles.forEach(function (key) { $value.append($('<span class="role-pill">').text(roleLabel(key))); });
            }
            return $('<div class="kv">').append($('<span class="k">').text('Roles')).append($value);
        }

        function grantSummary(user) {
                if ((user.dashboardRoles || []).indexOf('admin') >= 0) return 'Admin: all models + MCP';
                var models = user.allowedModels || [];
                var modules = user.allowedMcpModules || [];
                var mcp = user.mcpAccess
                    ? (modules.length ? 'allowed (' + modules.length + ' module' + (modules.length === 1 ? '' : 's') + ')' : 'allowed (all modules)')
                    : 'denied';
                return (models.length ? models.join(', ') : 'no models') + ' · MCP ' + mcp;
            }

        function loadUsers() {
            api('GET', '/api/users').then(function (list) {
                var $cards = $('#user-cards').empty();
                if (!list.length) {
                    $cards.append($('<p class="muted">').text('No users yet. Add a user, then create an API key for them.'));
                    return;
                }
                list.forEach(function (u) {
                    var $card = $('<div class="card">');
                    var $title = $('<h3>')
                        .append($('<span class="card-title">').text(u.name + ' '))
                        .append($('<span class="pill">').addClass(u.isEnabled ? 'ok' : '').text(u.isEnabled ? 'enabled' : 'disabled'))
                        .append($('<span class="card-kebab">').append(kebabButton([
                            { label: 'Create API key', action: function () { keyDialog(u, null); } },
                            { label: 'Edit user', action: function () { userDialog(u); } },
                            { label: u.isEnabled ? 'Disable user' : 'Enable user', action: function () { toggleUser(u); } },
                            { separator: true },
                            {
                                label: 'Delete user', danger: true, action: function () {
                                    confirmDialog('Delete user', 'Delete user "' + u.name + '" and all of their API keys? This cannot be undone.', function () {
                                        api('DELETE', '/api/users/' + u.id).then(function () { toast('User deleted.'); loadUsers(); });
                                    });
                                }
                            }
                        ])));
                    $card.append($title);
                    $card.append(kv('Email', u.email || '—'))
                        .append(kv('Dashboard login', u.dashboardLoginEnabled ? (u.dashboardUsername || '—') : 'disabled'))
                        .append(rolesRow(u))
                        .append(kv('Model access', grantSummary(u)))
                        .append(kv('Created', u.createdUtc ? new Date(u.createdUtc).toLocaleString() : '—'));

                    var $keys = $('<div class="user-keys">');
                    if (!(u.keys || []).length) {
                        $keys.append($('<p class="muted">').text('No API keys.'));
                    } else {
                        u.keys.forEach(function (k) {
                            var $row = $('<div class="key-row">');
                            var $head = $('<div class="key-head">')
                                .append($('<span class="key-name">').text(k.label || '(unlabeled)'))
                                .append($('<span class="key-prefix">').text(k.keyPrefix + '\u2026'))
                                .append(kebabButton([
                                    { label: 'Reveal key', action: function () { revealKey(u, k); } },
                                    { label: 'Edit label', action: function () { keyDialog(u, k); } },
                                    { label: 'Regenerate key', action: function () { regenerateKey(u, k); } },
                                    { separator: true },
                                    {
                                        label: 'Delete key', danger: true, action: function () {
                                            confirmDialog('Delete API key', 'Delete key "' + (k.label || k.keyPrefix) + '" for ' + u.name + '? Clients using it lose access immediately.', function () {
                                                api('DELETE', '/api/keys/' + k.id).then(function () { toast('Key deleted.'); loadUsers(); });
                                            });
                                        }
                                    }
                                ]));
                            $row.append($head);
                            $row.append($('<div class="key-meta">').text(
                                'last used ' + (k.lastUsedUtc ? new Date(k.lastUsedUtc).toLocaleString() : 'never')));
                            $keys.append($row);
                        });
                    }
                    $card.append($keys);
                    $cards.append($card);
                });
            });
        }

        function toggleUser(u) {
            api('PUT', '/api/users/' + u.id, { isEnabled: !u.isEnabled })
                .then(function () { toast('User ' + (u.isEnabled ? 'disabled' : 'enabled') + '.'); loadUsers(); });
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
            var $loginEnabled = $('<input type="checkbox">').prop('checked', !!(existing && existing.dashboardLoginEnabled));
            $body.append($('<div class="field bool">').append($loginEnabled).append($('<label>').text('Enable dashboard login')));
            var $username = field('Dashboard username', $('<input type="text" autocomplete="username">')
                .val(existing ? existing.dashboardUsername : ''), 'Separate from the proxy API keys used by model clients.');
            var $password = field('Dashboard password', $('<input type="password" autocomplete="new-password">'),
                existing && existing.hasDashboardPassword ? 'Leave blank to keep the current password; enter a new one to reset it.' : 'Use at least 12 characters.');

            var held = (existing && existing.dashboardRoles) || (existing && existing.dashboardRole ? [existing.dashboardRole] : ['readOnly']);
            var $roles = $('<div class="model-checks">');
            DASHBOARD_ROLES.forEach(function (role) {
                var $cb = $('<input type="checkbox">').val(role.key)
                    .prop('checked', held.indexOf(role.key) >= 0);
                $roles.append($('<label class="check">')
                    .append($cb)
                    .append($('<span>').append($('<strong>').text(role.label))
                        .append(document.createTextNode(' — ' + role.description))));
            });
            $body.append($('<div class="field">').append($('<label>').text('Dashboard roles'))
                .append($('<span class="hint">').text('Hold as many as apply; the permissions are combined. Admin already includes everything.'))
                .append($roles));

            var $models = null;
            var $mcpAccess = null;
            var $mcpModules = null;

            function buildGrants(models, modules) {
                var granted = (existing && existing.allowedModels) || [];
                var $wrap = $('<div class="field">').append($('<label>').text('Model access'))
                    .append($('<span class="hint">').text('Every API key this user holds inherits these grants. Admin users always have access to every model.'));
                var $list = $('<div class="model-checks">');
                if (!models.length) {
                    $list.append($('<p class="muted">').text('No models are configured yet.'));
                }
                models.forEach(function (name) {
                    var checked = granted.some(function (g) { return g.toLowerCase() === name.toLowerCase(); });
                    var $cb = $('<input type="checkbox">').val(name).prop('checked', checked);
                    $list.append($('<label class="check">').append($cb).append($('<span>').text(name)));
                });
                $wrap.append($list);
                $models = $list;

                $mcpAccess = $('<input type="checkbox">').prop('checked', !!(existing && existing.mcpAccess));
                $wrap.append($('<div class="field bool">').append($mcpAccess)
                    .append($('<label>').text('Allow MCP server access'))
                    .append($('<span class="hint">').text('Grants this user the MCP server and its tools.')));

                var grantedModules = (existing && existing.allowedMcpModules) || [];
                var $moduleList = $('<div class="model-checks">');
                if (!modules.length) {
                    $moduleList.append($('<p class="muted">').text('No MCP modules are registered.'));
                }
                modules.forEach(function (m) {
                    var checked = grantedModules.some(function (g) { return g.toLowerCase() === m.key.toLowerCase(); });
                    var $cb = $('<input type="checkbox">').val(m.key).prop('checked', checked);
                    $moduleList.append($('<label class="check">').append($cb)
                        .append($('<span>').text(m.name + (m.isEnabled ? '' : ' (disabled)'))));
                });
                $wrap.append($('<div class="field">').append($('<label>').text('MCP modules'))
                    .append($('<span class="hint">').text('Leave every box clear to allow every module. Ticking any box limits the user to exactly those modules, and a tool from any other module is refused.'))
                    .append($moduleList));
                $mcpModules = $moduleList;

                $body.append($wrap);
            }

            function selectedRoles() {
                return $roles.find('input:checked').map(function () { return $(this).val(); }).get();
            }

            function save() {
                var name = $name.val().trim();
                var username = $username.val().trim();
                var password = $password.val();
                var enabled = $loginEnabled.prop('checked');
                var roles = selectedRoles();
                if (!name) { modalError('User name is required.'); return; }
                if (enabled && !roles.length) { modalError('Pick at least one dashboard role.'); return; }
                if (enabled && !password && !(existing && existing.hasDashboardPassword)) {
                    modalError('Set a password when enabling a dashboard login.');
                    return;
                }
                var payload = {
                    name: name,
                    email: $email.val().trim(),
                    dashboardLoginEnabled: enabled,
                    dashboardUsername: username,
                    dashboardRoles: roles
                };
                if ($models) {
                    payload.allowedModels = $models.find('input:checked').map(function () { return $(this).val(); }).get();
                    payload.mcpAccess = $mcpAccess.prop('checked');
                    payload.allowedMcpModules = $mcpModules.find('input:checked').map(function () { return $(this).val(); }).get();
                }
                if (password) payload.dashboardPassword = password;
                var request = existing
                    ? api('PUT', '/api/users/' + existing.id, payload)
                    : api('POST', '/api/users', payload);
                request.then(function () {
                    closeModal();
                    toast(existing ? 'User updated.' : 'User added.');
                    loadUsers();
                }).catch(function (err) { modalError(err.message); });
            }

            // The role vocabulary, the model list, and the MCP module list all come from the server so
            // the dialog can never drift from what the endpoints accept.
            $.when(
                api('GET', '/api/auth/roles', undefined, { silent: true }).catch(function () { return { roles: DASHBOARD_ROLES }; }),
                api('GET', '/api/mappings', undefined, { silent: true }).catch(function () { return []; }),
                api('GET', '/api/users/mcp-modules', undefined, { silent: true }).catch(function () { return []; })
            ).then(function (rolesResult, mappings, modules) {
                if (rolesResult && rolesResult.roles && rolesResult.roles.length) DASHBOARD_ROLES = rolesResult.roles;
                var models = (mappings || [])
                    .filter(function (m) { return m.IsEnabled !== false && m.OllamaName; })
                    .map(function (m) { return m.OllamaName; })
                    .sort(function (a, b) { return a.toLowerCase().localeCompare(b.toLowerCase()); });
                buildGrants(models, modules || []);
                openModal(existing ? 'Edit user' : 'Add user', $body, [
                    { label: 'Cancel' },
                    { label: 'Save', cls: 'primary', onClick: save }
                ]);
            });
        }

        function keyDialog(user, existing) {
            var $body = $('<div>');
            var $label = $('<input type="text">').val(existing ? existing.label : '');
            $body.append($('<div class="field">').append($('<label>').text('Label')).append($label)
                .append($('<span class="hint">').text('What this key is for, e.g. "laptop" or "CI pipeline".')));
            $body.append($('<p class="muted">').text(
                'Model and MCP access belong to ' + user.name + ', not to the key, so every key they hold has the same access. Change it with "Edit user".'));

            function save() {
                var payload = { label: $label.val().trim() };
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
        }

    function showNewKeyDialog(user, plainText) {
        showKeyDialog('New API key', plainText,
            'Copy this key now, or reveal it again later from the Users tab. Send it as "Authorization: Bearer <key>" or "x-api-key".',
            false, user.name);
    }

    // Shared by create, reveal, and regenerate. When startMasked is set the value stays hidden
    // until the operator asks for it, so a revealed key is not left on screen by accident.
    function showKeyDialog(title, plainText, note, startMasked, subject) {
        var masked = !!startMasked;
        var $input = $('<input type="text" readonly>').val(plainText).on('focus', function () { this.select(); });
        if (masked) $input.attr('type', 'password');
        var $showToggle = $('<input type="checkbox">').prop('checked', !masked).on('change', function () {
            masked = !this.checked;
            $input.attr('type', masked ? 'password' : 'text');
        });
        var $body = $('<div>')
            .append($('<p>').text(note))
            .append($('<div class="field">').append($('<label>').text(subject ? 'API key for ' + subject : 'API key')).append($input))
            .append($('<div class="field bool">').append($showToggle).append($('<label>').text('Show key')));
        openModal(title, $body, [
            {
                label: 'Copy', cls: 'primary', onClick: function () {
                    function fallback() {
                        $input.attr('type', 'text').trigger('focus').trigger('select');
                        try { document.execCommand('copy'); toast('Key copied to clipboard.'); }
                        catch (e) { toast('Copy failed - select the text and copy manually.', true); }
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

    function revealKey(user, key) {
        api('GET', '/api/keys/' + key.id + '/reveal').then(function (result) {
            showKeyDialog('API key', result.plainText,
                'Key "' + (key.label || key.keyPrefix) + '". Anyone holding it can use this user\'s models, so share it carefully.',
                true, user.name);
        }).catch(function (err) {
            if (err.status === 409) {
                openModal('Key cannot be shown', $('<p>').text(err.message), [
                    { label: 'Cancel' },
                    { label: 'Regenerate', cls: 'primary', onClick: function () { closeModal(); regenerateKey(user, key); } }
                ]);
            } else {
                toast(err.message, true);
            }
        });
    }

    function regenerateKey(user, key) {
        openModal('Regenerate API key', $('<p>').text(
            'Replace the key "' + (key.label || key.keyPrefix) + '" with a new one? The current key stops working immediately. The label stays the same, and model access is unchanged because it belongs to ' + user.name + '.'), [
            { label: 'Cancel' },
            {
                label: 'Regenerate', cls: 'danger', onClick: function () {
                    closeModal();
                    api('POST', '/api/keys/' + key.id + '/regenerate').then(function (result) {
                        loadUsers();
                        showKeyDialog('New API key', result.plainText,
                            'This replaced the previous key. Copy it now, or reveal it again later from the Users tab.',
                            false, user.name);
                    }).catch(function (err) { toast(err.message, true); });
                }
            }
        ]);
    }

    loaders.users = loadUsers;

    // ------------------------------------------------------------------ usage tab
    var USAGE_COLORS = ['#2563eb', '#16a34a', '#d97706', '#dc2626', '#7c3aed', '#0891b2', '#db2777', '#65a30d', '#ea580c', '#475569', '#0d9488', '#9333ea'];
    var usageState = { period: 'thisWeek', group: 'day', model: '', user: 'all' };
    var usageColorMap = {};
    var usageColorNext = 0;
    var lastUsageData = null;
    var usageResizeTimer = null;

    function usageColor(model) {
        if (!usageColorMap.hasOwnProperty(model)) {
            usageColorMap[model] = USAGE_COLORS[usageColorNext % USAGE_COLORS.length];
            usageColorNext++;
        }
        return usageColorMap[model];
    }

    function usagePad(n) { return (n < 10 ? '0' : '') + n; }
    function usageDateStr(d) { return d.getFullYear() + '-' + usagePad(d.getMonth() + 1) + '-' + usagePad(d.getDate()); }

    function usageRange(period) {
        var now = new Date();
        var y = now.getFullYear(), m = now.getMonth(), d = now.getDate();
        var from, to, dow;
        if (period === 'lastMonth') { from = new Date(y, m - 1, 1); to = new Date(y, m, 0); }
        else if (period === 'thisWeek') { dow = (now.getDay() + 6) % 7; from = new Date(y, m, d - dow); to = new Date(y, m, d); }
        else if (period === 'lastWeek') { dow = (now.getDay() + 6) % 7; from = new Date(y, m, d - dow - 7); to = new Date(y, m, d - dow - 1); }
        else if (period === 'last30') { from = new Date(y, m, d - 29); to = new Date(y, m, d); }
        else if (period === 'last90') { from = new Date(y, m, d - 89); to = new Date(y, m, d); }
        else { from = new Date(y, m, 1); to = new Date(y, m, d); }
        return { from: usageDateStr(from), to: usageDateStr(to) };
    }

    function loadUsage() {
        var range = usageRange(usageState.period);
        var url = '/api/usage?from=' + range.from + '&to=' + range.to + '&groupBy=' + usageState.group;
        if (usageState.model) url += '&model=' + encodeURIComponent(usageState.model);
        if (can('Admin') && usageState.user) url += '&userId=' + encodeURIComponent(usageState.user);
        api('GET', url).then(renderUsage).catch(function () { /* toast already shown */ });
    }
    loaders.usage = loadUsage;

    function renderUsage(data) {
        lastUsageData = data;
        $('#usage-range').text(data.from + ' \u2192 ' + data.to);

        // Rebuild model options only when unfiltered: a filtered response carries just the
        // selected model and would otherwise collapse the list.
        var $model = $('#usage-model');
        if (!usageState.model) {
            $model.empty().append($('<option>').val('').text('All models'));
            (data.models || []).forEach(function (m) { $model.append($('<option>').val(m).text(m)); });
        }

        var isAdmin = can('Admin');
        $('#usage-user-wrap').toggle(isAdmin);
        $('#usage-byuser-wrap').toggle(isAdmin);
        if (isAdmin) {
            var keepUser = usageState.user || 'all';
            var $user = $('#usage-user').empty().append($('<option>').val('all').text('All users'));
            if (currentIdentity && currentIdentity.username)
                $user.append($('<option>').val('me').text('Me (' + currentIdentity.username + ')'));
            (data.users || []).forEach(function (u) { $user.append($('<option>').val(String(u.id)).text(u.name)); });
            $user.val(keepUser);
            if (!$user.val()) { $user.val('all'); usageState.user = 'all'; }
        }

        var t = data.totals || {};
        var $cards = $('#usage-summary').empty();
        [['Total tokens', t.totalTokens], ['Prompt tokens', t.promptTokens], ['Completion tokens', t.completionTokens], ['Requests', t.requests]].forEach(function (pair) {
            $cards.append($('<div class="card usage-card">').append($('<h3>').text(pair[0]))
                .append($('<div class="usage-big">').text(fmt(pair[1] || 0))));
        });

        renderUsageChart(data);
        renderUsageLegend(data);
        if (isAdmin) renderUsageByUser(data);
        renderUsageDays(data);
    }

    function svgEl(tag, attrs) {
        var el = document.createElementNS('http://www.w3.org/2000/svg', tag);
        for (var k in attrs) if (attrs.hasOwnProperty(k)) el.setAttribute(k, attrs[k]);
        return el;
    }

    function usageNiceMax(value) {
        if (value <= 0) return 10;
        var exp = Math.pow(10, Math.floor(Math.log(value) / Math.LN10));
        var frac = value / exp;
        var nice = frac <= 1 ? 1 : frac <= 2 ? 2 : frac <= 5 ? 5 : 10;
        return nice * exp;
    }

    function renderUsageChart(data) {
        var $wrap = $('#usage-chart').empty();
        var buckets = data.buckets || [];
        var width = Math.max($wrap.width() || 800, 320);
        var height = 260, padL = 64, padR = 12, padT = 12, padB = 34;
        var plotW = width - padL - padR, plotH = height - padT - padB;
        var totals = buckets.map(function (b) { return b.totalTokens; }).concat([0]);
        var max = usageNiceMax(Math.max.apply(null, totals));
        var svg = svgEl('svg', { width: width, height: height, viewBox: '0 0 ' + width + ' ' + height, role: 'img' });

        var steps = 4, i;
        for (i = 0; i <= steps; i++) {
            var gy = padT + plotH - plotH * i / steps;
            svg.appendChild(svgEl('line', { x1: padL, y1: gy, x2: width - padR, y2: gy, 'class': 'usage-grid' }));
            var yLabel = svgEl('text', { x: padL - 8, y: gy + 4, 'text-anchor': 'end', 'class': 'usage-axis' });
            yLabel.textContent = fmt(Math.round(max * i / steps));
            svg.appendChild(yLabel);
        }

        var n = buckets.length || 1;
        var slot = plotW / n;
        var barW = Math.max(2, Math.min(48, slot * 0.7));
        var labelStep = Math.max(1, Math.ceil(n / Math.max(1, Math.floor(plotW / 70))));
        buckets.forEach(function (b, idx) {
            var x = padL + slot * idx + (slot - barW) / 2;
            var yCursor = padT + plotH;
            (b.byModel || []).forEach(function (m) {
                var h = max > 0 ? (m.totalTokens / max) * plotH : 0;
                if (h <= 0) return;
                yCursor -= h;
                var rect = svgEl('rect', { x: x, y: yCursor, width: barW, height: h, fill: usageColor(m.model), 'class': 'usage-bar' });
                var title = svgEl('title', {});
                title.textContent = b.label + ' \u2014 ' + m.model + ': ' + fmt(m.totalTokens) + ' tokens (' +
                    fmt(m.promptTokens) + ' prompt / ' + fmt(m.completionTokens) + ' completion)';
                rect.appendChild(title);
                svg.appendChild(rect);
            });
            if (idx % labelStep === 0) {
                var xLabel = svgEl('text', { x: padL + slot * idx + slot / 2, y: height - 10, 'text-anchor': 'middle', 'class': 'usage-axis' });
                xLabel.textContent = b.label;
                svg.appendChild(xLabel);
            }
        });

        if ((data.totals || {}).totalTokens === 0) {
            var empty = svgEl('text', { x: padL + plotW / 2, y: padT + plotH / 2, 'text-anchor': 'middle', 'class': 'usage-axis' });
            empty.textContent = 'No token usage in this range';
            svg.appendChild(empty);
        }
        $wrap.append(svg);
    }

    function renderUsageLegend(data) {
        var $l = $('#usage-legend').empty();
        (data.models || []).forEach(function (m) {
            $l.append($('<span class="usage-legend-item">')
                .append($('<span class="usage-swatch">').css('background', usageColor(m)))
                .append($('<span>').text(m)));
        });
    }

    function renderUsageByUser(data) {
        var $wrap = $('#usage-byuser').empty();
        var list = data.byUser || [];
        if (!list.length) { $wrap.append($('<p class="muted">').text('No usage in this range.')); return; }
        var max = Math.max.apply(null, list.map(function (u) { return u.totalTokens; }).concat([1]));
        var $table = $('<table>').append($('<thead>').append($('<tr>')
            .append('<th>User</th><th>Requests</th><th>Prompt</th><th>Completion</th><th>Total</th><th class="usage-share-col">Share</th>')));
        var $tbody = $('<tbody>');
        list.forEach(function (u) {
            var pct = Math.round(u.totalTokens / max * 100);
            $tbody.append($('<tr>')
                .append($('<td>').text(u.name))
                .append($('<td class="num">').text(fmt(u.requests)))
                .append($('<td class="num">').text(fmt(u.promptTokens)))
                .append($('<td class="num">').text(fmt(u.completionTokens)))
                .append($('<td class="num">').text(fmt(u.totalTokens)))
                .append($('<td class="usage-share-col">').append($('<div class="usage-share">')
                    .append($('<div class="usage-share-fill">').css('width', pct + '%')))));
        });
        $wrap.append($('<div class="table-wrap">').append($table.append($tbody)));
    }

    function renderUsageDays(data) {
        // Direct child only: the expanded detail rows contain their own model tables, and a
        // descendant selector would match those tbodies too — jQuery then clones the day rows
        // into them and the visible copies lose their click handlers.
        var $tbody = $('#usage-day-table').children('tbody').empty();
        var days = data.days || [];
        if (!days.length) {
            $tbody.append($('<tr>').append($('<td colspan="6" class="muted">').text('No token usage in this range.')));
            return;
        }
        days.forEach(function (d) {
            var $row = $('<tr class="usage-day-row">')
                .append($('<td class="usage-caret">').text('\u25b8'))
                .append($('<td>').text(d.day))
                .append($('<td class="num">').text(fmt(d.requests)))
                .append($('<td class="num">').text(fmt(d.promptTokens)))
                .append($('<td class="num">').text(fmt(d.completionTokens)))
                .append($('<td class="num">').text(fmt(d.totalTokens)));
            var $detail = $('<tr class="usage-day-detail hidden">').append($('<td colspan="6">'));
            var $inner = $('<table class="usage-model-table">').append($('<thead>').append($('<tr>')
                .append('<th>Model</th><th>Requests</th><th>Prompt</th><th>Completion</th><th>Total</th><th>Cached prompt</th><th>Reasoning</th>')));
            var $innerBody = $('<tbody>');
            (d.models || []).forEach(function (m) {
                $innerBody.append($('<tr>')
                    .append($('<td>').text(m.model))
                    .append($('<td class="num">').text(fmt(m.requests)))
                    .append($('<td class="num">').text(fmt(m.promptTokens)))
                    .append($('<td class="num">').text(fmt(m.completionTokens)))
                    .append($('<td class="num">').text(fmt(m.totalTokens)))
                    .append($('<td class="num">').text(fmt(m.cachedPromptTokens)))
                    .append($('<td class="num">').text(fmt(m.reasoningTokens))));
            });
            $detail.find('td').append($inner.append($innerBody));
            $row.on('click', function () {
                var wasOpen = !$detail.hasClass('hidden');
                $detail.toggleClass('hidden', wasOpen);
                $row.toggleClass('open', !wasOpen);
                $row.find('.usage-caret').text(wasOpen ? '\u25b8' : '\u25be');
            });
            $tbody.append($row).append($detail);
        });
    }

    $('#usage-period').on('change', function () { usageState.period = $(this).val(); loadUsage(); });
    $('#usage-group').on('change', function () { usageState.group = $(this).val(); loadUsage(); });
    $('#usage-model').on('change', function () { usageState.model = $(this).val() || ''; loadUsage(); });
    $('#usage-user').on('change', function () { usageState.user = $(this).val() || 'all'; loadUsage(); });
    $('#btn-refresh-usage').on('click', loadUsage);
    $(window).on('resize', function () {
        if (activeTab !== 'usage' || !lastUsageData) return;
        clearTimeout(usageResizeTimer);
        usageResizeTimer = setTimeout(function () { renderUsageChart(lastUsageData); }, 150);
    });

    // ------------------------------------------------------------------ settings tab
    var SETTINGS_SCHEMA = [
        {
            title: 'Proxy', fields: [
                { path: 'listenPort', label: 'Listen port', type: 'number', hint: '1–65535. Applies after proxy restart.' },
                { path: 'listenAddress', label: 'Listen address', type: 'text', hint: 'localhost, 0.0.0.0, or a specific IP.' },
                { path: 'autoStartProxy', label: 'Auto-start proxy on launch', type: 'bool' },
                { path: 'allowMultipleInstances', label: 'Allow multiple instances', type: 'bool', hint: 'Applies on next launch. Off = a second launch shows "already running" and exits.' },
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
            title: 'Model auto-discovery', fields: [
                { path: 'enableModelAutoDiscovery', label: 'Enable auto-discovery', type: 'bool', hint: 'Polls the upstream of every auto-managed model for its model list.' },
                { path: 'modelAutoDiscoveryIntervalSeconds', label: 'Scan interval (s)', type: 'number', hint: '15–3600. How often each auto-managed upstream is polled.' },
                { path: 'modelAutoDiscoveryOfflineMinutes', label: 'Offline grace (minutes)', type: 'number', hint: '1–1440. How long a model must be missing before discovery disables it.' }
            ]
        },
        {
            title: 'Diagnostics and API documents', fields: [
                { path: 'enablePerformanceSampling', label: 'Performance sampling', type: 'bool' },
                { path: 'enableApiExplorer', label: 'Proxy API explorer', type: 'bool', hint: 'Serves the proxy OpenAPI document and the Scalar UI at /scalar. The Dashboard card and Help link to them only while this is on.' },
                { path: 'enableRegistrationApiSpec', label: 'Registration API document', type: 'bool', hint: 'Serves /api/mappings/openapi.json to the registration roles. Off answers 404 there and hides every link to it.' }
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
                { path: 'webDashboard.requireLogin', label: 'Require login', type: 'bool', hint: 'Applies immediately. Off = anyone who can reach the dashboard gets full admin access — use only on a trusted network.' },
                { path: 'webDashboard.scheme', label: 'Scheme', type: 'select', options: ['Http', 'Https'] },
                { path: 'webDashboard.certificateCredentialName', label: 'HTTPS certificate credential', type: 'credentials', hint: 'HTTPS only. Store the PFX (base64) or PEM certificate on the Credentials tab, then select it here.' }
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
                    if (f.hint) $boolField.append($('<span class="hint">').text(f.hint));
                    $input.attr('data-path', f.path).prop('disabled', !!f.readonly);
                    $grid.append($boolField);
                    return;
                }
                if (f.type === 'select') {
                    $input = $('<select>');
                    f.options.forEach(function (o) { $('<option>').val(o).text(o).appendTo($input); });
                    $input.val(value || f.options[0]);
                } else if (f.type === 'credentials') {
                    // Credential names come from a separate endpoint, so the options arrive after
                    // the form renders. The select stays disabled until they do, which keeps Save
                    // from submitting an empty value and clearing the stored selection.
                    $input = $('<select>').append($('<option>').val('').text('(none)')).prop('disabled', true);
                    api('GET', '/api/credentials', undefined, { silent: true }).then(function (creds) {
                        (creds || []).forEach(function (c) {
                            $('<option>').val(c.name).text(c.name).appendTo($input);
                        });
                        $input.val(value || '').prop('disabled', !!f.readonly);
                    }).catch(function () { /* the picker degrades to "(none)" only */ });
                } else if (f.type === 'password') {
                    var has = !!getPath(view, f.hasPath);
                    $input = $('<input type="password" autocomplete="new-password">')
                        .attr('placeholder', has ? '(set — leave blank to keep)' : '(not set)');
                } else {
                    $input = $('<input>').attr('type', f.type === 'number' ? 'number' : 'text')
                        .val(value === null || value === undefined ? '' : value);
                }
                $input.attr('data-path', f.path);
                // Only ever disable here: an async field (credentials) enables itself once its
                // options exist, and re-enabling it here would let Save submit an empty value.
                if (f.readonly) $input.prop('disabled', true);
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
                // The API-document toggles change what the Dashboard and Help may link to, so the
                // identity is refreshed before those tabs redraw.
                refreshIdentity();
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
            // The explorer is a separate listener, so the tab links to it here as well as on the
            // Dashboard card: this is the page that shows the toggle's state.
            var mcpExplorer = explorers.mcp || {};
            if (mcpExplorer.enabled) {
                $card.append($('<p class="card-links">')
                    .append(explorerLink(explorerUrl(mcpExplorer, mcpExplorer.scalarPath), 'API explorer', m.isRunning))
                    .append(explorerLink(explorerUrl(mcpExplorer, mcpExplorer.specPath), 'OpenAPI spec', m.isRunning)));
            }
            $view.append($('<div class="cards">').append($card));
            $view.append($('<p class="muted">').text(
                'Clients authenticate with the configured shared credential or with a proxy API key that has '
                + '"Allow MCP server access" enabled. Admin users always have access; Read Only keys need the grant.'));

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

    loaders.mcp = function () { loadMcp(); loadModules(); };

        // ------------------------------------------------------------------ test console tab
        // Talks to /api/testchat, which streams SSE frames: delta / thinking / usage / done / error.
        // The model list is filtered server-side to what this user may actually call, so there is no
        // client-side gate to bypass.
        var tcModels = [];
        var tcAbort = null;

        function tcSetStatus(text, isError) {
            $('#tc-status').text(text || '').toggleClass('err', !!isError);
        }

        function tcBusy(busy) {
            $('#tc-send').prop('disabled', busy);
            $('#tc-cancel').prop('disabled', !busy);
            $('#tc-model, #tc-prompt, #tc-system, #tc-temperature, #tc-repeat-penalty').prop('disabled', busy);
        }

        function tcApplyModelDefaults() {
            var model = tcModels.filter(function (m) { return m.name === $('#tc-model').val(); })[0];
            if (!model) return;
            var $temp = $('#tc-temperature');
            var $penalty = $('#tc-repeat-penalty');
            if (typeof model.temperature === 'number' && !$temp.val()) $temp.val(model.temperature);
            if (typeof model.repeatPenalty === 'number' && !$penalty.val()) $penalty.val(model.repeatPenalty);
            // A provider-priority setting wins over the request, so lock the input instead of letting
            // the operator type a value the proxy will overwrite.
            $temp.prop('disabled', !!model.temperatureLocked)
                .attr('title', model.temperatureLocked ? 'Temperature is fixed by this model\'s configuration.' : '');
            $penalty.prop('disabled', !!model.repeatPenaltyLocked)
                .attr('title', model.repeatPenaltyLocked ? 'Repeat penalty is fixed by this model\'s configuration.' : '');
        }

        function loadTestConsole() {
            api('GET', '/api/testchat/models').then(function (result) {
                tcModels = (result && result.models) || [];
                var $select = $('#tc-model').empty();
                if (!tcModels.length) {
                    $select.append($('<option>').val('').text('No models available'));
                    tcSetStatus('No models are available to your account. Ask an admin to grant you model access.', true);
                    return;
                }
                tcModels.forEach(function (m) { $select.append($('<option>').val(m.name).text(m.name)); });
                tcSetStatus('');
                tcApplyModelDefaults();
            }).catch(function (err) { tcSetStatus(err.message, true); });
        }

        function tcClear() {
            $('#tc-response').text('');
            $('#tc-thinking').text('');
            $('#tc-thinking-wrap').addClass('hidden');
            tcSetStatus('');
        }

        function tcAppend(target, text) {
            var el = document.getElementById(target);
            if (!el) return;
            var atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 24;
            el.textContent += text;
            if (atBottom) el.scrollTop = el.scrollHeight;
        }

        function tcSend() {
            var model = $('#tc-model').val();
            var prompt = $('#tc-prompt').val();
            if (!model) { tcSetStatus('Select a model first.', true); return; }
            if (!prompt || !prompt.trim()) { tcSetStatus('Enter a prompt first.', true); return; }

            tcClear();
            tcBusy(true);
            tcSetStatus('Sending to ' + model + '\u2026');

            var body = { model: model, prompt: prompt };
            var system = $('#tc-system').val();
            if (system && system.trim()) body.system = system;
            var temperature = parseFloat($('#tc-temperature').val());
            if (!isNaN(temperature)) body.temperature = temperature;
            var penalty = parseFloat($('#tc-repeat-penalty').val());
            if (!isNaN(penalty)) body.repeatPenalty = penalty;

            var controller = new AbortController();
            tcAbort = controller;
            var started = Date.now();
            var chunks = 0;

            fetch('/api/testchat', {
                method: 'POST',
                credentials: 'same-origin',
                headers: { 'Content-Type': 'application/json', 'Accept': 'text/event-stream' },
                body: JSON.stringify(body),
                signal: controller.signal
            }).then(function (response) {
                var contentType = response.headers.get('content-type') || '';
                if (contentType.indexOf('application/json') >= 0) {
                    return response.json().then(function (payload) {
                        throw new Error((payload && payload.error) || 'The Test Console request was rejected.');
                    });
                }
                if (!response.ok) throw new Error('Request failed with status ' + response.status + '.');
                if (!response.body || !response.body.getReader) throw new Error('This browser cannot read a streaming response.');

                var reader = response.body.getReader();
                var decoder = new TextDecoder();
                var buffer = '';

                function handleFrame(frame) {
                    var line = frame.split('\n').filter(function (l) { return l.indexOf('data:') === 0; })[0];
                    if (!line) return;
                    var payload;
                    try { payload = JSON.parse(line.slice(5).trim()); } catch (e) { return; }
                    if (payload.type === 'delta') { tcAppend('tc-response', payload.text || ''); chunks++; }
                    else if (payload.type === 'thinking') {
                        $('#tc-thinking-wrap').removeClass('hidden');
                        tcAppend('tc-thinking', payload.text || '');
                    } else if (payload.type === 'usage') {
                        var usage = payload.usage || {};
                        var promptTokens = usage.prompt_tokens || usage.promptTokens || 0;
                        var completionTokens = usage.completion_tokens || usage.completionTokens || 0;
                        if (promptTokens || completionTokens) {
                            tcSetStatus('Prompt ' + promptTokens + ' \u00b7 completion ' + completionTokens + ' tokens');
                        }
                    } else if (payload.type === 'error') {
                        tcSetStatus(payload.message || 'The upstream rejected the request.', true);
                    } else if (payload.type === 'done') {
                        var elapsed = payload.durationMs != null ? Math.round(payload.durationMs) : Date.now() - started;
                        tcSetStatus('Done in ' + elapsed + ' ms \u00b7 ' + (payload.chunks != null ? payload.chunks : chunks) + ' chunks');
                    }
                }

                function pump() {
                    return reader.read().then(function (result) {
                        if (result.done) {
                            if (buffer.trim()) handleFrame(buffer);
                            return;
                        }
                        buffer += decoder.decode(result.value, { stream: true });
                        var split;
                        while ((split = buffer.indexOf('\n\n')) >= 0) {
                            handleFrame(buffer.slice(0, split));
                            buffer = buffer.slice(split + 2);
                        }
                        return pump();
                    });
                }

                return pump();
            }).catch(function (err) {
                if (err && err.name === 'AbortError') tcSetStatus('Cancelled.');
                else tcSetStatus(err && err.message ? err.message : String(err), true);
            }).then(function () {
                tcAbort = null;
                tcBusy(false);
                tcApplyModelDefaults();
            });
        }

        loaders.testconsole = loadTestConsole;

        // ------------------------------------------------------------------ help tab
        // Content lives in js/help.js. Each section names the capability that unlocks it, using the
        // same wire names the server puts in /api/auth/me, so this page can only ever describe the
        // surfaces the signed-in user can actually reach.
        var HELP_CAPABILITY_LABELS = {
            ViewLogs: 'Requests log',
            ViewUsage: 'Usage',
            ViewDashboard: 'Dashboard',
            DashboardControl: 'Proxy controls',
            ManageUsers: 'Users and keys',
            ManageModels: 'Models',
            ViewModels: 'Models (read only)',
            ViewOwnModels: 'Your models (read only)',
            ViewRegistration: 'Registration API (read only)',
            ManageRegistration: 'Registration API (read-write)',
            TestConsole: 'Test Console',
            ManageSettings: 'Settings, credentials, and modules',
            Admin: 'Everything (Admin)'
        };

        function renderHelpAccess() {
            var $wrap = $('#help-access').empty();
            var identity = currentIdentity || {};
            var roles = identity.roles || [];
            var $card = $('<div class="card">').append($('<h3>').text('Your access'));

            var $roleList = $('<div class="help-roles">');
            if (roles.length) {
                roles.forEach(function (role) { $roleList.append($('<span class="role-pill">').text(role.label || role.key)); });
            } else {
                $roleList.append($('<span class="muted">').text('No dashboard roles'));
            }
            $card.append($roleList);

            var granted = Object.keys(capabilities).filter(function (name) { return !!capabilities[name]; })
                .map(function (name) { return HELP_CAPABILITY_LABELS[name] || name; });
            $card.append($('<p>').text(granted.length
                ? 'You can use: ' + granted.join(' \u00b7 ')
                : 'Your account holds no dashboard permissions.'));
            if (identity.username) $card.append($('<p class="muted">').text('Signed in as ' + identity.username + '.'));

            $wrap.append($card);
        }

        function loadHelp() {
            var sections = (window.KaeoHelp && window.KaeoHelp.sections) || [];
            var $sections = $('#help-sections').empty();
            var shown = 0;

            sections.forEach(function (section) {
                if (!canAny(section.capability)) return;
                shown++;

                var $card = $('<div class="help-card">');
                // A topic with command examples needs the full width; the grid is multi-column.
                if (section.wide) $card.addClass('wide');
                $card.append($('<h3>').text(section.title));
                if (section.summary) $card.append($('<p>').text(section.summary));

                if (section.table) {
                    var $head = $('<tr>');
                    (section.table.head || []).forEach(function (cell) { $head.append($('<th>').text(cell)); });
                    var $body = $('<tbody>');
                    (section.table.rows || []).forEach(function (row) {
                        var $row = $('<tr>');
                        row.forEach(function (cell) { $row.append($('<td>').text(cell)); });
                        $body.append($row);
                    });
                    $card.append($('<table class="help-table">')
                        .append($('<thead>').append($head))
                        .append($body));
                }

                if (section.items && section.items.length) {
                    var $list = $('<ul>');
                    section.items.forEach(function (item) {
                        if (typeof item === 'string') {
                            $list.append($('<li>').text(item));
                            return;
                        }
                        $list.append($('<li>')
                            .append($('<strong>').text(item.label))
                            .append(document.createTextNode(' \u2014 ' + item.text)));
                    });
                    $card.append($list);
                }

                if (section.code && section.code.length) {
                    section.code.forEach(function (block) {
                        var $block = $('<div class="help-code-block">');
                        if (block.label) $block.append($('<p class="help-code-label">').text(block.label));
                        $block.append($('<pre class="help-code">').text(block.text));
                        $card.append($block);
                    });
                }

                if (section.note) $card.append($('<p class="help-note">').text(section.note));

                if (section.links && section.links.length) {
                    var $links = $('<p class="help-links">');
                    section.links.forEach(function (link) {
                        var href = helpLinkHref(link);
                        if (!href) return;
                        $links.append($('<a target="_blank" rel="noopener noreferrer">')
                            .attr('href', href)
                            .text(link.text));
                    });
                    if ($links.children().length) $card.append($links);
                }

                $sections.append($card);
            });

            if (!shown) {
                $sections.append($('<p class="muted">').text('No help topics apply to your account yet.'));
            }
            renderHelpAccess();
        }

        loaders.help = loadHelp;

            // ------------------------------------------------------------------ polling
    var lastStatusPoll = 0;
    var lastLogPoll = 0;
    var lastStreamPoll = 0;
    var lastSyslogPoll = 0;

    // Manual is the default for both log views, so nothing is fetched until the user asks for it
    // or picks an interval. The status card keeps its own fixed 3-second cadence.
    setInterval(function () {
        if (document.hidden) return;
        var now = Date.now();
        if (activeTab === 'dashboard' && now - lastStatusPoll >= 3000) {
            lastStatusPoll = now;
            refreshDashboard();
        }

        var requestsInterval = readRefreshSeconds(REFRESH_STORAGE.requests) * 1000;
        if (activeTab === 'requests' && requestsInterval > 0) {
            if (!logsPaused && now - lastLogPoll >= requestsInterval) {
                lastLogPoll = now;
                pollLogs(false);
            }
            if (now - lastStreamPoll >= requestsInterval) {
                lastStreamPoll = now;
                loadStreams();
            }
        }

        var syslogInterval = readRefreshSeconds(REFRESH_STORAGE.syslog) * 1000;
        if (activeTab === 'syslog' && syslogInterval > 0 && now - lastSyslogPoll >= syslogInterval) {
            lastSyslogPoll = now;
            loadSyslog();
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
        $('#btn-signin').on('click', function () {
            api('POST', '/api/auth/logout', {}).then(function () {
                window.location.replace('/login.html');
            });
        });

        $('#modal-close').on('click', closeModal);
        $('#modal-backdrop').on('click', function (e) { if (e.target === this) closeModal(); });
        $(document).on('keydown', function (e) { if (e.key === 'Escape' && modalOpen) closeModal(); });

        $('#log-source').on('change', resetLogsView);
                $('#log-client').on('change', applyClientFilter);
        $('#btn-pause').on('click', function () {
            logsPaused = !logsPaused;
            $(this).text(logsPaused ? 'Resume' : 'Pause');
        });
        $('#btn-clear-logs').on('click', function () { $('#log-table tbody').empty(); updateLogCount(); });

        // Refresh cadence. Manual (0) means the toolbar buttons are the only trigger.
        $('#log-refresh').val(String(readRefreshSeconds(REFRESH_STORAGE.requests)))
            .on('change', function () { writeRefreshSeconds(REFRESH_STORAGE.requests, parseInt(this.value, 10) || 0); });
        $('#syslog-refresh').val(String(readRefreshSeconds(REFRESH_STORAGE.syslog)))
            .on('change', function () { writeRefreshSeconds(REFRESH_STORAGE.syslog, parseInt(this.value, 10) || 0); });
        $('#btn-refresh-logs').on('click', function () {
            var source = currentSource();
            pollLogs(!logLoaded[source]);
            loadStreams();
        });
        $('#btn-refresh-syslog').on('click', loadSyslog);
        $('#btn-clear-syslog').on('click', function () {
            $('#syslog-table tbody').empty();
            $('#syslog-count').text('0 rows');
        });
        $('#syslog-levels').on('change', 'input', loadSyslog);
        $('#syslog-hide-heartbeats').on('change', loadSyslog);
        var syslogSearchTimer = null;
        $('#syslog-search').on('input', function () {
            clearTimeout(syslogSearchTimer);
            syslogSearchTimer = setTimeout(loadSyslog, 300);
        });

        $('#stream-table').on('click', 'tbody tr[data-id]', function () {
            openStreamDetail($(this).attr('data-id'));
        });
        // <details> has no jQuery event; load the first snapshot as the panel opens.
        $('#streams-panel').on('click', 'summary', function () {
            var panel = document.getElementById('streams-panel');
            if (panel && panel.open) setTimeout(loadStreams, 0);
        });

        $('#btn-add-mapping').on('click', function () { mappingDialog(null); });
        $('#btn-remove-mapping').on('click', removeSelectedMapping);
        $('#btn-duplicate-mapping').on('click', duplicateSelectedMapping);
        $('#btn-configure-mapping').on('click', configureSelectedMapping);
        $('#btn-toggle-mapping').on('click', toggleSelectedMapping);
        $('#btn-refresh-mappings').on('click', loadMappings);
        $('#btn-discover-models').on('click', discoverModelsNow);
        $('#mapping-table').on('click', 'tbody tr[data-id]', function () {
            selectMapping(Number($(this).attr('data-id')));
        });
        $('#mapping-table').on('dblclick', 'tbody tr[data-id]', function () {
            selectMapping(Number($(this).attr('data-id')));
            configureSelectedMapping();
        });
        $('#btn-add-credential').on('click', function () { credentialDialog(); });
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

                $('#tc-model').on('change', function () {
                    $('#tc-temperature').val('');
                    $('#tc-repeat-penalty').val('');
                    tcApplyModelDefaults();
                });
                $('#tc-send').on('click', tcSend);
                $('#tc-clear').on('click', tcClear);
                $('#tc-cancel').on('click', function () {
                    if (tcAbort) tcAbort.abort();
                });
                // Ctrl+Enter sends, matching the Windows GUI's Test Console.
                $('#tc-prompt').on('keydown', function (e) {
                    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); tcSend(); }
                });

        // Role labels drive the user cards and the role reference in Help, so refresh the fallback
                // list once at boot. A failure just leaves the built-in copy in place.
                api('GET', '/api/auth/roles', undefined, { silent: true })
                    .then(function (result) {
                        if (result && result.roles && result.roles.length) DASHBOARD_ROLES = result.roles;
                    })
                    .catch(function () { /* keep the built-in list */ });

                api('GET', '/api/auth/me', undefined, { silent: true })
                    .then(function (identity) {
                        setIdentity(identity);
                        activateTab(location.hash.replace('#', '') || 'dashboard');
                    })
                    .catch(function (err) { if (err.status !== 401) setConn('err'); });
    });
})(jQuery);