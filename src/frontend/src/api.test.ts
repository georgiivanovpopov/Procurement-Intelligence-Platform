import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getJson } from './api';

describe('getJson', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('returns typed JSON for a successful response', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ snapshotId: 'fixture' }), { status: 200 })));
    await expect(getJson<{snapshotId:string}>('/api/v1/meta')).resolves.toEqual({ snapshotId: 'fixture' });
  });

  it('preserves the server explanation for an unknown EIK', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'No supplier matched this EIK in the current data snapshot.' }), { status: 404 })));
    try {
      await getJson('/api/v1/suppliers/123456789');
      throw new Error('Expected request to fail');
    } catch (error) {
      expect(error).toBeInstanceOf(ApiError);
      expect((error as ApiError).status).toBe(404);
      expect((error as ApiError).detail).toContain('No supplier matched');
    }
  });
});
