using System.Text.Json;
using B_VIP.Application.Services;
using B_VIP.Core;
using B_VIP.Core.Modules;
using B_VIP.Core.Operations;
using Xunit;

namespace B_VIP.UnitTests
{
    public sealed class ReportBuilderTests
    {
        private static ReportInput SampleInput(IReadOnlyDictionary<string, string>? extra = null) => new(
            Module: "android-diagnostics",
            Title: "Full diagnostic run",
            DeviceId: "device-123",
            DeviceDescription: "Samsung SM-G991B",
            AppVersion: "1.0.0",
            OsVersion: "Windows 11 23H2",
            OperationId: Guid.NewGuid().ToString("N"),
            CorrelationId: Guid.NewGuid().ToString("N"),
            Checks:
            [
                new ReportCheckResult("identity", "pass", "samsung SM-G991B", 12),
                new ReportCheckResult("battery", "pass", "level 88", 30),
                new ReportCheckResult("root_state", "fail", "su found", 8),
                new ReportCheckResult("storage", "skip", "not available", null)
            ],
            Warnings: ["Security patch level is older than six months."],
            Errors: ["Device disconnected mid-run."],
            Extra: extra);

        [Fact]
        public void BuildJson_reports_the_real_check_results_and_summary()
        {
            DateTime generated = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
            string json = ReportBuilder.BuildJson(SampleInput(), generated);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            Assert.Equal("B_VIP Tool", root.GetProperty("product").GetString());
            Assert.Equal("Bashir Al-Farhan", root.GetProperty("productAuthor").GetString());
            Assert.Equal("2026-10-28T12:00:00.0000000Z", root.GetProperty("generatedUtc").GetString());
            Assert.Equal("android-diagnostics", root.GetProperty("module").GetString());

            JsonElement summary = root.GetProperty("summary");
            Assert.Equal(4, summary.GetProperty("total").GetInt32());
            Assert.Equal(2, summary.GetProperty("passed").GetInt32());
            Assert.Equal(1, summary.GetProperty("failed").GetInt32());
            Assert.Equal(1, summary.GetProperty("skipped").GetInt32());
            Assert.True(summary.GetProperty("hasErrors").GetBoolean());

            Assert.Equal(4, root.GetProperty("checks").GetArrayLength());
            Assert.Equal(1, root.GetProperty("warnings").GetArrayLength());
            Assert.Equal(1, root.GetProperty("errors").GetArrayLength());
        }

        [Fact]
        public void BuildJson_carries_the_extra_entries()
        {
            string json = ReportBuilder.BuildJson(
                SampleInput(new Dictionary<string, string> { ["toolVersion"] = "35.0.2" }), DateTime.UtcNow);

            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("35.0.2", document.RootElement.GetProperty("extra.toolVersion").GetString());
        }

        [Fact]
        public void BuildText_contains_every_check_warning_and_error()
        {
            string text = ReportBuilder.BuildText(SampleInput(), DateTime.UtcNow);

            Assert.Contains("B_VIP Tool", text);
            Assert.Contains("Bashir Al-Farhan", text);
            Assert.Contains("[PASS] identity", text);
            Assert.Contains("[FAIL] root_state", text);
            Assert.Contains("[SKIP] storage", text);
            Assert.Contains("WARNINGS:", text);
            Assert.Contains("Security patch level is older than six months.", text);
            Assert.Contains("ERRORS:", text);
            Assert.Contains("Device disconnected mid-run.", text);
        }

        [Fact]
        public void Redact_replaces_device_identifying_and_secret_values()
        {
            string json = """
            {
              "password": "hunter2",
              "imei": "356938035643809",
              "modelName": "Pixel 7",
              "nested": { "serialNumber": "abc123", "keep": "visible" },
              "items": [ { "token": "eyJhbGciOi" } ]
            }
            """;

            string redacted = ReportBuilder.Redact(json);

            Assert.DoesNotContain("hunter2", redacted);
            Assert.DoesNotContain("356938035643809", redacted);
            Assert.DoesNotContain("abc123", redacted);
            Assert.DoesNotContain("eyJhbGciOi", redacted);
            Assert.Contains("***REDACTED***", redacted);
            Assert.Contains("Pixel 7", redacted);
            Assert.Contains("visible", redacted);
        }

        [Fact]
        public void Redact_leaves_non_json_input_untouched()
        {
            Assert.Equal("not json at all", ReportBuilder.Redact("not json at all"));
            Assert.Equal(string.Empty, ReportBuilder.Redact(string.Empty));
        }
    }

    public sealed class OperationTypeCatalogTests
    {
        [Fact]
        public void The_registry_contains_a_unique_key_for_every_operation()
        {
            // 43 through the MediaTek phase, six Qualcomm EDL routines, six Unisoc/SPD routines,
            // plus the shared vendor reboot, the Xiaomi Mi Account bypass and the batched fastboot erase.
            Assert.Equal(58, OperationTypes.All.Count);
            Assert.Equal(OperationTypes.All.Count, OperationTypes.All.Select(o => o.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public void Find_and_IsKnown_are_case_insensitive()
        {
            Assert.True(OperationTypes.IsKnown("ADB.Devices"));
            Assert.NotNull(OperationTypes.Find("FASTBOOT.WIPE"));
            Assert.False(OperationTypes.IsKnown("no.such.operation"));
            Assert.Null(OperationTypes.Find("no.such.operation"));
        }

        [Theory]
        [InlineData(OperationTypes.FastbootFlash, true)]
        [InlineData(OperationTypes.FastbootWipe, true)]
        [InlineData(OperationTypes.FastbootResetFrp, true)]
        [InlineData(OperationTypes.AdbReadInfo, false)]
        [InlineData(OperationTypes.FastbootReadInfo, false)]
        [InlineData(OperationTypes.AdbReboot, false)]
        [InlineData(OperationTypes.AdbCollectLogcat, false)]
        [InlineData(OperationTypes.GoogleRecoveryGuidance, false)]
        public void Data_loss_operations_are_flagged(string operation, bool dataLossRisk)
        {
            Assert.Equal(dataLossRisk, OperationTypes.Find(operation)!.DataLossRisk);
        }

        [Fact]
        public void Every_operation_declares_a_valid_success_condition_and_positive_timeout()
        {
            string[] knownConditions = ["exit-code-zero", "exit-code-nonzero-ok", "verified", "always"];

            foreach (OperationTypeDefinition definition in OperationTypes.All)
            {
                Assert.NotEmpty(definition.DisplayName);
                Assert.NotEmpty(definition.DisplayNameAr);
                Assert.NotEmpty(definition.Module);
                Assert.Contains(definition.SuccessCondition, knownConditions);
                Assert.True(definition.DefaultTimeoutSeconds > 0);
            }
        }

        [Fact]
        public void The_diagnostic_operations_require_a_device_and_a_tool()
        {
            OperationTypeDefinition adbProperties = OperationTypes.Find(OperationTypes.AdbProperties)!;

            Assert.True(adbProperties.RequiresDevice);
            Assert.True(adbProperties.RequiresExternalTool);
            Assert.False(OperationTypes.Find(OperationTypes.AdbDevices)!.RequiresDevice);
        }
    }

    public sealed class ModuleCatalogTests
    {
        [Fact]
        public void The_catalog_lists_the_brand_and_platform_modules_with_unique_keys()
        {
            // 21 modules through the Qualcomm phase, plus the Drivers & Prerequisites utility.
            Assert.Equal(22, ModuleCatalog.All.Count);
            Assert.Equal(22, ModuleCatalog.All.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // Apple is gone; the dedicated brand and chipset modules are all present.
            Assert.DoesNotContain(ModuleCatalog.All, m => m.Key == "apple");
            foreach (string key in new[] { "samsung", "xiaomi", "infinix", "teco", "mediatek", "qualcomm", "spreadtrum" })
            {
                Assert.NotNull(ModuleCatalog.Find(key));
            }
        }

        [Fact]
        public void Every_module_has_english_and_arabic_titles()
        {
            foreach (ModuleDefinition module in ModuleCatalog.All)
            {
                Assert.NotEmpty(module.TitleEn);
                Assert.NotEmpty(module.TitleAr);
                Assert.NotEmpty(module.Group);
                Assert.NotEmpty(module.Icon);
            }
        }

        [Fact]
        public void Find_resolves_modules_case_insensitively()
        {
            Assert.NotNull(ModuleCatalog.Find(ModuleKeys.Dashboard));
            Assert.NotNull(ModuleCatalog.Find("DEVICE-MANAGER"));
            Assert.Null(ModuleCatalog.Find("no-such-module"));
        }

        [Fact]
        public void The_recovery_modules_point_to_official_links_only()
        {
            Assert.StartsWith("https://", OfficialLinks.GoogleAccountRecovery);
            Assert.Contains("google.com", OfficialLinks.GoogleAccountsSupport);
            Assert.Contains("support.google.com", OfficialLinks.AndroidSupport);
        }
    }

    public sealed class PermissionCatalogTests
    {
        [Fact]
        public void The_permission_catalog_is_unique_and_has_a_group_for_every_entry()
        {
            Assert.Equal(25, Permissions.All.Length);
            Assert.Equal(25, Permissions.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());

            foreach (string permission in Permissions.All)
            {
                Assert.NotEmpty(Permissions.GroupOf(permission));
            }
        }

        [Fact]
        public void Super_admin_receives_every_permission()
        {
            IReadOnlyList<string> granted = Permissions.DefaultForRole(RoleNames.SuperAdmin);

            Assert.Equal(Permissions.All.Length, granted.Count);
            Assert.All(Permissions.All, p => Assert.Contains(p, granted));
        }

        [Theory]
        [InlineData(RoleNames.GeneralManager)]
        [InlineData(RoleNames.FinanceManager)]
        [InlineData(RoleNames.Supervisor)]
        [InlineData(RoleNames.SupportStaff)]
        [InlineData(RoleNames.User)]
        public void Every_builtin_role_receives_only_known_permissions(string role)
        {
            IReadOnlyList<string> granted = Permissions.DefaultForRole(role);

            Assert.NotEmpty(granted);
            Assert.All(granted, p => Assert.Contains(p, Permissions.All));
        }

        [Fact]
        public void Only_the_elevated_roles_receive_the_ticket_overview_permission()
        {
            Assert.DoesNotContain(Permissions.TicketsAll, Permissions.DefaultForRole(RoleNames.User));
            Assert.DoesNotContain(Permissions.TicketsAll, Permissions.DefaultForRole(RoleNames.SupportStaff));
            Assert.Contains(Permissions.TicketsAll, Permissions.DefaultForRole(RoleNames.Supervisor));
            Assert.Contains(Permissions.UsersWrite, Permissions.DefaultForRole(RoleNames.SuperAdmin));
            Assert.DoesNotContain(Permissions.UsersWrite, Permissions.DefaultForRole(RoleNames.Supervisor));
        }

        [Fact]
        public void The_six_built_in_roles_are_unique()
        {
            Assert.Equal(6, RoleNames.All.Length);
            Assert.Equal(6, RoleNames.All.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }
}
