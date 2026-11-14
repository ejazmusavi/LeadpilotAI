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

  // Confirmation dialog for destructive actions: <button data-confirm="message" data-confirm-label="Delete">
  var modalEl = document.getElementById('confirm-modal');
  if (modalEl && window.bootstrap) {
    var modal = new bootstrap.Modal(modalEl), pending = null;
    document.addEventListener('click', function (e) {
      var b = e.target.closest('[data-confirm]');
      if (!b || !b.form) return;
      e.preventDefault();
      pending = b;
      document.getElementById('confirm-message').textContent = b.getAttribute('data-confirm');
      document.getElementById('confirm-ok').textContent = b.getAttribute('data-confirm-label') || 'Confirm';
      modal.show();
    });
    document.getElementById('confirm-ok').addEventListener('click', function () {
      if (!pending) return;
      var b = pending; pending = null; modal.hide();
      b.removeAttribute('data-confirm');
      b.form.requestSubmit(b);
    });
  }
})();
