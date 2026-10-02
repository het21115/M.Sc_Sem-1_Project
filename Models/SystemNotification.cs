using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("SystemNotifications")]
    public class SystemNotification
    {
        [Key]
        public int Notification_id { get; set; }

        public int User_id { get; set; }

        [Required]
        [StringLength(200)]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Message { get; set; } = string.Empty;

        public DateTime Created_at { get; set; }
        public bool Is_read { get; set; }
    }
}
