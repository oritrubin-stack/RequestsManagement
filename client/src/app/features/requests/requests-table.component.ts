import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RequestDto, SortField } from '../../core/models/request.models';
import { SortState } from './requests-list.models';

// Presentational: renders one page of requests as returned by the server and reports sort requests.
@Component({
  selector: 'app-requests-table',
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="table-wrapper">
      <table>
        <thead>
          <tr>
            <th scope="col" class="check">
              <input type="checkbox" aria-label="Select all requests on this page"
                     [checked]="allSelected()" (change)="toggleAll.emit(checked($event))" />
            </th>
            <th scope="col">Title</th>
            <th scope="col">Organization</th>
            <th scope="col">Status</th>
            <th scope="col" [attr.aria-sort]="ariaSort('priority')">
              <button type="button" class="sort" (click)="sortChange.emit('priority')">Priority {{ indicator('priority') }}</button>
            </th>
            <th scope="col">Assigned to</th>
            <th scope="col" [attr.aria-sort]="ariaSort('createdAt')">
              <button type="button" class="sort" (click)="sortChange.emit('createdAt')">Created {{ indicator('createdAt') }}</button>
            </th>
            <th scope="col" [attr.aria-sort]="ariaSort('updatedAt')">
              <button type="button" class="sort" (click)="sortChange.emit('updatedAt')">Updated {{ indicator('updatedAt') }}</button>
            </th>
          </tr>
        </thead>
        <tbody>
          @for (request of items(); track request.id) {
            <tr [class.selected]="request.id === selectedId()">
              <td class="check">
                <input type="checkbox" [attr.aria-label]="'Select ' + request.title"
                       [checked]="checkedIds().has(request.id)"
                       [disabled]="!checkedIds().has(request.id) && !canSelectMore()"
                       (change)="toggle.emit({ id: request.id, checked: checked($event) })" />
              </td>
              <td class="title">
                <button type="button" class="link" (click)="select.emit(request.id)"
                        [attr.aria-current]="request.id === selectedId() ? 'true' : null">{{ request.title }}</button>
              </td>
              <td>{{ request.organizationName }}</td>
              <td><span class="badge" [attr.data-status]="request.status">{{ request.status }}</span></td>
              <td>{{ request.priority }}</td>
              <td>{{ request.assignedTo }}</td>
              <td>{{ request.createdAt | date: 'yyyy-MM-dd HH:mm' }}</td>
              <td>{{ request.updatedAt | date: 'yyyy-MM-dd HH:mm' }}</td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
  styles: `
    .table-wrapper { overflow-x: auto; }
    table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
    th, td { text-align: left; padding: 0.5rem; border-bottom: 1px solid var(--border); white-space: nowrap; }
    td.title { white-space: normal; min-width: 14rem; }
    .check { width: 2rem; }
    .sort { background: none; border: none; padding: 0; font: inherit; font-weight: 600; cursor: pointer; color: inherit; }
    .link { background: none; border: none; padding: 0; font: inherit; color: var(--accent); text-align: left; cursor: pointer; }
    tr.selected { background: #eef4ff; }
  `
})
export class RequestsTableComponent {
  readonly items = input.required<RequestDto[]>();
  readonly sort = input.required<SortState>();
  readonly selectedId = input<string | null>(null);
  readonly checkedIds = input<ReadonlySet<string>>(new Set<string>());
  readonly canSelectMore = input(true);

  readonly sortChange = output<SortField>();
  readonly select = output<string>();
  readonly toggle = output<{ id: string; checked: boolean }>();
  readonly toggleAll = output<boolean>();

  protected readonly allSelected = computed(() =>
    this.items().length > 0 && this.items().every(item => this.checkedIds().has(item.id)));

  protected checked(event: Event): boolean {
    return (event.target as HTMLInputElement).checked;
  }

  protected indicator(field: SortField): string {
    if (this.sort().field !== field) {
      return '';
    }
    return this.sort().direction === 'asc' ? '▲' : '▼';
  }

  protected ariaSort(field: SortField): string | null {
    if (this.sort().field !== field) {
      return null;
    }
    return this.sort().direction === 'asc' ? 'ascending' : 'descending';
  }
}
