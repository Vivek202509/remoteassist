# Deploying the RemoteAssist website

The site is static: six HTML files, one stylesheet, three scripts and two SVGs. There is no
build step, no server-side rendering and no runtime dependency. Any static host or reverse
proxy can serve it.

## `serve.js` is not a production server

`web/serve.js` is a **development / local preview server**. It exists so you can look at the
site without installing anything:

```bash
node web/serve.js          # http://localhost:5173
PORT=8000 node web/serve.js
```

It has no TLS, no compression, no HTTP caching policy, no rate limiting, no access log, no
graceful shutdown and none of the response headers below except `X-Content-Type-Options`. It
does refuse path traversal and non-GET/HEAD methods, and `web/test/qa.js` proves that — but
"does not have a directory traversal bug" is a long way from "hardened web server". Do not
put it on a public address.

## What to upload

Upload only the site itself:

```
index.html  download.html  document.html  pricing.html  blog.html  contact.html
assets/
```

Leave `serve.js`, `test/`, `README.md` and this file behind — they are repository files, not
site content. The local preview server serves them because it serves the directory it lives
in; a deployment should not.

## Response headers

Set these at the host or reverse proxy. Nothing in the site needs a relaxed policy: it makes
no network requests, embeds no third-party resources and carries no `style=""` attributes.

| Header | Value | Why |
|---|---|---|
| `Content-Security-Policy` | see below | The site loads nothing remote, so the policy can be strict enough to be worth having |
| `X-Content-Type-Options` | `nosniff` | Stops a browser re-interpreting a response as a type it was not served as |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Outbound links to GitHub should not carry the full path someone was reading |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()` | The site uses none of these; denying them removes the surface entirely |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains` | Only once the site is served over HTTPS on its final domain |
| `Cross-Origin-Opener-Policy` | `same-origin` | Isolates the browsing context |
| `X-Frame-Options` | — | Not needed: `frame-ancestors` below supersedes it for every browser that matters |

### Content-Security-Policy

```
Content-Security-Policy:
  default-src 'none';
  script-src 'self' 'sha256-3fpuGB5y8OimeJFsjVcGs9owDtiW5FGEKYoJPq6XuzE=';
  style-src 'self';
  img-src 'self';
  font-src 'self';
  connect-src 'none';
  form-action 'none';
  base-uri 'none';
  frame-ancestors 'none';
  object-src 'none'
```

Notes on the parts that are easy to get wrong:

- **The `sha256-` value is the one inline `<script>` in every page's `<head>`** — the four-line
  block that applies the stored or system colour theme before first paint. It has to be inline,
  because an external script would arrive after the first paint and the page would flash white
  before turning dark. Hashing it is what lets `script-src` stay free of `'unsafe-inline'`.
  The script is byte-identical on all six pages, and `web/test/qa.js` fails if it changes
  without this hash being updated with it.
- **`style-src 'self'` works with no `'unsafe-inline'`** because the HTML carries no `style=""`
  attributes; the handful that used to exist became utility classes in `site.css`. The QA suite
  enforces that too.
- **`form-action 'none'`** is correct today because the contact form posts nowhere. Change it to
  the endpoint's origin at the same time you wire the form up, and not before.
- **`connect-src 'none'`** likewise: the site makes no `fetch`, `XHR` or WebSocket calls.
- **`frame-ancestors 'none'`** is the modern replacement for `X-Frame-Options: DENY`. Set it in
  the CSP header — it is ignored inside a `<meta>` tag.

CSP cannot be set from the HTML for `frame-ancestors`, and a `<meta http-equiv>` policy is
weaker in general, so configure these at the server.

### nginx

```nginx
server {
  listen 443 ssl http2;
  server_name example.invalid;          # your domain
  root /srv/remoteassist-web;

  add_header X-Content-Type-Options "nosniff" always;
  add_header Referrer-Policy "strict-origin-when-cross-origin" always;
  add_header Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=(), usb=()" always;
  add_header Cross-Origin-Opener-Policy "same-origin" always;
  add_header Strict-Transport-Security "max-age=31536000; includeSubDomains" always;
  add_header Content-Security-Policy "default-src 'none'; script-src 'self' 'sha256-3fpuGB5y8OimeJFsjVcGs9owDtiW5FGEKYoJPq6XuzE='; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'" always;

  location / {
    try_files $uri $uri.html $uri/ =404;
  }

  # Fingerprint the asset filenames before caching them hard; until then, keep
  # it short, because assets/css/site.css is a stable name.
  location /assets/ {
    add_header Cache-Control "public, max-age=3600";
  }
}
```

### Caddy

```
example.invalid {
  root * /srv/remoteassist-web
  file_server
  try_files {path} {path}.html
  header {
    X-Content-Type-Options "nosniff"
    Referrer-Policy "strict-origin-when-cross-origin"
    Permissions-Policy "camera=(), microphone=(), geolocation=(), payment=(), usb=()"
    Cross-Origin-Opener-Policy "same-origin"
    Content-Security-Policy "default-src 'none'; script-src 'self' 'sha256-3fpuGB5y8OimeJFsjVcGs9owDtiW5FGEKYoJPq6XuzE='; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'; object-src 'none'"
  }
}
```

### Static hosts

On Netlify use `_headers`, on Cloudflare Pages use `_headers`, on GitHub Pages you cannot set
response headers at all — which is worth knowing before choosing it, since it means no CSP.

## Extensionless URLs

`serve.js` maps `/pricing` to `pricing.html`. Reproduce that with `try_files` (nginx) or
`try_files {path} {path}.html` (Caddy) if you want the same URLs in production; otherwise the
in-site links all use explicit `.html` and work either way.

## Before the first public deploy

The site deliberately ships without invented business details. The list of what a real launch
still needs — pricing, a contact endpoint, support contact details, an account backend, a
production domain and real distribution URLs — is in [`README.md`](README.md).
