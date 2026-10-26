import { HttpErrorResponse } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { describeHttpError } from '../../core/api/http-error';
import { RequestsApiService } from '../../core/api/requests-api.service';
import {
  BulkUpdateItemResult,
  BulkUpdateResponse,
  REQUEST_STATUSES,
  RequestDto,
  RequestStatus,
  StatusUpdateOutcome
} from '../../core/models/request.models';
import { isTransitionAllowed } from '../../core/models/status-transitions';
import { CurrentUserService } from '../../core/user/current-user.service';

const OUTCOME_LABELS: Record<StatusUpdateOutcome, string> = {
  Updated: 'Updated',
  NotFound: 'Not found',
  VersionConflict: 'Changed by someone else',
  InvalidTransition: 'Transition not allowed'
};

// Changes the status of the selected requests in one POST /api/requests/status/bulk call.
// Each item is reported exactly as the server answered: an HTTP 200 does not mean that every item was updated.
@Component({
  selector: 'app-bulk-status-bar',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (items().length > 0 || result() || error()) {
      <section class="panel bulk" aria-label="Bulk status change">
        @if (items().length > 0) {
          <form class="bulk__form" (submit)="$event.preventDefault(); submit()">
            <strong role="status">{{ items().length }} selected</strong>
            <label>
              New status
              <select [value]="target()" (change)="target.set(selectValue($event))" [disabled]="submitting()">
                <option value="">Choose…</option>
                @for (status of statuses; track status) {
                  <option [value]="status">{{ status }}</option>
                }
              </select>
            </label>
            <button type="submit" [disabled]="!canSubmit()">{{ submitting() ? 'Updating…' : 'Change status of selected' }}</button>
            <button type="button" class="secondary" [disabled]="submitting()" (click)="clearSelection.emit()">Clear selection</button>
            @if (target() && notAllowedCount() > 0) {
              <p class="hint">
                {{ notAllowedCount() }} of the selected cannot change to {{ target() }} under the status rules;
                the server will report them as not allowed.
              </p>
            }
            @if (!currentUser.isValid()) {
              <p class="hint">Enter your name at the top of the page to change statuses.</p>
            }
          </form>
        }

        @if (error(); as error) {
          <div class="message message--error" role="alert">
            <p>{{ error }}</p>
          </div>
        }

        @if (result(); as result) {
          <div class="bulk__result" role="status">
            <p>
              <strong>{{ result.updatedCount }} of {{ result.results.length }} updated.</strong>
              Not found: {{ result.notFoundCount }} ·
              Changed by someone else: {{ result.versionConflictCount }} ·
              Transition not allowed: {{ result.invalidTransitionCount }}
            </p>
            <div class="table-wrapper">
              <table>
                <thead>
                  <tr><th scope="col">Request</th><th scope="col">Result</th><th scope="col">Details</th></tr>
                </thead>
                <tbody>
                  @for (item of result.results; track item.id) {
                    <tr [attr.data-outcome]="item.outcome">
                      <td>{{ titleOf(item.id) }}</td>
                      <td>{{ outcomeLabel(item.outcome) }}</td>
                      <td>{{ describe(item) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <button type="button" class="secondary" (click)="dismiss()">Dismiss</button>
          </div>
        }
      </section>
    }
  `,
  styles: `
    /* Takes no space in the page layout while nothing is selected. */
    :host { display: contents; }
    .bulk { display: grid; gap: 0.75rem; }
    .bulk__form { display: flex; flex-wrap: wrap; align-items: end; gap: 0.5rem 0.75rem; }
    .bulk__form label { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.85rem; }
    .hint { width: 100%; margin: 0; color: var(--muted); font-size: 0.85rem; }
    .bulk__result p { margin: 0 0 0.5rem; }
    .table-wrapper { overflow-x: auto; margin-bottom: 0.5rem; }
    table { width: 100%; border-collapse: collapse; font-size: 0.85rem; }
    th, td { text-align: left; padding: 0.35rem 0.5rem; border-bottom: 1px solid var(--border); }
    tr[data-outcome='Updated'] td:nth-child(2) { color: #067647; }
    tr:not([data-outcome='Updated']) td:nth-child(2) { color: var(--error); }
  `
})
export class BulkStatusBarComponent {
  // The selected rows as currently displayed; their versions are sent as expectedVersion.
  readonly items = input.required<RequestDto[]>();

  readonly completed = output<BulkUpdateResponse>();
  readonly failed = output<void>();
  readonly clearSelection = output<void>();

  protected readonly currentUser = inject(CurrentUserService);
  private readonly api = inject(RequestsApiService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly statuses = REQUEST_STATUSES;
  protected readonly target = signal<RequestStatus | ''>('');
  protected readonly submitting = signal(false);
  protected readonly result = signal<BulkUpdateResponse | null>(null);
  protected readonly error = signal<string | null>(null);

  // Titles of the items that were sent, so results stay readable after the selection is cleared.
  private readonly sentTitles = signal(new Map<string, string>());

  protected readonly canSubmit = computed(() =>
    !this.submitting() && this.target() !== '' && this.items().length > 0 && this.currentUser.isValid());

  protected readonly notAllowedCount = computed(() => {
    const target = this.target();
    return target === '' ? 0 : this.items().filter(item => !isTransitionAllowed(item.status, target)).length;
  });

  protected submit(): void {
    const target = this.target();
    if (!this.canSubmit() || target === '') {
      return;
    }

    const items = this.items();
    this.submitting.set(true);
    this.error.set(null);
    this.result.set(null);
    this.sentTitles.set(new Map(items.map(item => [item.id, item.title])));

    this.api.bulkUpdateStatus(
      items.map(item => ({ id: item.id, status: target, expectedVersion: item.version })),
      this.currentUser.name())
      .pipe(finalize(() => this.submitting.set(false)), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: response => {
          this.result.set(response);
          this.target.set('');
          this.completed.emit(response);
        },
        error: error => {
          this.error.set(this.describeFailure(error));
          this.failed.emit();
        }
      });
  }

  protected dismiss(): void {
    this.result.set(null);
    this.error.set(null);
  }

  protected titleOf(id: string): string {
    return this.sentTitles().get(id) ?? id;
  }

  protected outcomeLabel(outcome: StatusUpdateOutcome): string {
    return OUTCOME_LABELS[outcome];
  }

  protected describe(item: BulkUpdateItemResult): string {
    switch (item.outcome) {
      case 'Updated':
        return `Now ${item.request?.status} (version ${item.request?.version}).`;
      case 'NotFound':
        return 'The request no longer exists.';
      case 'VersionConflict':
        return `Current status ${item.currentStatus} (version ${item.currentVersion}). Reload before trying again.`;
      case 'InvalidTransition':
        return item.message ?? `Not allowed from ${item.currentStatus}.`;
    }
  }

  protected selectValue(event: Event): RequestStatus | '' {
    return (event.target as HTMLSelectElement).value as RequestStatus | '';
  }

  // A failed batch may still have applied some items before the error (the API stops at the first failure).
  private describeFailure(error: unknown): string {
    const message = describeHttpError(error);
    return error instanceof HttpErrorResponse && error.status !== 400
      ? `${message} Some of the selected requests may already have been updated; the list has been reloaded.`
      : message;
  }
}
