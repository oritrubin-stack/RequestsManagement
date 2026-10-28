import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Subject, catchError, combineLatest, finalize, forkJoin, map, of, startWith, switchMap } from 'rxjs';
import { describeHttpError } from '../../core/api/http-error';
import { RequestsApiService } from '../../core/api/requests-api.service';
import { ProblemDetails, RequestDto, RequestStatus, StatusChange } from '../../core/models/request.models';
import { allowedTargets } from '../../core/models/status-transitions';
import { CurrentUserService } from '../../core/user/current-user.service';

interface DetailsView {
  loading: boolean;
  request: RequestDto | null;
  history: StatusChange[] | null;
  error: string | null;
}

type Feedback =
  | { kind: 'success'; message: string }
  | { kind: 'conflict'; currentStatus?: RequestStatus; currentVersion?: number }
  | { kind: 'error'; message: string };

// One request with its status history and a status change form.
// Updates use the version that was last loaded; a 409 is shown to the user and is never retried automatically.
@Component({
  selector: 'app-request-details',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="panel details" aria-label="Request details">
      <header class="details__header">
        <h2>Request details</h2>
        <button type="button" class="secondary" (click)="closed.emit()">Close</button>
      </header>

      @if (view().loading) {
        <p class="status-line" role="status">Loading request…</p>
      } @else if (view().error) {
        <div class="message message--error" role="alert">
          <p>{{ view().error }}</p>
          <button type="button" (click)="reload()">Try again</button>
        </div>
      } @else {
        @if (view().request; as request) {
        <h3>{{ request.title }}</h3>
        <dl class="details__fields">
          <div><dt>Organization</dt><dd>{{ request.organizationName }}</dd></div>
          <div><dt>Status</dt><dd><span class="badge" [attr.data-status]="request.status">{{ request.status }}</span></dd></div>
          <div><dt>Priority</dt><dd>{{ request.priority }}</dd></div>
          <div><dt>Assigned to</dt><dd>{{ request.assignedTo }}</dd></div>
          <div><dt>Created</dt><dd>{{ request.createdAt | date: 'yyyy-MM-dd HH:mm' }}</dd></div>
          <div><dt>Updated</dt><dd>{{ request.updatedAt | date: 'yyyy-MM-dd HH:mm' }}</dd></div>
          <div><dt>Version</dt><dd>{{ request.version }}</dd></div>
        </dl>

        <form class="details__form" (submit)="$event.preventDefault(); changeStatus()">
          <h4>Change status</h4>
          @if (targets().length === 0) {
            <p class="status-line">{{ request.status }} is a final status.</p>
          } @else {
            <label>
              New status
              <select [value]="target()" (change)="target.set(selectValue($event))" [disabled]="formLocked()">
                <option value="">Choose…</option>
                @for (status of targets(); track status) {
                  <option [value]="status">{{ status }}</option>
                }
              </select>
            </label>
            <button type="submit" [disabled]="!canSubmit()">{{ saving() ? 'Saving…' : 'Change status' }}</button>
            @if (!currentUser.isValid()) {
              <p class="hint">Enter your name at the top of the page to change the status.</p>
            }
          }
        </form>

        @if (feedback(); as feedback) {
          @switch (feedback.kind) {
            @case ('success') {
              <p class="message" role="status">{{ feedback.message }}</p>
            }
            @case ('conflict') {
              <div class="message message--error" role="alert">
                <p>
                  This request was changed by another update. Its current status is
                  <strong>{{ feedback.currentStatus }}</strong> (version {{ feedback.currentVersion }}).
                  Reload it before changing the status again.
                </p>
                <button type="button" (click)="reload()">Reload request</button>
              </div>
            }
            @case ('error') {
              <p class="message message--error" role="alert">{{ feedback.message }}</p>
            }
          }
        }

        <h4>Status history</h4>
        @if (view().history; as history) {
          @if (history.length === 0) {
            <p class="status-line">No status changes yet.</p>
          } @else {
            <ol class="history">
              @for (change of history; track $index) {
                <li>
                  <span>{{ change.previousStatus }} → {{ change.newStatus }}</span>
                  <small>{{ change.changedAt | date: 'yyyy-MM-dd HH:mm:ss' }} · {{ change.changedBy }}</small>
                </li>
              }
            </ol>
          }
        }
        }
      }
    </section>
  `,
  styles: `
    .details__header { display: flex; justify-content: space-between; align-items: center; gap: 0.5rem; }
    h2 { font-size: 1rem; margin: 0; }
    h3 { font-size: 1.05rem; margin: 0.75rem 0; }
    h4 { font-size: 0.9rem; margin: 1rem 0 0.5rem; }
    .details__fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 0.5rem; margin: 0; }
    dt { font-size: 0.75rem; color: var(--muted); }
    dd { margin: 0; }
    .details__form { display: flex; flex-wrap: wrap; align-items: end; gap: 0.5rem; }
    .details__form h4 { width: 100%; }
    .details__form label { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.85rem; }
    .hint { width: 100%; margin: 0; color: var(--muted); font-size: 0.85rem; }
    .message { margin: 0.75rem 0 0; }
    .history { margin: 0; padding-left: 1.25rem; display: grid; gap: 0.35rem; }
    .history li small { display: block; color: var(--muted); }
  `
})
export class RequestDetailsComponent {
  readonly requestId = input.required<string>();

  readonly updated = output<RequestDto>();
  readonly removed = output<string>();
  readonly closed = output<void>();

  protected readonly currentUser = inject(CurrentUserService);
  private readonly api = inject(RequestsApiService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly reload$ = new Subject<void>();

  protected readonly view = signal<DetailsView>({ loading: true, request: null, history: null, error: null });
  protected readonly target = signal<RequestStatus | ''>('');
  protected readonly saving = signal(false);
  protected readonly feedback = signal<Feedback | null>(null);

  protected readonly targets = computed(() => {
    const request = this.view().request;
    return request ? allowedTargets(request.status) : [];
  });

  // After a conflict the loaded version is known to be stale, so the form stays locked until the request is reloaded.
  protected readonly formLocked = computed(() => this.saving() || this.feedback()?.kind === 'conflict');

  protected readonly canSubmit = computed(() =>
    !this.formLocked() && this.target() !== '' && this.currentUser.isValid() && this.view().request !== null);

  constructor() {
    // Selecting another request (or reloading) cancels a load that is still in progress.
    combineLatest([toObservable(this.requestId), this.reload$.pipe(startWith(undefined))])
      .pipe(
        switchMap(([id]) => forkJoin({ request: this.api.getById(id), history: this.api.getHistory(id) }).pipe(
          map(({ request, history }): DetailsView => ({ loading: false, request, history, error: null })),
          catchError(error => of<DetailsView>({ loading: false, request: null, history: null, error: this.describeLoadError(error) })),
          startWith<DetailsView>({ loading: true, request: null, history: null, error: null }))),
        takeUntilDestroyed())
      .subscribe(view => {
        if (view.loading) {
          this.feedback.set(null);
          this.target.set('');
        }
        this.view.set(view);
      });
  }

  protected reload(): void {
    this.reload$.next();
  }

  protected changeStatus(): void {
    const request = this.view().request;
    const target = this.target();
    if (!this.canSubmit() || !request || target === '') {
      return;
    }

    this.saving.set(true);
    this.feedback.set(null);

    this.api.updateStatus(request.id, target, request.version, this.currentUser.name())
      .pipe(finalize(() => this.saving.set(false)), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: updated => this.onUpdated(updated),
        error: error => this.onUpdateFailed(request.id, error)
      });
  }

  protected selectValue(event: Event): RequestStatus | '' {
    return (event.target as HTMLSelectElement).value as RequestStatus | '';
  }

  private onUpdated(updated: RequestDto): void {
    // The page is told about every successful update, even when another request is displayed by now.
    this.updated.emit(updated);
    if (!this.isDisplayed(updated.id)) {
      return;
    }

    this.view.update(view => ({ ...view, request: updated }));
    this.target.set('');
    this.feedback.set({ kind: 'success', message: `Status changed to ${updated.status} (version ${updated.version}).` });

    this.api.getHistory(updated.id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: history => {
          if (this.isDisplayed(updated.id)) {
            this.view.update(view => ({ ...view, history }));
          }
        },
        error: () => {
          if (this.isDisplayed(updated.id)) {
            this.feedback.set({ kind: 'error', message: 'The status was changed, but the history could not be reloaded.' });
          }
        }
      });
  }

  private onUpdateFailed(id: string, error: unknown): void {
    if (!this.isDisplayed(id)) {
      return;
    }

    if (!(error instanceof HttpErrorResponse)) {
      this.feedback.set({ kind: 'error', message: describeHttpError(error) });
      return;
    }

    switch (error.status) {
      case 409: {
        const problem = (error.error ?? {}) as ProblemDetails;
        this.feedback.set({ kind: 'conflict', currentStatus: problem.currentStatus, currentVersion: problem.currentVersion });
        break;
      }
      case 404:
        this.feedback.set({ kind: 'error', message: 'This request no longer exists.' });
        this.removed.emit(id);
        break;
      default:
        // 400 (validation or a disallowed transition), 503 and network errors.
        this.feedback.set({ kind: 'error', message: describeHttpError(error) });
    }
  }

  // The user may select another request while a save is in flight; its outcome is then not shown in this panel.
  private isDisplayed(id: string): boolean {
    return this.requestId() === id;
  }

  private describeLoadError(error: unknown): string {
    return error instanceof HttpErrorResponse && error.status === 404
      ? 'This request no longer exists.'
      : describeHttpError(error);
  }
}
