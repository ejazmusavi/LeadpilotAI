(function () {
  var form = document.getElementById('campaign-form');
  if (!form) return;
  var box = document.getElementById('preview-box');
  var btn = document.getElementById('preview-btn');
  var type = document.getElementById('campaign-type');
  var tpl = document.getElementById('template-select');
  var pre = document.getElementById('template-preview');
  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML; }

  function syncType() {
    var t = type.value;
    var fu = document.getElementById('follow-up-box'), dl = document.getElementById('delay-box');
    if (fu) fu.style.display = (t === 'FollowUp' || t === 'ReEngagement') ? '' : 'none';
    if (dl) dl.style.display = t === 'FollowUp' ? '' : 'none';
  }
  function syncTemplate() {
    var o = tpl.options[tpl.selectedIndex];
    pre.textContent = o && o.dataset.body ? o.dataset.body : 'Choose a template to see its text.';
  }
  type.addEventListener('change', syncType); tpl.addEventListener('change', syncTemplate); syncType(); syncTemplate();

  function render(r) {
    var h = '<div class="row text-center g-2 mb-3">' +
      '<div class="col-4"><div class="fs-3">' + r.candidates + '</div><div class="small text-muted">examined</div></div>' +
      '<div class="col-4"><div class="fs-3 text-success">' + r.eligible + '</div><div class="small text-muted">would be messaged</div></div>' +
      '<div class="col-4"><div class="fs-3 text-secondary">' + r.excluded + '</div><div class="small text-muted">excluded</div></div></div>';
    (r.warnings || []).forEach(function (w) { h += '<div class="alert alert-warning py-1 small">' + esc(w) + '</div>'; });
    if (r.breakdown && r.breakdown.length) {
      h += '<div class="fw-semibold small mb-1">Why contacts are excluded</div><ul class="list-group mb-3">';
      r.breakdown.forEach(function (b) { h += '<li class="list-group-item d-flex justify-content-between small"><span>' + esc(b.explanation) + '</span><span class="badge text-bg-secondary">' + b.count + '</span></li>'; });
      h += '</ul>';
    }
    if (r.samples && r.samples.length) {
      h += '<div class="fw-semibold small mb-1">Sample messages</div>';
      r.samples.forEach(function (s) {
        h += '<div class="border rounded p-2 mb-2"><div class="small text-muted">' + esc(s.leadName) + ' · ' + esc(s.maskedDestination) + '</div>' +
          (s.problem ? '<div class="small text-danger">' + esc(s.problem) + '</div>' : '<div class="small" style="white-space:pre-wrap">' + esc(s.renderedMessage) + '</div>') + '</div>';
      });
    }
    box.innerHTML = h;
  }

  function preview() {
    if (form.dataset.canPreview !== 'true') return;
    btn.disabled = true; box.innerHTML = '<div class="text-muted small"><span class="spinner-border spinner-border-sm"></span> Examining the audience…</div>';
    fetch(form.dataset.previewUrl, { method: 'POST', body: new FormData(form), credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } })
      .then(function (res) { return res.json().then(function (j) { return { ok: res.ok, j: j }; }); })
      .then(function (x) { if (x.ok) render(x.j); else box.innerHTML = '<div class="alert alert-danger small mb-0">' + esc(x.j.error || 'The preview failed.') + '</div>'; })
      .catch(function () { box.innerHTML = '<div class="alert alert-danger small mb-0">The preview could not be loaded.</div>'; })
      .finally(function () { btn.disabled = false; });
  }
  btn.addEventListener('click', preview);
  var t; form.addEventListener('change', function (e) { if (e.target.id === 'template-select' || e.target.id === 'campaign-type') return; clearTimeout(t); t = setTimeout(preview, 900); });
})();
