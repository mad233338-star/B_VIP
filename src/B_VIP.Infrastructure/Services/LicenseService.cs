using B_VIP.Application.Interfaces;
using B_VIP.Contracts.Auth;
using B_VIP.Contracts.Common;
using B_VIP.Contracts.Licenses;
using B_VIP.Core;
using B_VIP.Core.Common;
using B_VIP.Core.Entities;
using B_VIP.Core.Rules;
using B_VIP.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace B_VIP.Infrastructure.Services
{
    /// <summary>Licensing: plans, licenses, verification and device activation. All decisions are made on the server.</summary>
    public sealed class LicenseService : ILicenseService
    {
        private readonly B_VIPDbContext _db;
        private readonly IAuditLogger _audit;
        private readonly ILogger<LicenseService> _logger;

        public LicenseService(B_VIPDbContext db, IAuditLogger audit, ILogger<LicenseService> logger)
        {
            _db = db;
            _audit = audit;
            _logger = logger;
        }

        public async Task<IReadOnlyList<LicensePlanDto>> GetPlansAsync(CancellationToken ct = default)
        {
            List<LicensePlan> plans = await _db.LicensePlans.AsNoTracking()
                .OrderBy(p => p.SortOrder).ThenBy(p => p.Code)
                .ToListAsync(ct);

            var counts = await _db.Licenses.GroupBy(l => l.LicensePlanId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

            return plans.Select(p => MapPlan(p, counts.TryGetValue(p.Id, out int c) ? c : 0)).ToList();
        }

        private static LicensePlanDto MapPlan(LicensePlan plan, int licenseCount) => new(
            plan.Id, plan.Code, plan.Name, plan.NameAr, plan.Description, plan.DurationDays,
            plan.MaxActivations, plan.IsLifetime, plan.IsActive, plan.SortOrder,
            licenseCount, plan.Price, plan.Currency);

        public async Task<LicensePlanDto> CreatePlanAsync(
            CreatePlanRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            (string code, string? error) = LicenseRules.ValidatePlan(
                request.Code, request.Name, request.DurationDays, request.MaxActivations, request.IsLifetime, request.Price);

            if (error is not null)
            {
                throw new DomainException("invalid-plan", error, 400);
            }

            if (await _db.LicensePlans.AnyAsync(p => p.Code == code, ct))
            {
                throw new DomainException("plan-exists", $"A plan with the code '{code}' already exists.", 409);
            }

            DateTime now = DateTime.UtcNow;
            LicensePlan plan = new()
            {
                Id = Guid.NewGuid(),
                Code = code,
                Name = request.Name.Trim(),
                NameAr = string.IsNullOrWhiteSpace(request.NameAr) ? null : request.NameAr.Trim(),
                Description = string.IsNullOrWhiteSpace(request.Description) ? request.Name.Trim() : request.Description.Trim(),
                DurationDays = request.IsLifetime ? null : request.DurationDays,
                MaxActivations = request.MaxActivations,
                Price = request.Price,
                Currency = string.IsNullOrWhiteSpace(request.Currency) ? "USD" : request.Currency.Trim().ToUpperInvariant(),
                IsLifetime = request.IsLifetime,
                IsActive = request.IsActive,
                SortOrder = request.SortOrder,
                CreatedUtc = now,
                UpdatedUtc = now
            };

            _db.LicensePlans.Add(plan);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "plan.create", "license-plan", plan.Id.ToString(),
                AuditOutcome.Success, $"Plan {plan.Code} ({plan.Name}) created; {plan.DurationDays?.ToString() ?? "lifetime"} day(s).",
                null, correlationId, ct);

            return MapPlan(plan, 0);
        }

        public async Task<LicensePlanDto?> UpdatePlanAsync(
            Guid id, UpdatePlanRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            LicensePlan? plan = await _db.LicensePlans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null) return null;

            string name = request.Name?.Trim() ?? plan.Name;
            int? duration = request.DurationDays ?? plan.DurationDays;
            int maxActivations = request.MaxActivations ?? plan.MaxActivations;
            bool lifetime = request.IsLifetime ?? plan.IsLifetime;
            decimal? price = request.Price ?? plan.Price;

            (_, string? error) = LicenseRules.ValidatePlan(plan.Code, name, duration, maxActivations, lifetime, price);
            if (error is not null)
            {
                throw new DomainException("invalid-plan", error, 400);
            }

            plan.Name = name;
            plan.NameAr = request.NameAr is null ? plan.NameAr : (string.IsNullOrWhiteSpace(request.NameAr) ? null : request.NameAr.Trim());
            if (request.Description is not null) plan.Description = request.Description.Trim();
            plan.DurationDays = lifetime ? null : duration;
            plan.MaxActivations = maxActivations;
            plan.Price = price;
            if (!string.IsNullOrWhiteSpace(request.Currency)) plan.Currency = request.Currency.Trim().ToUpperInvariant();
            plan.IsLifetime = lifetime;
            if (request.SortOrder.HasValue) plan.SortOrder = request.SortOrder.Value;
            if (request.IsActive.HasValue) plan.IsActive = request.IsActive.Value;
            plan.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "plan.update", "license-plan", plan.Id.ToString(),
                AuditOutcome.Success, $"Plan {plan.Code} updated ({(plan.IsActive ? "active" : "inactive")}).",
                null, correlationId, ct);

            int count = await _db.Licenses.CountAsync(l => l.LicensePlanId == plan.Id, ct);
            return MapPlan(plan, count);
        }

        public async Task<bool> DeletePlanAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            LicensePlan? plan = await _db.LicensePlans.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (plan is null) return false;

            int issued = await _db.Licenses.CountAsync(l => l.LicensePlanId == plan.Id, ct);
            if (issued > 0)
            {
                // Deleting a tier would orphan the licenses sold with it: deactivate instead.
                throw new DomainException("plan-in-use",
                    $"'{plan.Name}' has {issued} license(s) attached. Deactivate it instead of deleting it.", 409);
            }

            _db.LicensePlans.Remove(plan);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "plan.delete", "license-plan", id.ToString(),
                AuditOutcome.Success, $"Plan {plan.Code} deleted.", null, correlationId, ct);

            return true;
        }

        public async Task<LicenseDto?> ChangePlanAsync(
            Guid userId, Guid planId, bool resetPeriod, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            LicensePlan? plan = await _db.LicensePlans.FirstOrDefaultAsync(p => p.Id == planId, ct)
                ?? throw new DomainException("plan-not-found", "The selected plan does not exist.", 404);

            if (!plan.IsActive)
            {
                throw new DomainException("plan-inactive", $"The plan '{plan.Name}' is deactivated.", 409);
            }

            License? license = await _db.Licenses
                .Include(l => l.LicensePlan)
                .Include(l => l.Entitlements)
                .Include(l => l.Activations)
                .Include(l => l.User)
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            if (license is null)
            {
                // No license yet: assign the tier now instead of failing the administrator.
                LicenseDto created = await CreateLicenseAsync(
                    new CreateLicenseRequest(userId, plan.Code, Notes: $"Assigned by administrator; moved from no plan."),
                    actorUserId, correlationId, ct);
                return created;
            }

            string previous = license.LicensePlan.Code;
            DateTime now = DateTime.UtcNow;

            license.LicensePlanId = plan.Id;
            license.MaxActivations = Math.Max(1, plan.MaxActivations);
            license.Status = LicenseStatus.Active;
            license.UpdatedUtc = now;

            if (resetPeriod)
            {
                license.StartsAt = now;
                license.ExpiresAt = LicenseRules.ComputeExpiry(now, plan.DurationDays, plan.IsLifetime);
            }
            else if (plan.IsLifetime)
            {
                license.ExpiresAt = null;
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "license.change-plan", "license", license.Id.ToString(),
                AuditOutcome.Success,
                $"Plan changed {previous} -> {plan.Code} for user {userId}; expires {(license.ExpiresAt?.ToString("u") ?? "never")}.",
                null, correlationId, ct);

            return await GetLicenseAsync(license.Id, ct);
        }

        public async Task<LicenseDto?> EnsureTrialAsync(Guid userId, string? correlationId, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;
            bool alreadyGranted = await _db.Licenses
                .AnyAsync(l => l.UserId == userId && l.Source == "trial", ct);

            if (alreadyGranted)
            {
                return null;
            }

            LicensePlan? plan = await _db.LicensePlans.FirstOrDefaultAsync(p => p.Code == LicenseRules.TrialPlanCode, ct);
            if (plan is null)
            {
                // The seed may not have run on an older database: create the tier on demand.
                plan = new LicensePlan
                {
                    Id = Guid.NewGuid(),
                    Code = LicenseRules.TrialPlanCode,
                    Name = "Trial",
                    NameAr = "تجريبي",
                    Description = $"{LicenseRules.TrialDays}-day full-access trial",
                    DurationDays = LicenseRules.TrialDays,
                    MaxActivations = 1,
                    IsActive = true,
                    SortOrder = 1,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                _db.LicensePlans.Add(plan);
            }

            DateTime expiresAt = LicenseRules.TrialExpiry(now);

            License license = new()
            {
                Id = Guid.NewGuid(),
                LicenseKey = LicenseRules.GenerateLicenseKey(),
                UserId = userId,
                LicensePlanId = plan.Id,
                Status = LicenseStatus.Active,
                StartsAt = now,
                ExpiresAt = expiresAt,
                MaxActivations = Math.Max(1, plan.MaxActivations),
                CreatedUtc = now,
                UpdatedUtc = now,
                CreatedByUserId = "system",
                AdminNotes = "Automatic free trial granted on e-mail activation.",
                Source = "trial"
            };

            license.Entitlements.Add(new LicenseFeatureEntitlement
            {
                Id = Guid.NewGuid(),
                LicenseId = license.Id,
                FeatureKey = "core",
                Enabled = true,
                UpdatedUtc = now
            });

            _db.Licenses.Add(license);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "license.trial", "license", license.Id.ToString(),
                AuditOutcome.Success,
                $"{LicenseRules.TrialDays}-day trial started; expires {expiresAt:u}.",
                null, correlationId, ct);

            _logger.LogInformation("Trial license {Key} granted to user {UserId} until {Expires:u}",
                license.LicenseKey, userId, expiresAt);

            return await GetLicenseAsync(license.Id, ct);
        }

        public async Task<PagedResult<LicenseDto>> SearchLicensesAsync(string? query, string? status, Guid? userId, int page, int pageSize, CancellationToken ct = default)
        {
            page = Paging.NormalizePage(page);
            pageSize = Paging.NormalizePageSize(pageSize);

            IQueryable<License> licenses = _db.Licenses.AsNoTracking()
                .Include(l => l.LicensePlan)
                .Include(l => l.Activations)
                .Include(l => l.Entitlements)
                .Include(l => l.User);

            if (!string.IsNullOrWhiteSpace(query))
            {
                string term = query.Trim().ToUpperInvariant();
                licenses = licenses.Where(l =>
                    l.LicenseKey.ToUpper().Contains(term) ||
                    l.User.NormalizedUserName.Contains(term) ||
                    l.User.NormalizedEmail.Contains(term));
            }

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<LicenseStatus>(status, true, out LicenseStatus parsed))
            {
                // Effective status can differ from the stored column (Active past its expiry).
                licenses = parsed == LicenseStatus.Expired
                    ? licenses.Where(l => l.Status == LicenseStatus.Active && l.ExpiresAt != null && l.ExpiresAt <= DateTime.UtcNow)
                    : licenses.Where(l => l.Status == parsed);
            }

            if (userId.HasValue)
            {
                licenses = licenses.Where(l => l.UserId == userId.Value);
            }

            int total = await licenses.CountAsync(ct);
            List<License> items = await licenses
                .OrderByDescending(l => l.CreatedUtc)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(ct);

            return new PagedResult<LicenseDto>(items.Select(Map).ToList(), total, page, pageSize);
        }

        public async Task<LicenseDto?> GetLicenseAsync(Guid id, CancellationToken ct = default)
        {
            License? license = await _db.Licenses.AsNoTracking()
                .Include(l => l.LicensePlan)
                .Include(l => l.Activations)
                .Include(l => l.Entitlements)
                .Include(l => l.User)
                .FirstOrDefaultAsync(l => l.Id == id, ct);

            return license is null ? null : Map(license);
        }

        public async Task<LicenseDto> CreateLicenseAsync(CreateLicenseRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            User? user = await _db.Users.FirstOrDefaultAsync(u => u.Id == request.UserId && !u.IsDeleted, ct)
                ?? throw new DomainException("user-not-found", "The target user was not found.", 404);

            LicensePlan? plan = await _db.LicensePlans.FirstOrDefaultAsync(
                p => p.Code == request.PlanCode && p.IsActive, ct)
                ?? throw new DomainException("plan-not-found", $"Unknown or inactive plan '{request.PlanCode}'.", 404);

            DateTime startsAt = request.StartsAt ?? DateTime.UtcNow;
            DateTime? expiresAt = request.ExpiresAt ?? LicenseRules.ComputeExpiry(startsAt, plan.DurationDays, plan.IsLifetime);

            if (expiresAt.HasValue && expiresAt.Value <= startsAt)
            {
                throw new DomainException("invalid-period", "The expiry date must be after the start date.");
            }

            DateTime now = DateTime.UtcNow;
            License license = new()
            {
                Id = Guid.NewGuid(),
                LicenseKey = LicenseRules.GenerateLicenseKey(),
                UserId = user.Id,
                LicensePlanId = plan.Id,
                Status = LicenseStatus.Active,
                StartsAt = startsAt,
                ExpiresAt = expiresAt,
                MaxActivations = Math.Max(1, request.MaxActivations ?? plan.MaxActivations),
                CreatedUtc = now,
                UpdatedUtc = now,
                CreatedByUserId = actorUserId.ToString(),
                AdminNotes = request.Notes,
                Source = "admin"
            };

            license.Entitlements.Add(new LicenseFeatureEntitlement
            {
                Id = Guid.NewGuid(),
                LicenseId = license.Id,
                FeatureKey = "core",
                Enabled = true,
                UpdatedUtc = now
            });

            _db.Licenses.Add(license);
            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "license.create", "license", license.Id.ToString(),
                AuditOutcome.Success, $"Key {license.LicenseKey}, plan {plan.Code}, user {user.UserName}.",
                null, correlationId, ct);

            await NotifyExpiryAsync(license, plan, ct);

            LicenseDto? created = await GetLicenseAsync(license.Id, ct);
            return created ?? Map(license);
        }

        public async Task<LicenseDto?> UpdateLicenseAsync(Guid id, UpdateLicenseRequest request, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            License? license = await _db.Licenses
                .Include(l => l.LicensePlan)
                .Include(l => l.Activations)
                .Include(l => l.Entitlements)
                .Include(l => l.User)
                .FirstOrDefaultAsync(l => l.Id == id, ct);
            if (license is null) return null;

            if (request.StartsAt.HasValue) license.StartsAt = request.StartsAt.Value;
            if (request.ExpiresAt.HasValue) license.ExpiresAt = request.ExpiresAt.Value;
            if (request.MaxActivations.HasValue) license.MaxActivations = Math.Max(1, request.MaxActivations.Value);
            if (request.Notes is not null) license.AdminNotes = request.Notes;

            if (request.Features is not null)
            {
                foreach (string feature in request.Features)
                {
                    if (license.Entitlements.All(e => !string.Equals(e.FeatureKey, feature, StringComparison.OrdinalIgnoreCase)))
                    {
                        license.Entitlements.Add(new LicenseFeatureEntitlement
                        {
                            Id = Guid.NewGuid(),
                            LicenseId = license.Id,
                            FeatureKey = feature,
                            Enabled = true,
                            UpdatedUtc = DateTime.UtcNow
                        });
                    }
                }
            }

            license.UpdatedUtc = DateTime.UtcNow;
            if (license.Status == LicenseStatus.Active && LicenseRules.IsPastExpiry(license, DateTime.UtcNow))
            {
                license.Status = LicenseStatus.Expired;
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "license.update", "license", license.Id.ToString(),
                AuditOutcome.Success, $"Starts={license.StartsAt:u}, expires={(license.ExpiresAt.HasValue ? license.ExpiresAt.Value.ToString("u") : "never")}, max={license.MaxActivations}.",
                null, correlationId, ct);

            return await GetLicenseAsync(id, ct);
        }

        public Task<LicenseDto?> SuspendLicenseAsync(Guid id, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct = default)
            => ChangeStatusAsync(id, LicenseStatus.Suspended, reason, "license.suspend", actorUserId, correlationId, ct);

        public Task<LicenseDto?> ReactivateLicenseAsync(Guid id, Guid actorUserId, string? correlationId, CancellationToken ct = default)
            => ChangeStatusAsync(id, LicenseStatus.Active, null, "license.reactivate", actorUserId, correlationId, ct);

        public Task<LicenseDto?> RevokeLicenseAsync(Guid id, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct = default)
            => ChangeStatusAsync(id, LicenseStatus.Revoked, reason, "license.revoke", actorUserId, correlationId, ct);

        public async Task<LicenseActivationListResponse?> GetActivationsAsync(Guid id, CancellationToken ct = default)
        {
            License? license = await _db.Licenses.AsNoTracking()
                .Include(l => l.Activations)
                .FirstOrDefaultAsync(l => l.Id == id, ct);

            if (license is null) return null;

            IReadOnlyList<ActivationDto> activations = license.Activations
                .OrderByDescending(a => a.ActivatedUtc)
                .Select(a => new ActivationDto(
                    a.Id, a.DeviceFingerprint, a.DeviceName, a.Platform, a.AppVersion,
                    a.ActivatedUtc, a.LastSeenUtc, a.DeactivatedUtc))
                .ToList();

            return new LicenseActivationListResponse(activations, license.MaxActivations);
        }

        public async Task<bool> DeactivateActivationAsync(Guid licenseId, Guid activationId, string? reason, Guid actorUserId, string? correlationId, CancellationToken ct = default)
        {
            LicenseActivation? activation = await _db.LicenseActivations
                .FirstOrDefaultAsync(a => a.Id == activationId && a.LicenseId == licenseId, ct);

            if (activation is null || activation.DeactivatedUtc.HasValue) return false;

            activation.DeactivatedUtc = DateTime.UtcNow;
            activation.DeactivatedReason = reason;

            License? license = await _db.Licenses.FirstOrDefaultAsync(l => l.Id == licenseId, ct);
            if (license is not null)
            {
                license.CurrentActivations = Math.Max(0, license.CurrentActivations - 1);
                license.UpdatedUtc = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, "license.deactivate-device", "license", licenseId.ToString(),
                AuditOutcome.Success, $"Activation {activationId} released. {reason}", null, correlationId, ct);
            return true;
        }

        public async Task<VerifyLicenseResponse> VerifyAsync(VerifyLicenseRequest request, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            DateTime now = DateTime.UtcNow;
            License? license = null;

            if (!string.IsNullOrWhiteSpace(request?.LicenseKey))
            {
                string key = request.LicenseKey.Trim().ToUpperInvariant();
                license = await LoadLicenseAsync(l => l.LicenseKey == key, ct);
            }

            if (license is null && userId.HasValue)
            {
                license = await LoadLicenseAsync(l => l.UserId == userId.Value, ct);
            }

            if (license is null)
            {
                await RecordVerificationAsync(null, userId, VerificationResult.NotFound, "No matching license.", ipAddress, correlationId, request?.ClientVersion, ct);
                return new VerifyLicenseResponse("NotFound", null, null, null, null, 0, 0, Array.Empty<string>(),
                    "No license matches this request.", now);
            }

            LicenseStatus status = LicenseRules.EffectiveStatus(license, now);
            VerificationResult result = LicenseRules.ToVerificationResult(status);

            license.LastVerifiedUtc = now;
            license.UpdatedUtc = now;

            string message = status switch
            {
                LicenseStatus.Active => "License is valid.",
                LicenseStatus.Expired => "The license has expired.",
                LicenseStatus.Suspended => "The license is suspended. Contact support.",
                LicenseStatus.Revoked => "The license has been revoked.",
                LicenseStatus.Pending => "The license is pending activation.",
                _ => "The license is invalid."
            };

            _db.LicenseVerificationEvents.Add(new LicenseVerificationEvent
            {
                Id = Guid.NewGuid(),
                LicenseId = license.Id,
                UserId = userId,
                PerformedUtc = now,
                Result = result,
                Detail = message,
                IpAddress = ipAddress,
                CorrelationId = correlationId,
                ClientVersion = request?.ClientVersion
            });

            await _db.SaveChangesAsync(ct);

            return new VerifyLicenseResponse(
                result.ToString(),
                license.LicenseKey,
                license.LicensePlan.Code,
                license.StartsAt,
                license.ExpiresAt,
                license.MaxActivations,
                license.Activations.Count(a => a.DeactivatedUtc is null),
                license.Entitlements.Where(e => e.Enabled).Select(e => e.FeatureKey).ToList(),
                message,
                now);
        }

        public async Task<ActivateLicenseResponse> ActivateAsync(ActivateLicenseRequest request, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.LicenseKey) || string.IsNullOrWhiteSpace(request.DeviceFingerprint))
            {
                throw new DomainException("invalid-request", "License key and device fingerprint are required.");
            }

            string key = request.LicenseKey.Trim().ToUpperInvariant();
            License? license = await LoadLicenseAsync(l => l.LicenseKey == key, ct);

            if (license is null)
            {
                return new ActivateLicenseResponse(false, string.Empty, "No license matches this key.", 0);
            }

            if (userId.HasValue && license.UserId != userId.Value)
            {
                await _audit.LogAsync(userId, null, "license.activate", "license", license.Id.ToString(),
                    AuditOutcome.Denied, "License belongs to another account.", ipAddress, correlationId, ct);
                return new ActivateLicenseResponse(false, string.Empty, "This license belongs to another account.", 0);
            }

            DateTime now = DateTime.UtcNow;
            LicenseActivation? existing = license.Activations
                .FirstOrDefault(a => a.DeviceFingerprint == request.DeviceFingerprint);

            if (existing is not null && existing.DeactivatedUtc.HasValue)
            {
                existing.DeactivatedUtc = null;
                existing.DeactivatedReason = null;
                existing.LastSeenUtc = now;
                existing.ActivatedUtc = now;
            }
            else if (existing is not null)
            {
                existing.LastSeenUtc = now;
            }
            else
            {
                if (!LicenseRules.CanActivate(license, now, out string? reason))
                {
                    await RecordVerificationAsync(license.Id, userId,
                        reason == "activation-limit-reached" ? VerificationResult.ActivationLimitReached : LicenseRules.ToVerificationResult(LicenseRules.EffectiveStatus(license, now)),
                        $"Activation refused: {reason}", ipAddress, correlationId, request.AppVersion, ct);

                    return new ActivateLicenseResponse(false, string.Empty,
                        reason == "activation-limit-reached"
                            ? "The activation limit for this license has been reached."
                            : $"The license is not active ({reason}).", 0);
                }

                existing = new LicenseActivation
                {
                    Id = Guid.NewGuid(),
                    LicenseId = license.Id,
                    DeviceFingerprint = request.DeviceFingerprint.Trim(),
                    DeviceName = Truncate(request.DeviceName, 256) ?? "unknown-device",
                    Platform = Truncate(request.Platform, 64),
                    AppVersion = Truncate(request.AppVersion, 32),
                    IpAddress = ipAddress,
                    ActivatedUtc = now,
                    LastSeenUtc = now
                };
                _db.LicenseActivations.Add(existing);
            }

            license.CurrentActivations = license.Activations.Count(a => a.DeactivatedUtc is null);
            license.LastVerifiedUtc = now;
            license.UpdatedUtc = now;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "license.activate", "license", license.Id.ToString(),
                AuditOutcome.Success, $"Device {request.DeviceFingerprint}.", ipAddress, correlationId, ct);

            int remaining = Math.Max(0, license.MaxActivations - license.CurrentActivations);
            return new ActivateLicenseResponse(true, existing.Id.ToString(), "Device activated.", remaining);
        }

        public async Task<bool> DeactivateAsync(DeactivateLicenseRequest request, Guid? userId, string? ipAddress, string? correlationId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(request?.DeviceFingerprint)) return false;

            License? license = await LoadLicenseAsync(
                l => l.Activations.Any(a => a.DeviceFingerprint == request.DeviceFingerprint), ct);

            if (license is null) return false;
            if (userId.HasValue && license.UserId != userId.Value) return false;

            LicenseActivation? activation = license.Activations
                .FirstOrDefault(a => a.DeviceFingerprint == request.DeviceFingerprint && a.DeactivatedUtc is null);

            if (activation is null) return false;

            activation.DeactivatedUtc = DateTime.UtcNow;
            activation.DeactivatedReason = request.Reason;
            license.CurrentActivations = Math.Max(0, license.Activations.Count(a => a.DeactivatedUtc is null));
            license.UpdatedUtc = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(userId, null, "license.deactivate", "license", license.Id.ToString(),
                AuditOutcome.Success, $"Device {request.DeviceFingerprint} released.", ipAddress, correlationId, ct);
            return true;
        }

        public async Task<IReadOnlyList<LicenseVerificationEventDto>> GetVerificationEventsAsync(Guid licenseId, int limit, CancellationToken ct = default)
        {
            limit = Math.Clamp(limit, 1, 500);
            return await _db.LicenseVerificationEvents.AsNoTracking()
                .Where(e => e.LicenseId == licenseId)
                .OrderByDescending(e => e.PerformedUtc)
                .Take(limit)
                .Select(e => new LicenseVerificationEventDto(
                    e.Id, e.LicenseId, e.PerformedUtc, e.Result.ToString(), e.Detail, e.IpAddress, e.ClientVersion))
                .ToListAsync(ct);
        }

        public async Task<LicenseSummaryDto?> GetSummaryForUserAsync(Guid userId, CancellationToken ct = default)
        {
            License? license = await _db.Licenses.AsNoTracking()
                .Include(l => l.LicensePlan)
                .Include(l => l.Entitlements)
                .Include(l => l.Activations)
                .Where(l => l.UserId == userId)
                .OrderByDescending(l => l.CreatedUtc)
                .FirstOrDefaultAsync(ct);

            if (license is null) return null;

            LicenseStatus status = LicenseRules.EffectiveStatus(license, DateTime.UtcNow);
            return new LicenseSummaryDto(
                license.Id, license.LicenseKey, license.LicensePlan.Code, license.LicensePlan.Name,
                status.ToString(), license.StartsAt, license.ExpiresAt, license.MaxActivations,
                license.Activations.Count(a => a.DeactivatedUtc is null),
                license.Entitlements.Where(e => e.Enabled).Select(e => e.FeatureKey).ToList());
        }

        private async Task<LicenseDto?> ChangeStatusAsync(Guid id, LicenseStatus status, string? reason, string action, Guid actorUserId, string? correlationId, CancellationToken ct)
        {
            License? license = await _db.Licenses
                .Include(l => l.LicensePlan)
                .Include(l => l.Activations)
                .Include(l => l.Entitlements)
                .Include(l => l.User)
                .FirstOrDefaultAsync(l => l.Id == id, ct);

            if (license is null) return null;

            license.Status = status;
            license.UpdatedUtc = DateTime.UtcNow;

            if (status == LicenseStatus.Suspended)
            {
                license.SuspendedAt = DateTime.UtcNow;
                license.SuspensionReason = reason;
            }
            else if (status == LicenseStatus.Revoked)
            {
                license.RevokedAt = DateTime.UtcNow;
                license.SuspensionReason = reason;
                foreach (LicenseActivation activation in license.Activations.Where(a => a.DeactivatedUtc is null))
                {
                    activation.DeactivatedUtc = DateTime.UtcNow;
                    activation.DeactivatedReason = "license-revoked";
                }

                license.CurrentActivations = 0;
            }
            else if (status == LicenseStatus.Active)
            {
                license.SuspendedAt = null;
                license.RevokedAt = null;
                license.SuspensionReason = null;
                license.CurrentActivations = license.Activations.Count(a => a.DeactivatedUtc is null);
            }

            await _db.SaveChangesAsync(ct);

            await _audit.LogAsync(actorUserId, null, action, "license", license.Id.ToString(),
                AuditOutcome.Success, $"Status -> {status}. {reason}", null, correlationId, ct);

            return Map(license);
        }

        private async Task<License?> LoadLicenseAsync(System.Linq.Expressions.Expression<Func<License, bool>> predicate, CancellationToken ct)
        {
            return await _db.Licenses
                .Include(l => l.LicensePlan)
                .Include(l => l.Activations)
                .Include(l => l.Entitlements)
                .Include(l => l.User)
                .FirstOrDefaultAsync(predicate, ct);
        }

        private async Task RecordVerificationAsync(Guid? licenseId, Guid? userId, VerificationResult result, string detail, string? ipAddress, string? correlationId, string? clientVersion, CancellationToken ct)
        {
            if (licenseId.HasValue)
            {
                _db.LicenseVerificationEvents.Add(new LicenseVerificationEvent
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId.Value,
                    UserId = userId,
                    PerformedUtc = DateTime.UtcNow,
                    Result = result,
                    Detail = detail,
                    IpAddress = ipAddress,
                    CorrelationId = correlationId,
                    ClientVersion = clientVersion
                });

                await _db.SaveChangesAsync(ct);
            }

            _logger.LogWarning("License verification failed: {Result} - {Detail}", result, detail);
        }

        private async Task NotifyExpiryAsync(License license, LicensePlan plan, CancellationToken ct)
        {
            if (!license.ExpiresAt.HasValue) return;

            await new NotificationService(_db, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationService>.Instance)
                .NotifyAsync(license.UserId, NotificationType.LicenseExpiry.ToString(),
                    "License created",
                    $"Your {plan.Name} license is valid until {license.ExpiresAt:u}.",
                    "/license-activation", "licensing", ct);
        }

        private static LicenseDto Map(License l) => new(
            l.Id,
            l.LicenseKey,
            l.UserId,
            l.User?.UserName ?? string.Empty,
            l.LicensePlanId,
            l.LicensePlan?.Code ?? string.Empty,
            l.LicensePlan?.Name ?? string.Empty,
            l.Status.ToString(),
            LicenseRules.EffectiveStatus(l, DateTime.UtcNow).ToString(),
            l.StartsAt,
            l.ExpiresAt,
            l.MaxActivations,
            l.Activations?.Count(a => a.DeactivatedUtc is null) ?? 0,
            l.CreatedUtc,
            l.LastVerifiedUtc,
            l.Source,
            l.AdminNotes,
            l.Entitlements?.Where(e => e.Enabled).Select(e => e.FeatureKey).ToList() ?? new List<string>(),
            l.Activations?.OrderByDescending(a => a.ActivatedUtc).Select(a => new ActivationDto(
                a.Id, a.DeviceFingerprint, a.DeviceName, a.Platform, a.AppVersion,
                a.ActivatedUtc, a.LastSeenUtc, a.DeactivatedUtc)).ToList() ?? new List<ActivationDto>());

        private static string? Truncate(string? value, int max)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= max ? value : value[..max];
        }
    }
}
