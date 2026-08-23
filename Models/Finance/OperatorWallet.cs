using TicketPortal.Api.Models.Common;
using TicketPortal.Api.Models.CompanyNetwork;
using System;

namespace TicketPortal.Api.Models.Finance
{
    // The fast-read "current balance" snapshot for ONE operator — one row per operator. This
    // is a CACHE, not the real source of truth: every number here should always match what you'd
    // get by summing up that operator's rows in PlatformLedger. FinanceLedgerService is the
    // only code allowed to change these numbers, and it always updates this row in the very
    // same transaction as the ledger entry that justifies the change, so the two can never
    // drift apart.
    public class OperatorWallet : AuditableEntity
    {
        public Guid BusOperatorId { get; set; }

        public decimal TotalOnlineSalesAmount { get; set; }   // Running total of online fares collected for them.
        public decimal TotalCounterSalesAmount { get; set; }  // Running total of their counter sales (for commission billing only — the cash itself never touches us).
        public decimal PendingSettlementBalance { get; set; } // Net amount not yet included in a finished settlement.
        public decimal AvailablePayoutBalance { get; set; }   // What's actually ready to be paid out to them right now.
        public decimal WithdrawnAmount { get; set; }          // Total already paid out historically.
        public decimal TotalPlatformCommission { get; set; }  // Our total earnings from this operator so far.
        public decimal TotalGatewayCharge { get; set; }
        public decimal OperatorReceivableFromPlatform { get; set; } // What WE owe THEM right now.
        public decimal PlatformReceivableFromOperator { get; set; } // What THEY owe US right now (e.g. counter commission).
        public DateTime? LastStatementDateUtc { get; set; }
        public DateTime? LastSettlementDateUtc { get; set; }
        public bool IsActive { get; set; } = true;

        public BusOperator BusOperator { get; set; } = default!;
    }
}
