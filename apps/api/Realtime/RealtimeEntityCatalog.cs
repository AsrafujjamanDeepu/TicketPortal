namespace TicketPortal.Api.Realtime
{
    // Who, besides the platform group, may be told that a row of a table changed.
    //
    //   PlatformOnly  nobody else. This is also what any table NOT listed in the catalog gets
    //                 (docs/02-Project-Concept-and-Solution.md (Real-time updates), principle 8: fail closed).
    //   Operator      the staff of the operator the row belongs to (operator-{id} group).
    //   Customer      the customer the row belongs to (customer-{id} group).
    //   Shared        every operator's staff (staff group) — only for rows that belong to no
    //                 single operator.
    [Flags]
    public enum RealtimeAudience
    {
        PlatformOnly = 0,
        Operator = 1,
        Customer = 2,
        Shared = 4
    }

    // docs/02-Project-Concept-and-Solution.md (Real-time updates), Chunk 2 — the explicit "entity -> who may hear about it" map.
    //
    // Table names are EF table names, which equal the DbSet property names here (AppDbContext
    // has no ToTable/[Table] overrides). A message only ever says "row X of table Y changed", so
    // even a wrong entry cannot leak data — but a missing entry would silently starve a screen
    // of updates, and a too-generous one would wake screens needlessly. That is why EVERY table
    // is listed below, including the platform-only ones, and why RealtimeEntityCatalogTests
    // fails the build when a new DbSet is added without a decision here.
    public static class RealtimeEntityCatalog
    {
        private const RealtimeAudience Op = RealtimeAudience.Operator;
        private const RealtimeAudience Cust = RealtimeAudience.Customer;
        private const RealtimeAudience Shared = RealtimeAudience.Shared;
        private const RealtimeAudience PlatformOnly = RealtimeAudience.PlatformOnly;

        // Pure plumbing: never announced. (__EFMigrationsHistory is not part of the EF model, so
        // it can never appear in a change set; it is listed to document the intent.)
        public static readonly IReadOnlySet<string> IgnoredTables = new HashSet<string>(StringComparer.Ordinal)
        {
            "__EFMigrationsHistory",
            "AspNetUserTokens",
            "AspNetUserLogins",
            "AspNetUserClaims",
            "AspNetRoleClaims"
        };

        // Identity tables that are part of the model but are not DbSets on AppDbContext
        // (they come from IdentityDbContext).
        public static readonly IReadOnlySet<string> IdentityTables = new HashSet<string>(StringComparer.Ordinal)
        {
            "AspNetUsers",
            "AspNetRoles",
            "AspNetUserRoles"
        };

        private static readonly IReadOnlyDictionary<string, RealtimeAudience> Rules =
            new Dictionary<string, RealtimeAudience>(StringComparer.Ordinal)
            {
                // ---- Bookings, tickets and what hangs off them -----------------------------
                ["Bookings"] = Op | Cust,
                ["BookingPassengers"] = Op | Cust,
                ["Tickets"] = Op | Cust,
                ["CancellationRequests"] = Op | Cust,
                ["Complaints"] = Op | Cust,
                ["Reviews"] = Op | Cust,
                ["CouponUsages"] = Op | Cust,
                ["Payments"] = Op | Cust,
                ["PaymentHistories"] = Op,
                ["Refunds"] = Op | Cust,
                ["RefundHistories"] = PlatformOnly,
                ["PaymentWebhookEvents"] = PlatformOnly,

                // ---- Seats: the public seat map is served by the trip-{id} group, not by these.
                ["SeatHolds"] = PlatformOnly,
                ["SeatHoldItems"] = PlatformOnly,
                ["TripSeats"] = PlatformOnly,

                // ---- Customers --------------------------------------------------------------
                ["CustomerProfiles"] = Cust,
                ["CustomerAddresses"] = Cust,
                ["EmergencyContacts"] = Cust,
                ["CustomerWalletTransactions"] = Cust,

                // ---- Fleet and schedule (operator-owned) -----------------------------------
                ["Buses"] = Op,
                ["BusAmenityMappings"] = Op,
                ["BusImages"] = Op,
                ["BusMaintenanceLogs"] = Op,
                ["Seats"] = Op,
                ["OperatorBranches"] = Op,
                ["OperatorRoutes"] = Op,
                ["OperatorRouteStops"] = Op,
                ["Schedules"] = Op,
                ["Trips"] = Op,
                ["TripCrews"] = Op,
                ["TripStatusHistories"] = Op,

                // ---- The operator itself and its people ------------------------------------
                ["BusOperators"] = Op,
                ["OperatorSettings"] = Op,
                ["StaffProfiles"] = Op,
                ["StaffAttendances"] = Op,
                ["StaffSalaries"] = Op,
                ["DriverLicenses"] = Op,
                ["SalesCounters"] = Op,
                ["StaffSalesCounterAssignments"] = Op,
                ["Agents"] = Op,

                // ---- Finance the operator's own staff see ----------------------------------
                ["CommissionRules"] = Op,
                ["OperatorContracts"] = Op,
                ["OperatorInvoices"] = Op,
                ["OperatorPaymentReceipts"] = Op,
                ["OperatorPayouts"] = Op,
                ["OperatorSettlements"] = Op,
                ["OperatorSettlementItems"] = Op,
                ["OperatorStatements"] = Op,
                ["OperatorStatementItems"] = Op,
                ["OperatorWallets"] = Op,
                ["OperatorIntegrations"] = Op,

                // ---- Platform-internal finance and integration plumbing --------------------
                ["PlatformLedgers"] = PlatformOnly,
                ["OperatorIntegrationEndpoints"] = PlatformOnly,
                ["ExternalRouteMappings"] = PlatformOnly,
                ["ExternalTripMappings"] = PlatformOnly,
                ["ExternalSeatMappings"] = PlatformOnly,
                ["ExternalBookingMappings"] = PlatformOnly,
                ["IntegrationSyncLogs"] = PlatformOnly,
                ["IntegrationWebhookLogs"] = PlatformOnly,

                // ---- Reference data: operator-scoped rows go to that operator, the rest to every
                //      operator's staff -----------------------------------------------------------
                ["CancellationPolicies"] = Op | Shared,
                ["CancellationPolicyRules"] = Op | Shared,
                ["FareRules"] = Op | Shared,
                ["Offers"] = Op | Shared,

                // ---- Reference data that belongs to nobody in particular -------------------
                ["Terminals"] = Shared,
                ["BusRoutes"] = Shared,
                ["RouteStops"] = Shared,
                ["BusCategories"] = Shared,
                ["BusAmenities"] = Shared,
                ["Currencies"] = Shared,
                ["Languages"] = Shared,
                ["TaxRules"] = Shared,
                ["PaymentProviders"] = Shared,
                ["PaymentMethodConfigurations"] = Shared,
                ["Coupons"] = Shared,

                // ---- Platform administration -----------------------------------------------
                ["SystemSettings"] = PlatformOnly,
                ["PromoBanners"] = PlatformOnly,
                ["ActivityLogs"] = PlatformOnly,
                ["AuditLogs"] = PlatformOnly,
                ["LoginHistories"] = PlatformOnly,
                ["NotificationLogs"] = PlatformOnly,
                ["AspNetUsers"] = PlatformOnly,
                ["AspNetRoles"] = PlatformOnly,
                ["AspNetUserRoles"] = PlatformOnly
            };

        // Every table the catalog makes a decision about (used by the drift-guard test).
        public static IReadOnlyCollection<string> ListedTables => Rules.Keys.ToArray();

        // Unknown table => platform only (fail closed).
        public static RealtimeAudience AudienceOf(string table) =>
            Rules.TryGetValue(table, out var audience) ? audience : RealtimeAudience.PlatformOnly;

        public static bool AllowsOperator(string table) => AudienceOf(table).HasFlag(RealtimeAudience.Operator);

        public static bool AllowsCustomer(string table) => AudienceOf(table).HasFlag(RealtimeAudience.Customer);

        public static bool AllowsShared(string table) => AudienceOf(table).HasFlag(RealtimeAudience.Shared);

        // Tables whose rows change what the public seat map shows. A change to any of these,
        // with a known trip, produces the anonymous-safe "SeatAvailability" signal for that
        // trip's group. (Bulk-SQL seat changes that EF never sees are Chunk 3.)
        public static readonly IReadOnlySet<string> SeatAffecting = new HashSet<string>(StringComparer.Ordinal)
        {
            "TripSeats",
            "SeatHolds",
            "SeatHoldItems",
            "Trips"
        };
    }
}
