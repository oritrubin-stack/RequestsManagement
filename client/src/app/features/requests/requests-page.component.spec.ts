import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { PagedResult, RequestDto, RequestSummary } from '../../core/models/request.models';
import { CurrentUserService } from '../../core/user/current-user.service';
import { RequestsPageComponent, SEARCH_DEBOUNCE_MS } from './requests-page.component';

describe('RequestsPageComponent', () => {
  let fixture: ComponentFixture<RequestsPageComponent>;
  let http: HttpTestingController;
  let element: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RequestsPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    localStorage.removeItem('requestsManagement.userName');
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RequestsPageComponent);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    http.expectOne('/api/requests/summary').flush(summary());
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('requestsManagement.userName');
  });

  function pendingSearch(): TestRequest {
    return http.expectOne(request => request.url === '/api/requests');
  }

  function respond(request: TestRequest, result: Partial<PagedResult<RequestDto>> = {}): void {
    const items = result.items ?? [requestDto('1'), requestDto('2')];
    request.flush({ items, page: 1, pageSize: 20, totalCount: items.length, totalPages: 1, ...result });
    fixture.detectChanges();
  }

  function typeSearch(text: string): void {
    const input = element.querySelector<HTMLInputElement>('input[type=search]')!;
    input.value = text;
    input.dispatchEvent(new Event('input'));
  }

  function change(selector: string, value: string, index = 0): void {
    const control = element.querySelectorAll<HTMLInputElement | HTMLSelectElement>(selector)[index];
    control.value = value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function click(text: string): void {
    const button = Array.from(element.querySelectorAll('button')).find(b => b.textContent!.includes(text))!;
    button.click();
    fixture.detectChanges();
  }

  it('loads the first page from the server with the default sort and shows the rows', () => {
    const request = pendingSearch();
    expect(request.request.params.get('page')).toBe('1');
    expect(request.request.params.get('pageSize')).toBe('20');
    expect(request.request.params.get('sortBy')).toBe('createdAt');
    expect(request.request.params.get('sortDirection')).toBe('desc');
    expect(element.textContent).toContain('Loading requests');

    respond(request, { items: [requestDto('1', 'Invoice A'), requestDto('2', 'Invoice B')], totalCount: 42, totalPages: 3 });

    expect(element.querySelectorAll('tbody tr').length).toBe(2);
    expect(element.textContent).toContain('Invoice A');
    expect(element.textContent).toContain('Page 1 of 3');
    expect(element.textContent).not.toContain('Loading requests');
  });

  it('debounces typing and sends one search request after the user stops typing', fakeAsync(() => {
    respond(pendingSearch());

    typeSearch('inv');
    tick(100);
    typeSearch('invoi');
    tick(100);
    typeSearch('invoice');
    tick(SEARCH_DEBOUNCE_MS - 1);
    http.expectNone(request => request.url === '/api/requests');

    tick(1);
    const request = pendingSearch();
    expect(request.request.params.get('search')).toBe('invoice');
    respond(request);
  }));

  it('does not search again when the text only changes by surrounding spaces', fakeAsync(() => {
    respond(pendingSearch());
    typeSearch('invoice');
    tick(SEARCH_DEBOUNCE_MS);
    respond(pendingSearch());

    typeSearch(' invoice ');
    tick(SEARCH_DEBOUNCE_MS);

    expect(http.match(request => request.url === '/api/requests').length).toBe(0);
  }));

  it('cancels the request in flight when the criteria change (switchMap)', () => {
    const first = pendingSearch();

    change('app-request-filters select', 'Waiting');

    expect(first.cancelled).toBeTrue();
    const second = pendingSearch();
    expect(second.request.params.get('status')).toBe('Waiting');
    respond(second);
  });

  it('cancels a slow search when a newer search starts', fakeAsync(() => {
    respond(pendingSearch());
    typeSearch('invoice');
    tick(SEARCH_DEBOUNCE_MS);
    const slow = pendingSearch();

    typeSearch('payroll');
    tick(SEARCH_DEBOUNCE_MS);

    expect(slow.cancelled).toBeTrue();
    const latest = pendingSearch();
    expect(latest.request.params.get('search')).toBe('payroll');
    respond(latest);
  }));

  it('returns to the first page when a filter, the sort or the page size changes', () => {
    respond(pendingSearch(), { totalCount: 100, totalPages: 5 });

    click('Next');
    const pageTwo = pendingSearch();
    expect(pageTwo.request.params.get('page')).toBe('2');
    respond(pageTwo, { page: 2, totalCount: 100, totalPages: 5 });

    change('app-request-filters select', 'High', 1);
    const filtered = pendingSearch();
    expect(filtered.request.params.get('priority')).toBe('High');
    expect(filtered.request.params.get('page')).toBe('1');
    respond(filtered, { totalCount: 100, totalPages: 5 });

    click('Next');
    respond(pendingSearch(), { page: 2, totalCount: 100, totalPages: 5 });
    click('Updated');
    expect(pendingSearch().request.params.get('page')).toBe('1');
  });

  it('sorts on the server: a new field starts descending, the same field toggles', () => {
    respond(pendingSearch());

    click('Created');
    const ascending = pendingSearch();
    expect(ascending.request.params.get('sortBy')).toBe('createdAt');
    expect(ascending.request.params.get('sortDirection')).toBe('asc');
    respond(ascending);

    click('Priority');
    const priority = pendingSearch();
    expect(priority.request.params.get('sortBy')).toBe('priority');
    expect(priority.request.params.get('sortDirection')).toBe('desc');
    respond(priority);
  });

  it('changes the page size on the server', () => {
    respond(pendingSearch());

    change('app-pager select', '50');

    const request = pendingSearch();
    expect(request.request.params.get('pageSize')).toBe('50');
    expect(request.request.params.get('page')).toBe('1');
    respond(request);
  });

  it('sends the date range as a half-open UTC range with an inclusive end day', () => {
    respond(pendingSearch());

    change('input[type=date]', '2025-01-01', 0);
    respond(pendingSearch());
    change('input[type=date]', '2025-06-30', 1);

    const params = pendingSearch().request.params;
    expect(params.get('createdFrom')).toBe('2025-01-01T00:00:00Z');
    expect(params.get('createdTo')).toBe('2025-07-01T00:00:00Z');
  });

  it('sends the exact-match filters for organization and assignee', () => {
    respond(pendingSearch());

    change('app-request-filters input[type=text]', 'Acme Logistics', 0);
    respond(pendingSearch());
    change('app-request-filters input[type=text]', 'noa.cohen', 1);

    const params = pendingSearch().request.params;
    expect(params.get('organizationName')).toBe('Acme Logistics');
    expect(params.get('assignedTo')).toBe('noa.cohen');
  });

  it('shows the empty state and clears all filters with one request', () => {
    respond(pendingSearch());
    change('app-request-filters select', 'Completed');
    respond(pendingSearch(), { items: [], totalCount: 0, totalPages: 0 });

    expect(element.textContent).toContain('No requests match');
    expect(element.querySelector('table')).toBeNull();

    click('Clear filters');

    const request = pendingSearch();
    expect(request.request.params.has('status')).toBeFalse();
    respond(request);
    expect(element.querySelectorAll('tbody tr').length).toBe(2);
  });

  it('shows the error state with the server message and retries the same query', () => {
    respond(pendingSearch());
    change('app-request-filters select', 'Waiting');

    pendingSearch().flush(
      { status: 503, title: 'The database is temporarily unavailable.' },
      { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')?.textContent).toContain('The database is temporarily unavailable.');

    click('Try again');
    const retried = pendingSearch();
    expect(retried.request.params.get('status')).toBe('Waiting');
    respond(retried);
    expect(element.querySelector('[role=alert]')).toBeNull();
  });

  it('shows server validation messages as the error', () => {
    respond(pendingSearch());
    change('input[type=date]', '2025-07-01', 0);
    respond(pendingSearch());
    change('input[type=date]', '2025-01-01', 1);

    pendingSearch().flush(
      { status: 400, title: 'One or more validation errors occurred.', errors: { CreatedFrom: ['CreatedFrom must be earlier than CreatedTo.'] } },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')?.textContent).toContain('CreatedFrom must be earlier than CreatedTo.');
  });

  it('opens request details without reloading the list or changing its criteria', () => {
    respond(pendingSearch(), { totalCount: 100, totalPages: 5 });
    click('Next');
    respond(pendingSearch(), { page: 2, totalCount: 100, totalPages: 5 });

    click('Request 1');

    http.expectOne('/api/requests/1').flush(requestDto('1'));
    http.expectOne('/api/requests/1/history').flush([]);
    http.expectNone(request => request.url === '/api/requests');
    fixture.detectChanges();

    expect(element.querySelector('app-request-details')).not.toBeNull();
    expect(element.querySelector('tr.selected')?.textContent).toContain('Request 1');
    expect(element.textContent).toContain('Page 2 of 5');
  });

  it('after a successful status change, reloads the same page and refreshes the summary', () => {
    TestBed.inject(CurrentUserService).set('dana.cohen');
    respond(pendingSearch(), { totalCount: 100, totalPages: 5 });
    change('app-request-filters select', 'New');
    respond(pendingSearch(), { totalCount: 100, totalPages: 5 });
    click('Next');
    respond(pendingSearch(), { page: 2, totalCount: 100, totalPages: 5 });

    click('Request 1');
    http.expectOne('/api/requests/1').flush(requestDto('1'));
    http.expectOne('/api/requests/1/history').flush([]);
    fixture.detectChanges();

    change('app-request-details form select', 'InProgress');
    (element.querySelector('app-request-details form button[type=submit]') as HTMLButtonElement).click();
    http.expectOne('/api/requests/1/status').flush({ ...requestDto('1'), status: 'InProgress', version: 2 });

    const reloaded = pendingSearch();
    expect(reloaded.request.params.get('page')).toBe('2');
    expect(reloaded.request.params.get('status')).toBe('New');
    respond(reloaded, { page: 2, totalCount: 99, totalPages: 5 });
    http.expectOne('/api/requests/summary').flush(summary());
    http.expectOne('/api/requests/1/history').flush([]);
  });

  function checkRow(index: number): void {
    const box = element.querySelectorAll<HTMLInputElement>('tbody input[type=checkbox]')[index];
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('counts the checked rows and clears the selection when the page or the filters change', () => {
    respond(pendingSearch(), { totalCount: 100, totalPages: 5 });

    checkRow(0);
    checkRow(1);
    expect(element.querySelector('app-bulk-status-bar')?.textContent).toContain('2 selected');

    click('Next');
    respond(pendingSearch(), { page: 2, totalCount: 100, totalPages: 5 });
    expect(element.querySelector('app-bulk-status-bar form')).toBeNull();

    checkRow(0);
    change('app-request-filters select', 'Waiting');
    respond(pendingSearch());
    expect(element.querySelector('app-bulk-status-bar form')).toBeNull();
  });

  it('selects every row of the page with the header checkbox', () => {
    respond(pendingSearch());

    const all = element.querySelector('thead input[type=checkbox]') as HTMLInputElement;
    all.checked = true;
    all.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(element.querySelector('app-bulk-status-bar')?.textContent).toContain('2 selected');
  });

  it('after a bulk update, clears the selection, reloads the same page and refreshes the summary', () => {
    TestBed.inject(CurrentUserService).set('dana.cohen');
    respond(pendingSearch(), { totalCount: 100, totalPages: 5 });
    click('Next');
    respond(pendingSearch(), { page: 2, totalCount: 100, totalPages: 5 });
    checkRow(0);
    checkRow(1);

    change('app-bulk-status-bar select', 'InProgress');
    (element.querySelector('app-bulk-status-bar button[type=submit]') as HTMLButtonElement).click();
    const bulk = http.expectOne('/api/requests/status/bulk');
    expect(bulk.request.body.items.map((item: { id: string }) => item.id)).toEqual(['1', '2']);
    bulk.flush({
      updatedCount: 2, notFoundCount: 0, versionConflictCount: 0, invalidTransitionCount: 0,
      results: [
        { id: '1', outcome: 'Updated', request: { ...requestDto('1'), status: 'InProgress', version: 2 } },
        { id: '2', outcome: 'Updated', request: { ...requestDto('2'), status: 'InProgress', version: 2 } }
      ]
    });

    const reloaded = pendingSearch();
    expect(reloaded.request.params.get('page')).toBe('2');
    respond(reloaded, { page: 2, totalCount: 100, totalPages: 5 });
    http.expectOne('/api/requests/summary').flush(summary());

    expect(element.querySelector('app-bulk-status-bar form')).toBeNull();
    expect(element.querySelector('app-bulk-status-bar')?.textContent).toContain('2 of 2 updated.');
  });

  it('after a failed bulk update, keeps the selection and reloads the list and the summary', () => {
    TestBed.inject(CurrentUserService).set('dana.cohen');
    respond(pendingSearch());
    checkRow(0);

    change('app-bulk-status-bar select', 'InProgress');
    (element.querySelector('app-bulk-status-bar button[type=submit]') as HTMLButtonElement).click();
    http.expectOne('/api/requests/status/bulk').flush(
      { status: 503, title: 'The database is temporarily unavailable.' },
      { status: 503, statusText: 'Service Unavailable' });

    respond(pendingSearch());
    http.expectOne('/api/requests/summary').flush(summary());

    expect(element.querySelector('app-bulk-status-bar')?.textContent).toContain('1 selected');
  });
});

function summary(): RequestSummary {
  return {
    totalCount: 100,
    byStatus: [{ status: 'New', count: 100 }],
    byPriority: [{ priority: 'Medium', count: 100 }],
    generatedAt: '2026-10-26T10:00:00.000Z'
  };
}

function requestDto(id: string, title = `Request ${id}`): RequestDto {
  return {
    id,
    title,
    organizationName: 'Acme Logistics',
    status: 'New',
    priority: 'Medium',
    assignedTo: 'noa.cohen',
    createdAt: '2025-05-24T10:21:06.088Z',
    updatedAt: '2025-05-24T10:21:06.088Z',
    version: 1
  };
}
