import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RequestDto, RequestStatus, StatusChange } from '../../core/models/request.models';
import { CurrentUserService } from '../../core/user/current-user.service';
import { RequestDetailsComponent } from './request-details.component';

describe('RequestDetailsComponent', () => {
  let fixture: ComponentFixture<RequestDetailsComponent>;
  let http: HttpTestingController;
  let element: HTMLElement;
  let updated: RequestDto[];
  let removed: string[];

  beforeEach(() => {
    localStorage.removeItem('requestsManagement.userName');
    TestBed.configureTestingModule({
      imports: [RequestDetailsComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RequestDetailsComponent);
    element = fixture.nativeElement as HTMLElement;
    updated = [];
    removed = [];
    fixture.componentInstance.updated.subscribe(request => updated.push(request));
    fixture.componentInstance.removed.subscribe(id => removed.push(id));
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('requestsManagement.userName');
  });

  function open(id: string, request: RequestDto, history: StatusChange[] = []): void {
    fixture.componentRef.setInput('requestId', id);
    fixture.detectChanges();
    http.expectOne(`/api/requests/${id}`).flush(request);
    http.expectOne(`/api/requests/${id}/history`).flush(history);
    fixture.detectChanges();
  }

  function signIn(): void {
    TestBed.inject(CurrentUserService).set('dana.cohen');
    fixture.detectChanges();
  }

  function chooseAndSubmit(status: RequestStatus): void {
    const select = element.querySelector('form select') as HTMLSelectElement;
    select.value = status;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('form button[type=submit]') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  function submitButton(): HTMLButtonElement {
    return element.querySelector('form button[type=submit]') as HTMLButtonElement;
  }

  it('loads the request and its history, and shows the history in chronological order', () => {
    open('r1', dto('r1', 'Completed', 3), [
      change('New', 'InProgress', '2025-05-27T10:50:46.075Z', 'dana.biton'),
      change('InProgress', 'Completed', '2025-05-31T14:46:43.665Z', 'maya.peretz')
    ]);

    expect(element.textContent).toContain('Request r1');
    const items = Array.from(element.querySelectorAll('.history li')).map(li => li.textContent!.replace(/\s+/g, ' ').trim());
    expect(items.length).toBe(2);
    expect(items[0]).toContain('New → InProgress');
    expect(items[0]).toContain('dana.biton');
    expect(items[1]).toContain('InProgress → Completed');
    expect(element.textContent).toContain('Completed is a final status');
  });

  it('shows an empty history and offers only the allowed target statuses', () => {
    open('r1', dto('r1', 'New', 1));

    expect(element.textContent).toContain('No status changes yet.');
    const options = Array.from(element.querySelectorAll('form select option')).map(o => (o as HTMLOptionElement).value);
    expect(options).toEqual(['', 'InProgress']);
  });

  it('requires a valid user name before a status can be changed', () => {
    open('r1', dto('r1', 'New', 1));
    const select = element.querySelector('form select') as HTMLSelectElement;
    select.value = 'InProgress';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(submitButton().disabled).toBeTrue();
    expect(element.textContent).toContain('Enter your name');

    signIn();
    expect(submitButton().disabled).toBeFalse();
  });

  it('sends the loaded version and the user name, then shows the result and reloads the history', () => {
    open('r1', dto('r1', 'Waiting', 4));
    signIn();

    chooseAndSubmit('InProgress');

    const put = http.expectOne('/api/requests/r1/status');
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ status: 'InProgress', expectedVersion: 4 });
    expect(put.request.headers.get('X-User-Name')).toBe('dana.cohen');
    put.flush(dto('r1', 'InProgress', 5));
    http.expectOne('/api/requests/r1/history').flush([change('Waiting', 'InProgress', '2026-10-26T10:00:00Z', 'dana.cohen')]);
    fixture.detectChanges();

    expect(updated.map(r => r.version)).toEqual([5]);
    expect(element.textContent).toContain('Status changed to InProgress (version 5)');
    expect(element.querySelector('.history')?.textContent).toContain('Waiting → InProgress');
  });

  it('shows the current status and version on 409, locks the form and does not retry', () => {
    open('r1', dto('r1', 'New', 1));
    signIn();

    chooseAndSubmit('InProgress');
    http.expectOne('/api/requests/r1/status').flush(
      { status: 409, title: 'The request was changed by another update.', currentVersion: 2, currentStatus: 'InProgress' },
      { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    const alert = element.querySelector('[role=alert]')!.textContent!;
    expect(alert).toContain('InProgress');
    expect(alert).toContain('version 2');
    expect(updated.length).toBe(0);
    expect(submitButton().disabled).toBeTrue();
    http.expectNone('/api/requests/r1/status');

    (element.querySelector('[role=alert] button') as HTMLButtonElement).click();
    fixture.detectChanges();
    http.expectOne('/api/requests/r1').flush(dto('r1', 'InProgress', 2));
    http.expectOne('/api/requests/r1/history').flush([]);
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')).toBeNull();
    expect(element.textContent).toContain('Version2');
  });

  it('shows the server validation message on 400', () => {
    open('r1', dto('r1', 'New', 1));
    signIn();

    chooseAndSubmit('InProgress');
    http.expectOne('/api/requests/r1/status').flush(
      { status: 400, errors: { Status: ['Transition from New to InProgress is not allowed.'] } },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')?.textContent).toContain('is not allowed');
    expect(updated.length).toBe(0);
  });

  it('reports a request that no longer exists (404) to the page', () => {
    open('r1', dto('r1', 'New', 1));
    signIn();

    chooseAndSubmit('InProgress');
    http.expectOne('/api/requests/r1/status').flush({ status: 404, title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')?.textContent).toContain('no longer exists');
    expect(removed).toEqual(['r1']);
  });

  it('shows an unavailable database (503) and allows trying again', () => {
    open('r1', dto('r1', 'New', 1));
    signIn();

    chooseAndSubmit('InProgress');
    http.expectOne('/api/requests/r1/status').flush(
      { status: 503, title: 'The database is temporarily unavailable.' },
      { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')?.textContent).toContain('temporarily unavailable');
    expect(submitButton().disabled).toBeFalse();
  });

  it('shows a request that cannot be loaded (404) with a retry button', () => {
    fixture.componentRef.setInput('requestId', 'gone');
    fixture.detectChanges();
    const history = http.expectOne('/api/requests/gone/history');
    http.expectOne('/api/requests/gone').flush({ status: 404, title: 'Not Found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    // forkJoin stops waiting for the history once the request itself has failed.
    expect(history.cancelled).toBeTrue();
    expect(element.querySelector('[role=alert]')?.textContent).toContain('no longer exists');
    expect(element.querySelector('[role=alert] button')?.textContent).toContain('Try again');
  });

  it('cancels a load in progress when another request is selected', () => {
    fixture.componentRef.setInput('requestId', 'r1');
    fixture.detectChanges();
    const first = http.expectOne('/api/requests/r1');
    const firstHistory = http.expectOne('/api/requests/r1/history');

    fixture.componentRef.setInput('requestId', 'r2');
    fixture.detectChanges();

    expect(first.cancelled).toBeTrue();
    expect(firstHistory.cancelled).toBeTrue();
    http.expectOne('/api/requests/r2').flush(dto('r2', 'New', 1));
    http.expectOne('/api/requests/r2/history').flush([]);
  });
});

function dto(id: string, status: RequestStatus, version: number): RequestDto {
  return {
    id,
    title: `Request ${id}`,
    organizationName: 'Acme Logistics',
    status,
    priority: 'Medium',
    assignedTo: 'noa.cohen',
    createdAt: '2025-05-24T10:21:06.088Z',
    updatedAt: '2025-05-31T14:46:43.665Z',
    version
  };
}

function change(previousStatus: RequestStatus, newStatus: RequestStatus, changedAt: string, changedBy: string): StatusChange {
  return { requestId: 'r1', previousStatus, newStatus, changedAt, changedBy };
}
