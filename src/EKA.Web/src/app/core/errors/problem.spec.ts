import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { describeHttpError, describeProblem } from './problem';

describe('describeProblem', () => {
  it('tells a spent monthly budget apart from the per-minute limit by code, not by status', () => {
    const budget = describeProblem(429, { code: 'usage.budget_exhausted' });
    const rate = describeProblem(429, { code: 'rate_limited' }, '7');

    expect(budget.retryable).toBe(false);
    expect(budget.message).toContain('allowance');
    expect(rate.retryable).toBe(true);
    expect(rate.retryAfterSeconds).toBe(7);
    expect(rate.message).toContain('7 seconds');
  });

  it('says a provider failure saved nothing, because the turn is atomic on the server', () => {
    expect(describeProblem(502, { code: 'ai.unavailable' }).message).toContain('Nothing was saved');
  });

  it('shows the first validation message, which names what to fix', () => {
    const error = describeProblem(400, { errors: { Question: ['A question is required.'] } });

    expect(error.message).toBe('A question is required.');
  });

  it('asks for a new sign-in on 401 instead of offering a retry that cannot work', () => {
    const error = describeProblem(401, null);

    expect(error.signInRequired).toBe(true);
    expect(error.retryable).toBe(false);
  });

  it('keeps the trace id so a person can quote it', () => {
    expect(describeProblem(500, { traceId: '00-abc' }).traceId).toBe('00-abc');
  });
});

describe('describeHttpError', () => {
  it('reads the problem body and the Retry-After header of an HttpClient failure', () => {
    const error = new HttpErrorResponse({
      status: 429,
      error: { code: 'rate_limited' },
      headers: new HttpHeaders({ 'Retry-After': '3' }),
    });

    expect(describeHttpError(error).retryAfterSeconds).toBe(3);
  });

  it('treats anything that is not an HTTP response as the network being unreachable', () => {
    expect(describeHttpError(new TypeError('Failed to fetch')).retryable).toBe(true);
  });
});
