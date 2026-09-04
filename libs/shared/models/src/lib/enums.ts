// Mirrors Models/Enums/ModelEnums.cs on the backend. Kept as string unions
// (not numeric TS enums) because the API serializes enums as their PascalCase
// name by default — do NOT switch these to numbers without checking
// Program.cs's JsonSerializerOptions first.

export type TripStatus =
  | 'Scheduled'
  | 'Boarding'
  | 'Departed'
  | 'Running'
  | 'Arrived'
  | 'Completed'
  | 'Delayed'
  | 'Cancelled';

export type TripSeatStatus = 'Available' | 'Held' | 'Booked' | 'Blocked' | 'Cancelled';

export type SeatHoldStatus = 'Active' | 'ConvertedToBooking' | 'Expired' | 'Released' | 'Cancelled';

export type BookingStatus =
  | 'Draft'
  | 'PendingPayment'
  | 'Confirmed'
  | 'Completed'
  | 'PartiallyCancelled'
  | 'Cancelled'
  | 'Expired'
  | 'Failed'
  | 'Refunded';

export type TicketStatus = 'PendingPayment' | 'Issued' | 'CheckedIn' | 'Used' | 'Cancelled' | 'Refunded' | 'NoShow';

export type BookingSource = 'Web' | 'MobileApp' | 'Counter' | 'Agent' | 'Admin' | 'ExternalApi';

export type SaleChannel = 'Online' | 'Counter' | 'Agent' | 'Admin' | 'ExternalApi';

export type MoneyCollectedBy = 'Platform' | 'Operator' | 'Agent' | 'Unknown';

export type OperatorInventoryMode = 'PlatformManaged' | 'ExternalApiManaged' | 'Hybrid';

export type PaymentStatus =
  | 'Initiated'
  | 'Pending'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled'
  | 'PartiallyRefunded'
  | 'Refunded'
  | 'ReconciliationNeeded';

export type RefundStatus =
  | 'Requested'
  | 'Approved'
  | 'Processing'
  | 'Succeeded'
  | 'Rejected'
  | 'Failed'
  | 'PendingManualPayout'
  | 'ReconciliationNeeded';

export type PaymentMethod = 'Cash' | 'Card' | 'MobileBanking' | 'BankTransfer' | 'OnlineGateway' | 'Wallet';

export type PaymentGateway =
  | 'None'
  | 'SslCommerz'
  | 'Bkash'
  | 'Nagad'
  | 'Rocket'
  | 'Stripe'
  | 'PayPal'
  | 'Visa'
  | 'MasterCard'
  | 'Manual';

export type Gender = 'Unknown' | 'Male' | 'Female' | 'Other';

export type PassengerType = 'Adult' | 'Child' | 'Senior' | 'Student';

export type BusType = 'NonAc' | 'Ac' | 'Sleeper' | 'DoubleDecker' | 'BusinessClass' | 'Economy' | 'Luxury';

export type SeatType = 'Regular' | 'Window' | 'Aisle' | 'Middle' | 'Sleeper' | 'Business';

export type CancellationRequestStatus = 'Requested' | 'Approved' | 'Rejected' | 'Completed';

export type SettlementDirection = 'PlatformPaysOperator' | 'OperatorPaysPlatform' | 'NetZero';

export type SettlementStatus = 'Draft' | 'Approved' | 'Invoiced' | 'Paid' | 'Cancelled';

export type CouponType = 'FixedAmount' | 'Percentage';

export type CustomerWalletTransactionType = 'TopUp' | 'BookingPayment' | 'RefundCredit' | 'AdminAdjustment';
