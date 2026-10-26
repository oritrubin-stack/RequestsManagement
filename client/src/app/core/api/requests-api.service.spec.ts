import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RequestDto, RequestQuery } from '../models/request.models';
import { RequestsApiService } from './requests-api.service';

describe('RequestsApiService', () => {
  let service: RequestsApiService;
  let http: HttpTestingController;

  const baseQuery: RequestQuery = { sortBy: 'createdAt', sortDirection: 'desc', page: 1, pageSize: 20 };

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(RequestsApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends paging and sorting, and omits empty filters', () => {
    service.search({ ...baseQuery, search: '   ', organizationName: '' }).subscribe();

    const request = http.expectOne(r => r.url === '/api/requests');
    expect(request.request.method).toBe('GET');
    expect(request.request.params.keys().sort()).toEqual(['page', 'pageSize', 'sortBy', 'sortDirection']);
    expect(request.request.params.get('sortBy')).toBe('createdAt');
    expect(request.request.params.get('pageSize')).toBe('20');
    request.flush({ items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 });
  });

  it('sends every non-empty filter to the server, with the search text trimmed', () => {
    service.search({
      ...baseQuery,
      search: ' invoice ',
      status: 'Waiting',
      priority: 'High',
      organizationName: 'Acme Logistics',
      assignedTo: 'noa.cohen',
      createdFrom: '2025-01-01T00:00:00Z',
      createdTo: '2025-07-01T00:00:00Z',
      sortBy: 'priority',
      sortDirection: 'asc',
      page: 3
    }).subscribe();

    const params = http.expectOne(r => r.url === '/api/requests').request.params;
    expect(params.get('search')).toBe('invoice');
    expect(params.get('status')).toBe('Waiting');
    expect(params.get('priority')).toBe('High');
    expect(params.get('organizationName')).toBe('Acme Logistics');
    expect(params.get('assignedTo')).toBe('noa.cohen');
    expect(params.get('createdFrom')).toBe('2025-01-01T00:00:00Z');
    expect(params.get('createdTo')).toBe('2025-07-01T00:00:00Z');
    expect(params.get('sortBy')).toBe('priority');
    expect(params.get('sortDirection')).toBe('asc');
    expect(params.get('page')).toBe('3');
  });

  it('reads the summary and the history from their endpoints', () => {
    service.getSummary().subscribe();
    service.getHistory('6ad881e1a051e7edd89d0ef0').subscribe();

    const summary = http.expectOne('/api/requests/summary');
    const history = http.expectOne('/api/requests/6ad881e1a051e7edd89d0ef0/history');
    expect(summary.request.method).toBe('GET');
    expect(history.request.method).toBe('GET');
    summary.flush({});
    history.flush([]);
  });

  it('encodes the request id in the URL', () => {
    service.getById('a/b').subscribe();

    const request = http.expectOne('/api/requests/a%2Fb');
    expect(request.request.method).toBe('GET');
    request.flush({});
  });

  it('updates a status with the expected version in the body and the user in X-User-Name', () => {
    let result: RequestDto | undefined;
    service.updateStatus('6ad881e1a051e7edd89d0ef0', 'InProgress', 3, 'dana.cohen').subscribe(r => (result = r));

    const request = http.expectOne('/api/requests/6ad881e1a051e7edd89d0ef0/status');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ status: 'InProgress', expectedVersion: 3 });
    expect(request.request.headers.get('X-User-Name')).toBe('dana.cohen');
    request.flush({ id: '6ad881e1a051e7edd89d0ef0', status: 'InProgress', version: 4 });

    expect(result?.version).toBe(4);
  });

  it('passes a 409 conflict through with its ProblemDetails body', () => {
    let error: HttpErrorResponse | undefined;
    service.updateStatus('6ad881e1a051e7edd89d0ef0', 'Waiting', 1, 'dana.cohen').subscribe({ error: e => (error = e) });

    http.expectOne('/api/requests/6ad881e1a051e7edd89d0ef0/status').flush(
      { status: 409, title: 'The request was changed by another update.', currentVersion: 2, currentStatus: 'InProgress' },
      { status: 409, statusText: 'Conflict' });

    expect(error?.status).toBe(409);
    expect(error?.error.currentVersion).toBe(2);
    expect(error?.error.currentStatus).toBe('InProgress');
  });

  it('sends a bulk update with all items and the user in X-User-Name', () => {
    const items = [
      { id: '6ad881e1a051e7edd89d0ef0', status: 'InProgress' as const, expectedVersion: 1 },
      { id: '6ad881e1a051e7edd89d0ef1', status: 'Completed' as const, expectedVersion: 4 }
    ];
    service.bulkUpdateStatus(items, 'dana.cohen').subscribe();

    const request = http.expectOne('/api/requests/status/bulk');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ items });
    expect(request.request.headers.get('X-User-Name')).toBe('dana.cohen');
    request.flush({ updatedCount: 2, notFoundCount: 0, versionConflictCount: 0, invalidTransitionCount: 0, results: [] });
  });
});
