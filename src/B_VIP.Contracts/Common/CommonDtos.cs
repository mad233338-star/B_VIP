namespace B_VIP.Contracts.Common
{
    /// <summary>Standard error payload returned by every failing endpoint.</summary>
    public sealed record ApiError(string Code, string Message, string? CorrelationId = null);

    public sealed record ApiErrorEnvelope(ApiError Error)
    {
        /// <summary>
        /// The failure message repeated at the top level, so a client that reads
        /// <c>{ "message": ... }</c> sees the same text as one that reads <c>error.message</c>.
        /// </summary>
        public string Message => Error.Message;
    }

    public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize, int TotalPages);

    public sealed record HealthComponentDto(string Name, string Status, string? Detail, DateTime CheckedUtc);

    public sealed record LanguageDto(string Code, string NativeName, string EnglishName, bool IsRtl);

    /// <summary>Generic acknowledgement payload used by endpoints that only return a message.</summary>
    public sealed record MessageResponse(string Message);

    public sealed record MetaModuleDto(
        string Key,
        string Title,
        string TitleAr,
        string Group,
        bool RequiresDevice,
        bool RequiresExternalTool);

    public sealed record MetaDto(
        string Product,
        string Author,
        string Version,
        DateTime ServerTimeUtc,
        IReadOnlyList<LanguageDto> Languages,
        string DefaultLanguage,
        IReadOnlyList<MetaModuleDto> Modules);
}

namespace B_VIP.Contracts.Devices
{
    using B_VIP.Core;

    public sealed record DeviceInfoDto(
        string DeviceId,
        string? Serial,
        string? Manufacturer,
        string? Model,
        string? Product,
        string? AndroidVersion,
        string? OsBuild,
        DeviceConnectionState State,
        DeviceInterfaceType InterfaceType,
        string InterfaceDetail,
        string? TransportId,
        string? DriverDescription,
        string? DetectedBy,
        DateTime DiscoveredUtc,
        IReadOnlyList<string> Capabilities);

    public sealed record DeviceListResponse(
        IReadOnlyList<DeviceInfoDto> Devices,
        DateTime ScannedUtc,
        long DurationMs,
        string ScanTool,
        string? ToolVersion,
        string? ScanError);

    public sealed record DevicePropertyDto(string Name, string? Value);

    public sealed record ToolStatusDto(
        string Tool,
        bool Found,
        string? Path,
        string? Version,
        string? Error);

    public sealed record ModuleStateDto(
        string Key,
        ModuleAvailability State,
        string? Detail);

    /// <summary>One executed diagnostic check. Status is pass, fail, warn or skip.</summary>
    public sealed record DiagnosticCheckDto(string Name, string Status, string? Detail, long? DurationMs);

    public sealed record DeviceDiagnosticsResponse(
        string Serial,
        string? DeviceDescription,
        IReadOnlyList<DiagnosticCheckDto> Checks,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors,
        DateTime GeneratedUtc);

    public sealed record DeviceActionRequest(string Serial, string Action, string? Argument);

    public sealed record DeviceActionResult(string Serial, string Action, bool Succeeded, string Detail);
}

namespace B_VIP.Contracts.Modules
{
    public sealed record DiagnosticFindingDto(
        string Code,
        string Title,
        string Severity,
        string ActualResult,
        string Recommendation);

    public sealed record DiagnosticSessionDto(
        string SessionId,
        string Module,
        string DeviceId,
        DateTime StartedUtc,
        DateTime? CompletedUtc,
        IReadOnlyList<DiagnosticFindingDto> Findings,
        IReadOnlyDictionary<string, string> RawData,
        bool DeviceRequired);

    public sealed record OfficialLinkDto(string Title, string TitleAr, string Url, string Description);
}
