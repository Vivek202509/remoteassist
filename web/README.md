# RemoteAssist website

The public marketing site: plain HTML, one stylesheet, three small scripts. No build step, no
package manager, no external requests — no CDN fonts, no analytics, no third-party embeds — so
it runs from a `file://` double-click, from the bundled preview server, or from any static host.

```bash
node web/serve.js          # http://localhost:5173  (development preview only)
PORT=8000 node web/serve.js
node web/test/qa.js        # the QA suite: links, metadata, status drift, serve.js
```

## Layout

```
web/
├── index.html        # hero slider, features, how it works, security, platform status, cost, FAQ
├── download.html     # tabbed: Android app / Windows host preview / signaling broker
├── document.html     # docs with a sticky table of contents; status & naming; troubleshooting
├── pricing.html      # "pricing coming soon" — no figures, nothing purchasable
├── blog.html         # summary cards linking to the repository's own documents
├── contact.html      # contact form with submission disabled, plus routes that do work
├── serve.js          # zero-dependency preview server (NOT for production)
├── DEPLOYMENT.md     # static hosting + the response headers to set
├── test/qa.js        # zero-dependency QA suite
└── assets/
    ├── css/site.css              # every style, themed with CSS custom properties
    ├── js/product-status.js      # ← the product-fact source of truth (see below)
    ├── js/site.js                # slider, tabs, theme, nav, scrollspy, copy buttons, forms
    ├── js/i18n.js                # language shell (English only; see below)
    └── img/                      # logo and favicon, hand-drawn SVG
```

## Product status lives in one file

`assets/js/product-status.js` holds the facts that appear on more than one page: the version,
platform status and badges, build requirements, distribution shape, test counts and the
acceptance notices. Everything else — ordinary page copy — stays readable in the HTML. There is
no template engine and no framework; this is a data file and about forty lines that write it
into the DOM.

Pages tag the value and repeat it as their own text:

```html
<dd data-status="version.label">1.0 (build 1)</dd>
<span class="badge badge--preview" data-status-badge="platforms.windowsHost">Preview</span>
```

The text in the HTML is what a visitor with JavaScript disabled sees, so it has to match the
data file. `node web/test/qa.js` fails if the two drift apart — which is the whole point: it is
no longer possible for `index.html` to say *Preview* while `download.html` says *Available*.

### Updating the version or a platform status

1. Edit `assets/js/product-status.js`.
2. Update the matching fallback text in the HTML — the QA suite will name any element you miss.
3. Run `node web/test/qa.js`.

Nothing else needs touching. If a status changes because a real-device acceptance step passed,
update `docs/CROSS_PLATFORM_TEST_MATRIX.md` in the same commit; the site's claims are written
from that file, not the other way round.

## Placeholder policy

**Nothing on this site invents a fact.** Where a real value does not exist, the page says so in
plain language instead of showing a plausible-looking stand-in:

| Thing | State on the site |
|---|---|
| Prices | "Pricing coming soon". No figures, no trial, nothing purchasable. |
| Sign-in | The utility bar explains there is no account system. There is no login form. |
| Contact form | Visible, submission refused with an explicit "not sent" message, no endpoint. |
| Social links | Only the real GitHub repository. Facebook / X / LinkedIn icons were removed rather than pointed at `#`. |
| Postal address, phone, support inbox | Absent. None exist. |
| Canonical URL | Omitted. No production domain exists, and inventing one is worse than having none. |
| Downloads | No installer links. Every platform is a source build, and the page says so first. |

The QA suite enforces the mechanical half of this: no `href="#"`, no `localhost` links, no
password inputs, no absolute developer paths, no "Shipping" badges.

## Honesty about product claims

Feature copy, the platform table and the version panel are written from the repository's own
evidence logs, including the parts that are inconvenient:

- **Android is described as *implemented*, not *shipping*.** The code is feature-complete and
  its unit tests pass, but every row of `docs/GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md` is still
  `MANUAL TEST REQUIRED` — no session has been observed between two physical handsets.
- **The Windows host is a *preview*.** It streams and accepts input between two in-process
  peers; no Android controller has rendered its desktop.
- **RemoteAssist is not described as Internet-ready remote control**, because TURN has not been
  exercised and no session has crossed a network.
- **The Android unattended ceiling is stated**, not glossed.

If the code advances past any of those, change `product-status.js` and the sentence in
`document.html#status` together.

## Naming

**RemoteAssist** is the product name; **Techee** is the internal project name and appears
throughout the repository — the working directory, `Techee.Windows.slnx`, and the signed
protocol transcript prefixes (`techee-register-v1`, `techee-sdp-v1`). Those prefixes are pinned
by cross-language fixtures, so they are deliberately not renamed. `document.html#status`
explains this to visitors, once.

## Features

- **Hero slider** — three slides, one autoplay timer that can never be duplicated, pause on
  hover / focus / hidden tab, arrows, dots, arrow keys and swipe. Autoplay does not start at all
  under `prefers-reduced-motion`; every manual control still works.
- **Sticky header** with a mobile drawer under 980px that closes on link selection, Escape
  (returning focus to the toggle) and outside click.
- **Dark mode** — follows the system preference on a first visit, remembers an explicit choice,
  and is applied inline in `<head>` so there is no flash of the light theme.
- **Language shell** — English only. `i18n.js` builds the selector from its own `LOCALES` table
  and offers a locale for selection only when it sets `complete: true`; anything else appears
  as a disabled "coming soon" option. Hindi has chrome strings and is deliberately *not*
  selectable, because the page bodies are still English.
- **Tabs** on the download page, with roving tabindex, arrow keys, Home and End.
- **Copy buttons** injected into every `.code` block, each with a distinct accessible name.
- **Scrollspy** on the documentation table of contents, exposing `aria-current`.
- **Responsive tables** — wide tables scroll inside their own container, never the page.

## Accessibility

One `<h1>` per page, no skipped heading levels, real `<button>` elements throughout, a visible
focus ring on every focusable control (switching to white on the dark bands), labelled form
fields, decorative SVG hidden from assistive technology, and `aria-expanded` / `aria-selected` /
`aria-current` maintained by the scripts. Text contrast meets WCAG AA in both themes; the
`--muted`, `--link`, `--accent-text` and badge colours were chosen by measurement rather than by
eye. `prefers-reduced-motion` disables autoplay and animation.

## Known limitations

- No canonical URLs, no `sitemap.xml`, no `robots.txt` and no Open Graph image — all four need a
  production domain first.
- The blog is six summary cards, not articles. Each links to the repository document it
  describes; there are no per-post pages.
- `i18n.js` translates page *chrome* only. It is a shell, kept honest by the `complete` flag.
- The QA suite scrapes HTML with regular expressions rather than parsing it. That is adequate
  for six files under our own control and would not be for a generated site.
- The preview server has no HTTP caching, compression or TLS, and is not meant to.

## Production deployment

See [`DEPLOYMENT.md`](DEPLOYMENT.md): what to upload, the response headers to set
(`Content-Security-Policy` with a hash for the one inline script, `X-Content-Type-Options`,
`Referrer-Policy`, `Permissions-Policy`, `frame-ancestors`), and nginx / Caddy examples.
`serve.js` is a development preview server and must not be exposed publicly.

## Still needed from the project owner

These are business and product decisions, not code. Every one of them is currently represented
on the site by an explicit "does not exist yet", so the site is publishable as it stands — but
each is a gap someone has to close before it can claim otherwise.

1. **Commercial pricing** — whether there is a paid tier at all, and what it costs. Until then
   `pricing.html` says "pricing coming soon" and sells nothing.
2. **A contact endpoint** — a form handler URL, or the decision to keep pointing people at
   GitHub issues. Also update `form-action` in the CSP when one exists.
3. **Support contact details** — a postal address, phone number or support inbox, if any are
   to be listed. None are invented today.
4. **An account / login backend** — the utility bar currently states that no account system
   exists. It is not a stub waiting to be switched on.
5. **A production domain** — needed before canonical URLs, an Open Graph image, `sitemap.xml`
   or HSTS make sense.
6. **Real distribution URLs** — a signed APK, a Windows installer, or a release page. Until one
   exists the download page correctly offers source builds only.
7. **Social accounts** — if Facebook / X / LinkedIn accounts are created, add them back to the
   `.socials` block on all six pages.
8. **Device acceptance evidence** — not a website decision, but the site's platform status
   cannot improve until `docs/GALAXY_A03_REAL_DEVICE_TEST_MATRIX.md` and the W3/W4 procedures
   record real observations.
