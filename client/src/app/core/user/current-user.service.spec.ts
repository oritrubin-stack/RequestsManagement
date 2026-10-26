import { TestBed } from '@angular/core/testing';
import { CurrentUserService } from './current-user.service';

describe('CurrentUserService', () => {
  const key = 'requestsManagement.userName';

  beforeEach(() => localStorage.removeItem(key));
  afterEach(() => localStorage.removeItem(key));

  it('starts empty and invalid when nothing is stored', () => {
    const service = TestBed.inject(CurrentUserService);

    expect(service.name()).toBe('');
    expect(service.isValid()).toBeFalse();
  });

  it('trims, stores and restores the name', () => {
    TestBed.inject(CurrentUserService).set('  dana.cohen ');

    expect(localStorage.getItem(key)).toBe('dana.cohen');
    TestBed.resetTestingModule();
    expect(TestBed.inject(CurrentUserService).name()).toBe('dana.cohen');
  });

  it('applies the same rule as the API header validation', () => {
    const service = TestBed.inject(CurrentUserService);

    service.set('dana.cohen@example');
    expect(service.isValid()).toBeTrue();

    service.set('dana cohen');
    expect(service.isValid()).toBeFalse();

    service.set('a'.repeat(101));
    expect(service.isValid()).toBeFalse();
  });
});
