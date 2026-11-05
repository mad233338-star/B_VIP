using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Rules;
using Xunit;

namespace B_VIP.UnitTests
{
    public sealed class OperationRulesTests
    {
        [Theory]
        [InlineData(OperationStatus.Queued, OperationStatus.Validating, true)]
        [InlineData(OperationStatus.Queued, OperationStatus.Cancelled, true)]
        [InlineData(OperationStatus.Queued, OperationStatus.Running, false)]
        [InlineData(OperationStatus.Queued, OperationStatus.Completed, false)]
        [InlineData(OperationStatus.Validating, OperationStatus.Running, true)]
        [InlineData(OperationStatus.Validating, OperationStatus.RequiresAttention, true)]
        [InlineData(OperationStatus.Running, OperationStatus.Completed, true)]
        [InlineData(OperationStatus.Running, OperationStatus.TimedOut, true)]
        [InlineData(OperationStatus.Running, OperationStatus.Running, false)]
        [InlineData(OperationStatus.Completed, OperationStatus.Failed, false)]
        [InlineData(OperationStatus.Completed, OperationStatus.Running, false)]
        [InlineData(OperationStatus.Failed, OperationStatus.Queued, false)]
        [InlineData(OperationStatus.Cancelled, OperationStatus.Validating, false)]
        [InlineData(OperationStatus.TimedOut, OperationStatus.Running, false)]
        [InlineData(OperationStatus.RequiresAttention, OperationStatus.Running, true)]
        [InlineData(OperationStatus.RequiresAttention, OperationStatus.Completed, false)]
        public void CanTransition_follows_the_state_machine(OperationStatus from, OperationStatus to, bool expected)
        {
            Assert.Equal(expected, OperationRules.CanTransition(from, to));
        }

        [Fact]
        public void ResolveOutcome_reports_success_for_exit_code_zero()
        {
            Assert.Equal(OperationStatus.Completed, OperationRules.ResolveOutcome(0, "exit-code-zero", cancelled: false, timedOut: false, verified: false));
            Assert.Equal(OperationStatus.Failed, OperationRules.ResolveOutcome(1, "exit-code-zero", cancelled: false, timedOut: false, verified: false));
            Assert.Equal(OperationStatus.Failed, OperationRules.ResolveOutcome(null, "exit-code-zero", cancelled: false, timedOut: false, verified: false));
        }

        [Fact]
        public void ResolveOutcome_requires_verification_for_verified_operations()
        {
            Assert.Equal(OperationStatus.Completed, OperationRules.ResolveOutcome(null, "verified", false, false, verified: true));
            Assert.Equal(OperationStatus.Failed, OperationRules.ResolveOutcome(null, "verified", false, false, verified: false));
        }

        [Fact]
        public void ResolveOutcome_cancellation_and_timeout_beat_everything()
        {
            Assert.Equal(OperationStatus.Cancelled, OperationRules.ResolveOutcome(0, "exit-code-zero", cancelled: true, timedOut: false, verified: true));
            Assert.Equal(OperationStatus.TimedOut, OperationRules.ResolveOutcome(null, "verified", cancelled: false, timedOut: true, verified: false));
            Assert.Equal(OperationStatus.Cancelled, OperationRules.ResolveOutcome(null, "verified", cancelled: true, timedOut: true, verified: false));
        }

        [Fact]
        public void ResolveOutcome_always_and_unknown_conditions()
        {
            Assert.Equal(OperationStatus.Completed, OperationRules.ResolveOutcome(null, "always", false, false, false));
            Assert.Equal(OperationStatus.Failed, OperationRules.ResolveOutcome(0, "no-such-condition", false, false, true));
        }

        [Theory]
        [InlineData(OperationStatus.Completed, true)]
        [InlineData(OperationStatus.Failed, true)]
        [InlineData(OperationStatus.Cancelled, true)]
        [InlineData(OperationStatus.TimedOut, true)]
        [InlineData(OperationStatus.Queued, false)]
        [InlineData(OperationStatus.Running, false)]
        [InlineData(OperationStatus.RequiresAttention, false)]
        public void IsTerminal_matches_the_final_states(OperationStatus status, bool expected)
        {
            Assert.Equal(expected, OperationRules.IsTerminal(status));
        }
    }

    public sealed class PermissionRulesTests
    {
        [Fact]
        public void HasPermission_is_case_insensitive()
        {
            string[] permissions = [Permissions.UsersRead, Permissions.LicensesRead];

            Assert.True(PermissionRules.HasPermission(permissions, Permissions.UsersRead));
            Assert.True(PermissionRules.HasPermission(permissions, "USERS.READ"));
            Assert.False(PermissionRules.HasPermission(permissions, Permissions.UsersWrite));
        }

        [Fact]
        public void HasAnyPermission_matches_at_least_one()
        {
            string[] permissions = [Permissions.TicketsWrite];

            Assert.True(PermissionRules.HasAnyPermission(permissions, Permissions.TicketsRead, Permissions.TicketsWrite));
            Assert.False(PermissionRules.HasAnyPermission(permissions, Permissions.TicketsAll, Permissions.AdminAudit));
        }

        [Fact]
        public void Super_admin_may_grant_any_permission()
        {
            Assert.True(PermissionRules.CanAssignRole(RoleNames.SuperAdmin, [], Permissions.All));
        }

        [Fact]
        public void A_role_manager_cannot_grant_permissions_they_do_not_hold()
        {
            string[] actor = [Permissions.UsersRead, Permissions.LicensesRead];

            Assert.True(PermissionRules.CanAssignRole(RoleNames.Supervisor, actor, [Permissions.UsersRead]));
            Assert.False(PermissionRules.CanAssignRole(RoleNames.Supervisor, actor, [Permissions.UsersWrite]));
            Assert.False(PermissionRules.CanAssignRole(RoleNames.Supervisor, actor, [Permissions.UsersRead, Permissions.UsersSuspend]));
        }

        [Fact]
        public void Role_assignment_comparison_ignores_case()
        {
            Assert.True(PermissionRules.CanAssignRole(RoleNames.Supervisor, ["users.read"], ["USERS.READ"]));
        }

        [Fact]
        public void The_last_super_admin_cannot_be_modified_or_suspended()
        {
            Assert.False(PermissionRules.CanModifyUser(RoleNames.SuperAdmin, [RoleNames.SuperAdmin],
                AccountStatus.Active, activeSuperAdminCount: 1,
                targetUserId: Guid.NewGuid().ToString(), actorUserId: Guid.NewGuid().ToString()));
        }

        [Fact]
        public void A_super_admin_may_modify_another_super_admin()
        {
            string target = Guid.NewGuid().ToString();

            Assert.True(PermissionRules.CanModifyUser(RoleNames.SuperAdmin, [RoleNames.SuperAdmin],
                AccountStatus.Active, activeSuperAdminCount: 2, targetUserId: target, actorUserId: Guid.NewGuid().ToString()));
        }

        [Fact]
        public void A_non_super_admin_cannot_modify_themselves_through_the_admin_api()
        {
            string self = Guid.NewGuid().ToString();

            Assert.False(PermissionRules.CanModifyUser(RoleNames.User, [RoleNames.User],
                AccountStatus.Active, activeSuperAdminCount: 1, targetUserId: self, actorUserId: self));
        }

        [Fact]
        public void A_non_super_admin_cannot_modify_a_super_admin()
        {
            Assert.False(PermissionRules.CanModifyUser(RoleNames.Supervisor, [RoleNames.SuperAdmin],
                AccountStatus.Active, activeSuperAdminCount: 3,
                targetUserId: Guid.NewGuid().ToString(), actorUserId: Guid.NewGuid().ToString()));
        }
    }

    public sealed class ValidationRulesTests
    {
        [Theory]
        [InlineData("user@example.com", true)]
        [InlineData("USER@EXAMPLE.COM", true)]
        [InlineData(" first.last@example.co.uk ", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("not-an-email", false)]
        [InlineData("user@@example.com", false)]
        [InlineData("contact me", false)]
        public void IsValidEmail_matches_the_policy(string? email, bool expected)
        {
            Assert.Equal(expected, ValidationRules.IsValidEmail(email));
        }

        [Theory]
        [InlineData("abc", true)]
        [InlineData("user.name_1-x", true)]
        [InlineData("ab", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("user name", false)]
        [InlineData("user@name", false)]
        [InlineData("user/name", false)]
        public void IsValidUserName_matches_the_policy(string? userName, bool expected)
        {
            Assert.Equal(expected, ValidationRules.IsValidUserName(userName));
        }

        [Fact]
        public void IsValidUserName_enforces_the_length_bounds()
        {
            Assert.True(ValidationRules.IsValidUserName(new string('a', 64)));
            Assert.False(ValidationRules.IsValidUserName(new string('a', 65)));
            Assert.False(ValidationRules.IsValidUserName(new string('a', 2)));
        }

        [Theory]
        [InlineData(null, "password-required")]
        [InlineData("", "password-required")]
        [InlineData("Ab1!", "password-too-short")]
        [InlineData("abcdefghijklmnopqrstuvwxyza1!", null)]
        [InlineData("Abcdefgh1!", null)]
        [InlineData("Aa1!Aa1!Aa1!", null)]
        [InlineData("alllettersonlyhere", "password-too-simple")]
        [InlineData("1234567890!", "password-too-simple")]
        [InlineData("abcdefghij123456", "password-too-simple")]
        public void ValidatePasswordStrength_matches_the_policy(string? password, string? expectedError)
        {
            (bool valid, string? error) = ValidationRules.ValidatePasswordStrength(password);

            Assert.Equal(expectedError is null, valid);
            Assert.Equal(expectedError, error);
        }

        [Fact]
        public void ValidatePasswordStrength_rejects_passwords_over_the_maximum_length()
        {
            string tooLong = new('a', 130); // 130 characters, beyond the 128 limit
            (bool valid, string? error) = ValidationRules.ValidatePasswordStrength(tooLong);

            Assert.False(valid);
            Assert.Equal("password-too-long", error);
        }

        [Fact]
        public void Normalize_helpers_trim_and_uppercase()
        {
            Assert.Equal("USER@EXAMPLE.COM", ValidationRules.NormalizeEmail("  user@example.com "));
            Assert.Equal("USER.NAME_1", ValidationRules.NormalizeUserName("  user.name_1 "));
        }
    }

    public sealed class ResultAndPagingTests
    {
        [Fact]
        public void Result_success_and_failure_carry_the_right_state()
        {
            Result ok = Result.Success();
            Assert.True(ok.IsSuccess);
            Assert.Null(ok.ErrorCode);

            Result failed = Result.Failure("code-x", "message");
            Assert.False(failed.IsSuccess);
            Assert.Equal("code-x", failed.ErrorCode);
            Assert.Equal("message", failed.ErrorMessage);
            Assert.Contains("code-x", failed.ToString());
        }

        [Fact]
        public void Result_of_T_ensure_only_fails_on_a_violation()
        {
            Result<int> success = Result<int>.Success(10);
            Assert.True(success.Ensure(v => v > 0, "too-small", "value is too small").IsSuccess);

            Result<int> violated = success.Ensure(v => v > 100, "too-small", "value is too small");
            Assert.False(violated.IsSuccess);
            Assert.Equal("too-small", violated.ErrorCode);

            Result<int> alreadyFailed = Result<int>.Failure("boom", "bad");
            Assert.Equal("boom", alreadyFailed.Ensure(_ => true, "other", "other").ErrorCode);
        }

        [Fact]
        public void PagedResult_computes_total_pages()
        {
            Assert.Equal(1, new PagedResult<string>(new[] { "a" }, 1, 1, 25).TotalPages);
            Assert.Equal(1, new PagedResult<string>(new[] { "a" }, 25, 1, 25).TotalPages);
            Assert.Equal(2, new PagedResult<string>(new[] { "a" }, 26, 1, 25).TotalPages);
            Assert.Equal(0, PagedResult<string>.Empty(1, 25).TotalPages);
            Assert.Equal(0, new PagedResult<string>(Array.Empty<string>(), 10, 1, 0).TotalPages);
        }

        [Fact]
        public void Paging_normalization_clamps_to_the_supported_range()
        {
            Assert.Equal(1, Paging.NormalizePage(0));
            Assert.Equal(1, Paging.NormalizePage(-10));
            Assert.Equal(7, Paging.NormalizePage(7));

            Assert.Equal(Paging.DefaultPageSize, Paging.NormalizePageSize(0));
            Assert.Equal(Paging.DefaultPageSize, Paging.NormalizePageSize(-1));
            Assert.Equal(Paging.MaxPageSize, Paging.NormalizePageSize(5000));
            Assert.Equal(50, Paging.NormalizePageSize(50));
        }

        [Fact]
        public void DomainException_defaults_to_a_bad_request_and_keeps_its_code()
        {
            var error = new DomainException("code-y", "something went wrong");

            Assert.Equal(400, error.StatusCode);
            Assert.Equal("code-y", error.Code);
            Assert.Equal("something went wrong", error.Message);

            var forbidden = new DomainException("license-required", "no", 403);
            Assert.Equal(403, forbidden.StatusCode);
        }
    }
}
