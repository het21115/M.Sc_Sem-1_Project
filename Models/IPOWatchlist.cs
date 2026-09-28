using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace IPOInvestmentManagement.Models
{
    [Table("IPOWatchlist")]
    public class IPOWatchlist
    {
        [Key]
        public int Watchlist_id { get; set; }

        public int User_id { get; set; }

        public int IPO_id { get; set; }

        public DateTime Added_at { get; set; }
    }
}