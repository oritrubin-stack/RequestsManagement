import { AsyncPipe, DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Subject, catchError, map, of, scan, startWith, switchMap } from 'rxjs';
import { describeHttpError } from '../../core/api/http-error';
import { RequestsApiService } from '../../core/api/requests-api.service';
import { RequestSummary } from '../../core/models/request.models';

interface SummaryState {
  loading: boolean;
  summary: RequestSummary | null;
  error: string | null;
}

type SummaryEvent = { kind: 'loading' } | { kind: 'loaded'; summary: RequestSummary } | { kind: 'failed'; error: string };

// Counts by status and priority, computed by the API (and cached there for up to 30 seconds).
@Component({
  selector: 'app-summary-panel',
  imports: [AsyncPipe, DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (state$ | async; as state) {
      <section class="panel summary" aria-label="Summary" [attr.aria-busy]="state.loading">
        @if (state.error) {
          <div class="message message--error" role="alert">
            <p>Summary: {{ state.error }}</p>
            <button type="button" (click)="refresh()">Try again</button>
          </div>
        } @else {
          @if (state.summary; as summary) {
          <div class="summary__total" [class.dimmed]="state.loading">
            <strong>{{ summary.totalCount }}</strong> requests
          </div>
          <dl class="summary__group" [class.dimmed]="state.loading">
            @for (item of summary.byStatus; track item.status) {
              <div><dt>{{ item.status }}</dt><dd>{{ item.count }}</dd></div>
            }
          </dl>
          <dl class="summary__group" [class.dimmed]="state.loading">
            @for (item of summary.byPriority; track item.priority) {
              <div><dt>{{ item.priority }}</dt><dd>{{ item.count }}</dd></div>
            }
          </dl>
          <small class="summary__time">Computed at {{ summary.generatedAt | date: 'HH:mm:ss' }} (may be cached for up to 30 seconds)</small>
          } @else {
            <p class="status-line" role="status">Loading summary…</p>
          }
        }
      </section>
    }
  `,
  styles: `
    .summary { display: flex; flex-wrap: wrap; align-items: center; gap: 0.75rem 1.5rem; }
    .summary__total strong { font-size: 1.4rem; }
    .summary__group { display: flex; flex-wrap: wrap; gap: 0.5rem; margin: 0; }
    .summary__group div { padding: 0.25rem 0.6rem; border: 1px solid var(--border); border-radius: 4px; }
    dt { display: inline; color: var(--muted); font-size: 0.8rem; margin-right: 0.35rem; }
    dd { display: inline; margin: 0; font-weight: 600; }
    .summary__time { color: var(--muted); width: 100%; }
  `
})
export class SummaryPanelComponent {
  private readonly api = inject(RequestsApiService);
  private readonly refresh$ = new Subject<void>();

  // The previous counts stay visible (dimmed) while a refresh is loading.
  protected readonly state$ = this.refresh$.pipe(
    startWith(undefined),
    switchMap(() => this.api.getSummary().pipe(
      map((summary): SummaryEvent => ({ kind: 'loaded', summary })),
      catchError(error => of<SummaryEvent>({ kind: 'failed', error: describeHttpError(error) })),
      startWith<SummaryEvent>({ kind: 'loading' }))),
    scan((state: SummaryState, event: SummaryEvent): SummaryState => {
      switch (event.kind) {
        case 'loading':
          return { ...state, loading: true, error: null };
        case 'loaded':
          return { loading: false, summary: event.summary, error: null };
        case 'failed':
          return { ...state, loading: false, error: event.error };
      }
    }, { loading: true, summary: null, error: null }));

  refresh(): void {
    this.refresh$.next();
  }
}
