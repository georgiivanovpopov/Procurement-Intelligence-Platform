export class ApiError extends Error {
  constructor(public status: number, public detail: string, public code?: string) {
    super(detail);
  }
}

async function parse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => ({ detail: 'Заявката не може да бъде изпълнена.' }));
    throw new ApiError(response.status, body.detail ?? body.title, body.code);
  }
  return response.json();
}

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  return parse<T>(await fetch(path, { signal, credentials: 'same-origin' }));
}

export async function postJson<T>(path: string, body: unknown): Promise<T> {
  const { token } = await getJson<{ token: string }>('/api/v1/auth/csrf');
  return parse<T>(await fetch(path, {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token },
    body: JSON.stringify(body)
  }));
}

export async function deleteJson<T>(path: string): Promise<T> {
  const { token } = await getJson<{ token: string }>('/api/v1/auth/csrf');
  return parse<T>(await fetch(path, {
    method: 'DELETE', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': token }
  }));
}
