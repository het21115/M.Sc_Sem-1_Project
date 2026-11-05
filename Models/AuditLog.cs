using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("AuditLogs")]
    public class AuditLog
    {
        [Key]
        public int AuditLogId { get; set; }

        public int? ActorUserId { get; set; }

        [Required, StringLength(100)]
        public string Action { get; set; } = string.Empty;

        [StringLength(100)]
        public string? EntityType { get; set; }

        [StringLength(100)]
        public string? EntityId { get; set; }

        [StringLength(1000)]
        public string? Details { get; set; }

        [StringLength(64)]
        public string? IpAddress { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
