/* Dashboard help content.
 *
 * Each section names the capability that unlocks it, using the same wire names the server puts in
 * /api/auth/me (ViewLogs, ViewUsage, ViewDashboard, DashboardControl, ManageUsers, ManageModels,
 * TestConsole, ManageSettings, Admin). app.js renders only the sections the signed-in user can
 * actually use, so this page can never describe a surface the caller cannot reach. A null
 * capability means "everyone signed in".
 *
 * Items are either a plain string or { label, text } to lead with a bold term.
 */
window.KaeoHelp = {
    sections: [
        {
            id: 'start',
            title: 'Finding your way around',
            capability: null,
            summary: 'The dashboard is served by the proxy itself. What you can see is decided by the roles on your account, not by the tab you type into the address bar — the server re-checks every call.',
            items: [
                { label: 'Navigation', text: 'Only the tabs your roles allow are listed. A missing tab is not merely hidden: its API calls are refused with a message naming the role you would need.' },
                { label: 'Help', text: 'Always available, and filtered the same way — a topic appears only when you hold the role that unlocks it.' },
                { label: 'Theme', text: 'The Auto / Light / Dark button in the top-right cycles the theme and remembers your choice for this browser.' },
                { label: 'Connection', text: 'The pill beside the theme button shows API state. Red means the last dashboard call failed; it retries on the next poll.' },
                { label: 'Sign out', text: 'Ends your dashboard session. Proxy API keys are unaffected, so clients using them keep working.' }
            ],
            note: 'Missing something? An Admin or a User Admin has to grant the role that unlocks it.'
        },
        {
            id: 'dashboard',
            title: 'Dashboard',
            capability: 'ViewDashboard',
            summary: 'Six cards: proxy, MCP server, web server, performance, modules, and dashboard identity, plus a Registration API card when that document is published. The card set refreshes every three seconds while the tab is open.',
            items: [
                { label: 'Proxy', text: 'Listen address, port, running state, and version, plus the last start error when a bind failed. When the API explorer is enabled the card also links to the explorer and the OpenAPI document.' },
                { label: 'MCP server', text: 'Running state, status text, and the endpoint URL clients connect to, with the same explorer links when the MCP explorer is enabled.' },
                { label: 'Web server', text: 'State, URL, listen address, and the certificate credential in use (a dash means plain HTTP).' },
                { label: 'Performance', text: 'Process CPU, working set, Gen 2 collections, and large-object-heap size.' },
                { label: 'Modules', text: 'Registered, enabled, and running counts for the imported MCP modules.' },
                { label: 'Dashboard', text: 'Auth mode (login or open), scheme, and the database path this instance is using.' },
                { label: 'Registration API', text: 'The document path and a link, shown only to roles that can read it and only while it is published.' }
            ],
            note: 'A red banner means the database could not be opened and this instance is running on a private in-memory store, so changes will not survive a restart. Explorer links disappear when the document is switched off on the Settings tab, and go grey while the server they belong to is stopped.'
        },
        {
            id: 'controls',
            title: 'Starting and stopping the proxy',
            capability: 'DashboardControl',
            summary: 'Start, Stop, and Restart for the proxy, the MCP server, and the web server, from the Dashboard cards.',
            items: [
                { label: 'Proxy', text: 'Start binds the listen address, Stop drains in-flight requests and closes it, Restart rebinds without leaving the page.' },
                { label: 'MCP server', text: 'The same three buttons for the MCP listener on its own port.' },
                { label: 'Web server', text: 'Stop asks for confirmation first because it disconnects this page; restart it afterwards from the Windows app or the headless console.' }
            ],
            note: 'Stop never changes the Enabled setting, so the next proxy start brings the component back.'
        },
        {
            id: 'requests',
            title: 'Requests and request details',
            capability: 'ViewLogs',
            summary: 'Newest-first rows read from the request-log database, not just the small in-memory window, then topped up as new entries arrive.',
            items: [
                { label: 'Source', text: 'Proxy, MCP, Non-proxied, or Web. Only Admin sees anything but Proxy, because the other sources are not attributed to API-key users.' },
                { label: 'Refresh', text: 'Manual by default, or 5 / 10 / 30 / 60 seconds. Manual means the Refresh button is the only trigger.' },
                { label: 'Pause', text: 'Freezes the table so rows stop sliding while you read. Pause only stops the timer; Refresh still works, and the button reads Resume while it is held.' },
                { label: 'Client', text: 'Hides loaded rows whose detected client app does not match. The filter survives refreshes because it is applied to the rows, not the query.' },
                { label: 'Row', text: 'Date, time, requestor, method, path, model, status, duration, prompt and completion tokens, and tokens per second.' },
                { label: 'Details', text: 'Click a row. The table carries summaries only; bodies, headers, and proxy notes are fetched on demand as the modal opens.' },
                { label: 'Clear view', text: 'Empties the table without deleting anything. Refresh loads the history again.' }
            ],
            note: 'Requestor is the part of the user\u2019s email before @, and blank when the request carried no key. The path (web test console) marks a run from the Test Console tab.'
        },
        {
            id: 'streams',
            title: 'Live streams',
            capability: 'Admin',
            summary: 'The Live streams panel at the top of Requests shows SSE streams in flight plus those that finished in the last fifteen minutes.',
            items: [
                { label: 'Row', text: 'State, start time, kind, method, path, model, upstream and client byte counts, and frame counts. The list shows fifteen rows at a time and scrolls for more.' },
                { label: 'Detail', text: 'Click a row to open a dialog with the captured raw upstream tail beside the bytes forwarded to the client. Close it with the button, the ×, Escape, or a click outside.' }
            ],
            note: 'Admin-only: the panes contain other users\u2019 payload text.'
        },
        {
            id: 'syslog',
            title: 'Sys Log',
            capability: 'Admin',
            summary: 'The application\u2019s own log, straight from the running process.',
            items: [
                { label: 'Levels', text: 'Tick the Serilog levels to include. With none ticked the table stays empty and a hint says so.' },
                { label: 'Search', text: 'Filters on message text as you type.' },
                { label: 'Hide heartbeats', text: 'Drops the periodic upstream heartbeat lines so real events stay visible.' },
                { label: 'Refresh', text: 'Manual or 5 / 10 / 30 / 60 seconds. Clear view empties the table without touching the log files.' }
            ],
            note: 'Admin-only. The line above the table reports the log folder state and any read error.'
        },
        {
            id: 'usage',
            title: 'Usage',
            capability: 'ViewUsage',
            summary: 'Token consumption counted from the request-log database.',
            items: [
                { label: 'Period', text: 'This week (the default), last week, this or last month, last 30 or 90 days. Group the stacked chart by day, week, or month.' },
                { label: 'Model', text: 'Filter to a single model, or All models.' },
                { label: 'User', text: 'Admin only: All users, yourself, or one user, plus a Usage by user breakdown with per-user totals and share bars.' },
                { label: 'By day', text: 'Click a day to expand its per-model rows: requests, prompt, completion, total, cached prompt, and reasoning tokens.' }
            ],
            note: 'Only requests that reported token usage are counted, so zero-token probes are excluded. Requests without a key appear as Unattributed, and days use the server\u2019s local time zone.'
        },
        {
            id: 'models',
            title: 'Models',
            capability: ['ManageModels', 'ViewModels', 'ViewOwnModels'],
            summary: 'The model mappings clients see and what each one points at.',
            items: [
                { label: 'Add model', text: 'Proxy name (what clients request), upstream model, upstream URL, credential, and type.' },
                { label: 'Configure', text: 'The full editor: sampling, thinking handling, reasoning effort, context window, instruction set, heartbeats, redaction, Copilot compatibility, and compaction thresholds.' },
                { label: 'Toggle enabled / Duplicate / Remove', text: 'Mapping lifecycle. Double-click a row to open Configure.' },
                { label: 'Columns', text: 'Enabled, proxy name, upstream model, instruction set, type, credential, hidden, and Auto.' },
                { label: 'Auto-managed', text: 'Tick “Auto-managed by upstream” in the model editor. That mapping becomes a discovery source: every model the upstream lists is registered automatically, using the auto-managed mapping as the template for settings and credential.' },
                { label: 'Auto column', text: '“yes” means discovery registered the model, “off” means discovery disabled it because the model stayed missing. Hover the pill for the reason.' },
                { label: 'Discover now', text: 'Runs one scan immediately instead of waiting for the interval. The result lists every model registered, re-enabled, or disabled.' }
            ],
            note: 'Models Read Only shows every mapping; Own Models Read Only shows only the models granted to you. Neither can change anything — the toolbar buttons belong to Model Admin. A disabled or hidden mapping disappears from /api/tags and /v1/models immediately, so clients stop offering it; a hidden mapping can still be reached by its exact name. Discovery only disables a model after a successful model-list read shows it missing for the whole offline grace period, so an upstream outage never unregisters everything. A model that comes back is re-enabled — but a model you switched off yourself stays off, and saving a model in the editor clears any discovery-applied disable.',
            links: [
                { explorer: 'registration', text: 'Registration API document (the same mappings, described for scripts)' }
            ]
        },
        {
            id: 'users',
            title: 'Users, API keys, and access',
            capability: 'ManageUsers',
            summary: 'Accounts, their roles, their API keys, and what each one can reach.',
            items: [
                { label: 'Add user', text: 'Name (required), email, dashboard login with username and password, roles, model access, and MCP access.' },
                { label: 'Roles combine', text: 'The permissions are the union of the roles held. Admin always holds everything, and a user with no valid role has no dashboard access at all.' },
                { label: 'Grants belong to the user', text: 'Model and MCP access sit on the user record, so every API key that user holds inherits them.' },
                { label: 'MCP modules', text: 'With MCP access on, leave every module box clear to allow every module, or tick specific modules to limit the user to exactly those. A tool from an unticked module is not even listed, and calling it is refused.' },
                { label: 'Card menu', text: 'The 3-dot button holds Create API key, Edit user, Enable or Disable user, and Delete user.' },
                { label: 'Key menu', text: 'The 3-dot button on a key holds Reveal key, Edit label, Regenerate key, and Delete key.' },
                { label: 'Service accounts', text: 'Create a user with only Registration Read Only or Registration Read-Write, turn dashboard login off, and give it one API key. That key can then call /api/mappings with an Authorization: Bearer header and nothing else.' }
            ],
            note: 'An empty model list means no models, not all models. An Admin user always reaches every model and the MCP server regardless of their grants.'
        },
        {
            id: 'registration',
            title: 'Registration API for service accounts',
            capability: ['ViewRegistration', 'ManageRegistration', 'ManageUsers', 'ManageModels'],
            wide: true,
            summary: 'Register and unregister models from a script or a machine that cannot hold a dashboard login. This topic and its OpenAPI document are shown only to the roles that use them: Admin, User Admin, Model Admin, Registration Read Only, and Registration Read-Write. Every other role gets a 403 from the routes, not just a hidden topic.',
            table: {
                head: ['Call', 'Capability it needs'],
                rows: [
                    ['GET /api/mappings', 'Registration Read Only'],
                    ['GET /api/mappings/discovery', 'Registration Read Only'],
                    ['GET /api/mappings/openapi.json', 'Registration Read Only'],
                    ['POST /api/mappings', 'Registration Read-Write'],
                    ['PUT /api/mappings/{id}', 'Registration Read-Write'],
                    ['DELETE /api/mappings/{id}', 'Registration Read-Write'],
                    ['POST /api/mappings/discover', 'Registration Read-Write']
                ]
            },
            items: [
                { label: 'Credentials', text: 'Send the key as an Authorization: Bearer header or as x-api-key. A dashboard session cookie works too, but a service account never needs one.' },
                { label: 'Create', text: 'POST a mapping with OllamaName (the name clients request) and LlamaCppName (the upstream model), plus upstreamUrl, upstreamType, credentialName, and whatever else you want. The reply carries the id the proxy assigned.' },
                { label: 'Update', text: 'PUT is a full replace, not a patch: read the mapping, change what you need, and send the whole object back. The id in the path wins over any id in the body.' },
                { label: 'Delete', text: 'Unregisters the model, so clients asking for that proxy name get 404 from then on.' },
                { label: 'Discovery', text: 'GET /api/mappings/discovery reports the settings, whether a scan is running, and what the last one did; POST /api/mappings/discover runs one scan immediately.' },
                { label: 'Scope', text: 'The key reaches /api/mappings and nothing else: users, keys, settings, credentials, modules, logs, and the dashboard tabs all stay refused even for an Admin user\u2019s key.' },
                { label: 'Revoking', text: 'Disabling the user or deleting the key takes effect within a few seconds, and dashboard-login settings never apply to a key.' }
            ],
            code: [
                {
                    label: 'Register a model from a startup script',
                    text: 'export KAEO_URL="https://proxy.example.com:8443"\nexport KAEO_API_KEY="kaeo-..."\n\ncurl -sS -X POST "$KAEO_URL/api/mappings" \\\n  -H "Authorization: Bearer $KAEO_API_KEY" \\\n  -H "Content-Type: application/json" \\\n  -d \'{"OllamaName":"qwen3-8b","LlamaCppName":"qwen3-8b","upstreamUrl":"http://10.0.0.5:11434","upstreamType":2,"credentialName":"mac-studio"}\''
                },
                {
                    label: 'Unregister it again',
                    text: 'curl -sS -X DELETE "$KAEO_URL/api/mappings/42" \\\n  -H "Authorization: Bearer $KAEO_API_KEY"'
                },
                {
                    label: 'Run a discovery scan now instead of waiting for the interval',
                    text: 'curl -sS -X POST "$KAEO_URL/api/mappings/discover" \\\n  -H "Authorization: Bearer $KAEO_API_KEY"'
                }
            ],
            note: 'A registration role grants no dashboard tabs, so a service account that does sign in sees an empty dashboard — that is intentional. The document lives on the dashboard port rather than at the proxy’s /openapi/v1/openapi.json because these are dashboard routes, and it needs the same grant they do. It can be switched off on the Settings tab, which answers 404 here and hides this topic’s link.',
            links: [
                { explorer: 'registration', text: 'OpenAPI document (import into Postman, Insomnia, or Swagger Editor)' },
                { href: '/api/mappings', text: 'Mapping list as JSON' }
            ]
        },
        {
            id: 'roles',
            title: 'Role reference',
            capability: 'ManageUsers',
            summary: 'Every role a user can hold. Roles are additive, so a user usually needs more than one.',
            table: {
                head: ['Role', 'What it adds'],
                rows: [
                    ['Admin', 'Everything: every model, every MCP tool, every tab, and every action.'],
                    ['Read Only', 'The Requests log and Usage, limited to that user\u2019s own traffic.'],
                    ['Models Read Only', 'Read Only plus the Models tab in read-only form, listing every model mapping.'],
                    ['Own Models Read Only', 'Read Only plus the Models tab in read-only form, limited to the models granted to that user.'],
                    ['Dashboard Read Only', 'The Dashboard tab: state, performance, and traffic totals, without the control buttons.'],
                    ['Dashboard Read-Write', 'Dashboard Read Only plus start, stop, and restart for the proxy, the MCP server, and the web server.'],
                    ['User Admin', 'Read Only plus adding, updating, and removing users, roles, API keys, and grants.'],
                    ['Model Admin', 'Read Only plus adding, updating, and removing model mappings.'],
                    ['Test Console', 'The Test Console tab, limited to the models that user can access.'],
                    ['Registration Read Only', 'The model registration API in read-only form. No dashboard tabs; intended for a service account API key.'],
                    ['Registration Read-Write', 'Registration Read Only plus registering, updating, and removing models and running a discovery scan. No dashboard tabs; intended for a service account API key.']
                ]
            },
            note: 'Sys Log, the live stream panes, Settings, Credentials, and Modules stay Admin-only whatever else is granted, because they expose payload text and stored secrets.'
        },
        {
            id: 'testconsole',
            title: 'Test Console',
            capability: 'TestConsole',
            summary: 'Send a prompt straight through the proxy and watch the answer stream back into the page.',
            items: [
                { label: 'Model', text: 'Only the models your account can access. The server enforces the same list, so a forged request is refused with 403.' },
                { label: 'Inputs', text: 'Prompt, an optional system prompt, temperature, and repeat penalty. Ctrl+Enter (Cmd+Enter on macOS) sends.' },
                { label: 'Output', text: 'Deltas stream into Response, thinking-mode output lands in its own pane, and token usage plus elapsed time show in the status line.' },
                { label: 'Cancel / Clear', text: 'Cancel aborts the upstream request; Clear empties both panes.' }
            ],
            note: 'Temperature and repeat penalty are pre-filled from the model mapping and locked when the mapping fixes that value, so you cannot send a setting the proxy would overwrite. Every run is logged with the path (web test console).'
        },
        {
            id: 'credentials',
            title: 'Credentials',
            capability: 'ManageSettings',
            summary: 'Named secrets, referenced by name from model mappings, MCP auth, and the HTTPS certificate.',
            items: [
                { label: 'Fields', text: 'Name, description, username, secret, certificate, and private key.' },
                { label: 'Write-only', text: 'After saving, a secret is never shown again. Leaving a field blank while editing keeps the stored value, so a description can change without re-pasting anything.' }
            ],
            note: 'The HTTPS certificate is a credential too: store the PFX as base64 with its export password, or the PEM chain with its private key, then select it in Settings.'
        },
        {
            id: 'settings',
            title: 'Settings',
            capability: 'ManageSettings',
            summary: 'Proxy, web dashboard, MCP, logging, and default behaviour.',
            items: [
                { label: 'Proxy', text: 'Listen address and port, plus the multi-instance setting.' },
                { label: 'Web dashboard', text: 'Enabled, scheme, port, listen address, require login, and the HTTPS certificate credential.' },
                { label: 'Model auto-discovery', text: 'The master switch, the scan interval, and the offline grace period. Changes apply to the running scan loop without a restart.' },
                { label: 'API documents', text: 'Proxy API explorer serves /scalar and the proxy OpenAPI document; Registration API document serves /api/mappings/openapi.json. Each has its own switch, and the MCP explorer follows the MCP server settings instead \u2014 it has nothing to describe while that server is off.' },
                { label: 'Applied when', text: 'Save applies what it can immediately; listen-address and web-port changes take effect on the next restart.' }
            ],
            note: 'Turning Require login off gives everyone who can reach the dashboard full Admin access. Only use it on an isolated, trusted network.'
        },
        {
            id: 'mcp',
            title: 'MCP server',
            capability: 'ViewDashboard',
            summary: 'The built-in Model Context Protocol server and the tools it currently exposes.',
            items: [
                { label: 'Server', text: 'State, endpoint, listen address, API explorer on or off, auth credential name, and the request/response detail collection flags.' },
                { label: 'Tools', text: 'Every tool currently exposed, with the registry id of the module that contributed it.' }
            ],
            note: 'MCP clients authenticate with a proxy API key or the shared bearer credential. A key whose user lacks MCP access gets 403.',
            links: [
                { explorer: 'mcp', path: '/scalar', text: 'MCP API explorer (Scalar)' },
                { explorer: 'mcp', path: '/openapi/v1/openapi.json', text: 'MCP OpenAPI document' }
            ]
        },
        {
            id: 'modules',
            title: 'Tool modules',
            capability: 'ManageSettings',
            summary: 'Imported .kaeomod packages that add tools to the MCP server.',
            items: [
                { label: 'Card', text: 'Module id, version, enabled, registration time, and the signer key id when the package was signed.' },
                { label: 'Configure', text: 'Edits the module settings as JSON. Invalid JSON or out-of-range values are rejected by the module itself.' },
                { label: 'Upload package', text: 'Imports a .kaeomod file into the running instance.' }
            ],
            note: 'Packages with native dependencies are per-platform, so a Windows package will not load on macOS or Linux.'
        },
        {
            id: 'clients',
            title: 'Connecting clients',
            capability: null,
            summary: 'Point any Ollama or OpenAI-compatible client at the proxy.',
            items: [
                { label: 'Ollama clients', text: 'Base URL http://<host>:11434.' },
                { label: 'OpenAI-compatible clients', text: 'Base URL http://<host>:11434/v1. The /v1 prefix is optional; the proxy normalises either shape.' },
                { label: 'Model name', text: 'Use the mapping\u2019s proxy name, which is what /api/tags and /v1/models report.' }
            ],
            note: 'Clients authenticate with a proxy API key from the Users tab. A request that carries no key is logged as unattributed.',
            links: [
                { explorer: 'proxy', path: '/scalar', text: 'Proxy API explorer (Scalar)' },
                { explorer: 'proxy', path: '/openapi/v1/openapi.json', text: 'Proxy OpenAPI document' }
            ]
        },
        {
            id: 'troubleshooting',
            title: 'Troubleshooting',
            capability: null,
            summary: 'First places to look when something does not work.',
            items: [
                { label: 'A tab is missing', text: 'Your roles do not include it. Ask an Admin or a User Admin.' },
                { label: '403 from the proxy', text: 'The user\u2019s model or MCP grant does not cover the request.' },
                { label: '401 from the dashboard', text: 'The session expired, or the account was disabled, had its password changed, or had dashboard login turned off.' },
                { label: 'A client does not offer a model', text: 'It is disabled or hidden on the Models tab.' },
                { label: 'A stream hangs', text: 'Open the Live streams panel (Admin) and check the upstream frame count and captured tails.' }
            ],
            note: 'The Sys Log tab (Admin) carries the application log with the full error text behind most failures.'
        }
    ]
};