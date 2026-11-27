using Kaeo.LlmProxy.Core.Models;
using Xunit;

namespace Kaeo.LlmProxy.Tests.Web;

/// <summary>
/// Pins the shared dashboard role vocabulary: how stored role strings parse, what each role grants,
/// and the fail-closed rules. Both GUIs and the web middleware read their answers from here, so a
/// silent change to any of these would hand out access the administrator never granted.
/// </summary>
public sealed class DashboardRolesTests
{
    [Fact]
    public void Parse_SplitsCommaSeparatedKeys_AndOrdersThemByDisplayOrder()
    {
        Assert.Equal(["admin"], DashboardRoles.Parse("admin"));
        Assert.Equal([DashboardRoles.ReadOnly, DashboardRoles.UserAdmin],
            DashboardRoles.Parse("readOnly, userAdmin"));
        Assert.Equal([DashboardRoles.DashboardReadWrite, DashboardRoles.ModelAdmin, DashboardRoles.TestConsole],
            DashboardRoles.Parse("testConsole,dashboardReadWrite,modelAdmin"));
    }

    [Fact]
    public void Parse_ToleratesLegacySpellingsDuplicatesAndUnknownTokens()
    {
        // "readonly" predates the camelCase keys; hand-edited databases and old exports carry it.
        Assert.Equal([DashboardRoles.ReadOnly], DashboardRoles.Parse("readonly"));
        Assert.Equal([DashboardRoles.ReadOnly], DashboardRoles.Parse("ReadOnly,readOnly"));
        Assert.Equal([DashboardRoles.Admin], DashboardRoles.Parse("admin,notARealRole"));
        Assert.Equal([], DashboardRoles.Parse(null));
        Assert.Equal([], DashboardRoles.Parse("   "));
        Assert.Equal([], DashboardRoles.Parse(",,"));
        Assert.Equal([], DashboardRoles.Parse("garbage"));
    }

    [Fact]
    public void Normalize_ReturnsCanonicalList_OrNullWhenNothingValid()
    {
        Assert.Equal("readOnly", DashboardRoles.Normalize("READONLY"));
        Assert.Equal("admin,testConsole", DashboardRoles.Normalize("testConsole,admin,testConsole"));
        Assert.Null(DashboardRoles.Normalize(null));
        Assert.Null(DashboardRoles.Normalize(""));
        Assert.Null(DashboardRoles.Normalize("nonsense"));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        string? once = DashboardRoles.Normalize("dashboardReadOnly,userAdmin");
        Assert.Equal(once, DashboardRoles.Normalize(once));
    }

    [Fact]
    public void Admin_GrantsEveryCapability()
    {
        DashboardCapability granted = DashboardRoles.CapabilitiesFor(DashboardRoles.Admin);
        foreach (DashboardCapability capability in Enum.GetValues<DashboardCapability>())
            Assert.True((granted & capability) == capability, $"Admin should grant {capability}.");

        Assert.True(DashboardRoles.HasCapability(DashboardRoles.Admin, DashboardCapability.ManageSettings));
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.Admin, DashboardCapability.TestConsole));
        Assert.True(DashboardRoles.HasRole(DashboardRoles.Admin, DashboardRoles.Admin));
    }

    [Fact]
    public void ReadOnly_SeesLogsAndUsage_ButHoldsNoControlButtons()
    {
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.ViewLogs));
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.ViewUsage));

        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.ViewDashboard));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.DashboardControl));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.ManageUsers));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.ManageModels));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.TestConsole));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ReadOnly, DashboardCapability.Admin));
    }

    [Fact]
    public void DashboardRoles_SeparateSeeingTheDashboardFromPressingItsButtons()
    {
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.DashboardReadOnly, DashboardCapability.ViewDashboard));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.DashboardReadOnly, DashboardCapability.DashboardControl));

        Assert.True(DashboardRoles.HasCapability(DashboardRoles.DashboardReadWrite, DashboardCapability.ViewDashboard));
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.DashboardReadWrite, DashboardCapability.DashboardControl));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.DashboardReadWrite, DashboardCapability.ManageUsers));
    }

    [Fact]
    public void UserAdminAndModelAdmin_KeepTheReadOnlyBaseline()
    {
        foreach (string role in new[] { DashboardRoles.UserAdmin, DashboardRoles.ModelAdmin })
        {
            Assert.True(DashboardRoles.HasCapability(role, DashboardCapability.ViewLogs), role);
            Assert.True(DashboardRoles.HasCapability(role, DashboardCapability.ViewUsage), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.DashboardControl), role);
        }

        Assert.True(DashboardRoles.HasCapability(DashboardRoles.UserAdmin, DashboardCapability.ManageUsers));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.UserAdmin, DashboardCapability.ManageModels));

        Assert.True(DashboardRoles.HasCapability(DashboardRoles.ModelAdmin, DashboardCapability.ManageModels));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ModelAdmin, DashboardCapability.ManageUsers));
    }

    [Fact]
    public void TestConsoleRole_GrantsOnlyTheConsole()
    {
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.TestConsole, DashboardCapability.TestConsole));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.TestConsole, DashboardCapability.ViewLogs));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.TestConsole, DashboardCapability.ViewDashboard));
    }

    [Fact]
    public void ModelReadOnlyRoles_GrantVisibilityWithoutTheModelAdminRights()
    {
        // "Models Read Only" sees every mapping; "Own Models Read Only" only the user's own grants.
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.ModelsReadOnly, DashboardCapability.ViewModels));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ModelsReadOnly, DashboardCapability.ViewOwnModels));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.ModelsReadOnly, DashboardCapability.ManageModels));

        Assert.True(DashboardRoles.HasCapability(DashboardRoles.OwnModelsReadOnly, DashboardCapability.ViewOwnModels));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.OwnModelsReadOnly, DashboardCapability.ViewModels));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.OwnModelsReadOnly, DashboardCapability.ManageModels));

        // Both keep the Read Only baseline and nothing else.
        foreach (string role in new[] { DashboardRoles.ModelsReadOnly, DashboardRoles.OwnModelsReadOnly })
        {
            Assert.True(DashboardRoles.HasCapability(role, DashboardCapability.ViewLogs), role);
            Assert.True(DashboardRoles.HasCapability(role, DashboardCapability.ViewUsage), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ManageUsers), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ManageSettings), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.DashboardControl), role);
        }

        // Holding both is the wider grant: the full list wins over the own-models list.
        string both = $"{DashboardRoles.OwnModelsReadOnly},{DashboardRoles.ModelsReadOnly}";
        Assert.True(DashboardRoles.HasCapability(both, DashboardCapability.ViewModels));
        Assert.True(DashboardRoles.HasCapability(both, DashboardCapability.ViewOwnModels));
    }

    [Fact]
    public void ModelReadOnlyRoles_ParseAndDescribeLikeEveryOtherRole()
    {
        Assert.Equal([DashboardRoles.ModelsReadOnly, DashboardRoles.OwnModelsReadOnly],
            DashboardRoles.Parse("ownModelsReadOnly,modelsReadOnly"));
        Assert.Equal("modelsReadOnly", DashboardRoles.Normalize("MODELSREADONLY"));
        Assert.Equal("Models Read Only, Own Models Read Only",
            DashboardRoles.Describe($"{DashboardRoles.ModelsReadOnly},{DashboardRoles.OwnModelsReadOnly}"));
        Assert.Equal("Models Read Only", DashboardRoles.Find(DashboardRoles.ModelsReadOnly)?.Label);
        Assert.Equal("Own Models Read Only", DashboardRoles.Find(DashboardRoles.OwnModelsReadOnly)?.Label);
    }

    [Fact]
    public void RegistrationRoles_GrantOnlyTheRegistrationSurface()
    {
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.RegistrationReadOnly, DashboardCapability.ViewRegistration));
        Assert.False(DashboardRoles.HasCapability(DashboardRoles.RegistrationReadOnly, DashboardCapability.ManageRegistration));

        Assert.True(DashboardRoles.HasCapability(DashboardRoles.RegistrationReadWrite, DashboardCapability.ViewRegistration));
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.RegistrationReadWrite, DashboardCapability.ManageRegistration));

        // A service account gets the registration API and nothing that a person would use.
        foreach (string role in new[] { DashboardRoles.RegistrationReadOnly, DashboardRoles.RegistrationReadWrite })
        {
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ViewLogs), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ViewUsage), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ViewDashboard), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.DashboardControl), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ManageUsers), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ManageModels), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ManageSettings), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.TestConsole), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ViewModels), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.ViewOwnModels), role);
            Assert.False(DashboardRoles.HasCapability(role, DashboardCapability.Admin), role);
        }
    }

    [Fact]
    public void RegistrationRoles_ParseAndDescribeLikeEveryOtherRole()
    {
        Assert.Equal([DashboardRoles.RegistrationReadOnly, DashboardRoles.RegistrationReadWrite],
            DashboardRoles.Parse("registrationReadWrite,registrationReadOnly"));
        Assert.Equal("registrationReadWrite", DashboardRoles.Normalize("REGISTRATIONREADWRITE"));
        Assert.Equal("Registration Read Only", DashboardRoles.Find(DashboardRoles.RegistrationReadOnly)?.Label);
        Assert.Equal("Registration Read-Write", DashboardRoles.Find(DashboardRoles.RegistrationReadWrite)?.Label);

        // Model Admin keeps writing the registration surface even though it does not name the role.
        Assert.True(DashboardRoles.HasCapability(DashboardRoles.ModelAdmin, DashboardCapability.ManageRegistration));
    }

    [Fact]
    public void MultipleRoles_UnionTheirCapabilities()
    {
        string combined = $"{DashboardRoles.UserAdmin},{DashboardRoles.TestConsole}";

        Assert.True(DashboardRoles.HasCapability(combined, DashboardCapability.ManageUsers));
        Assert.True(DashboardRoles.HasCapability(combined, DashboardCapability.TestConsole));
        Assert.True(DashboardRoles.HasCapability(combined, DashboardCapability.ViewLogs));
        Assert.False(DashboardRoles.HasCapability(combined, DashboardCapability.ManageModels));

        // Admin anywhere in the list is still everything.
        Assert.True(DashboardRoles.HasCapability($"{DashboardRoles.TestConsole},{DashboardRoles.Admin}",
            DashboardCapability.ManageSettings));
    }

    [Fact]
    public void UnknownOrEmptyRoles_GrantNothing()
    {
        foreach (string? stored in new string?[] { null, "", "  ", "garbage" })
        {
            Assert.Equal(DashboardCapability.None, DashboardRoles.CapabilitiesFor(stored));
            Assert.False(DashboardRoles.HasCapability(stored, DashboardCapability.ViewLogs));
        }

        // "None" is always satisfied: the middleware uses it for routes any session may call.
        Assert.True(DashboardRoles.HasCapability(null, DashboardCapability.None));
    }

    [Fact]
    public void CapabilityNames_MatchesTheGrantedSet()
    {
        Assert.Equal([], DashboardRoles.CapabilityNames((string?)null));
        Assert.Equal(["TestConsole"], DashboardRoles.CapabilityNames(DashboardRoles.TestConsole));

        IReadOnlyList<string> admin = DashboardRoles.CapabilityNames(DashboardRoles.Admin);
        Assert.Contains("Admin", admin);
        Assert.Contains("ManageSettings", admin);
        Assert.Contains("DashboardControl", admin);
        Assert.DoesNotContain("None", admin);
    }

    [Fact]
    public void Describe_ProducesTheLabelTheGuisShow()
    {
        Assert.Equal("Admin", DashboardRoles.Describe("admin"));
        Assert.Equal("Read Only, User Admin", DashboardRoles.Describe("readOnly,userAdmin"));
        Assert.Equal("No dashboard access", DashboardRoles.Describe(null));
        Assert.Equal("No dashboard access", DashboardRoles.Describe("garbage"));
    }

    [Fact]
    public void Find_ResolvesKnownKeysOnly()
    {
        Assert.Equal("User Admin", DashboardRoles.Find(DashboardRoles.UserAdmin)?.Label);
        Assert.Null(DashboardRoles.Find("garbage"));
        Assert.Null(DashboardRoles.Find(null));
    }

    [Fact]
    public void All_KeysAreUniqueAndMatchTheirDefinitions()
    {
        Assert.Equal(DashboardRoles.All.Count, DashboardRoles.All.Select(r => r.Key).Distinct().Count());
        Assert.Equal(DashboardRoles.All.Count, DashboardRoles.All.Select(r => r.Label).Distinct().Count());
        foreach (DashboardRoleDefinition role in DashboardRoles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(role.Label));
            Assert.False(string.IsNullOrWhiteSpace(role.Description));
            Assert.NotEqual(DashboardCapability.None, role.Capabilities);
            Assert.Same(role, DashboardRoles.Find(role.Key));
        }
    }
}