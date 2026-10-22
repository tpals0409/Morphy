// Static page and DMGs come from web/ (assets). This script only runs for paths with no matching file.
const latest = env => env.DB.prepare('SELECT version, path, sha256, bytes, published_at FROM releases ORDER BY published_at DESC LIMIT 1').first();

export default {
  async fetch(request, env) {
    const { pathname } = new URL(request.url);
    if (pathname === '/download') {
      const r = await latest(env);
      if (!r) return new Response('아직 배포된 버전이 없어요.', { status: 404, headers: { 'content-type': 'text/plain; charset=utf-8' } });
      return Response.redirect(new URL(r.path, request.url), 302);
    }
    if (pathname === '/api/release') {
      const r = await latest(env);
      return Response.json(r, { status: r ? 200 : 404, headers: { 'cache-control': 'public, max-age=60' } });
    }
    return env.ASSETS.fetch(request);
  },
};
