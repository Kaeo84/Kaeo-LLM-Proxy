using System.Reflection;
using Kaeo.LlmProxy.Core.Models;
using Xunit;

namespace Kaeo.LlmProxy.Tests;

/// <summary>
/// Verifies that <see cref="AppSettings.Clone"/> copies every public get/set property so
/// a new property added to AppSettings silently resets to its default after a settings save
/// unless the developer updates Clone() alongside it.
/// </summary>
public class AppSettingsCloneTests
{
    [Fact]
    public void CloneCoversEveryPublicInstanceProperty()
    {
        AppSettings source = CreateFullyPopulatedSettings();
        AppSettings clone = source.Clone();

        foreach (PropertyInfo prop in typeof(AppSettings).GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                continue;

            object? sourceValue = prop.GetValue(source);
            object? cloneValue = prop.GetValue(clone);

            // List-type properties must be deep-copied (distinct references, same count).
            if (sourceValue is System.Collections.IList sourceList
                && cloneValue is System.Collections.IList cloneList)
            {
                Assert.NotSame(sourceValue, cloneValue);
                Assert.Equal(sourceList.Count, cloneList.Count);
                for (int i = 0; i < sourceList.Count; i++)
                {
                    if (sourceList[i] is not null && cloneList[i] is not null)
                        Assert.IsType(sourceList[i]!.GetType(), cloneList[i]);
                }
                continue;
            }

            // LoggingSettings is a reference type — must be deep-copied, and every one of its
            // properties must carry over (a missed property silently resets on each save).
            if (sourceValue is LoggingSettings sourceLogging && cloneValue is LoggingSettings cloneLogging)
            {
                Assert.NotSame(sourceValue, cloneValue);
                foreach (PropertyInfo loggingProp in typeof(LoggingSettings).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!loggingProp.CanRead || !loggingProp.CanWrite || loggingProp.GetIndexParameters().Length > 0)
                        continue;

                    Assert.Equal(loggingProp.GetValue(sourceLogging), loggingProp.GetValue(cloneLogging));
                }
                continue;
            }

            // WebDashboardSettings is a reference type — must be deep-copied like LoggingSettings.
            if (sourceValue is WebDashboardSettings sourceDashboard && cloneValue is WebDashboardSettings cloneDashboard)
            {
                Assert.NotSame(sourceValue, cloneValue);
                foreach (PropertyInfo dashboardProp in typeof(WebDashboardSettings).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!dashboardProp.CanRead || !dashboardProp.CanWrite || dashboardProp.GetIndexParameters().Length > 0)
                        continue;

                    Assert.Equal(dashboardProp.GetValue(sourceDashboard), dashboardProp.GetValue(cloneDashboard));
                }
                continue;
            }

            // ProxySecuritySettings is a reference type — must be deep-copied like WebDashboardSettings.
            if (sourceValue is ProxySecuritySettings sourceSecurity && cloneValue is ProxySecuritySettings cloneSecurity)
            {
                Assert.NotSame(sourceValue, cloneValue);
                foreach (PropertyInfo securityProp in typeof(ProxySecuritySettings).GetProperties(
                    BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!securityProp.CanRead || !securityProp.CanWrite || securityProp.GetIndexParameters().Length > 0)
                        continue;

                    Assert.Equal(securityProp.GetValue(sourceSecurity), securityProp.GetValue(cloneSecurity));
                }
                continue;
            }

            Assert.Equal(sourceValue, cloneValue);
        }
    }

    private static AppSettings CreateFullyPopulatedSettings()
    {
        AppSettings s = new()
        {
            ListenPort = 8080,
            ListenAddress = "0.0.0.0",
            MaxRequestBodyBytes = 10_000_000,
            MaxConcurrentRequests = 32,
            EnableCors = true,
            MaxLogEntries = 1000,
            AutoStartProxy = true,
            StartWithDashboardOpen = false,
            AllowMultipleInstances = true,
            RunAsAdministrator = true,
            ShowCloseToTrayNotification = false,
            CollectRequestDetails = true,
            CollectResponseDetails = true,
            DebugMode = true,
            EnableSseKeepAlive = false,
            UseIrTranslation = true,
            HideHeartbeatLogs = true,
            SseKeepAliveIntervalSeconds = 15,
            HeartbeatIntervalSeconds = 60,
            CompactionFallbackContextTokens = 4096,
            MaxConcurrentCompactions = 2,
            CompactionInstructionSetName = "test-instructions",
            CompactionTargetTokens = 8192,
            CopilotCompactionModelName = "compact-model",
            EnableCopilotCompactionRouting = true,
            EnablePerformanceSampling = false,
            EnableApiExplorer = true,
            SecurityPassphrase = "test-passphrase",
            RuntimePassphrase = "runtime-passphrase",
            Logging = new LoggingSettings
            {
                MinimumLevel = "Debug",
                HeartbeatMinimumLevel = "Verbose",
                AppLogFileSizeLimitMb = 20,
                AppLogRetainedFileCount = 14,
                RequestLogFileSizeLimitMb = 100,
                // Not a literal "C:\temp\..." — the value only has to round-trip through the clone, and
                // a Windows-shaped literal breaks the suite on non-Windows runners.
                ApplicationDatabasePath = Path.Combine(Path.GetTempPath(), "test.db"),
                LogRetentionHours = 48,
                MaxLogEntriesWithBodies = 25,
                // Non-default so a Clone() that forgets LogDirectory cannot pass unnoticed.
                LogDirectory = Path.Combine(Path.GetTempPath(), "kaeo-clone-test-logs"),
            },
            WebDashboard = new WebDashboardSettings
            {
                Enabled = false,
                Port = 9090,
                ListenAddress = "0.0.0.0",
                ApiKey = "dashboard-key",
                ReadOnlyApiKey = "dashboard-read-only-key",
                RequireApiKey = true,
                Scheme = "Https",
                CertificatePath = Path.Combine(Path.GetTempPath(), "dashboard.pfx"),
                CertificatePassword = "cert-password",
            },
            // Non-default so a Clone() that forgets ProxySecurity cannot pass unnoticed.
            ProxySecurity = new ProxySecuritySettings { RequireApiKey = true },
        };

        s.CollectNonProxiedCategories.Add(NonProxiedCategory.HealthProbes);
        s.CollectNonProxiedCategories.Add(NonProxiedCategory.RejectedRequests);

        s.Credentials.Add(new StoredCredential
        {
            Name = "test-cred",
            Secret = "secret-value",
            Description = "test",
            Username = "user",
            PrivateKey = "key-material",
            Certificate = "cert-material",
        });

        s.ModelMappings.Add(new ModelMapping { ProxyName = "test-model" });
        s.InstructionSets.Add(new InstructionSet { Name = "test-instructions", Instructions = "be helpful" });

        return s;
    }
}