/**
 * Canonical event names and error categories — the frontend mirror of the backend
 * EventCatalog (docs/event-catalog.md) and the Errors table's `error_type` field.
 *
 * Single source of truth for the filter dropdowns (Events/Errors pages) and the demo
 * data generator, so they never drift apart.
 */

export const EVENT_NAMES = [
  'page_viewed',
  'auth_login_success',
  'auth_logout',
  'api_request_failed',
  'frontend_exception',
  'server_error_occurred',
  'background_job_failed',
] as const;
export type EventName = (typeof EVENT_NAMES)[number];

export type ErrorCategory = 'server' | 'background_job' | 'frontend';

export interface ErrorCategoryOption {
  value: ErrorCategory;
  label: string;
}

/**
 * Error categories for the Errors filter.
 *
 * IMPORTANT: category is NOT a stored column. `ErrorRecord.error_type` holds the specific
 * exception class (e.g. "NullReferenceException", "TypeError") — not a category. The category
 * is derived from which fields are populated, mirroring the backend ingestion classifier:
 *   exception_type present  -> backend server error   (.NET SDK sets exception_type)
 *   else job_name present   -> background job failure
 *   else                    -> frontend exception      (JS captureException: no exception_type)
 * The backend GetErrors `category` query param applies the same predicate in SQL.
 */
export const ERROR_CATEGORIES: ErrorCategoryOption[] = [
  { value: 'server', label: 'Backend (5xx)' },
  { value: 'frontend', label: 'Frontend exception' },
  { value: 'background_job', label: 'Background job' },
];

export function errorCategory(row: { exception_type: string | null; job_name: string | null }): ErrorCategory {
  if (row.exception_type) return 'server';
  if (row.job_name) return 'background_job';
  return 'frontend';
}

export function errorCategoryLabel(value: string): string {
  return ERROR_CATEGORIES.find((c) => c.value === value)?.label ?? value;
}
