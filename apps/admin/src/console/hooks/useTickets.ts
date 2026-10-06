// src/hooks/useTickets.ts
//
// Reusable list hook for Tickets: server-side paging, search and status filter (C7-3: the API
// pages, so only one page of rows is ever loaded), and "realtime" via SignalR push
// (startLiveRefresh in lib/realtime.ts).
// POLL_INTERVAL_MS is now only the fallback cadence used while the hub is offline.
//
// Drop this hook into ANY page/dropdown that needs the ticket list —
// TicketsList.tsx, a booking's "tickets in this booking" panel, a dashboard
// widget, etc. — it always returns the same shape.

import { startLiveRefresh } from '../lib/realtime';
import { useCallback, useEffect, useRef, useState } from 'react';
import ticketService from '../services/ticketService';
import type { TicketResponseDto, TicketStatus } from '../types/ticket.types';
import { MAX_PAGE_SIZE } from '../types/paging.types';

const POLL_INTERVAL_MS = 15000; // 15s "realtime" refresh

interface UseTicketsOptions {
  /** Turn off the polling loop (e.g. inside a dropdown you only open once). */
  live?: boolean;
  pageSize?: number;
}

export function useTickets(options: UseTicketsOptions = {}) {
  const { live = true, pageSize = 10 } = options;

  const [tickets, setTickets] = useState<TicketResponseDto[]>([]);
  const [filteredCount, setFilteredCount] = useState(0);
  const [allCount, setAllCount] = useState(0); // total with no filter applied (last seen)
  const [totalPages, setTotalPages] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const [search, setSearch] = useState('');
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [status, setStatus] = useState<TicketStatus | 'all'>('all');
  const [page, setPage] = useState(1);

  // Typing in the search box must not fire one request per keystroke.
  useEffect(() => {
    const handle = setTimeout(() => setDebouncedSearch(search), 300);
    return () => clearTimeout(handle);
  }, [search]);

  // A slow response for an older page/filter must never overwrite a newer one.
  const requestId = useRef(0);

  const load = useCallback(async () => {
    const mine = ++requestId.current;
    try {
      setError(null);
      const result = await ticketService.getPage({
        page,
        pageSize: Math.min(pageSize, MAX_PAGE_SIZE),
        search: debouncedSearch,
        status,
      });
      if (mine !== requestId.current) return;
      // The last page can disappear (rows cancelled/deleted elsewhere): step back instead of
      // showing an empty table.
      if (result.items.length === 0 && result.totalCount > 0 && page > 1) {
        setPage(Math.max(1, result.totalPages));
        return;
      }
      setTickets(result.items);
      setFilteredCount(result.totalCount);
      setTotalPages(Math.max(1, result.totalPages));
      if (!debouncedSearch.trim() && status === 'all') setAllCount(result.totalCount);
    } catch (err: any) {
      if (mine !== requestId.current) return;
      setError(err?.response?.data?.message ?? err?.message ?? 'Failed to load tickets.');
    } finally {
      if (mine === requestId.current) setLoading(false);
    }
  }, [page, pageSize, debouncedSearch, status]);

  // Initial load + polling for "realtime" updates (re-created whenever page/filters change).
  useEffect(() => {
    setLoading(true);
    load();
    if (!live) return;
    return startLiveRefresh(['Tickets'], load, POLL_INTERVAL_MS);
  }, [load, live]);

  // Back to page 1 whenever the filters change.
  useEffect(() => {
    setPage(1);
  }, [debouncedSearch, status]);

  return {
    tickets,
    allCount: Math.max(allCount, filteredCount),
    filteredCount,
    loading,
    error,
    refresh: load,
    // filters
    search,
    setSearch,
    status,
    setStatus,
    // pagination
    page,
    setPage,
    totalPages,
    pageSize,
  };
}

/**
 * Single-ticket variant for Details pages / edit-style drill-downs.
 * Also polls, so a status change (e.g. staff checks someone in from another
 * tab) shows up here without a manual refresh.
 */
export function useTicket(id: string | undefined, { live = true }: { live?: boolean } = {}) {
  const [ticket, setTicket] = useState<TicketResponseDto | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notFound, setNotFound] = useState(false);
  const [forbidden, setForbidden] = useState(false);

  const load = useCallback(async () => {
    if (!id) return;
    try {
      setError(null);
      setNotFound(false);
      setForbidden(false);
      const data = await ticketService.getById(id);
      setTicket(data);
    } catch (err: any) {
      const status = err?.response?.status;
      if (status === 404) setNotFound(true);
      else if (status === 403) setForbidden(true);
      else setError(err?.message ?? 'Failed to load ticket.');
    } finally {
      setLoading(false);
    }
  }, [id]);

  useEffect(() => {
    setLoading(true);
    load();
    if (!live || !id) return;
    return startLiveRefresh(['Tickets'], load, POLL_INTERVAL_MS);
  }, [load, live, id]);

  return { ticket, loading, error, notFound, forbidden, refresh: load };
}
