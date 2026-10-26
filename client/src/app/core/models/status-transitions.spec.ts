import { allowedTargets, isTransitionAllowed } from './status-transitions';

// Keeps the UI mirror in line with the server policy (Domain/StatusTransitions.cs).
describe('status transitions (UI mirror of the server policy)', () => {
  it('allows only the server policy transitions', () => {
    expect(allowedTargets('New')).toEqual(['InProgress']);
    expect(allowedTargets('InProgress')).toEqual(['Waiting', 'Completed']);
    expect(allowedTargets('Waiting')).toEqual(['InProgress']);
  });

  it('treats Completed as final and rejects the current status', () => {
    expect(allowedTargets('Completed')).toEqual([]);
    expect(isTransitionAllowed('InProgress', 'InProgress')).toBeFalse();
    expect(isTransitionAllowed('New', 'Completed')).toBeFalse();
  });
});
