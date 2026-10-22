import test from 'node:test';
import assert from 'node:assert/strict';
import { handleWindows } from './windows-routes.mjs';
const release = { version: '0.1.0', path: '/releases/Morphy-0.1.0-win-x64-Setup.exe', sha256: 'a'.repeat(64), bytes: 3 };
function bucket(r = release, size = 3) {
  return {
    async get(key) { return key === 'windows/latest.json' ? (r ? { json: async () => r } : null) : key === release.path.slice(1) ? { ...await this.head(key), body: 'exe' } : null; },
    async head(key) { return key === release.path.slice(1) && size != null ? { size, httpEtag: '"hash"', writeHttpMetadata() {} } : null; }
  };
}
const request = (path, method = 'GET') => new Request('https://morphy.tpals0409dev.workers.dev' + path, { method });
test('Mac downloads, metadata and homepage fall through untouched', async () => {
  for (const path of ['/', '/download', '/api/release', '/releases/Morphy-0.1.0.dmg']) assert.equal(await handleWindows(request(path), bucket()), null);
});
test('Windows metadata and redirect use the same release', async () => {
  const meta = await handleWindows(request('/api/release/windows'), bucket());
  assert.deepEqual(await meta.json(), release); assert.equal(meta.headers.get('Cache-Control'), 'no-store');
  const redirect = await handleWindows(request('/download/windows'), bucket());
  assert.equal(redirect.status, 302); assert.equal(redirect.headers.get('Location'), release.path);
});
test('missing, malformed and incomplete releases are not advertised', async () => {
  assert.equal((await handleWindows(request('/api/release/windows'), bucket(null))).status, 404);
  for (const r of [{ ...release, path: 'https://evil.example/a.exe' }, { ...release, sha256: 'bad' }, { ...release, bytes: -1 }])
    assert.equal((await handleWindows(request('/api/release/windows'), bucket(r))).status, 503);
  for (const size of [null, 1]) assert.equal((await handleWindows(request('/download/windows'), bucket(release, size))).status, 503);
});
test('installer streams with download headers; HEAD has no body', async () => {
  const file = await handleWindows(request(release.path), bucket());
  assert.equal(await file.text(), 'exe'); assert.equal(file.headers.get('Content-Length'), '3');
  assert.match(file.headers.get('Content-Disposition'), /attachment/);
  assert.equal(await (await handleWindows(request(release.path, 'HEAD'), bucket())).text(), '');
});
test('write methods are rejected', async () => assert.equal((await handleWindows(request('/api/release/windows', 'POST'), bucket())).status, 405));
