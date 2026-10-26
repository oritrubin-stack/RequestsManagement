import { HttpErrorResponse } from '@angular/common/http';
import { describeHttpError } from './http-error';

describe('describeHttpError', () => {
  it('reports an unreachable server', () => {
    expect(describeHttpError(new HttpErrorResponse({ status: 0 }))).toContain('cannot be reached');
  });

  it('prefers validation messages over the ProblemDetails title', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { title: 'One or more validation errors occurred.', errors: { PageSize: ['Too large.'], Page: ['Too small.'] } }
    });

    expect(describeHttpError(error)).toBe('Too large. Too small.');
  });

  it('uses the ProblemDetails title, or the status when there is none', () => {
    expect(describeHttpError(new HttpErrorResponse({ status: 404, error: { title: 'Not Found' } }))).toBe('Not Found');
    expect(describeHttpError(new HttpErrorResponse({ status: 500 }))).toBe('The request failed (HTTP 500).');
  });
});
