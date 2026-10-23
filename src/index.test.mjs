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

// Contact form → GitHub issue
const post = (body, e) => worker.fetch(new Request('https://morphy.tpals0409dev.workers.dev/api/feedback', {
  method: 'POST', body: JSON.stringify(body), headers: { 'User-Agent': 'Mozilla/5.0 (Windows NT 10.0)', 'CF-Connecting-IP': '1.2.3.4' },
}), e);
function feedbackEnv({ token = 'test-token', allow = true, github = { status: 201, body: { number: 7, html_url: 'https://github.com/tpals0409/Morphy/issues/7' } } } = {}) {
  const calls = { github: [], db: [] };
  globalThis.fetch = async (url, init) => { calls.github.push({ url, init, body: JSON.parse(init.body) }); return Response.json(github.body, { status: github.status }); };
  const e = {
    ...env(), GITHUB_TOKEN: token,
    FEEDBACK_LIMIT: { limit: async () => ({ success: allow }) },
    DB: { prepare: sql => ({ bind: (...args) => ({ run: async () => calls.db.push({ sql, args }) }) }) },
  };
  return [e, calls];
}
const valid = { kind: 'bug', message: 'Excel을 CSV로 바꾸면 한글이 깨져요.\n두 번째 줄', email: 'me@example.com' };

test('a valid message files a GitHub issue; the reply email stays private in D1', async () => {
  const [e, calls] = feedbackEnv();
  const res = await post(valid, e);
  assert.equal(res.status, 200); assert.deepEqual(await res.json(), { ok: true, number: 7, url: 'https://github.com/tpals0409/Morphy/issues/7' });
  const [{ url, init, body }] = calls.github;
  assert.equal(url, 'https://api.github.com/repos/tpals0409/Morphy/issues');
  assert.equal(init.headers.Authorization, 'Bearer test-token');
  assert.equal(body.title, '[버그] Excel을 CSV로 바꾸면 한글이 깨져요.');
  assert.match(body.body, /방문자 OS: Windows/); assert.match(body.body, /답장 이메일: 있음/);
  assert.ok(!JSON.stringify(body).includes('me@example.com'), 'email must not reach the public issue');
  assert.deepEqual(calls.db[0].args, [7, 'me@example.com']);
});
test('@mentions in a message cannot ping anyone', async () => {
  const [e, calls] = feedbackEnv();
  await post({ kind: 'question', message: '안녕하세요 @tpals0409 @org/team 확인 부탁해요' }, e);
  assert.ok(!/@\w/.test(calls.github[0].body.body + calls.github[0].body.title));
  assert.equal(calls.db.length, 0, 'no email, nothing stored');
});
test('bad input, honeypot, rate limit and missing token never reach GitHub', async () => {
  for (const [body, opts, status] of [
    [{ kind: 'spam', message: '1234567890' }, {}, 400],
    [{ kind: 'bug', message: '짧아요' }, {}, 400],
    [{ ...valid, email: 'not-an-email' }, {}, 400],
    [{ ...valid, website: 'http://spam' }, {}, 200],
    [valid, { allow: false }, 429],
    [valid, { token: '' }, 503],
  ]) {
    const [e, calls] = feedbackEnv(opts);
    assert.equal((await post(body, e)).status, status, JSON.stringify(body));
    assert.equal(calls.github.length, 0);
  }
  const [e] = feedbackEnv();
  assert.equal((await worker.fetch(new Request('https://x/api/feedback'), e)).status, 405);
});
test('a GitHub failure is reported, not hidden', async () => {
  const [e] = feedbackEnv({ github: { status: 401, body: { message: 'Bad credentials' } } });
  const orig = console.error; console.error = () => {};
  try { assert.equal((await post(valid, e)).status, 502); } finally { console.error = orig; }
});
