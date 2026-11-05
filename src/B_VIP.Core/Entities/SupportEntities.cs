namespace B_VIP.Core.Entities
{
    public class SupportCategory
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? NameAr { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
    }

    public class ServiceTicket
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public Guid UserId { get; set; }
        public User User { get; set; } = null!;
        public Guid? CategoryId { get; set; }
        public SupportCategory? Category { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public TicketPriority Priority { get; set; } = TicketPriority.Normal;
        public TicketStatus Status { get; set; } = TicketStatus.Open;
        public Guid? AssignedToUserId { get; set; }
        public User? AssignedTo { get; set; }
        public Guid? LastMessageByUserId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? ClosedUtc { get; set; }
        public DateTime? LastMessageUtc { get; set; }
        public Guid? DiagnosticReportId { get; set; }
        public string? CorrelationId { get; set; }

        public ICollection<TicketMessage> Messages { get; set; } = new List<TicketMessage>();
        public ICollection<TicketAssignment> Assignments { get; set; } = new List<TicketAssignment>();
        public ICollection<TicketAttachment> Attachments { get; set; } = new List<TicketAttachment>();
    }

    public class TicketMessage
    {
        public Guid Id { get; set; }
        public Guid TicketId { get; set; }
        public ServiceTicket Ticket { get; set; } = null!;
        public Guid AuthorUserId { get; set; }
        public User Author { get; set; } = null!;
        public string Body { get; set; } = string.Empty;
        public bool IsInternalNote { get; set; }
        public DateTime CreatedUtc { get; set; }
        public Guid? AttachmentId { get; set; }
    }

    public class TicketAssignment
    {
        public Guid Id { get; set; }
        public Guid TicketId { get; set; }
        public ServiceTicket Ticket { get; set; } = null!;
        public Guid AssignedToUserId { get; set; }
        public User AssignedTo { get; set; } = null!;
        public Guid AssignedByUserId { get; set; }
        public DateTime AssignedUtc { get; set; }
        public DateTime? UnassignedUtc { get; set; }
        public string? Comment { get; set; }
    }

    public class TicketAttachment
    {
        public Guid Id { get; set; }
        public Guid TicketId { get; set; }
        public ServiceTicket Ticket { get; set; } = null!;
        public Guid? TicketMessageId { get; set; }
        public TicketMessage? TicketMessage { get; set; }
        public Guid StoredFileId { get; set; }
        public StoredFileMetadata StoredFile { get; set; } = null!;
        public Guid UploadedByUserId { get; set; }
        public DateTime UploadedUtc { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string? ContentType { get; set; }
        public long SizeBytes { get; set; }
    }
}
