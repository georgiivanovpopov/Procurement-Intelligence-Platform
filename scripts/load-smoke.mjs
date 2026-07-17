const base = (process.argv[2] ?? 'http://127.0.0.1:5099').replace(/\/$/, '');
const concurrency = Number(process.argv[3] ?? 20);
if (!Number.isInteger(concurrency) || concurrency < 1 || concurrency > 200) throw new Error('Concurrency must be 1-200');
const paths = [
  '/api/v1/suppliers/103267194/signals/repeated-relationship?page=1&pageSize=50&sort=value&dir=desc',
  '/api/v1/suppliers/103267194/signals/repeated-relationship?page=2&pageSize=50&sort=date&dir=asc',
  '/api/v1/suppliers/203283623/signals/buyer-concentration?page=1&pageSize=50&sort=value&dir=desc'
];
const started = performance.now();
const results = await Promise.all(Array.from({ length: concurrency * paths.length }, async (_, request) => {
  const begin = performance.now();
  try {
    const response = await fetch(base + paths[request % paths.length], { signal: AbortSignal.timeout(10000), headers: { accept: 'application/json', 'accept-encoding': 'gzip, br' } });
    await response.arrayBuffer();
    return { status: response.status, ms: performance.now() - begin };
  } catch (error) { return { status: 0, ms: performance.now() - begin, error: String(error) }; }
}));
const latencies = results.map(x => x.ms).sort((a, b) => a - b);
const percentile = p => latencies[Math.min(latencies.length - 1, Math.floor(latencies.length * p))];
const failures = results.filter(x => x.status < 200 || x.status >= 300);
const statuses = results.reduce((all, x) => ({ ...all, [x.status]: (all[x.status] ?? 0) + 1 }), {});
console.log(JSON.stringify({ base, concurrency, requests: results.length, durationMs: Math.round(performance.now() - started), p50Ms: Math.round(percentile(.5)), p95Ms: Math.round(percentile(.95)), statuses, non2xxOrNetworkErrors: failures.length }, null, 2));
if (failures.length) process.exitCode = 1;
