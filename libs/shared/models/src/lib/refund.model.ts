import { RefundStatus } from './enums';

// Mirrors DTO/PaymentsExtraDtos.cs -> RefundResponseDto. Read-only from Piece 3's side — a
// Refund is only ever created automatically (PaymentConfirmationService on a paid-but-seats-lost
// race, or CancellationProcessingService.ApproveAsync) and only moves through staff actions on
// RefundsController (approve/reject/process/manual-payout). Customers track status here, they
// don't drive the workflow.
export interface Refund {
  id: string;
  bookingId: string;
  paymentId: string;
  cancellationRequestId: string | null;
  amount: number;
  currency: string;
  status: RefundStatus;
  reason: string;
  gatewayRefundReference: string | null;
  manualPayoutReference: string | null;
  requestedAtUtc: string;
  refundedAtUtc: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  rowVersion: string;
}
