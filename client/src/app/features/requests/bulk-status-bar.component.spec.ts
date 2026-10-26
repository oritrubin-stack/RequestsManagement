import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { BulkUpdateResponse, RequestDto, RequestStatus } from '../../core/models/request.models';
import { CurrentUserService } from '../../core/user/current-user.service';
import { BulkStatusBarComponent } from './bulk-status-bar.component';

describe('BulkStatusBarComponent', () => {
  let fixture: ComponentFixture<BulkStatusBarComponent>;
  let http: HttpTestingController;
  let element: HTMLElement;
  let completed: BulkUpdateResponse[];
  let failures: number;

  const selection = [dto('a', 'New', 1), dto('b', 'InProgress', 3), dto('c', 'Waiting', 2), dto('d', 'Completed', 4)];

  beforeEach(() => {
    localStorage.removeItem('requestsManagement.userName');
    TestBed.configureTestingModule({
      imports: [BulkStatusBarComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(BulkStatusBarComponent);
    element = fixture.nativeElement as HTMLElement;
    completed = [];
    failures = 0;
    fixture.componentInstance.completed.subscribe(response => completed.push(response));
    fixture.componentInstance.failed.subscribe(() => failures++);
    fixture.componentRef.setInput('items', selection);
    fixture.detectChanges();
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('requestsManagement.userName');
  });

  function signIn(): void {
    TestBed.inject(CurrentUserService).set('dana.cohen');
    fixture.detectChanges();
  }

  function choose(status: RequestStatus): void {
    const select = element.querySelector('select') as HTMLSelectElement;
    select.value = status;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  function submitButton(): HTMLButtonElement {
    return element.querySelector('button[type=submit]') as HTMLButtonElement;
  }

  it('shows how many requests are selected and requires a target status and a user name', () => {
    expect(element.textContent).toContain('4 selected');
    expect(submitButton().disabled).toBeTrue();

    choose('InProgress');
    expect(submitButton().disabled).toBeTrue();
    expect(element.textContent).toContain('Enter your name');

    signIn();
    expect(submitButton().disabled).toBeFalse();
  });

  it('warns how many selected requests the status rules do not allow', () => {
    choose('InProgress');

    // New -> InProgress and Waiting -> InProgress are allowed; InProgress -> InProgress and Completed -> InProgress are not.
    expect(element.textContent).toContain('2 of the selected cannot change to InProgress');
  });

  it('sends every selected id with its loaded version, the target status and X-User-Name', () => {
    signIn();
    choose('InProgress');

    submitButton().click();

    const request = http.expectOne('/api/requests/status/bulk');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('X-User-Name')).toBe('dana.cohen');
    expect(request.request.body).toEqual({
      items: [
        { id: 'a', status: 'InProgress', expectedVersion: 1 },
        { id: 'b', status: 'InProgress', expectedVersion: 3 },
        { id: 'c', status: 'InProgress', expectedVersion: 2 },
        { id: 'd', status: 'InProgress', expectedVersion: 4 }
      ]
    });
    request.flush(mixedResponse());
  });

  it('shows each item as reported by the server, even though the response is HTTP 200', () => {
    signIn();
    choose('InProgress');
    submitButton().click();
    http.expectOne('/api/requests/status/bulk').flush(mixedResponse());
    fixture.detectChanges();

    expect(element.textContent).toContain('1 of 4 updated.');
    expect(element.textContent).toContain('Not found: 1');
    expect(element.textContent).toContain('Changed by someone else: 1');
    expect(element.textContent).toContain('Transition not allowed: 1');

    const rows = Array.from(element.querySelectorAll('.bulk__result tbody tr')).map(row => ({
      outcome: row.getAttribute('data-outcome'),
      text: row.textContent!.replace(/\s+/g, ' ')
    }));
    expect(rows.map(row => row.outcome)).toEqual(['Updated', 'NotFound', 'VersionConflict', 'InvalidTransition']);
    expect(rows[0].text).toContain('Request a');
    expect(rows[0].text).toContain('Now InProgress (version 2)');
    expect(rows[1].text).toContain('no longer exists');
    expect(rows[2].text).toContain('Current status Waiting (version 5)');
    expect(rows[3].text).toContain('which is final');
    expect(completed.length).toBe(1);
  });

  it('does not send a second request while one is in progress', () => {
    signIn();
    choose('InProgress');

    submitButton().click();
    fixture.detectChanges();
    expect(submitButton().disabled).toBeTrue();
    expect(submitButton().textContent).toContain('Updating');
    submitButton().click();

    const request = http.expectOne('/api/requests/status/bulk');
    request.flush(mixedResponse());
    fixture.detectChanges();
    expect(completed.length).toBe(1);
  });

  it('reports a failed batch as possibly partial and does not show item results', () => {
    signIn();
    choose('InProgress');
    submitButton().click();

    http.expectOne('/api/requests/status/bulk').flush(
      { status: 503, title: 'The database is temporarily unavailable.' },
      { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    const alert = element.querySelector('[role=alert]')!.textContent!;
    expect(alert).toContain('temporarily unavailable');
    expect(alert).toContain('may already have been updated');
    expect(element.querySelector('.bulk__result')).toBeNull();
    expect(failures).toBe(1);
    expect(completed.length).toBe(0);
    expect(submitButton().disabled).toBeFalse();
  });

  it('shows a validation error without claiming a partial update', () => {
    signIn();
    choose('InProgress');
    submitButton().click();

    http.expectOne('/api/requests/status/bulk').flush(
      { status: 400, errors: { Items: ['Duplicate request ids: a.'] } },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    const alert = element.querySelector('[role=alert]')!.textContent!;
    expect(alert).toContain('Duplicate request ids');
    expect(alert).not.toContain('may already have been updated');
  });

  it('keeps the results visible after the selection is cleared, until dismissed', () => {
    signIn();
    choose('InProgress');
    submitButton().click();
    http.expectOne('/api/requests/status/bulk').flush(mixedResponse());

    fixture.componentRef.setInput('items', []);
    fixture.detectChanges();
    expect(element.querySelector('form')).toBeNull();
    expect(element.textContent).toContain('1 of 4 updated.');
    expect(element.textContent).toContain('Request a');

    (Array.from(element.querySelectorAll('button')).find(b => b.textContent!.includes('Dismiss')) as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('section')).toBeNull();
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

function mixedResponse(): BulkUpdateResponse {
  return {
    updatedCount: 1,
    notFoundCount: 1,
    versionConflictCount: 1,
    invalidTransitionCount: 1,
    results: [
      { id: 'a', outcome: 'Updated', request: dto('a', 'InProgress', 2) },
      { id: 'b', outcome: 'NotFound' },
      { id: 'c', outcome: 'VersionConflict', currentVersion: 5, currentStatus: 'Waiting' },
      { id: 'd', outcome: 'InvalidTransition', currentVersion: 4, currentStatus: 'Completed',
        message: 'The request is in status Completed, which is final.' }
    ]
  };
}
