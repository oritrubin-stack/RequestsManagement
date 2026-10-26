import { PagedResult, RequestDto, RequestPriority, RequestQuery, RequestStatus, SortDirection, SortField } from '../../core/models/request.models';

// Filter values as the user enters them. Dates are calendar days (yyyy-MM-dd); "to" is inclusive in the UI.
export interface RequestFilters {
  status: RequestStatus | '';
  priority: RequestPriority | '';
  organizationName: string;
  assignedTo: string;
  createdFrom: string;
  createdTo: string;
}

export interface SortState {
  field: SortField;
  direction: SortDirection;
}

// Everything that defines the result set. Changing any of it returns to the first page.
export interface ListCriteria {
  search: string;
  filters: RequestFilters;
  sort: SortState;
  pageSize: number;
}

export interface ListState {
  criteria: ListCriteria;
  page: number;
  loading: boolean;
  result: PagedResult<RequestDto> | null;
  error: string | null;
}

export const PAGE_SIZES: readonly number[] = [10, 20, 50, 100];

// The API accepts at most 100 items in one bulk status update.
export const BULK_MAX_ITEMS = 100;

// Selection helpers: they never exceed the limit and return a new set (the input is not changed).
export function toggleSelection(selected: ReadonlySet<string>, id: string, checked: boolean, max = BULK_MAX_ITEMS): Set<string> {
  const next = new Set(selected);
  if (!checked) {
    next.delete(id);
  } else if (next.size < max) {
    next.add(id);
  }
  return next;
}

export function selectAll(selected: ReadonlySet<string>, ids: readonly string[], max = BULK_MAX_ITEMS): Set<string> {
  const next = new Set(selected);
  for (const id of ids) {
    if (next.size >= max) {
      break;
    }
    next.add(id);
  }
  return next;
}

export const EMPTY_FILTERS: RequestFilters = {
  status: '',
  priority: '',
  organizationName: '',
  assignedTo: '',
  createdFrom: '',
  createdTo: ''
};

export const DEFAULT_CRITERIA: ListCriteria = {
  search: '',
  filters: EMPTY_FILTERS,
  sort: { field: 'createdAt', direction: 'desc' },
  pageSize: 20
};

export function hasActiveCriteria(criteria: ListCriteria): boolean {
  return criteria.search !== '' || Object.values(criteria.filters).some(value => value !== '');
}

// The API range is half-open [from, to) in UTC, so an inclusive "to" day is sent as the start of the next day.
export function toRequestQuery(criteria: ListCriteria, page: number): RequestQuery {
  const { filters } = criteria;
  return {
    search: criteria.search || undefined,
    status: filters.status || undefined,
    priority: filters.priority || undefined,
    organizationName: filters.organizationName.trim() || undefined,
    assignedTo: filters.assignedTo.trim() || undefined,
    createdFrom: filters.createdFrom ? startOfUtcDay(filters.createdFrom, 0) : undefined,
    createdTo: filters.createdTo ? startOfUtcDay(filters.createdTo, 1) : undefined,
    sortBy: criteria.sort.field,
    sortDirection: criteria.sort.direction,
    page,
    pageSize: criteria.pageSize
  };
}

function startOfUtcDay(day: string, addDays: number): string {
  const [year, month, date] = day.split('-').map(Number);
  return new Date(Date.UTC(year, month - 1, date + addDays)).toISOString().replace('.000Z', 'Z');
}
