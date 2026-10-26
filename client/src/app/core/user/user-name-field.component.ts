import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { CurrentUserService } from './current-user.service';

@Component({
  selector: 'app-user-name-field',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <label class="user-name">
      Your name for status changes
      <input type="text" placeholder="e.g. dana.cohen" [value]="currentUser.name()"
             (change)="currentUser.set(inputValue($event))" aria-describedby="user-name-hint" />
      <small id="user-name-hint">
        Sent as X-User-Name and recorded in the history. It is not a login and is not verified.
        @if (currentUser.name() && !currentUser.isValid()) {
          <span class="invalid">Use 1–100 letters, digits or . _ &#64; - (no spaces).</span>
        }
      </small>
    </label>
  `,
  styles: `
    .user-name { display: flex; flex-direction: column; gap: 0.25rem; font-size: 0.85rem; max-width: 22rem; }
    small { color: var(--muted); }
    .invalid { display: block; color: var(--error); }
  `
})
export class UserNameFieldComponent {
  protected readonly currentUser = inject(CurrentUserService);

  protected inputValue(event: Event): string {
    return (event.target as HTMLInputElement).value;
  }
}
