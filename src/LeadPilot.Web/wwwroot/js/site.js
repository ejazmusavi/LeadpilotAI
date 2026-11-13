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

  // Bulk selection on list pages: select-all + live counter
  var selectAll = document.getElementById('select-all');
  if (selectAll) {
    var checks = function () { return Array.prototype.slice.call(document.querySelectorAll('.lead-check')); };
    var counter = document.getElementById('bulk-count');
    var refresh = function () { if (counter) counter.textContent = checks().filter(function (c) { return c.checked; }).length; };
    selectAll.addEventListener('change', function () { checks().forEach(function (c) { c.checked = selectAll.checked; }); refresh(); });
    document.addEventListener('change', function (e) { if (e.target.classList && e.target.classList.contains('lead-check')) refresh(); });
  }

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
