namespace Kaeo.LlmProxy.Core.Models;

/// <summary>
/// One thing a dashboard session is allowed to do. Roles are bundles of these; a user may hold
/// several roles at once and the effective set is the union, so "User Admin" plus "Dashboard Read
/// Only" means the user can manage people and watch the dashboard but cannot press its buttons.
/// </summary>
[Flags]
internal enum DashboardCapability
{
    None = 0,

    /// <summary>The Requests log — every user's rows for an Admin, only their own otherwise.</summary>
    ViewLogs = 1 << 0,

    /// <summary>The Usage tab (charts, per-day breakdown, usage by user).</summary>
    ViewUsage = 1 << 1,

    /// <summary>The Dashboard tab: proxy/MCP/web-server state, performance, traffic counters.</summary>
    ViewDashboard = 1 << 2,

    /// <summary>Start/stop/restart the proxy, the MCP server, and the web dashboard.</summary>
    DashboardControl = 1 << 3,

    /// <summary>Add/update/remove users, their roles, their API keys, and their access grants.</summary>
    ManageUsers = 1 << 4,

    /// <summary>Add/update/remove model mappings.</summary>
    ManageModels = 1 << 5,

    /// <summary>Send test prompts through the proxy from the dashboard's Test Console tab.</summary>
    TestConsole = 1 << 6,

    /// <summary>Settings, credentials, and modules — the secrets-bearing surfaces.</summary>
    ManageSettings = 1 << 7,

    /// <summary>Held only by the Admin role; implies every other capability.</summary>
    Admin = 1 << 8,

    /// <summary>The Models tab in read-only form, listing every model mapping on the host.</summary>
    ViewModels = 1 << 9,

    /// <summary>
    /// The Models tab in read-only form, limited to the models granted to the signed-in user.
    /// The wider <see cref="ViewModels"/> grant wins when a user holds both.
    /// </summary>
    ViewOwnModels = 1 << 10,

    /// <summary>
    /// The model registration API in read-only form: list model mappings, read the discovery
    /// state, and read the registration OpenAPI document. This is the surface a service account's
    /// API key may reach.
    /// </summary>
    ViewRegistration = 1 << 11,

    /// <summary>
    /// Register, update, and unregister model mappings and trigger a discovery scan — the
    /// machine-facing registration surface a service account's API key drives.
    /// </summary>
    ManageRegistration = 1 << 12,
}

/// <summary>
/// A selectable dashboard role: the stored token, the label the two GUIs show, and the
/// capabilities it grants.
/// </summary>
internal sealed record DashboardRoleDefinition(
    string Key,
    string Label,
    string Description,
    DashboardCapability Capabilities);

/// <summary>
/// The dashboard role vocabulary shared by the web dashboard, the Windows GUI, and the login
/// path. A user's roles are persisted as a comma-separated list of <see cref="DashboardRoleDefinition.Key"/>
/// values in the existing <c>dashboard_role</c> column, so a single-role database (the old
/// <c>admin</c> / <c>readOnly</c> values) keeps working unchanged.
/// </summary>
internal static class DashboardRoles
{
    public const string Admin = "admin";
    public const string ReadOnly = "readOnly";
    public const string ModelsReadOnly = "modelsReadOnly";
    public const string OwnModelsReadOnly = "ownModelsReadOnly";
    public const string DashboardReadOnly = "dashboardReadOnly";
    public const string DashboardReadWrite = "dashboardReadWrite";
    public const string UserAdmin = "userAdmin";
    public const string ModelAdmin = "modelAdmin";
    public const string TestConsole = "testConsole";
    public const string RegistrationReadOnly = "registrationReadOnly";
    public const string RegistrationReadWrite = "registrationReadWrite";

    private const DashboardCapability AllCapabilities =
        DashboardCapability.ViewLogs
        | DashboardCapability.ViewUsage
        | DashboardCapability.ViewDashboard
        | DashboardCapability.DashboardControl
        | DashboardCapability.ManageUsers
        | DashboardCapability.ManageModels
        | DashboardCapability.TestConsole
        | DashboardCapability.ManageSettings
        | DashboardCapability.Admin
        | DashboardCapability.ViewModels
        | DashboardCapability.ViewOwnModels
        | DashboardCapability.ViewRegistration
        | DashboardCapability.ManageRegistration;

    /// <summary>Roles in display order: the broad ones first, then the additive ones.</summary>
    public static IReadOnlyList<DashboardRoleDefinition> All { get; } =
    [
        new(Admin, "Admin",
            "Everything: every model, every MCP tool, every tab, and every action.",
            AllCapabilities),

        new(ReadOnly, "Read Only",
            "The Requests log and Usage, limited to this user's own traffic.",
            DashboardCapability.ViewLogs | DashboardCapability.ViewUsage),

        new(ModelsReadOnly, "Models Read Only",
            "Read Only plus the Models tab in read-only form, listing every model mapping.",
            DashboardCapability.ViewLogs | DashboardCapability.ViewUsage | DashboardCapability.ViewModels),

        new(OwnModelsReadOnly, "Own Models Read Only",
            "Read Only plus the Models tab in read-only form, limited to the models granted to this user.",
            DashboardCapability.ViewLogs | DashboardCapability.ViewUsage | DashboardCapability.ViewOwnModels),

        new(DashboardReadOnly, "Dashboard Read Only",
            "See the Dashboard: proxy/MCP/web-server state, performance, and traffic totals. No control buttons.",
            DashboardCapability.ViewDashboard),

        new(DashboardReadWrite, "Dashboard Read-Write",
            "Dashboard Read Only plus the buttons: start/stop/restart the proxy, the MCP server, and the web dashboard.",
            DashboardCapability.ViewDashboard | DashboardCapability.DashboardControl),

        new(UserAdmin, "User Admin",
            "Read Only plus adding, updating, and removing users, their roles, their API keys, and their access grants.",
            DashboardCapability.ViewLogs | DashboardCapability.ViewUsage | DashboardCapability.ManageUsers
            | DashboardCapability.ViewRegistration),

        new(ModelAdmin, "Model Admin",
            "Read Only plus adding, updating, and removing model mappings.",
            DashboardCapability.ViewLogs | DashboardCapability.ViewUsage | DashboardCapability.ManageModels
            | DashboardCapability.ViewRegistration | DashboardCapability.ManageRegistration),

        new(TestConsole, "Test Console",
            "Send test prompts through the proxy from the Test Console tab, using only the models this user can access.",
            DashboardCapability.TestConsole),

        new(RegistrationReadOnly, "Registration Read Only",
            "The model registration API in read-only form: list model mappings and read discovery state. " +
            "For a service account's API key, not a person at the dashboard.",
            DashboardCapability.ViewRegistration),

        new(RegistrationReadWrite, "Registration Read-Write",
            "Registration Read Only plus registering, updating, and unregistering model mappings and " +
            "triggering a discovery scan. For a service account's API key, not a person at the dashboard.",
            DashboardCapability.ViewRegistration | DashboardCapability.ManageRegistration),
    ];

    private static readonly Dictionary<string, DashboardRoleDefinition> ByKey =
        All.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits a stored role string into canonical role keys, dropping unknown tokens and
    /// normalising legacy spellings. Order follows <see cref="All"/> so persisted values are stable.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return [];

        List<string> keys = [];
        foreach (string token in stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? key = CanonicalKey(token);
            if (key is not null && !keys.Contains(key, StringComparer.Ordinal))
                keys.Add(key);
        }

        return [.. All.Select(r => r.Key).Where(keys.Contains)];
    }

    /// <summary>
    /// Canonical comma-separated role list for storage, or null when nothing valid was supplied.
    /// Callers treat null as "no dashboard access" so an unrecognised value fails closed.
    /// </summary>
    public static string? Normalize(string? stored)
    {
        IReadOnlyList<string> keys = Parse(stored);
        return keys.Count == 0 ? null : string.Join(',', keys);
    }

    /// <summary>True when the stored role string contains the given role.</summary>
    public static bool HasRole(string? stored, string role)
        => Parse(stored).Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the stored roles grant <paramref name="capability"/>.</summary>
    public static bool HasCapability(string? stored, DashboardCapability capability)
    {
        DashboardCapability granted = CapabilitiesFor(stored);
        return capability == DashboardCapability.None
            ? true
            : (granted & capability) == capability;
    }

    /// <summary>The union of every capability the stored roles grant.</summary>
    public static DashboardCapability CapabilitiesFor(string? stored)
    {
        DashboardCapability result = DashboardCapability.None;
        foreach (string key in Parse(stored))
        {
            if (ByKey.TryGetValue(key, out DashboardRoleDefinition? definition))
                result |= definition.Capabilities;
        }

        return (result & DashboardCapability.Admin) != 0 ? AllCapabilities : result;
    }

    /// <summary>Wire names for the granted capabilities, for the dashboard's /api/auth/me response.</summary>
    public static IReadOnlyList<string> CapabilityNames(string? stored)
        => CapabilityNames(CapabilitiesFor(stored));

    /// <summary>Wire names for a capability set, in declaration order.</summary>
    public static IReadOnlyList<string> CapabilityNames(DashboardCapability granted)
        => [.. Enum.GetValues<DashboardCapability>()
            .Where(c => c != DashboardCapability.None && (granted & c) == c)
            .Select(c => c.ToString())];

    /// <summary>Human-readable role list ("User Admin, Read Only"), for logs and the Windows GUI.</summary>
    public static string Describe(string? stored)
    {
        IReadOnlyList<string> keys = Parse(stored);
        return keys.Count == 0
            ? "No dashboard access"
            : string.Join(", ", keys.Select(k => ByKey.TryGetValue(k, out DashboardRoleDefinition? d) ? d.Label : k));
    }

    /// <summary>The definition for a role key, or null when the key is unknown.</summary>
    public static DashboardRoleDefinition? Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out DashboardRoleDefinition? definition) ? definition : null;

    private static string? CanonicalKey(string token)
    {
        // "readonly" predates the camelCase keys and still shows up in hand-edited databases.
        if (string.Equals(token, "readonly", StringComparison.OrdinalIgnoreCase))
            return ReadOnly;

        return ByKey.TryGetValue(token, out DashboardRoleDefinition? definition) ? definition.Key : null;
    }
}