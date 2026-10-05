using TicketPortal.Api.Models.Common;
using System.ComponentModel.DataAnnotations;

namespace TicketPortal.Api.Models.Payments
{
    // A configurable demo percentage only. This global shape cannot encode jurisdictional,
    // operator, route, or vehicle-class taxability and must not be presented as a statutory tax
    // rule without an appropriately scoped model and legal review.
    public class TaxRule : AuditableEntity
    {
        [MaxLength(120)]
        public string Name { get; set; } = string.Empty;

        public decimal Percentage { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
