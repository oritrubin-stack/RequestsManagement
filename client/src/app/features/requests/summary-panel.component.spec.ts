import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { RequestSummary } from '../../core/models/request.models';
import { SummaryPanelComponent } from './summary-panel.component';

describe('SummaryPanelComponent', () => {
  let fixture: ComponentFixture<SummaryPanelComponent>;
  let http: HttpTestingController;
  let element: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [SummaryPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SummaryPanelComponent);
    element = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('shows the total and the counts by status and priority', () => {
    expect(element.textContent).toContain('Loading summary');

    http.expectOne('/api/requests/summary').flush(summary(100));
    fixture.detectChanges();

    expect(element.textContent).toContain('100');
    expect(element.textContent).toContain('Waiting');
    expect(element.textContent).toContain('15');
    expect(element.textContent).toContain('High');
    expect(element.textContent).toContain('Computed at');
  });

  it('reloads on refresh and keeps the previous counts visible while loading', () => {
    http.expectOne('/api/requests/summary').flush(summary(100));
    fixture.detectChanges();

    fixture.componentInstance.refresh();
    fixture.detectChanges();
    expect(element.querySelector('.dimmed')).not.toBeNull();
    expect(element.textContent).toContain('100');

    http.expectOne('/api/requests/summary').flush(summary(101));
    fixture.detectChanges();
    expect(element.textContent).toContain('101');
    expect(element.querySelector('.dimmed')).toBeNull();
  });

  it('shows an error with a retry button', () => {
    http.expectOne('/api/requests/summary').flush(
      { status: 503, title: 'The database is temporarily unavailable.' },
      { status: 503, statusText: 'Service Unavailable' });
    fixture.detectChanges();

    expect(element.querySelector('[role=alert]')?.textContent).toContain('temporarily unavailable');

    (element.querySelector('[role=alert] button') as HTMLButtonElement).click();
    http.expectOne('/api/requests/summary').flush(summary(100));
    fixture.detectChanges();
    expect(element.querySelector('[role=alert]')).toBeNull();
  });
});

function summary(total: number): RequestSummary {
  return {
    totalCount: total,
    byStatus: [
      { status: 'New', count: 20 },
      { status: 'InProgress', count: 30 },
      { status: 'Waiting', count: 15 },
      { status: 'Completed', count: total - 65 }
    ],
    byPriority: [
      { priority: 'Low', count: 40 },
      { priority: 'Medium', count: 40 },
      { priority: 'High', count: total - 80 }
    ],
    generatedAt: '2026-10-26T10:00:00.000Z'
  };
}
