using B_VIP.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace B_VIP.Infrastructure.Data.Configurations
{
    public sealed class SupportCategoryConfiguration : IEntityTypeConfiguration<SupportCategory>
    {
        public void Configure(EntityTypeBuilder<SupportCategory> builder)
        {
            builder.ToTable("support_categories");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(128).IsRequired();
            builder.Property(x => x.NameAr).HasColumnName("name_ar").HasMaxLength(128);
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(512);
            builder.Property(x => x.IsActive).HasColumnName("is_active");
            builder.Property(x => x.SortOrder).HasColumnName("sort_order");
            builder.HasIndex(x => x.Name).IsUnique();
        }
    }

    public sealed class ServiceTicketConfiguration : IEntityTypeConfiguration<ServiceTicket>
    {
        public void Configure(EntityTypeBuilder<ServiceTicket> builder)
        {
            builder.ToTable("service_tickets");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.Number).HasColumnName("number").HasMaxLength(24).IsRequired();
            builder.Property(x => x.UserId).HasColumnName("user_id");
            builder.Property(x => x.CategoryId).HasColumnName("category_id");
            builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(256).IsRequired();
            builder.Property(x => x.Description).HasColumnName("description").HasMaxLength(8000).IsRequired();
            builder.Property(x => x.Priority).HasColumnName("priority").HasConversion<string>().HasMaxLength(16);
            builder.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            builder.Property(x => x.AssignedToUserId).HasColumnName("assigned_to_user_id");
            builder.Property(x => x.LastMessageByUserId).HasColumnName("last_message_by_user_id");
            builder.Property(x => x.DiagnosticReportId).HasColumnName("diagnostic_report_id");
            builder.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);
            builder.HasIndex(x => x.Number).IsUnique();
            builder.HasIndex(x => new { x.Status, x.UpdatedUtc });
            builder.HasIndex(x => x.UserId);
            builder.HasOne(x => x.User).WithMany(x => x.Tickets).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
            builder.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.SetNull);
            builder.HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToUserId).OnDelete(DeleteBehavior.SetNull);
        }
    }

    public sealed class TicketMessageConfiguration : IEntityTypeConfiguration<TicketMessage>
    {
        public void Configure(EntityTypeBuilder<TicketMessage> builder)
        {
            builder.ToTable("ticket_messages");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.TicketId).HasColumnName("ticket_id");
            builder.Property(x => x.AuthorUserId).HasColumnName("author_user_id");
            builder.Property(x => x.Body).HasColumnName("body").HasMaxLength(8000).IsRequired();
            builder.Property(x => x.IsInternalNote).HasColumnName("is_internal_note");
            builder.Property(x => x.AttachmentId).HasColumnName("attachment_id");
            builder.HasIndex(x => new { x.TicketId, x.CreatedUtc });
            builder.HasOne(x => x.Ticket).WithMany(x => x.Messages).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.Author).WithMany().HasForeignKey(x => x.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
        }
    }

    public sealed class TicketAssignmentConfiguration : IEntityTypeConfiguration<TicketAssignment>
    {
        public void Configure(EntityTypeBuilder<TicketAssignment> builder)
        {
            builder.ToTable("ticket_assignments");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.TicketId).HasColumnName("ticket_id");
            builder.Property(x => x.AssignedToUserId).HasColumnName("assigned_to_user_id");
            builder.Property(x => x.AssignedByUserId).HasColumnName("assigned_by_user_id");
            builder.Property(x => x.Comment).HasColumnName("comment").HasMaxLength(1024);
            builder.HasIndex(x => x.TicketId);
            builder.HasOne(x => x.Ticket).WithMany(x => x.Assignments).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.AssignedTo).WithMany().HasForeignKey(x => x.AssignedToUserId).OnDelete(DeleteBehavior.Restrict);
        }
    }

    public sealed class TicketAttachmentConfiguration : IEntityTypeConfiguration<TicketAttachment>
    {
        public void Configure(EntityTypeBuilder<TicketAttachment> builder)
        {
            builder.ToTable("ticket_attachments");
            builder.HasKey(x => x.Id);
            builder.Property(x => x.Id).HasColumnName("id");
            builder.Property(x => x.TicketId).HasColumnName("ticket_id");
            builder.Property(x => x.TicketMessageId).HasColumnName("ticket_message_id");
            builder.Property(x => x.StoredFileId).HasColumnName("stored_file_id");
            builder.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id");
            builder.Property(x => x.FileName).HasColumnName("file_name").HasMaxLength(256);
            builder.Property(x => x.ContentType).HasColumnName("content_type").HasMaxLength(128);
            builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
            builder.HasIndex(x => x.TicketId);
            builder.HasOne(x => x.Ticket).WithMany(x => x.Attachments).HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.TicketMessage).WithMany().HasForeignKey(x => x.TicketMessageId).OnDelete(DeleteBehavior.Cascade);
            builder.HasOne(x => x.StoredFile).WithMany().HasForeignKey(x => x.StoredFileId).OnDelete(DeleteBehavior.Restrict);
        }
    }
}
