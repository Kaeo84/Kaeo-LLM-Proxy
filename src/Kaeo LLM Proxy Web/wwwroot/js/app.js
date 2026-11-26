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
    var dashboardRole = 'admin';
    var currentIdentity = null;
    var loaders = {};

    function setIdentity(identity) {
        currentIdentity = identity || null;
        applyDashboardRole(currentIdentity && currentIdentity.role);
        var loginRequired = !currentIdentity || currentIdentity.loginRequired !== false;
        $('#btn-signin').toggle(loginRequired)
            .text(currentIdentity && currentIdentity.username ? 'Sign out' : 'Sign in')
            .attr('title', currentIdentity && currentIdentity.username ? 'Signed in as ' + currentIdentity.username : 'Sign in to the dashboard');
    }

    function applyDashboardRole(role) {
        dashboardRole = role === 'readOnly' ? 'readOnly' : 'admin';
        applyLogSourceRole();
        $('#tabs a').show();
        $('#topbar').removeClass('read-only');
        if (dashboardRole !== 'readOnly') return;

        $('#tabs a').not('[data-tab="requests"],[data-tab="usage"]').hide();
        $('#topbar').addClass('read-only');
        $('#tabs a[data-tab="requests"], #tabs a[data-tab="usage"]').show();
        if (location.hash !== '#requests' && location.hash !== '#usage') location.hash = 'requests';
        activateTab(location.hash === '#usage' ? 'usage' : 'requests');
    }

    function activateTab(name) {
        if (dashboardRole === 'readOnly' && name !== 'requests' && name !== 'usage') name = 'requests';
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
        $('<button type="button">').text('Restart').prop('disabled', !s.proxy.isRunning)
            .on('click', function () { api('POST', '/api/proxy/restart').then(refreshDashboard); }).appendTo($proxyActions);
        $proxy.append($proxyActions);
        if (s.proxy.error) $proxy.append($('<p class="card-error">').text(s.proxy.error));
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
        $mcp.append($mcpActions);
        $cards.append($mcp);

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

    // Read Only sessions only see their own proxy requests; MCP/non-proxied traffic is not
    // attributed to API-key users, so those sources are admin-only.
    function applyLogSourceRole() {
        var $source = $('#log-source');
        var keep = $source.val() || 'proxy';
        $source.empty();
        [['proxy', 'Proxy'], ['mcp', 'MCP'], ['nonproxied', 'Non-proxied']].forEach(function (s) {
            if (dashboardRole === 'readOnly' && s[0] !== 'proxy') return;
            $source.append($('<option>').val(s[0]).text(s[1]));
        });
        $source.val(keep);
        if (!$source.val()) $source.val('proxy');
    }

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
        // Fresh load omits `after`: the server answers newest-first from the database (the full
        // retention window, even when the in-memory queue has rolled). Polls send the cursor.
        var url = '/api/logs?source=' + encodeURIComponent(source) + '&limit=' + (initial ? MAX_LOG_ROWS : 200);
        if (!initial) url += '&after=' + logCursors[source];
        api('GET', url, undefined, { silent: true })
            .then(function (data) {
                logCursors[source] = data.nextAfter || logCursors[source];
                var $tbody = $('#log-table tbody');
                var entries = data.entries || [];
                if (initial) {
                    entries.forEach(function (entry) { $tbody.append(logRow(entry)); });
                } else {
                    // Incremental batches arrive oldest-first; prepend reversed so newest stays on top.
                    for (var i = entries.length - 1; i >= 0; i--) $tbody.prepend(logRow(entries[i]));
                }
                while ($tbody.children().length > MAX_LOG_ROWS) $tbody.children().last().remove();
                updateLogCount();
            })
            .catch(function () { /* transient poll failure — next tick retries */ })
            .then(function () { logBusy = false; });
    }

    function logRow(e) {
        var statusName = STATUS_NAMES[e.status] || String(e.status);
        var $row = $('<tr>')
            .append($('<td>').text(new Date(e.timestamp).toLocaleTimeString()))
            .append($('<td>').text(e.requestor || ''))
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

        $('#modal-body').empty().append($body);
    }

    loaders.requests = function () { resetLogsView(); loadStreams(); };

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
                $('#stream-detail').addClass('hidden');
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

        // The list omits the captured tails (up to 64 KB per stream); they load on row click.
        function openStreamDetail(id) {
            selectedStreamId = id;
            $('#stream-table tbody tr[data-id]').each(function () {
                $(this).toggleClass('selected', $(this).attr('data-id') === id);
            });
            $('#stream-detail').removeClass('hidden');
            $('#stream-raw, #stream-forwarded').text('Loading\u2026');
            api('GET', '/api/streams/detail?id=' + encodeURIComponent(id), undefined, { silent: true })
                .then(function (s) {
                    $('#stream-raw').text(s.rawUpstreamText || '(no upstream bytes captured yet)');
                    $('#stream-forwarded').text(s.forwardedText || '(nothing forwarded to the client yet)');
                })
                .catch(function (err) {
                    $('#stream-raw, #stream-forwarded').text('');
                    toast(err.message, true);
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
                $tbody.append($('<tr>').append($('<td colspan="7" class="muted">').text('No model mappings yet.')));
                updateMappingButtons();
                return;
            }
            mappingCache.forEach(function (m) {
                var model = m.LlamaCppName || '';
                $tbody.append($('<tr>')
                    .attr('data-id', m.id)
                    .toggleClass('selected', m.id === selectedMappingId)
                    .append($('<td>').append($('<span class="pill">').addClass(m.isEnabled ? 'ok' : '').text(m.isEnabled ? 'yes' : 'no')))
                    .append($('<td>').text(m.OllamaName))
                    .append($('<td class="truncate">').attr('title', model).text(truncateText(model, UPSTREAM_MODEL_MAX_CHARS)))
                    .append($('<td>').text(m.instructionSetName || '\u2014'))
                    .append($('<td>').text(upstreamTypeName(m.upstreamType)))
                    .append($('<td>').text(m.credentialName || '\u2014'))
                    .append($('<td>').text(m.hidden ? 'yes' : 'no')));
            });
            updateMappingButtons();
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
                    .append(kv('Dashboard login', u.dashboardLoginEnabled
                        ? u.dashboardUsername + ' · ' + (u.dashboardRole === 'admin' ? 'Admin' : 'Read Only')
                        : 'disabled'))
                    .append(kv('Created', u.createdUtc ? new Date(u.createdUtc).toLocaleString() : '—'));

                var $keys = $('<div class="user-keys">');
                if (!(u.keys || []).length) {
                    $keys.append($('<p class="muted">').text('No API keys.'));
                } else {
                    u.keys.forEach(function (k) {
                        var $row = $('<div class="key-row">');
                        var $head = $('<div class="key-head">')
                            .append($('<span class="key-name">').text(k.label || '(unlabeled)'))
                            .append($('<span class="key-prefix">').text(k.keyPrefix + '\u2026'));
                        var $keyActions = $('<span class="key-actions">');
                        $('<button type="button" class="ghost">').text('Edit').on('click', function () {
                            keyDialog(u, k);
                        }).appendTo($keyActions);
                        $('<button type="button" class="ghost danger">').text('Delete').on('click', function () {
                            confirmDialog('Delete API key', 'Delete key "' + (k.label || k.keyPrefix) + '" for ' + u.name + '? Clients using it lose access immediately.', function () {
                                api('DELETE', '/api/keys/' + k.id).then(function () { toast('Key deleted.'); loadUsers(); });
                            });
                        }).appendTo($keyActions);
                        $head.append($keyActions);
                        $row.append($head);
                        $row.append($('<div class="key-meta">').text(
                            ((k.allowedModels && k.allowedModels.length) ? k.allowedModels.join(', ') : 'all models')
                            + ' \u00b7 last used ' + (k.lastUsedUtc ? new Date(k.lastUsedUtc).toLocaleString() : 'never')));
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
        var $loginEnabled = $('<input type="checkbox">').prop('checked', !!(existing && existing.dashboardLoginEnabled));
        $body.append($('<div class="field bool">').append($loginEnabled).append($('<label>').text('Enable dashboard login')));
        var $username = field('Dashboard username', $('<input type="text" autocomplete="username">')
            .val(existing ? existing.dashboardUsername : ''), 'Separate from the proxy API keys used by model clients.');
        var $password = field('Dashboard password', $('<input type="password" autocomplete="new-password">'),
            existing && existing.hasDashboardPassword ? 'Leave blank to keep the current password; enter a new one to reset it.' : 'Use at least 12 characters.');
        var $role = $('<select>').append($('<option>').val('admin').text('Admin — full dashboard access'))
            .append($('<option>').val('readOnly').text('Read Only — request logs only'))
            .val(existing ? existing.dashboardRole : 'readOnly');
        field('Dashboard role', $role, 'This role controls the web dashboard only, not model access through proxy API keys.');

        function syncLoginFields() {
            var disabled = !$loginEnabled.prop('checked');
            $username.add($password).add($role).prop('disabled', disabled);
        }
        $loginEnabled.on('change', syncLoginFields);
        syncLoginFields();

        function save() {
            var name = $name.val().trim();
            var username = $username.val().trim();
            var password = $password.val();
            var enabled = $loginEnabled.prop('checked');
            if (!name) { modalError('User name is required.'); return; }
            if (enabled && !password && !(existing && existing.hasDashboardPassword)) {
                modalError('Set a password when enabling a dashboard login.');
                return;
            }
            var payload = {
                name: name,
                email: $email.val().trim(),
                dashboardLoginEnabled: enabled,
                dashboardUsername: username,
                dashboardRole: $role.val()
            };
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

    // ------------------------------------------------------------------ usage tab
    var USAGE_COLORS = ['#2563eb', '#16a34a', '#d97706', '#dc2626', '#7c3aed', '#0891b2', '#db2777', '#65a30d', '#ea580c', '#475569', '#0d9488', '#9333ea'];
    var usageState = { period: 'thisMonth', group: 'day', model: '', user: 'all' };
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
        if (dashboardRole !== 'readOnly' && usageState.user) url += '&userId=' + encodeURIComponent(usageState.user);
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

        var isAdmin = dashboardRole !== 'readOnly';
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
                { path: 'webDashboard.requireLogin', label: 'Require login', type: 'bool', hint: 'Applies immediately. Off = anyone who can reach the dashboard gets full admin access — use only on a trusted network.' },
                { path: 'webDashboard.scheme', label: 'Scheme', type: 'select', options: ['Http', 'Https'] },
                { path: 'webDashboard.certificatePath', label: 'HTTPS certificate path (PFX)', type: 'text' },
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
                    if (f.hint) $boolField.append($('<span class="hint">').text(f.hint));
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

    loaders.mcp = function () { loadMcp(); loadModules(); };

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
        $('#btn-refresh-logs').on('click', function () { pollLogs(false); loadStreams(); });
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
        $('#mapping-table').on('click', 'tbody tr[data-id]', function () {
            selectMapping(Number($(this).attr('data-id')));
        });
        $('#mapping-table').on('dblclick', 'tbody tr[data-id]', function () {
            selectMapping(Number($(this).attr('data-id')));
            configureSelectedMapping();
        });
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

        api('GET', '/api/auth/me', undefined, { silent: true })
            .then(function (identity) {
                setIdentity(identity);
                if (dashboardRole !== 'readOnly')
                    activateTab(location.hash.replace('#', '') || 'dashboard');
            })
            .catch(function (err) { if (err.status !== 401) setConn('err'); });
    });
})(jQuery);