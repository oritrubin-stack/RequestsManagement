import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { PAGE_SIZES } from './requests-list.models';

// Presentational: page navigation and page size. The page itself is always loaded by the server.
@Component({
  selector: 'app-pager',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <nav class="pager" aria-label="Pagination">
      <button type="button" class="secondary" [disabled]="page() <= 1" (click)="pageChange.emit(page() - 1)">Previous</button>
      <span>Page {{ page() }} of {{ totalPages() || 1 }} · {{ totalCount() }} requests</span>
      <button type="button" class="secondary" [disabled]="page() >= totalPages()" (click)="pageChange.emit(page() + 1)">Next</button>
      <label>
        Page size
        <select [value]="pageSize()" (change)="pageSizeChange.emit(selectedSize($event))">
          @for (size of pageSizes; track size) {
            <option [value]="size">{{ size }}</option>
          }
        </select>
      </label>
    </nav>
  `,
  styles: `
    .pager { display: flex; flex-wrap: wrap; align-items: center; gap: 0.75rem; }
    label { display: flex; align-items: center; gap: 0.25rem; margin-left: auto; font-size: 0.85rem; }
  `
})
export class PagerComponent {
  readonly page = input.required<number>();
  readonly pageSize = input.required<number>();
  readonly totalPages = input.required<number>();
  readonly totalCount = input.required<number>();

  readonly pageChange = output<number>();
  readonly pageSizeChange = output<number>();

  protected readonly pageSizes = PAGE_SIZES;

  protected selectedSize(event: Event): number {
    return Number((event.target as HTMLSelectElement).value);
  }
}
