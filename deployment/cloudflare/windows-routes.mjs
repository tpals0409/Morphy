// Call before the existing Mac/site handler. Pass the existing R2 binding.
const manifestKey = 'windows/latest.json';
const installer = /^\/releases\/Morphy-\d+\.\d+\.\d+-win-x64-Setup\.exe$/;
const sourcePath = '/releases/WicHeic-v1.1.16-corresponding-source.zip';
const noCache = { 'Cache-Control': 'no-store' };

export function validRelease(r) {
  return r && /^\d+\.\d+\.\d+$/.test(r.version) &&
    r.path === `/releases/Morphy-${r.version}-win-x64-Setup.exe` &&
    /^[a-fA-F0-9]{64}$/.test(r.sha256) && Number.isSafeInteger(r.bytes) &&
    r.bytes > 0 && r.bytes <= 1_000_000_000;
}

export async function handleWindows(request, bucket) {
  const path = new URL(request.url).pathname;
  const metadata = path === '/api/release/windows';
  const download = path === '/download/windows';
  if (!metadata && !download && !installer.test(path) && path !== sourcePath) return null;
  if (!['GET', 'HEAD'].includes(request.method)) return new Response(null, { status: 405, headers: { Allow: 'GET, HEAD' } });
  const head = request.method === 'HEAD';
  if (metadata || download) {
    const object = await bucket.get(manifestKey);
    if (!object) return new Response(null, { status: 404, headers: noCache });
    let release;
    try { release = await object.json(); } catch { return new Response(null, { status: 503, headers: noCache }); }
    if (!validRelease(release)) return new Response(null, { status: 503, headers: noCache });
    // Never advertise a manifest whose installer has not finished uploading.
    const file = await bucket.head(release.path.slice(1));
    if (!file || file.size !== release.bytes) return new Response(null, { status: 503, headers: noCache });
    if (download) return new Response(null, { status: 302, headers: { ...noCache, Location: release.path } });
    return new Response(head ? null : JSON.stringify(release), { headers: { ...noCache, 'Content-Type': 'application/json; charset=utf-8' } });
  }
  const object = head ? await bucket.head(path.slice(1)) : await bucket.get(path.slice(1));
  if (!object) return new Response(null, { status: 404, headers: noCache });
  const headers = new Headers();
  object.writeHttpMetadata(headers);
  headers.set('Content-Type', path.endsWith('.exe') ? 'application/octet-stream' : 'application/zip');
  headers.set('Content-Disposition', `attachment; filename="${path.split('/').at(-1)}"`);
  headers.set('Content-Length', String(object.size));
  headers.set('ETag', object.httpEtag);
  headers.set('Cache-Control', 'public, max-age=31536000, immutable');
  headers.set('X-Content-Type-Options', 'nosniff');
  return new Response(head ? null : object.body, { headers });
}
