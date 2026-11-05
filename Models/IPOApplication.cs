using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOApplications")]
    public class IPOApplication
    {
        [Key]
        public int Application_id { get; set; }

        public int User_id { get; set; }

        public int IPO_id { get; set; }

        [StringLength(40)]
        public string? Application_number { get; set; }

        public int Applied_lots { get; set; }

        public int Applied_shares { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Bid_price { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Application_amount { get; set; }

        [StringLength(30)]
        public string? Demat_account_number { get; set; }

        [StringLength(100)]
        public string? Upi_id { get; set; }

        [StringLength(40)]
        public string? Payment_reference { get; set; }

        [StringLength(40)]
        public string? Razorpay_order_id { get; set; }

        [StringLength(40)]
        public string? Razorpay_payment_id { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal? Payment_amount { get; set; }

        [StringLength(20)]
        public string? Payment_status { get; set; }

        public DateTime Application_date { get; set; }

        [StringLength(30)]
        public string? Status { get; set; }

        public int? Cancellation_admin_id { get; set; }

        [StringLength(500)]
        public string? Cancellation_reason { get; set; }

        public DateTime? Cancellation_date { get; set; }
    }
}