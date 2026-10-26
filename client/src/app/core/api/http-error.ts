import { HttpErrorResponse } from '@angular/common/http';
import { ProblemDetails } from '../models/request.models';

// Turns an API error (ProblemDetails, validation errors or a network failure) into a short message for the user.
export function describeHttpError(error: unknown): string {
  if (!(error instanceof HttpErrorResponse)) {
    return 'An unexpected error occurred.';
  }

  if (error.status === 0) {
    return 'The server cannot be reached. Check that the API is running.';
  }

  const problem = (error.error ?? {}) as ProblemDetails;
  const validationMessages = Object.values(problem.errors ?? {}).flat();
  if (validationMessages.length > 0) {
    return validationMessages.join(' ');
  }

  return problem.title ?? `The request failed (HTTP ${error.status}).`;
}
