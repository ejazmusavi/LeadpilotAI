// Lead picker of the simulation form: searches the signed-in workspace's leads (server-side, permission-checked) and fills a hidden id.
(function () {
  var input = document.getElementById('lead-search'); if (!input) return;
  var hidden = document.getElementById('lead-id'), box = document.getElementById('lead-results'), timer = null;
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : s; return d.innerHTML; }
  input.addEventListener('input', function () {
    hidden.value = ''; clearTimeout(timer);
    var q = input.value.trim(); if (q.length < 2) { box.innerHTML = ''; return; }
    timer = setTimeout(function () {
      fetch(input.getAttribute('data-find') + '?q=' + encodeURIComponent(q), { credentials: 'same-origin' }).then(function (r) { return r.ok ? r.json() : []; }).then(function (items) {
        box.innerHTML = items.length === 0 ? '<span class="list-group-item small text-muted">No lead found</span>' : items.map(function (l) {
          return '<button type="button" class="list-group-item list-group-item-action" data-id="' + esc(l.id) + '" data-name="' + esc(l.name) + '">' + esc(l.name) + ' <span class="small text-muted">' + esc(l.city || '') + ' · ' + esc(l.stage) + '</span></button>'; }).join('');
      });
    }, 250);
  });
  box.addEventListener('click', function (e) {
    var b = e.target.closest('button[data-id]'); if (!b) return;
    hidden.value = b.getAttribute('data-id'); input.value = b.getAttribute('data-name'); box.innerHTML = '';
  });
})();
