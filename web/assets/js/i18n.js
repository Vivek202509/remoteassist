/* Minimal i18n for the site chrome — an extensible shell, honestly labelled.

   Only English content exists today. The Hindi entry below translates the
   navigation and a handful of headings, which is not the same thing as a
   translated site: every paragraph would still be English. Presenting that as
   "हिन्दी" would be a lie told in two languages, so a locale is offered for
   selection only when it sets `complete: true`. Everything else appears in the
   selector as a disabled "coming soon" option, which is the honest state and
   also keeps the work visible.

   To add a locale:
     1. add an entry to LOCALES with the same keys;
     2. translate the page bodies too;
     3. set `complete: true`.
   The <option> list is built from LOCALES here, so no page markup changes.
   Any element with data-i18n="key" is swapped; the English text stays in the
   HTML (correct with JavaScript disabled, and for crawlers) and is cached on
   first run, which is what makes switching back to English possible without an
   English dictionary. Missing keys fall back to that cached text. */
(function () {
  'use strict';

  var LOCALES = {
    en: {
      label: 'English',
      complete: true
    },

    hi: {
      label: 'हिन्दी',
      complete: false,               // chrome only — page bodies are still English
      'nav.what':      'रिमोटअसिस्ट क्या है?',
      'nav.download':  'डाउनलोड',
      'nav.document':  'दस्तावेज़',
      'nav.pricing':   'मूल्य',
      'nav.blog':      'ब्लॉग',
      'nav.contact':   'संपर्क',
      'top.accounts':  'खाते',
      'top.language':  'भाषा:',
      'top.darkmode':  'डार्क मोड',
      'btn.download':  'डाउनलोड',
      'btn.status':    'क्या बना है',
      'sec.features':  'रिमोट सपोर्ट, बेहद आसान',
      'sec.version':   'नवीनतम संस्करण',
      'sec.how':       'यह कैसे काम करता है',
      'sec.security':  'सुरक्षा',
      'sec.platforms': 'प्लेटफ़ॉर्म',
      'sec.pricing':   'मूल्य',
      'sec.faq':       'अक्सर पूछे जाने वाले प्रश्न',
      'foot.product':  'उत्पाद',
      'foot.resources':'संसाधन',
      'foot.company':  'कंपनी'
    }
  };

  var nodes = Array.prototype.slice.call(document.querySelectorAll('[data-i18n]'));
  var select = document.getElementById('lang-select');

  function available(code) { return LOCALES[code] && LOCALES[code].complete === true; }

  function apply(code) {
    var dict = available(code) ? LOCALES[code] : LOCALES.en;
    nodes.forEach(function (el) {
      if (el.dataset.i18nSource === undefined) el.dataset.i18nSource = el.textContent;
      var key = el.dataset.i18n;
      el.textContent = (key in dict) ? dict[key] : el.dataset.i18nSource;
    });
    document.documentElement.lang = available(code) ? code : 'en';
    try { localStorage.setItem('ra-lang', code); } catch (e) { /* private mode */ }
  }

  var stored = 'en';
  try { stored = localStorage.getItem('ra-lang') || 'en'; } catch (e) { /* ignore */ }
  if (!available(stored)) stored = 'en';

  if (select) {
    // Rebuild the option list from LOCALES so the selector can never advertise
    // a language the dictionary cannot actually deliver.
    select.innerHTML = '';
    Object.keys(LOCALES).forEach(function (code) {
      var opt = document.createElement('option');
      opt.value = code;
      if (LOCALES[code].complete === true) {
        opt.textContent = LOCALES[code].label;
      } else {
        opt.textContent = LOCALES[code].label + ' — coming soon';
        opt.disabled = true;
      }
      select.appendChild(opt);
    });
    select.value = stored;
    select.addEventListener('change', function () { apply(select.value); });
  }

  if (stored !== 'en') apply(stored);
})();
