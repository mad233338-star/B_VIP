namespace B_VIP.Contracts.Tickets
{
    public sealed record CreateTicketRequest(
        string Title,
        string Description,
        string Priority,
        Guid? CategoryId = null,
        Guid? DiagnosticReportId = null);

    public sealed record TicketDto(
        Guid Id,
        string Number,
        string Title,
        string Description,
        string Priority,
        string Status,
        Guid UserId,
        string UserName,
        string? CategoryName,
        Guid? AssignedToUserId,
        string? AssignedToName,
        DateTime CreatedUtc,
        DateTime UpdatedUtc,
        DateTime? LastMessageUtc,
        Guid? DiagnosticReportId,
        int MessageCount);

    public sealed record TicketMessageDto(
        Guid Id,
        Guid AuthorUserId,
        string AuthorName,
        string Body,
        bool IsInternalNote,
        DateTime CreatedUtc,
        IReadOnlyList<TicketAttachmentDto> Attachments);

    public sealed record TicketAttachmentDto(
        Guid Id,
        string FileName,
        string? ContentType,
        long SizeBytes,
        DateTime UploadedUtc);

    public sealed record TicketDetailDto(
        TicketDto Ticket,
        IReadOnlyList<TicketMessageDto> Messages);

    public sealed record AddMessageRequest(string Body, bool IsInternalNote = false);

    public sealed record UpdateTicketStatusRequest(string Status);

    public sealed record AssignTicketRequest(Guid? AssignedToUserId, string? Comment = null);

    public sealed record TicketListResponse(
        IReadOnlyList<TicketDto> Items,
        int TotalCount,
        int Page,
        int PageSize);

    public sealed record SupportCategoryDto(Guid Id, string Name, string? NameAr, bool IsActive, int TicketCount);
}
