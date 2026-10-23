import test from 'node:test';
import assert from 'node:assert/strict';
import worker from './index.js';

const rows = {
  mac: { version: '0.1.0', path: '/releases/Morphy-0.1.0.dmg', sha256: 'b'.repeat(64), bytes: 4, published_at: 'x' },
  windows: { version: '0.1.0', path: '/releases/Morphy-0.1.0-win-x64-Setup.exe', sha256: 'a'.repeat(64), bytes: 3, published_at: 'x' },
};
const env = ({ db = rows, files = { 'releases/Morphy-0.1.0.dmg': 'dmg!', 'releases/Morphy-0.1.0-win-x64-Setup.exe': 'exe' } } = {}) => ({
  DB: { prepare: () => ({ bind: platform => ({ first: async () => db[platform] ?? null }) }) },
  RELEASES: {
    async head(key) { return key in files ? { size: files[key].length, httpEtag: '"e"' } : null; },
    async get(key) { return key in files ? { ...await this.head(key), body: files[key] } : null; },
  },
  ASSETS: { fetch: async () => new Response('asset') },
});
const call = (path, e = env(), method = 'GET') => worker.fetch(new Request('https://morphy.tpals0409dev.workers.dev' + path, { method }), e);

test('pages fall through to static assets', async () => {
  for (const path of ['/', '/icon.png', '/releases/other.txt']) assert.equal(await (await call(path)).text(), 'asset');
});
test('each platform redirects to and describes its own latest release', async () => {
  for (const [suffix, row] of [['', rows.mac], ['/windows', rows.windows]]) {
    const redirect = await call('/download' + suffix);
    assert.equal(redirect.status, 302); assert.equal(redirect.headers.get('Location'), row.path);
    const info = await call('/api/release' + suffix);
    assert.deepEqual(await info.json(), row); assert.equal(info.headers.get('Cache-Control'), 'no-store');
  }
});
test('unpublished is 404; a row whose file is missing or incomplete is never advertised', async () => {
  assert.equal((await call('/api/release/windows', env({ db: { mac: rows.mac } }))).status, 404);
  assert.equal((await call('/download/windows', env({ files: {} }))).status, 503);
  assert.equal((await call('/download/windows', env({ files: { 'releases/Morphy-0.1.0-win-x64-Setup.exe': 'e' } }))).status, 503);
  assert.equal((await call('/api/release', env({ db: { mac: { ...rows.mac, path: 'https://evil.example/a.dmg' } } }))).status, 503);
});
test('files stream from R2 as downloads; HEAD has no body', async () => {
  const file = await call(rows.windows.path);
  assert.equal(await file.text(), 'exe'); assert.equal(file.headers.get('Content-Length'), '3');
  assert.match(file.headers.get('Content-Disposition'), /attachment; filename="Morphy-0.1.0-win-x64-Setup.exe"/);
  assert.equal((await call(rows.mac.path)).headers.get('Content-Type'), 'application/x-apple-diskimage');
  assert.equal(await (await call(rows.mac.path, env(), 'HEAD')).text(), '');
  assert.equal((await call('/releases/Morphy-9.9.9.dmg')).status, 404);
});
test('write methods are rejected', async () => assert.equal((await call('/api/release/windows', env(), 'POST')).status, 405));
