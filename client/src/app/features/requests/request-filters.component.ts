import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { REQUEST_PRIORITIES, REQUEST_STATUSES } from '../../core/models/request.models';
import { RequestFilters } from './requests-list.models';

// Presentational: shows the current search and filters and reports changes. It does not call the API.
@Component({
  selector: 'app-request-filters',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="filters" (submit)="$event.preventDefault()" aria-label="Search and filters">
      <label class="filters__search">
        Search
        <input type="search" placeholder="Words in title or organization" [value]="searchText()"
               (input)="searchInput.emit(inputValue($event))" />
      </label>

      <label>
        Status
        <select [value]="filters().status" (change)="update('status', inputValue($event))">
          <option value="">All</option>
          @for (status of statuses; track status) {
            <option [value]="status">{{ status }}</option>
          }
        </select>
      </label>

      <label>
        Priority
        <select [value]="filters().priority" (change)="update('priority', inputValue($event))">
          <option value="">All</option>
          @for (priority of priorities; track priority) {
            <option [value]="priority">{{ priority }}</option>
          }
        </select>
      </label>

      <label>
        Organization (exact)
        <input type="text" [value]="filters().organizationName" (change)="update('organizationName', inputValue($event))" />
      </label>

      <label>
        Assigned to (exact)
        <input type="text" [value]="filters().assignedTo" (change)="update('assignedTo', inputValue($event))" />
      </label>

      <label>
        Created from
        <input type="date" [value]="filters().createdFrom" (change)="update('createdFrom', inputValue($event))" />
      </label>

      <label>
        Created to
        <input type="date" [value]="filters().createdTo" (change)="update('createdTo', inputValue($event))" />
      </label>

      <button type="button" class="secondary" (click)="clear.emit()">Clear filters</button>
    </form>
  `,
  styles: `
    .filters {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: 0.75rem;
      align-items: end;
    }
    .filters__search { grid-column: 1 / -1; }
    label { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.85rem; min-width: 0; }
    input, select { width: 100%; min-width: 0; }
  `
})
export class RequestFiltersComponent {
  readonly searchText = input.required<string>();
  readonly filters = input.required<RequestFilters>();

  readonly searchInput = output<string>();
  readonly filtersChange = output<RequestFilters>();
  readonly clear = output<void>();

  protected readonly statuses = REQUEST_STATUSES;
  protected readonly priorities = REQUEST_PRIORITIES;

  protected update(field: keyof RequestFilters, value: string): void {
    this.filtersChange.emit({ ...this.filters(), [field]: value });
  }

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement | HTMLSelectElement).value;
  }
}
