// The page comes from web/ (static assets). This script runs only for paths with no matching asset:
// downloads and release info for Mac and Windows (files in R2, current version in D1), and the contact form,
// which becomes a GitHub issue.
const routes = {
  '/download': ['mac', 'download'], '/api/release': ['mac', 'info'],
  '/download/windows': ['windows', 'download'], '/api/release/windows': ['windows', 'info'],
};
const releaseFile = /^\/releases\/(Morphy-\d+\.\d+\.\d+(\.dmg|-win-x64-Setup\.exe)|WicHeic-v[\d.]+-corresponding-source\.zip)$/;
const types = { dmg: 'application/x-apple-diskimage', exe: 'application/octet-stream', zip: 'application/zip' };
const noStore = { 'Cache-Control': 'no-store' };

// null: nothing published. undefined: a row exists but its file is missing or incomplete in R2.
async function latest(env, platform) {
  const r = await env.DB.prepare('SELECT version, path, sha256, bytes, published_at FROM releases WHERE platform = ? ORDER BY published_at DESC LIMIT 1')
    .bind(platform).first();
  if (!r) return null;
  const file = releaseFile.test(r.path) && await env.RELEASES.head(r.path.slice(1));
  return file && file.size === r.bytes ? r : undefined;
}

// Contact form → public GitHub issue. The reply email never goes into the issue; it stays in D1.
const REPO = 'tpals0409/Morphy';
const kinds = { bug: '버그', idea: '기능 제안', question: '질문' };
const json = (body, status = 200) => Response.json(body, { status, headers: noStore });

async function feedback(request, env) {
  if (request.method !== 'POST') return new Response(null, { status: 405, headers: { Allow: 'POST' } });
  // ponytail: per-IP limit + honeypot only; add Turnstile if real spam gets through.
  const ip = request.headers.get('CF-Connecting-IP') ?? '';
  if (env.FEEDBACK_LIMIT && !(await env.FEEDBACK_LIMIT.limit({ key: ip })).success)
    return json({ error: '잠시 후 다시 보내 주세요.' }, 429);
  let form;
  try { form = await request.json(); } catch { return json({ error: '내용을 다시 확인해 주세요.' }, 400); }
  if (form.website) return json({ ok: true });  // honeypot field: bots fill it, people never see it
  const kind = Object.hasOwn(kinds, form.kind) ? form.kind : null;
  const message = typeof form.message === 'string' ? form.message.trim() : '';
  const email = typeof form.email === 'string' ? form.email.trim() : '';
  if (!kind || message.length < 10 || message.length > 4000) return json({ error: '종류를 고르고, 내용을 10자 이상 적어 주세요.' }, 400);
  if (email && (email.length > 200 || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(email))) return json({ error: '이메일 주소를 확인해 주세요.' }, 400);
  if (!env.GITHUB_TOKEN) return json({ error: '지금은 문의를 보낼 수 없어요. GitHub에 직접 남겨 주세요.' }, 503);

  // Break @mentions so a public form can't ping people or teams.
  const safe = t => t.replace(/@/g, '@\u200b');
  const ua = request.headers.get('User-Agent') ?? '';
  const os = /Windows/.test(ua) ? 'Windows' : /Mac OS X|Macintosh/.test(ua) ? 'macOS' : '기타';
  const title = `[${kinds[kind]}] ${safe(message.split('\n')[0]).slice(0, 60)}`;
  const body = `${safe(message)}\n\n---\n- 보낸 곳: 웹사이트 문의 양식\n- 방문자 OS: ${os}\n- 답장 이메일: ${email ? '있음 (비공개 보관)' : '없음'}`;
  const res = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${env.GITHUB_TOKEN}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'morphy-website' },
    body: JSON.stringify({ title, body }),
  });
  if (!res.ok) {
    console.error('github issue', res.status, await res.text());
    return json({ error: '지금은 문의를 보낼 수 없어요. 잠시 후 다시 시도해 주세요.' }, 502);
  }
  const issue = await res.json();
  if (email) {
    // The issue already exists; a failed insert only loses the reply address, so log it rather than fail the request.
    try { await env.DB.prepare('INSERT INTO feedback (issue_number, email) VALUES (?, ?)').bind(issue.number, email).run(); }
    catch (e) { console.error('feedback email', issue.number, e); }
  }
  return json({ ok: true, number: issue.number, url: issue.html_url });
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;
    if (path === '/api/feedback') return feedback(request, env);
    const route = routes[path];
    if (!route && !releaseFile.test(path)) return env.ASSETS.fetch(request);
    if (!['GET', 'HEAD'].includes(request.method)) return new Response(null, { status: 405, headers: { Allow: 'GET, HEAD' } });
    const head = request.method === 'HEAD';

    if (route) {
      const [platform, kind] = route;
      const r = await latest(env, platform);
      if (!r) return new Response(r === null ? null : 'unavailable', { status: r === null ? 404 : 503, headers: noStore });
      if (kind === 'download') return new Response(null, { status: 302, headers: { ...noStore, Location: r.path } });
      return new Response(head ? null : JSON.stringify(r), { headers: { ...noStore, 'Content-Type': 'application/json; charset=utf-8' } });
    }

    const object = head ? await env.RELEASES.head(path.slice(1)) : await env.RELEASES.get(path.slice(1));
    if (!object) return new Response(null, { status: 404, headers: noStore });
    const name = path.split('/').at(-1);
    return new Response(head ? null : object.body, {
      headers: {
        'Content-Type': types[name.split('.').at(-1)],
        'Content-Disposition': `attachment; filename="${name}"`,
        'Content-Length': String(object.size),
        ETag: object.httpEtag,
        'Cache-Control': 'public, max-age=31536000, immutable',  // a published version is never overwritten
        'X-Content-Type-Options': 'nosniff',
      },
    });
  },
};
