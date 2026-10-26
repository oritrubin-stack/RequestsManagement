// Client-side types for the API contracts. Enum values are sent and received as strings.

export type RequestStatus = 'New' | 'InProgress' | 'Waiting' | 'Completed';
export type RequestPriority = 'Low' | 'Medium' | 'High';
export type SortField = 'createdAt' | 'updatedAt' | 'priority';
export type SortDirection = 'asc' | 'desc';

export const REQUEST_STATUSES: readonly RequestStatus[] = ['New', 'InProgress', 'Waiting', 'Completed'];
export const REQUEST_PRIORITIES: readonly RequestPriority[] = ['Low', 'Medium', 'High'];

export interface RequestDto {
  id: string;
  title: string;
  organizationName: string;
  status: RequestStatus;
  priority: RequestPriority;
  assignedTo: string;
  createdAt: string;
  updatedAt: string;
  version: number;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

// Optional filters are omitted from the request when empty. Dates are ISO strings in UTC; createdTo is exclusive.
export interface RequestQuery {
  search?: string;
  status?: RequestStatus;
  priority?: RequestPriority;
  organizationName?: string;
  assignedTo?: string;
  createdFrom?: string;
  createdTo?: string;
  sortBy: SortField;
  sortDirection: SortDirection;
  page: number;
  pageSize: number;
}

export interface RequestSummary {
  totalCount: number;
  byStatus: { status: RequestStatus; count: number }[];
  byPriority: { priority: RequestPriority; count: number }[];
  generatedAt: string;
}

export interface StatusChange {
  requestId: string;
  previousStatus: RequestStatus;
  newStatus: RequestStatus;
  changedAt: string;
  changedBy: string;
}

export interface BulkUpdateItem {
  id: string;
  status: RequestStatus;
  expectedVersion: number;
}

export type StatusUpdateOutcome = 'Updated' | 'NotFound' | 'VersionConflict' | 'InvalidTransition';

export interface BulkUpdateItemResult {
  id: string;
  outcome: StatusUpdateOutcome;
  request?: RequestDto;
  currentVersion?: number;
  currentStatus?: RequestStatus;
  message?: string;
}

export interface BulkUpdateResponse {
  updatedCount: number;
  notFoundCount: number;
  versionConflictCount: number;
  invalidTransitionCount: number;
  results: BulkUpdateItemResult[];
}

// The API returns ProblemDetails for every error; 409 adds currentVersion and currentStatus.
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
  currentVersion?: number;
  currentStatus?: RequestStatus;
}
