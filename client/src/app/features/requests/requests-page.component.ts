import { AsyncPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  BehaviorSubject,
  Observable,
  Subject,
  catchError,
  combineLatest,
  debounceTime,
  filter,
  map,
  of,
  scan,
  startWith,
  switchMap
} from 'rxjs';
import { describeHttpError } from '../../core/api/http-error';
import { RequestsApiService } from '../../core/api/requests-api.service';
import { PagedResult, RequestDto, SortField } from '../../core/models/request.models';
import { PagerComponent } from './pager.component';
import { RequestDetailsComponent } from './request-details.component';
import { RequestFiltersComponent } from './request-filters.component';
import { SummaryPanelComponent } from './summary-panel.component';
import { RequestsTableComponent } from './requests-table.component';
import { BulkStatusBarComponent } from './bulk-status-bar.component';
import {
  BULK_MAX_ITEMS,
  DEFAULT_CRITERIA,
  EMPTY_FILTERS,
  ListCriteria,
  ListState,
  RequestFilters,
  hasActiveCriteria,
  selectAll,
  toRequestQuery,
  toggleSelection
} from './requests-list.models';

export const SEARCH_DEBOUNCE_MS = 300;

type FetchEvent =
  | { kind: 'loading'; criteria: ListCriteria; page: number }
  | { kind: 'loaded'; result: PagedResult<RequestDto> }
  | { kind: 'failed'; error: string };

// Container: owns the list criteria and loads each page from the server.
// Search, filters, sorting and paging are always done by the API; this component never filters or pages locally.
@Component({
  selector: 'app-requests-page',
  imports: [
    AsyncPipe,
    RequestFiltersComponent,
    RequestsTableComponent,
    PagerComponent,
    SummaryPanelComponent,
    RequestDetailsComponent,
    BulkStatusBarComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <app-summary-panel />

    <section class="panel">
      <app-request-filters
        [searchText]="searchText"
        [filters]="criteria$.value.filters"
        (searchInput)="onSearchInput($event)"
        (filtersChange)="onFiltersChange($event)"
        (clear)="clearFilters()" />
    </section>

    <div class="workspace" [class.workspace--with-details]="selectedId()">
    @if (state$ | async; as state) {
      <div class="list-column">
      <app-bulk-status-bar
        [items]="checkedRows(state.result?.items ?? [])"
        (completed)="onBulkCompleted()"
        (failed)="onBulkFailed()"
        (clearSelection)="checkedIds.set(emptySelection)" />

      <section class="panel" [attr.aria-busy]="state.loading">
        @if (state.loading) {
          <p class="status-line" role="status">Loading requests…</p>
        }

        @if (state.error) {
          <div class="message message--error" role="alert">
            <p>{{ state.error }}</p>
            <button type="button" (click)="retry()">Try again</button>
          </div>
        } @else if (state.result && state.result.totalCount === 0 && !state.loading) {
          <div class="message" role="status">
            <p>No requests match the current search and filters.</p>
            @if (hasFilters(state.criteria)) {
              <button type="button" (click)="clearFilters()">Clear filters</button>
            }
          </div>
        } @else if (state.result) {
          <div [class.dimmed]="state.loading">
            <app-requests-table
              [items]="state.result.items"
              [sort]="state.criteria.sort"
              [selectedId]="selectedId()"
              [checkedIds]="checkedIds()"
              [canSelectMore]="checkedIds().size < bulkMaxItems"
              (sortChange)="onSortChange($event)"
              (select)="selectedId.set($event)"
              (toggle)="checkedIds.set(toggleSelection(checkedIds(), $event.id, $event.checked))"
              (toggleAll)="onToggleAll($event, state.result.items)" />
            <app-pager
              [page]="state.page"
              [pageSize]="state.criteria.pageSize"
              [totalPages]="state.result.totalPages"
              [totalCount]="state.result.totalCount"
              (pageChange)="goToPage($event)"
              (pageSizeChange)="onPageSizeChange($event)" />
          </div>
        }
      </section>
      </div>
    }

    @if (selectedId(); as id) {
      <app-request-details
        [requestId]="id"
        (updated)="onRequestUpdated()"
        (removed)="reloadList()"
        (closed)="selectedId.set(null)" />
    }
    </div>
  `,
  styles: `
    :host { display: grid; gap: 1rem; }
    .workspace { display: grid; gap: 1rem; align-items: start; }
    .workspace > * { min-width: 0; }
    .list-column { display: grid; gap: 1rem; min-width: 0; }
    @media (min-width: 1000px) {
      .workspace--with-details { grid-template-columns: minmax(0, 1fr) minmax(300px, 400px); }
    }
  `
})
export class RequestsPageComponent {
  private readonly api = inject(RequestsApiService);

  protected readonly criteria$ = new BehaviorSubject<ListCriteria>(DEFAULT_CRITERIA);
  private readonly searchInput$ = new Subject<string>();
  private readonly pageNavigation$ = new Subject<number>();
  private readonly retry$ = new Subject<void>();

  // The raw text of the search box, which may be ahead of the debounced search in the criteria.
  protected searchText = '';

  // Opening a request does not change the list criteria, so the current page and filters are kept.
  protected readonly selectedId = signal<string | null>(null);
  private readonly summary = viewChild.required(SummaryPanelComponent);

  // Rows checked for a bulk status change. The selection belongs to the current page: it is cleared when the
  // page or the criteria change, so every checked row is displayed with the version that was last loaded.
  protected readonly emptySelection: ReadonlySet<string> = new Set<string>();
  protected readonly checkedIds = signal<ReadonlySet<string>>(this.emptySelection);
  protected readonly bulkMaxItems = BULK_MAX_ITEMS;
  protected readonly toggleSelection = toggleSelection;

  // Any change to the criteria restarts paging at page 1; page navigation keeps the criteria.
  private readonly query$ = this.criteria$.pipe(
    switchMap(criteria => this.pageNavigation$.pipe(
      startWith(1),
      map(page => ({ criteria, page })))));

  // switchMap cancels the HTTP request of a previous query when a newer one starts.
  readonly state$: Observable<ListState> = combineLatest([this.query$, this.retry$.pipe(startWith(undefined))]).pipe(
    switchMap(([{ criteria, page }]) => this.api.search(toRequestQuery(criteria, page)).pipe(
      map((result): FetchEvent => ({ kind: 'loaded', result })),
      catchError(error => of<FetchEvent>({ kind: 'failed', error: describeHttpError(error) })),
      startWith<FetchEvent>({ kind: 'loading', criteria, page }))),
    scan((state: ListState, event: FetchEvent): ListState => {
      switch (event.kind) {
        case 'loading':
          // The previous page stays visible (dimmed) while the next one loads.
          return { ...state, criteria: event.criteria, page: event.page, loading: true, error: null };
        case 'loaded':
          return { ...state, loading: false, result: event.result, error: null };
        case 'failed':
          return { ...state, loading: false, error: event.error };
      }
    }, { criteria: DEFAULT_CRITERIA, page: 1, loading: true, result: null, error: null }));

  constructor() {
    this.searchInput$
      .pipe(
        map(text => text.trim()),
        debounceTime(SEARCH_DEBOUNCE_MS),
        filter(search => search !== this.criteria$.value.search),
        takeUntilDestroyed())
      .subscribe(search => this.updateCriteria({ search }));
  }

  protected onSearchInput(text: string): void {
    this.searchText = text;
    this.searchInput$.next(text);
  }

  protected onFiltersChange(filters: RequestFilters): void {
    this.updateCriteria({ filters });
  }

  // Clicking the current sort field reverses the direction; a new field starts descending.
  protected onSortChange(field: SortField): void {
    const sort = this.criteria$.value.sort;
    const direction = sort.field === field && sort.direction === 'desc' ? 'asc' : 'desc';
    this.updateCriteria({ sort: { field, direction } });
  }

  protected onPageSizeChange(pageSize: number): void {
    this.updateCriteria({ pageSize });
  }

  protected goToPage(page: number): void {
    this.checkedIds.set(this.emptySelection);
    this.pageNavigation$.next(page);
  }

  protected onToggleAll(checked: boolean, rows: RequestDto[]): void {
    this.checkedIds.set(checked ? selectAll(this.checkedIds(), rows.map(row => row.id)) : this.emptySelection);
  }

  // The checked rows as currently displayed (latest loaded version of each).
  protected checkedRows(rows: RequestDto[]): RequestDto[] {
    const checked = this.checkedIds();
    return checked.size === 0 ? [] : rows.filter(row => checked.has(row.id));
  }

  protected onBulkCompleted(): void {
    this.checkedIds.set(this.emptySelection);
    this.reloadList();
    this.summary().refresh();
  }

  // The batch may have been partly applied before the error, so the data is reloaded; the selection is kept
  // so the user can try again (an item that was already applied is then reported, not applied twice).
  protected onBulkFailed(): void {
    this.reloadList();
    this.summary().refresh();
  }

  protected clearFilters(): void {
    this.searchText = '';
    this.updateCriteria({ search: '', filters: EMPTY_FILTERS });
  }

  protected retry(): void {
    this.retry$.next();
  }

  // Reloads the current page with the same criteria (after a status change or a request that no longer exists).
  protected reloadList(): void {
    this.retry$.next();
  }

  protected onRequestUpdated(): void {
    this.reloadList();
    this.summary().refresh();
  }

  protected hasFilters(criteria: ListCriteria): boolean {
    return hasActiveCriteria(criteria);
  }

  private updateCriteria(patch: Partial<ListCriteria>): void {
    this.checkedIds.set(this.emptySelection);
    this.criteria$.next({ ...this.criteria$.value, ...patch });
  }
}
