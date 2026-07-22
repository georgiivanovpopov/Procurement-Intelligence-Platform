import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getJson, postJson } from './api';

describe('getJson', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('returns typed JSON for a successful response', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ snapshotId: 'fixture' }), { status: 200 })));
    await expect(getJson<{snapshotId:string}>('/api/v1/meta')).resolves.toEqual({ snapshotId: 'fixture' });
  });

  it('preserves the server explanation for an unknown EIK', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'В текущата снимка на данните няма доставчик с този ЕИК.' }), { status: 404 })));
    try {
      await getJson('/api/v1/suppliers/123456789');
      throw new Error('Expected request to fail');
    } catch (error) {
      expect(error).toBeInstanceOf(ApiError);
      expect((error as ApiError).status).toBe(404);
      expect((error as ApiError).detail).toContain('няма доставчик');
    }
  });
});

describe('postJson', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('fetches an antiforgery token and sends it with same-origin credentials', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify({ token: 'csrf-token' }), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ authenticated: true, username: 'auditor' }), { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await postJson('/api/v1/auth/login', { username: 'auditor', password: 'a long passphrase' });

    expect(fetchMock).toHaveBeenNthCalledWith(1, '/api/v1/auth/csrf', expect.objectContaining({ credentials: 'same-origin' }));
    expect(fetchMock).toHaveBeenNthCalledWith(2, '/api/v1/auth/login', expect.objectContaining({
      method: 'POST',
      credentials: 'same-origin',
      headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-token' })
    }));
  });
});
