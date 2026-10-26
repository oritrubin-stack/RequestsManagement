import { RequestStatus } from './request.models';

// Mirrors the server policy (Domain/StatusTransitions.cs) for the user interface only: it hides targets the
// server would reject. The server remains the authority, and its validation message is shown if they ever differ.
const ALLOWED_TRANSITIONS: Readonly<Record<RequestStatus, readonly RequestStatus[]>> = {
  New: ['InProgress'],
  InProgress: ['Waiting', 'Completed'],
  Waiting: ['InProgress'],
  Completed: []
};

export function allowedTargets(from: RequestStatus): readonly RequestStatus[] {
  return ALLOWED_TRANSITIONS[from];
}

export function isTransitionAllowed(from: RequestStatus, to: RequestStatus): boolean {
  return ALLOWED_TRANSITIONS[from].includes(to);
}
