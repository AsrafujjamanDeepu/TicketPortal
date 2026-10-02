using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using TicketPortal.Api.Models.CompanyNetwork;
using TicketPortal.Api.Models.People;
using TicketPortal.Api.Models.Scheduling;

namespace TicketPortal.Api.Realtime
{
    // REALTIME_SIGNALR_PLAN.md, Chunk 2 — reads the EF change tracker just before a save and
    // turns every Added / Modified / Deleted row into a CapturedChange.
    //
    // Works from the EF model (property names), so it covers all ~80 tables without a per-table
    // switch: a row's own BusOperatorId / TripId / CustomerProfileId are read directly, and any
    // foreign key to a known parent table is remembered so RealtimeScopeResolver can work out
    // the rest after the commit. Per-entity-type metadata is computed once and cached.
    public static class RealtimeChangeCapture
    {
        private sealed class EntityShape
        {
            public string Table = string.Empty;
            public bool Ignored;
            public string? IdProperty;
            public string? OperatorProperty;
            public string? TripProperty;
            public string? CustomerProperty;
            public string? IsDeletedProperty;
            public bool SelfIsTrip;
            public bool SelfIsCustomer;
            public bool SelfIsOperator;
            public List<(RealtimeRef Kind, string Property)> Refs = new();
        }

        // Foreign-key property name -> the parent table it points at.
        private static readonly (string Property, RealtimeRef Kind)[] RefProperties =
        {
            ("BookingId", RealtimeRef.Booking),
            ("PaymentId", RealtimeRef.Payment),
            ("TripId", RealtimeRef.Trip),
            ("BusId", RealtimeRef.Bus),
            ("OperatorRouteId", RealtimeRef.OperatorRoute),
            ("StaffProfileId", RealtimeRef.StaffProfile),
            ("OperatorInvoiceId", RealtimeRef.OperatorInvoice),
            ("OperatorSettlementId", RealtimeRef.OperatorSettlement),
            ("SalesCounterId", RealtimeRef.SalesCounter),
            ("CancellationPolicyId", RealtimeRef.CancellationPolicy),
            ("SeatHoldId", RealtimeRef.SeatHold),
            ("TripSeatId", RealtimeRef.TripSeat)
        };

        private static readonly ConcurrentDictionary<IEntityType, EntityShape> Shapes = new();

        public static List<CapturedChange> FromEntries(IEnumerable<EntityEntry> entries)
        {
            var now = DateTime.UtcNow;
            var captured = new List<CapturedChange>();

            foreach (var entry in entries)
            {
                if (entry.State != EntityState.Added
                    && entry.State != EntityState.Modified
                    && entry.State != EntityState.Deleted)
                {
                    continue;
                }

                var shape = Shapes.GetOrAdd(entry.Metadata, BuildShape);
                if (shape.Ignored)
                    continue;

                captured.Add(Build(entry, shape, now));
            }

            return captured;
        }

        private static EntityShape BuildShape(IEntityType entityType)
        {
            var shape = new EntityShape
            {
                Table = entityType.GetTableName() ?? entityType.ClrType.Name
            };

            shape.Ignored = RealtimeEntityCatalog.IgnoredTables.Contains(shape.Table);
            if (shape.Ignored)
                return shape;

            var key = entityType.FindPrimaryKey();
            if (key is not null && key.Properties.Count == 1 && key.Properties[0].ClrType == typeof(Guid))
                shape.IdProperty = key.Properties[0].Name;

            shape.OperatorProperty = FindGuidProperty(entityType, "BusOperatorId");
            shape.TripProperty = FindGuidProperty(entityType, "TripId");
            shape.CustomerProperty = FindGuidProperty(entityType, "CustomerProfileId");

            var isDeleted = entityType.FindProperty("IsDeleted");
            if (isDeleted is not null && isDeleted.ClrType == typeof(bool))
                shape.IsDeletedProperty = isDeleted.Name;

            // A trip / customer profile / operator row IS its own scope.
            shape.SelfIsTrip = entityType.ClrType == typeof(Trip);
            shape.SelfIsCustomer = entityType.ClrType == typeof(CustomerProfile);
            shape.SelfIsOperator = entityType.ClrType == typeof(BusOperator);

            foreach (var (property, kind) in RefProperties)
            {
                if (FindGuidProperty(entityType, property) is { } found)
                    shape.Refs.Add((kind, found));
            }

            return shape;
        }

        private static string? FindGuidProperty(IEntityType entityType, string name)
        {
            var property = entityType.FindProperty(name);
            if (property is null)
                return null;

            return property.ClrType == typeof(Guid) || property.ClrType == typeof(Guid?)
                ? property.Name
                : null;
        }

        private static CapturedChange Build(EntityEntry entry, EntityShape shape, DateTime now)
        {
            var id = shape.IdProperty is null ? null : AsGuid(entry.Property(shape.IdProperty).CurrentValue);

            var action = entry.State switch
            {
                EntityState.Added => RealtimeActions.Created,
                EntityState.Deleted => RealtimeActions.Deleted,
                _ => RealtimeActions.Updated
            };

            // The app soft-deletes business rows (IsDeleted = true, an UPDATE); clients care that
            // the row disappeared, so announce those as "deleted".
            if (entry.State == EntityState.Modified && shape.IsDeletedProperty is not null)
            {
                var flag = entry.Property(shape.IsDeletedProperty);
                if (flag.IsModified && flag.CurrentValue is true)
                    action = RealtimeActions.Deleted;
            }

            var change = new CapturedChange
            {
                Entity = shape.Table,
                Action = action,
                Id = id,
                AtUtc = now,
                OperatorId = Read(entry, shape.OperatorProperty),
                TripId = Read(entry, shape.TripProperty),
                CustomerProfileId = Read(entry, shape.CustomerProperty)
            };

            if (id is not null)
            {
                if (shape.SelfIsTrip) change.TripId ??= id;
                if (shape.SelfIsCustomer) change.CustomerProfileId ??= id;
                if (shape.SelfIsOperator) change.OperatorId ??= id;
            }

            foreach (var (kind, property) in shape.Refs)
            {
                var value = Read(entry, property);
                if (value is null)
                    continue;

                change.Refs ??= new Dictionary<RealtimeRef, Guid>();
                change.Refs[kind] = value.Value;
            }

            return change;
        }

        private static Guid? Read(EntityEntry entry, string? propertyName) =>
            propertyName is null ? null : AsGuid(entry.Property(propertyName).CurrentValue);

        private static Guid? AsGuid(object? value) =>
            value is Guid guid && guid != Guid.Empty ? guid : null;
    }
}
