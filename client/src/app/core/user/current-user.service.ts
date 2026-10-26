import { Injectable, computed, signal } from '@angular/core';

const STORAGE_KEY = 'requestsManagement.userName';

// Same rule as the API's X-User-Name validation.
const USER_NAME_PATTERN = /^[A-Za-z0-9._@-]{1,100}$/;

// The name sent as X-User-Name and recorded as "changed by" in the status history.
// It is declared by the user and is not authentication: the API does not verify it.
@Injectable({ providedIn: 'root' })
export class CurrentUserService {
  readonly name = signal(this.read());
  readonly isValid = computed(() => USER_NAME_PATTERN.test(this.name()));

  set(value: string): void {
    const name = value.trim();
    this.name.set(name);
    try {
      localStorage.setItem(STORAGE_KEY, name);
    } catch {
      // Storage can be unavailable (private mode, blocked site data); the name still works for this session.
    }
  }

  private read(): string {
    try {
      return localStorage.getItem(STORAGE_KEY) ?? '';
    } catch {
      return '';
    }
  }
}
