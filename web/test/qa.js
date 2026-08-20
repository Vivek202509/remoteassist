'use strict';

/*
 * Website QA — run with:  node web/test/qa.js
 *
 * Zero dependencies, Node's own runtime only. This is not a browser test
 * framework and is not trying to become one; it catches the class of mistake a
 * static site actually ships with: a link to a file that was renamed, an anchor
 * that no longer exists, a placeholder href, a localhost URL left in the
 * markup, and product facts that drifted apart between pages.
 *
 * Sections:
 *   1. every page exists and is well-formed enough to reason about
 *   2. internal links, assets, scripts and stylesheets resolve
 *   3. in-page anchors resolve
 *   4. metadata: unique titles, descriptions, one h1
 *   5. placeholder and leakage checks
 *   6. product-status drift: the no-JS fallback text must match the data source
 *   7. serve.js: traversal, methods, MIME types, 404s
 */

const fs = require('fs');
const path = require('path');
const http = require('http');

const WEB = path.join(__dirname, '..');
const PAGES = ['index.html', 'download.html', 'document.html', 'pricing.html', 'blog.html', 'contact.html'];

let passed = 0;
const failures = [];

function check(name, condition, detail) {
  if (condition) { passed++; return true; }
  failures.push(detail ? `${name}\n      ${detail}` : name);
  return false;
}
function section(title) { console.log(`\n  -- ${title} --`); }
function ok(name) { console.log(`  ✓ ${name}`); }
function bad(name, detail) { console.log(`  ✗ ${name}${detail ? '\n      ' + detail : ''}`); }
function test(name, condition, detail) {
  if (check(name, condition, detail)) ok(name); else bad(name, detail);
}

/* ------------------------------------------------------------------ load */

section('pages exist');
const source = {};
for (const page of PAGES) {
  const file = path.join(WEB, page);
  const exists = fs.existsSync(file);
  test(`${page} exists`, exists);
  if (exists) source[page] = fs.readFileSync(file, 'utf8');
}
const loaded = PAGES.filter((p) => source[p]);

/* Attribute scraping. Regex, not a parser — the input is six files we control,
   and pulling in a DOM implementation for this would cost more than it saves. */
function attrs(html, re) {
  const out = [];
  let m;
  while ((m = re.exec(html)) !== null) out.push(m[1]);
  return out;
}
const hrefsOf = (html) => attrs(html, /<a\b[^>]*\bhref="([^"]*)"/g);
const srcsOf = (html) => attrs(html, /<(?:img|script|source)\b[^>]*\bsrc="([^"]*)"/g);
const linksOf = (html) => attrs(html, /<link\b[^>]*\bhref="([^"]*)"/g);
const idsOf = (html) => attrs(html, /\bid="([^"]*)"/g);

const isExternal = (url) => /^(https?:|mailto:|tel:|data:)/i.test(url);

/* --------------------------------------------------- 2. local references */

section('local references resolve');
for (const page of loaded) {
  const html = source[page];
  const refs = [...hrefsOf(html), ...srcsOf(html), ...linksOf(html)];
  const broken = [];
  for (const ref of refs) {
    if (!ref || isExternal(ref) || ref.startsWith('#')) continue;
    const [filePart] = ref.split('#');
    if (!filePart) continue;
    const target = path.join(WEB, filePart.split('?')[0]);
    if (!fs.existsSync(target)) broken.push(ref);
  }
  test(`${page}: every local href/src/link target exists`, broken.length === 0, broken.join(', '));
}

/* ------------------------------------------------------------ 3. anchors */

section('anchors resolve');
for (const page of loaded) {
  const html = source[page];
  const dangling = [];
  for (const ref of hrefsOf(html)) {
    if (!ref || isExternal(ref)) continue;
    const hash = ref.indexOf('#');
    if (hash < 0) continue;
    const id = ref.slice(hash + 1);
    if (!id) continue;
    const targetPage = hash === 0 ? page : ref.slice(0, hash);
    if (!source[targetPage]) { dangling.push(`${ref} (unknown page)`); continue; }
    if (!idsOf(source[targetPage]).includes(id)) dangling.push(ref);
  }
  test(`${page}: every in-page anchor has a matching id`, dangling.length === 0, dangling.join(', '));
}

/* ----------------------------------------------------------- 4. metadata */

section('metadata');
const titles = new Map();
for (const page of loaded) {
  const html = source[page];
  const title = (/<title>([\s\S]*?)<\/title>/.exec(html) || [])[1];
  test(`${page}: has a non-empty <title>`, Boolean(title && title.trim()));
  if (title) titles.set(page, title.trim());

  const desc = (/<meta name="description" content="([^"]*)">/.exec(html) || [])[1];
  test(`${page}: has a meta description`, Boolean(desc && desc.trim().length > 30));

  test(`${page}: declares og:title and og:description`,
    /property="og:title"/.test(html) && /property="og:description"/.test(html));

  const h1s = html.match(/<h1\b/g) || [];
  test(`${page}: has exactly one <h1>`, h1s.length === 1, `found ${h1s.length}`);

  test(`${page}: loads product-status.js before site.js`,
    html.indexOf('assets/js/product-status.js') > 0 &&
    html.indexOf('assets/js/product-status.js') < html.indexOf('assets/js/site.js'));
}
test('every page title is unique', new Set(titles.values()).size === titles.size,
  [...titles.entries()].map(([p, t]) => `${p}: ${t}`).join(' | '));

/* Canonical URLs are deliberately absent: no production domain exists, and
   inventing one is worse than omitting the tag. Fail if one appears without
   this test being updated alongside it. */
test('no canonical URL is invented', loaded.every((p) => !/rel="canonical"/.test(source[p])));

/* --------------------------------------------- 5. placeholders / leakage */

section('placeholders and leakage');
for (const page of loaded) {
  const html = source[page];

  const placeholders = (html.match(/href="#"/g) || []).length;
  test(`${page}: no placeholder href="#"`, placeholders === 0, `${placeholders} found`);

  const local = html.match(/(?:href|src)="[^"]*(?:localhost|127\.0\.0\.1)[^"]*"/g) || [];
  test(`${page}: no localhost link in markup`, local.length === 0, local.join(', '));

  // Example commands may name a LAN broker; a *link* to one is a mistake.
  const lan = html.match(/(?:href|src)="[^"]*192\.168\.[^"]*"/g) || [];
  test(`${page}: no LAN address linked`, lan.length === 0, lan.join(', '));

  // Nothing on a public page should look like a credential.
  const secretish = html.match(/type="password"/g) || [];
  test(`${page}: no password input`, secretish.length === 0);

  // Build paths from a developer machine must never reach the published site.
  const winPath = html.match(/[A-Z]:\\\\?Users\\\\?/g) || [];
  test(`${page}: no absolute developer path`, winPath.length === 0, winPath.join(', '));
}

/* ------------------------------------------------- 6. product-status drift */

section('product status is single-sourced');
const { lookup } = require('../assets/js/product-status.js');

// Collapse whitespace and decode the handful of entities the pages use, so
// wrapped HTML compares equal to the single-line string in the data source.
function normalise(text) {
  return text
    .replace(/<[^>]+>/g, '')
    .replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>')
    .replace(/&nbsp;/g, ' ').replace(/&middot;/g, '·')
    .replace(/&rsquo;/g, '’').replace(/&ldquo;/g, '“').replace(/&rdquo;/g, '”')
    .replace(/\s+/g, ' ')
    .trim();
}

let statusTags = 0;
for (const page of loaded) {
  const html = source[page];
  const drift = [];

  // <tag data-status="path">fallback text</tag>
  const re = /<(\w+)\b[^>]*\bdata-status="([^"]+)"[^>]*>([\s\S]*?)<\/\1>/g;
  let m;
  while ((m = re.exec(html)) !== null) {
    statusTags++;
    const key = m[2];
    const expected = lookup(key);
    if (expected === undefined) { drift.push(`${key} (not in product-status.js)`); continue; }
    if (normalise(m[3]) !== normalise(expected)) {
      drift.push(`${key}\n        html: ${normalise(m[3]).slice(0, 90)}\n        data: ${normalise(expected).slice(0, 90)}`);
    }
  }
  test(`${page}: data-status fallback text matches product-status.js`, drift.length === 0, drift.join('\n      '));

  const badges = attrs(html, /\bdata-status-badge="([^"]+)"/g);
  const unknown = badges.filter((key) => lookup(key + '.badge') === undefined);
  test(`${page}: every data-status-badge key exists`, unknown.length === 0, unknown.join(', '));
}
test('the status source is actually used', statusTags >= 10, `${statusTags} tagged elements`);

// The whole point of centralising is that these strings appear once. If a page
// hardcodes a platform badge instead of tagging it, the drift check above never
// sees it — so look for the words themselves outside a tagged element.
section('no stale status vocabulary');
for (const page of loaded) {
  const stale = (source[page].match(/\bbadge--ok">Shipping</g) || []).length;
  test(`${page}: no "Shipping" badge (nothing has shipped)`, stale === 0, `${stale} found`);
}

/* ------------------------------------------------ 7. CSP deployability */

// DEPLOYMENT.md publishes a strict Content-Security-Policy. Two properties have
// to hold for it to actually work, and both are easy to break by accident.
section('the documented CSP stays valid');
{
  const crypto = require('crypto');
  const deployment = fs.readFileSync(path.join(WEB, 'DEPLOYMENT.md'), 'utf8');

  // (a) no style="" anywhere, so style-src can stay free of 'unsafe-inline'
  for (const page of loaded) {
    const inline = (source[page].match(/\sstyle="/g) || []).length;
    test(`${page}: no inline style attribute`, inline === 0, `${inline} found`);
  }

  // (b) the one inline <script> is identical everywhere and matches the hash
  //     DEPLOYMENT.md tells operators to allow-list.
  const bodies = loaded.map((p) => (/<script>([\s\S]*?)<\/script>/.exec(source[p]) || [])[1]);
  test('every page carries exactly one inline script', bodies.every(Boolean));
  test('the inline theme script is byte-identical on every page',
    new Set(bodies).size === 1, `${new Set(bodies).size} variants`);

  for (const page of loaded) {
    const count = (source[page].match(/<script>/g) || []).length;
    test(`${page}: exactly one inline script to hash`, count === 1, `${count} found`);
  }

  const hash = 'sha256-' + crypto.createHash('sha256').update(bodies[0], 'utf8').digest('base64');
  test('DEPLOYMENT.md publishes the current inline-script hash',
    deployment.includes(hash), `computed ${hash}`);

  for (const h of ['Content-Security-Policy', 'X-Content-Type-Options', 'Referrer-Policy',
                   'Permissions-Policy', 'frame-ancestors']) {
    test(`DEPLOYMENT.md documents ${h}`, deployment.includes(h));
  }
  test('DEPLOYMENT.md does not call serve.js production-ready',
    /development \/ local preview server/i.test(deployment));
}

/* ------------------------------------------------------------ 8. serve.js */

section('serve.js');
const { createServer, resolvePath } = require('../serve.js');

function request(port, urlPath, method) {
  return new Promise((resolve, reject) => {
    const req = http.request(
      { host: '127.0.0.1', port, path: urlPath, method: method || 'GET' },
      (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => resolve({
          status: res.statusCode,
          headers: res.headers,
          body: Buffer.concat(chunks),
        }));
      }
    );
    req.on('error', reject);
    req.end();
  });
}

(async () => {
  // resolvePath is pure, so the nastiest inputs are checked without a socket.
  const traversals = [
    '/../package.json',
    '/../../etc/passwd',
    '/%2e%2e/serve.js',
    '/%2e%2e%2f%2e%2e%2fREADME.md',
    '/..%5c..%5cREADME.md',
    '/assets/../../serve.js',
  ];
  for (const t of traversals) {
    const r = resolvePath(t);
    const escaped = r.file && !r.file.startsWith(require('../serve.js').ROOT);
    test(`resolvePath refuses or contains ${t}`, r.error === 403 || r.error === 400 || !escaped,
      JSON.stringify(r));
  }
  // Double encoding is a filename, not a traversal — it must not escape either.
  const dbl = resolvePath('/%252e%252e/serve.js');
  test('resolvePath treats double-encoded traversal as a filename',
    dbl.error === undefined && dbl.file.includes('%2e%2e'), JSON.stringify(dbl));
  test('resolvePath refuses a NUL byte', resolvePath('/index%00.html').error === 400);
  test('resolvePath refuses malformed percent-encoding', resolvePath('/%zz').error === 400);

  const server = createServer();
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;

  try {
    const home = await request(port, '/');
    test('GET / serves the homepage', home.status === 200 && /RemoteAssist/.test(home.body.toString()));
    test('GET / is text/html', /text\/html/.test(home.headers['content-type']));
    test('responses carry X-Content-Type-Options: nosniff',
      home.headers['x-content-type-options'] === 'nosniff');

    const css = await request(port, '/assets/css/site.css');
    test('GET a stylesheet is text/css', css.status === 200 && /text\/css/.test(css.headers['content-type']));

    const svg = await request(port, '/assets/img/logo.svg');
    test('GET an SVG is image/svg+xml', svg.status === 200 && svg.headers['content-type'] === 'image/svg+xml');

    const extensionless = await request(port, '/pricing');
    test('GET /pricing resolves to pricing.html', extensionless.status === 200);

    const head = await request(port, '/index.html', 'HEAD');
    test('HEAD returns headers and no body', head.status === 200 && head.body.length === 0);
    test('HEAD still reports Content-Length', Number(head.headers['content-length']) > 0);

    const missing = await request(port, '/no-such-page.html');
    test('GET an unknown file is 404', missing.status === 404);
    test('the 404 body is the branded page', /404/.test(missing.body.toString()));

    const dir = await request(port, '/assets/');
    test('a directory has no index listing', dir.status === 404);

    const post = await request(port, '/', 'POST');
    test('POST is 405', post.status === 405);
    test('405 advertises Allow: GET, HEAD', post.headers.allow === 'GET, HEAD');
    const del = await request(port, '/index.html', 'DELETE');
    test('DELETE is 405', del.status === 405);

    // A literal ".." is collapsed by URL parsing before the server ever sees
    // it, exactly as every other web server does: /../../README.md becomes
    // /README.md. That is not an escape — the test that matters is that the
    // bytes returned never come from outside web/.
    const rootReadme = fs.readFileSync(path.join(WEB, '..', 'README.md'), 'utf8');
    const marker = '## Cross-platform status';
    test('the repository README contains the marker this test looks for', rootReadme.includes(marker));

    const outside = await request(port, '/../../README.md');
    test('GET /../../README.md never returns the repository README',
      !outside.body.toString().includes(marker), `status ${outside.status}`);

    const encoded = await request(port, '/%2e%2e/%2e%2e/README.md');
    test('GET an encoded traversal never returns the repository README',
      !encoded.body.toString().includes(marker), `status ${encoded.status}`);

    const sibling = await request(port, '/../server/package.json');
    test('GET a sibling directory above web/ is 404', sibling.status === 404);

    const deep = await request(port, '/assets/%2e%2e/%2e%2e/server/src/config.js');
    test('GET an encoded traversal into server/ is 404', deep.status === 404);
  } finally {
    server.close();
  }

  /* ------------------------------------------------------------- verdict */
  console.log('');
  if (failures.length) {
    console.log(`${passed} passed, ${failures.length} failed\n`);
    failures.forEach((f) => console.log(`  FAILED: ${f}`));
    process.exit(1);
  }
  console.log(`${passed} passed, 0 failed`);
})().catch((e) => {
  console.error(e);
  process.exit(1);
});
