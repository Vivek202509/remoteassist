'use strict';

// Static server for the marketing site — DEVELOPMENT / LOCAL PREVIEW ONLY.
//   node web/serve.js            -> http://localhost:5173
//   PORT=8000 node web/serve.js
//
// This is not a production web server and is not hardened as one. It has no
// TLS, no compression, no caching policy, no rate limiting, no access log and
// no security headers beyond `nosniff`. Serve the site from nginx, Caddy or a
// static host in production, and set the response headers documented in
// web/DEPLOYMENT.md there.
//
// What it does take seriously is not serving files outside web/. See
// resolvePath() below; web/test/qa.js exercises it with encoded traversal.

const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;

const TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.webp': 'image/webp',
  '.ico': 'image/x-icon',
  '.json': 'application/json; charset=utf-8',
  '.woff2': 'font/woff2',
  '.xml': 'application/xml; charset=utf-8',
  '.txt': 'text/plain; charset=utf-8',
};

const NOT_FOUND_BODY = '<!doctype html><meta charset="utf-8"><title>404 — not found</title>'
  + '<body style="font:16px/1.6 system-ui;padding:60px;text-align:center">'
  + '<h1>404 — not found</h1><p><a href="/">Back to the homepage</a></p>';

/**
 * Map a request URL onto a file inside ROOT, or reject it.
 *
 * Returns { file } on success, or { error: <http status> }.
 *
 * The order matters:
 *   1. `new URL(...).pathname` strips the query and normalises literal "..".
 *   2. decodeURIComponent turns %2e%2e and %2f back into characters, so an
 *      encoded traversal cannot slip past step 3 by hiding as a percent escape.
 *      Decoding once is deliberate: a double-encoded "%252e%252e" decodes to
 *      the literal text "%2e%2e", which is a filename, not a traversal.
 *   3. A NUL byte would make fs throw synchronously, so it is refused here.
 *   4. path.join collapses any remaining "..", and on Windows also treats "\"
 *      as a separator — which is why the containment check comes after it and
 *      not before.
 */
function resolvePath(requestUrl) {
  let pathname;
  try {
    pathname = decodeURIComponent(new URL(requestUrl, 'http://localhost').pathname);
  } catch {
    return { error: 400 };                       // malformed percent-encoding
  }
  if (pathname.indexOf('\0') !== -1) return { error: 400 };

  if (pathname.endsWith('/')) pathname += 'index.html';
  // Extensionless URLs (/pricing) resolve to their .html file. There are no
  // directory indexes: /assets becomes /assets.html and 404s.
  if (!path.extname(pathname)) pathname += '.html';

  const file = path.join(ROOT, pathname);
  if (file !== ROOT && !file.startsWith(ROOT + path.sep)) return { error: 403 };
  return { file };
}

function send(res, method, status, body, type, extraHeaders) {
  const headers = Object.assign({
    'Content-Type': type || 'text/plain; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'X-Content-Type-Options': 'nosniff',
    // Local preview: never let a stale page survive an edit.
    'Cache-Control': 'no-store',
  }, extraHeaders || {});
  res.writeHead(status, headers);
  // A HEAD response carries the headers, including Content-Length, but no body.
  if (method === 'HEAD') return res.end();
  res.end(body);
}

function handler(req, res) {
  const method = req.method;
  if (method !== 'GET' && method !== 'HEAD') {
    return send(res, method, 405, 'Method not allowed', undefined, { Allow: 'GET, HEAD' });
  }

  const resolved = resolvePath(req.url);
  if (resolved.error === 400) return send(res, method, 400, 'Bad request');
  if (resolved.error === 403) return send(res, method, 403, 'Forbidden');

  fs.readFile(resolved.file, (err, data) => {
    // EISDIR and ENOENT are both "nothing to serve here" — the reason is not
    // the client's business, and saying which would map the filesystem.
    if (err) return send(res, method, 404, NOT_FOUND_BODY, TYPES['.html']);
    const type = TYPES[path.extname(resolved.file).toLowerCase()] || 'application/octet-stream';
    send(res, method, 200, data, type);
  });
}

function createServer() {
  return http.createServer(handler);
}

module.exports = { createServer, resolvePath, ROOT, TYPES };

if (require.main === module) {
  const port = parseInt(process.env.PORT || process.argv[2] || '5173', 10);
  createServer().listen(port, () => {
    console.log(`[web] serving ${ROOT}`);
    console.log(`[web] http://localhost:${port}  (development preview only)`);
  });
}
