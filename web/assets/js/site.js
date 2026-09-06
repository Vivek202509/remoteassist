/* RemoteAssist site behaviour: header, slider, theme, tabs, forms.
   Vanilla ES2015+, no dependencies. Every block is a no-op when its markup
   is absent, so the same file can be included on every page. */
(function () {
  'use strict';

  var $  = function (sel, root) { return (root || document).querySelector(sel); };
  var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

  var reduceMotion = window.matchMedia
    ? window.matchMedia('(prefers-reduced-motion: reduce)')
    : { matches: false, addEventListener: function () {} };

  /* ------------------------------------------------------------------ theme */
  // Also applied inline in <head>, so there is no light-flash before this runs.
  // The button's name stays "Dark mode" in both states; aria-pressed carries the
  // state, which is what a toggle button is supposed to do.
  var themeBtn = $('.theme-toggle');
  function paintTheme(mode) {
    document.documentElement.setAttribute('data-theme', mode);
    if (!themeBtn) return;
    themeBtn.setAttribute('aria-pressed', String(mode === 'dark'));
    var icon = $('.theme-toggle__icon', themeBtn);
    if (icon) icon.textContent = mode === 'dark' ? '◑' : '◐';
  }
  paintTheme(document.documentElement.getAttribute('data-theme') || 'light');
  if (themeBtn) {
    themeBtn.addEventListener('click', function () {
      var next = document.documentElement.getAttribute('data-theme') === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem('ra-theme', next); } catch (e) { /* private mode */ }
      paintTheme(next);
    });
  }
  // Follow the system only while the visitor has never chosen for themselves.
  if (window.matchMedia) {
    var systemDark = window.matchMedia('(prefers-color-scheme: dark)');
    var onSystemTheme = function () {
      var stored = null;
      try { stored = localStorage.getItem('ra-theme'); } catch (e) { /* private mode */ }
      if (!stored) paintTheme(systemDark.matches ? 'dark' : 'light');
    };
    if (systemDark.addEventListener) systemDark.addEventListener('change', onSystemTheme);
  }

  /* ------------------------------------------------------------- mobile nav */
  var navToggle = $('.nav-toggle');
  var nav = $('#primary-nav');
  if (navToggle && nav) {
    var setNav = function (open) {
      nav.classList.toggle('is-open', open);
      navToggle.setAttribute('aria-expanded', String(open));
    };
    navToggle.addEventListener('click', function (e) {
      e.stopPropagation();
      setNav(!nav.classList.contains('is-open'));
    });
    // Following a link inside the drawer closes it.
    nav.addEventListener('click', function (e) {
      if (e.target.closest('a')) setNav(false);
    });
    // Escape closes and returns focus to the button that opened it.
    document.addEventListener('keydown', function (e) {
      if (e.key !== 'Escape' || !nav.classList.contains('is-open')) return;
      setNav(false);
      navToggle.focus();
    });
    // A tap outside the open drawer closes it, matching the dropdown.
    document.addEventListener('click', function (e) {
      if (!nav.classList.contains('is-open')) return;
      if (nav.contains(e.target) || navToggle.contains(e.target)) return;
      setNav(false);
    });
  }

  /* ---------------------------------------------------------- back to top */
  var topBtn = $('.to-top');
  if (topBtn) {
    topBtn.addEventListener('click', function () {
      window.scrollTo({ top: 0, behavior: reduceMotion.matches ? 'auto' : 'smooth' });
    });
  }

  /* --------------------------------------------------------- header shadow */
  var header = $('.site-header');
  if (header || topBtn) {
    var onScroll = function () {
      if (header) header.classList.toggle('is-stuck', window.scrollY > 4);
      // .to-top is visibility:hidden until this class lands, so it never
      // becomes an invisible tab stop.
      if (topBtn) topBtn.classList.toggle('is-visible', window.scrollY > 500);
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();
  }

  /* ------------------------------------------------- accounts disclosure */
  // A disclosure, not a menu: it reveals a paragraph explaining that there is
  // no account system. Nothing inside it is interactive.
  $$('.dropdown').forEach(function (dd) {
    var toggle = $('.dropdown__toggle', dd);
    if (!toggle) return;
    var setOpen = function (open) {
      dd.classList.toggle('is-open', open);
      toggle.setAttribute('aria-expanded', String(open));
    };
    toggle.addEventListener('click', function (e) {
      e.stopPropagation();
      var open = !dd.classList.contains('is-open');
      $$('.dropdown').forEach(function (o) {
        o.classList.remove('is-open');
        var t = $('.dropdown__toggle', o);
        if (t) t.setAttribute('aria-expanded', 'false');
      });
      setOpen(open);
    });
    dd.addEventListener('click', function (e) { e.stopPropagation(); });
    dd.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') { setOpen(false); toggle.focus(); }
    });
  });
  function closeDropdowns() {
    $$('.dropdown').forEach(function (dd) {
      dd.classList.remove('is-open');
      var t = $('.dropdown__toggle', dd);
      if (t) t.setAttribute('aria-expanded', 'false');
    });
  }
  document.addEventListener('click', closeDropdowns);
  document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeDropdowns(); });

  /* ---------------------------------------------------------- hero slider */
  var hero = $('[data-slider]');
  if (hero) {
    var slides = $$('.slide', hero);
    var dotsBox = $('.slider-dots', hero);
    var index = 0;
    for (var i = 0; i < slides.length; i++) {
      if (slides[i].classList.contains('is-active')) { index = i; break; }
    }

    // Exactly one timer handle exists. Every path goes through play()/stop(),
    // so a burst of visibilitychange or mouseleave events cannot stack
    // intervals and make the carousel accelerate.
    var timer = null;
    var hovered = false;
    var focused = false;
    var DELAY = 7000;

    function stop() {
      if (timer !== null) { clearInterval(timer); timer = null; }
    }
    function play() {
      stop();
      // Reduced motion disables autoplay outright; the arrows, dots, keys and
      // swipe still work, so the content stays reachable.
      if (reduceMotion.matches || document.hidden || hovered || focused) return;
      timer = setInterval(function () { go(index + 1); }, DELAY);
    }

    var dots = slides.map(function (_, n) {
      var b = document.createElement('button');
      b.type = 'button';
      b.setAttribute('aria-label', 'Show slide ' + (n + 1) + ' of ' + slides.length);
      b.addEventListener('click', function () { go(n); play(); });
      if (dotsBox) dotsBox.appendChild(b);
      return b;
    });

    function go(next) {
      index = (next + slides.length) % slides.length;
      slides.forEach(function (s, n) {
        var on = n === index;
        s.classList.toggle('is-active', on);
        // Inactive slides are display:none, so this is belt and braces —
        // but it keeps the state explicit for assistive technology.
        s.setAttribute('aria-hidden', String(!on));
      });
      dots.forEach(function (d, n) {
        if (n === index) d.setAttribute('aria-current', 'true');
        else d.removeAttribute('aria-current');
      });
    }

    $$('[data-slide-prev]', hero).forEach(function (b) {
      b.addEventListener('click', function () { go(index - 1); play(); });
    });
    $$('[data-slide-next]', hero).forEach(function (b) {
      b.addEventListener('click', function () { go(index + 1); play(); });
    });

    hero.addEventListener('mouseenter', function () { hovered = true; stop(); });
    hero.addEventListener('mouseleave', function () { hovered = false; play(); });
    hero.addEventListener('focusin', function () { focused = true; stop(); });
    hero.addEventListener('focusout', function (e) {
      if (hero.contains(e.relatedTarget)) return;
      focused = false;
      play();
    });
    document.addEventListener('visibilitychange', function () {
      if (document.hidden) stop(); else play();
    });
    if (reduceMotion.addEventListener) {
      reduceMotion.addEventListener('change', function () { play(); });
    }

    hero.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowLeft')  { e.preventDefault(); go(index - 1); play(); }
      if (e.key === 'ArrowRight') { e.preventDefault(); go(index + 1); play(); }
    });

    // Touch swipe. Deliberately crude: one axis, one threshold, no inertia.
    var startX = null;
    var startY = null;
    hero.addEventListener('touchstart', function (e) {
      startX = e.touches[0].clientX;
      startY = e.touches[0].clientY;
    }, { passive: true });
    hero.addEventListener('touchend', function (e) {
      if (startX === null) return;
      var dx = e.changedTouches[0].clientX - startX;
      var dy = e.changedTouches[0].clientY - startY;
      // Ignore anything that is really a vertical scroll.
      if (Math.abs(dx) > 45 && Math.abs(dx) > Math.abs(dy)) {
        go(dx < 0 ? index + 1 : index - 1);
        play();
      }
      startX = startY = null;
    }, { passive: true });

    go(index);
    play();
  }

  /* ----------------------------------------------------------------- tabs */
  $$('[data-tabs]').forEach(function (group) {
    var buttons = $$('[role="tab"]', group);
    function select(id) {
      buttons.forEach(function (b) {
        var on = b.getAttribute('aria-controls') === id;
        b.setAttribute('aria-selected', String(on));
        b.tabIndex = on ? 0 : -1;
        var panel = document.getElementById(b.getAttribute('aria-controls'));
        if (panel) panel.hidden = !on;
      });
    }
    buttons.forEach(function (b, i) {
      b.addEventListener('click', function () { select(b.getAttribute('aria-controls')); });
      b.addEventListener('keydown', function (e) {
        var target = null;
        if (e.key === 'ArrowRight') target = buttons[(i + 1) % buttons.length];
        else if (e.key === 'ArrowLeft') target = buttons[(i - 1 + buttons.length) % buttons.length];
        else if (e.key === 'Home') target = buttons[0];
        else if (e.key === 'End') target = buttons[buttons.length - 1];
        if (!target) return;
        e.preventDefault();
        target.focus();
        select(target.getAttribute('aria-controls'));
      });
    });
    // Platform hint: an Android visitor lands on the Android tab. Windows is
    // deliberately NOT auto-selected — that tab is a preview, and it should not
    // be the first thing every desktop visitor sees.
    var guess = /Android/i.test(navigator.userAgent) ? 'android' : null;
    var preferred = guess && buttons.filter(function (b) { return b.dataset.os === guess; })[0];
    select((preferred || buttons[0]).getAttribute('aria-controls'));
  });

  /* -------------------------------------------------------- copy to board */
  $$('.code').forEach(function (block, n) {
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'copy';
    btn.textContent = 'Copy';
    // Several "Copy" buttons on one page are indistinguishable by name alone.
    btn.setAttribute('aria-label', 'Copy code sample ' + (n + 1));
    btn.addEventListener('click', function () {
      var pre = $('pre', block);
      var text = (pre ? pre.innerText : block.innerText).trim();
      var done = function () {
        btn.textContent = 'Copied';
        setTimeout(function () { btn.textContent = 'Copy'; }, 1600);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(done, function () { btn.textContent = 'Press Ctrl+C'; });
      } else {
        var ta = document.createElement('textarea');
        ta.value = text; document.body.appendChild(ta); ta.select();
        try { document.execCommand('copy'); done(); } catch (e) { btn.textContent = 'Press Ctrl+C'; }
        document.body.removeChild(ta);
      }
    });
    block.appendChild(btn);
  });

  /* ------------------------------------------------------- doc scrollspy */
  var tocLinks = $$('.toc a[href^="#"]');
  if (tocLinks.length && 'IntersectionObserver' in window) {
    var byId = {};
    tocLinks.forEach(function (a) { byId[a.getAttribute('href').slice(1)] = a; });
    var targets = Object.keys(byId)
      .map(function (id) { return document.getElementById(id); })
      .filter(Boolean);

    var spy = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        tocLinks.forEach(function (a) {
          a.classList.remove('is-current');
          a.removeAttribute('aria-current');
        });
        var link = byId[entry.target.id];
        if (link) {
          link.classList.add('is-current');
          link.setAttribute('aria-current', 'true');
        }
      });
    }, { rootMargin: '-110px 0px -70% 0px', threshold: 0 });
    targets.forEach(function (t) { spy.observe(t); });
  }

  /* --------------------------------------------------------------- forms */
  // No endpoint exists. The submit handler exists precisely so a submission
  // cannot silently look successful: it refuses, and says why.
  $$('form[data-demo-form]').forEach(function (form) {
    form.addEventListener('submit', function (e) {
      e.preventDefault();
      var status = $('.form-status', form);
      if (!status) return;
      status.classList.add('form-status--blocked');
      status.textContent = 'Not sent. Online submission is not enabled on this site — the form has no '
        + 'endpoint, so nothing was transmitted or stored. Please use the GitHub issue tracker instead.';
    });
  });

  /* ------------------------------------------------------------ copyright */
  $$('[data-year]').forEach(function (el) { el.textContent = String(new Date().getFullYear()); });
})();
