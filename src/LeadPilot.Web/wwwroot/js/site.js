(function () {
  // Toasts
  document.querySelectorAll('.toast[data-auto-show]').forEach(function (el) { new bootstrap.Toast(el, { delay: 6000 }).show(); });

  // Loading state on form submit: disable button, show spinner, show top progress bar.
  document.querySelectorAll('form[data-loading-form]').forEach(function (form) {
    form.addEventListener('submit', function () {
      var valid = !window.jQuery || !jQuery.validator || jQuery(form).valid();
      if (!valid) return;
      var btn = form.querySelector('button[type=submit]');
      if (btn) { btn.disabled = true; var s = btn.querySelector('.spinner-border'); if (s) s.classList.remove('d-none'); }
      var bar = document.getElementById('page-loading'); if (bar) bar.hidden = false;
    });
  });
  // Re-enable on back/forward cache restore
  window.addEventListener('pageshow', function (e) {
    if (e.persisted) document.querySelectorAll('form[data-loading-form] button[type=submit]').forEach(function (b) { b.disabled = false; });
  });
})();
