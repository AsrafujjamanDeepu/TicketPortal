// src/services/ticketService.ts
//
// Thin wrapper over the real backend (TicketsController). Read-only on purpose —
// no create/update/delete here, matching the API (see ticket.types.ts header).
//
// Uses the shared `api` axios/fetch instance from `lib/api.ts`, same as
// bookingService.ts / busOperatorService.ts etc, so auth headers, base URL and
// error handling are all consistent app-wide.

import { api } from '../lib/api';
import type { TicketResponseDto } from '../types/ticket.types';
import type { PagedResult } from '../types/paging.types';

const BASE_URL = '/api/Tickets';

export const ticketService = {
  /**
   * GET /api/Tickets
   * Backend already scopes the result set by the caller's role
   * (Admin/Staff see all or their operator's; Customer sees only their own).
   */
  async getAll(): Promise<TicketResponseDto[]> {
    const { data } = await api.get<TicketResponseDto[]>(BASE_URL);
    return data;
  },

  /**
   * GET /api/Tickets?page=&pageSize=&search=&status=
   * C7-3: server-side paging, search and status filter. Role/operator/owner scoping is applied by
   * the API BEFORE counting and paging, newest first with a stable tie-break.
   */
  async getPage(params: {
    page: number;
    pageSize: number;
    search?: string;
    status?: number | string;
  }): Promise<PagedResult<TicketResponseDto>> {
    const { data } = await api.get<PagedResult<TicketResponseDto>>(BASE_URL, {
      params: {
        page: params.page,
        pageSize: params.pageSize,
        search: params.search?.trim() || undefined,
        status: params.status === undefined || params.status === 'all' ? undefined : params.status,
      },
    });
    return data;
  },

  /**
   * GET /api/Tickets/{id}
   * Returns 403 (Forbid) if the caller isn't allowed to see this ticket —
   * surface that to the UI as an access-denied state, not a generic error.
   */
  async getById(id: string): Promise<TicketResponseDto> {
    const { data } = await api.get<TicketResponseDto>(`${BASE_URL}/${id}`);
    return data;
  },
};

export default ticketService;
