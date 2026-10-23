// The page comes from web/ (static assets). This script runs only for paths with no matching asset:
// downloads and release info for Mac and Windows. Files live in R2; which version is current lives in D1.
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

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;
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
