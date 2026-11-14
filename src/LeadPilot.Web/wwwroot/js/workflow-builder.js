// Workflow builder: people pick triggers, conditions and steps from forms; this script keeps the state and posts it as JSON (a transport only, never typed by hand).
(function () {
  var root = document.getElementById('wf-builder'); if (!root) return;
  var catalog = JSON.parse(document.getElementById('wf-catalog').textContent);
  var initial = JSON.parse(document.getElementById('wf-initial').textContent);
  var form = document.getElementById('wf-form'), out = document.getElementById('wf-definition');
  var SAMPLE = { name: 'Green Valley School', first_name: 'Sara', business_name: 'Green Valley School', phone: '+923001234567', city: 'Lahore', country: 'Pakistan' };
  var DAYS = ['mon', 'tue', 'wed', 'thu', 'fri', 'sat', 'sun'];

  function esc(s) { var d = document.createElement('div'); d.textContent = s == null ? '' : String(s); return d.innerHTML.replace(/"/g, '&quot;'); }
  function field(name) { return catalog.fields.find(function (f) { return f.name === name; }); }
  function opts(list, sel, blank) { return (blank ? '<option value="">' + esc(blank) + '</option>' : '') + list.map(function (o) { var v = o.value !== undefined ? o.value : o; var l = o.label !== undefined ? o.label : o; return '<option value="' + esc(v) + '"' + (String(v) === String(sel) ? ' selected' : '') + '>' + esc(l) + '</option>'; }).join(''); }

  // ---------- conditions: rows <-> declarative JSON ----------
  function newRow() { return { kind: 'cmp', field: 'lead.city', op: 'eq', value: '' }; }
  function toCond(c) { // c = { mode, rows, raw }
    if (!c) return null; if (c.raw) return c.raw;
    var items = c.rows.map(function (r) {
      if (r.kind === 'window') return { window: { days: r.days, from: r.from, to: r.to } };
      var f = field(r.field); var o = { field: r.field, op: r.op };
      if (r.op === 'is_empty' || r.op === 'is_not_empty') return o;
      if (f.kind === 'Number') o.value = Number(r.value); else if (f.kind === 'Bool') o.value = String(r.value) === 'true'; else o.value = String(r.value == null ? '' : r.value);
      return o; });
    if (items.length === 0) return null;
    if (items.length === 1 && c.mode === 'all') return items[0];
    var o2 = {}; o2[c.mode] = items; return o2;
  }
  function fromCond(j) {
    if (!j) return { mode: 'all', rows: [] };
    var list = j.all || j.any; var mode = j.any ? 'any' : 'all';
    if (!list) list = [j];
    var rows = [];
    for (var i = 0; i < list.length; i++) {
      var x = list[i];
      if (x.window) rows.push({ kind: 'window', days: x.window.days, from: x.window.from, to: x.window.to });
      else if (x.field) rows.push({ kind: 'cmp', field: x.field, op: x.op, value: x.value === undefined ? '' : (Array.isArray(x.value) ? x.value.join(', ') : x.value) });
      else return { mode: 'all', rows: [], raw: j };   // nested / NOT: shown read-only and kept as saved
    }
    return { mode: mode, rows: rows };
  }
  function condHtml(path, c, label) {
    if (c.raw) return '<div class="small text-muted">An advanced condition is saved (' + esc(JSON.stringify(c.raw).slice(0, 160)) + '…). <button type="button" class="btn btn-link btn-sm p-0" data-act="cond-clear" data-path="' + path + '">Replace it</button></div>';
    var h = '<div class="small fw-semibold mb-1">' + esc(label) + '</div>';
    if (c.rows.length > 1) h += '<div class="mb-2 small">Match <select class="form-select form-select-sm d-inline-block w-auto" data-path="' + path + '" data-bind="mode"><option value="all"' + (c.mode === 'all' ? ' selected' : '') + '>ALL of these</option><option value="any"' + (c.mode === 'any' ? ' selected' : '') + '>ANY of these</option></select></div>';
    c.rows.forEach(function (r, i) {
      var p = path + '.rows.' + i;
      if (r.kind === 'window') {
        h += '<div class="row g-2 mb-2 align-items-center"><div class="col-12 col-md-6">' + DAYS.map(function (d) { return '<label class="me-2 small"><input type="checkbox" data-path="' + p + '" data-bind="day" value="' + d + '"' + (r.days.indexOf(d) >= 0 ? ' checked' : '') + '> ' + d + '</label>'; }).join('') + '</div>' +
          '<div class="col-5 col-md-2"><input type="time" class="form-control form-control-sm" data-path="' + p + '.from" data-bind="val" value="' + esc(r.from) + '" aria-label="From"></div><div class="col-5 col-md-2"><input type="time" class="form-control form-control-sm" data-path="' + p + '.to" data-bind="val" value="' + esc(r.to) + '" aria-label="To"></div>' +
          '<div class="col-2"><button type="button" class="btn btn-sm btn-outline-danger" data-act="row-del" data-path="' + path + '" data-i="' + i + '" aria-label="Remove">×</button></div><div class="col-12 small text-muted">Business hours in the workspace time zone.</div></div>';
        return;
      }
      var f = field(r.field) || field('lead.city'); var ops = catalog.operators[f.kind];
      var groups = {}; catalog.fields.forEach(function (x) { (groups[x.group] = groups[x.group] || []).push(x); });
      var fieldSel = Object.keys(groups).map(function (g) { return '<optgroup label="' + esc(g) + '">' + groups[g].map(function (x) { return '<option value="' + esc(x.name) + '"' + (x.name === r.field ? ' selected' : '') + '>' + esc(x.label) + '</option>'; }).join('') + '</optgroup>'; }).join('');
      var needsValue = !(r.op === 'is_empty' || r.op === 'is_not_empty');
      var valueInput = !needsValue ? '' : f.kind === 'Bool' ? '<select class="form-select form-select-sm" data-path="' + p + '.value" data-bind="val"><option value="true"' + (String(r.value) === 'true' ? ' selected' : '') + '>yes</option><option value="false"' + (String(r.value) !== 'true' ? ' selected' : '') + '>no</option></select>'
        : '<input class="form-control form-control-sm" data-path="' + p + '.value" data-bind="val" type="' + (f.kind === 'Number' ? 'number' : 'text') + '" maxlength="200" value="' + esc(r.value) + '" placeholder="' + (r.op === 'in' ? 'a, b, c' : 'value') + '" aria-label="Value">';
      h += '<div class="row g-2 mb-2 align-items-center"><div class="col-12 col-md-4"><select class="form-select form-select-sm" data-path="' + p + '.field" data-bind="field" aria-label="Field">' + fieldSel + '</select></div>' +
        '<div class="col-5 col-md-3"><select class="form-select form-select-sm" data-path="' + p + '.op" data-bind="val" aria-label="Operator">' + ops.map(function (o) { return '<option value="' + o + '"' + (o === r.op ? ' selected' : '') + '>' + opLabel(o) + '</option>'; }).join('') + '</select></div>' +
        '<div class="col-5 col-md-4">' + valueInput + '</div><div class="col-2 col-md-1"><button type="button" class="btn btn-sm btn-outline-danger" data-act="row-del" data-path="' + path + '" data-i="' + i + '" aria-label="Remove condition">×</button></div></div>';
    });
    h += '<button type="button" class="btn btn-sm btn-outline-secondary me-1" data-act="row-add" data-path="' + path + '">+ Condition</button><button type="button" class="btn btn-sm btn-outline-secondary" data-act="win-add" data-path="' + path + '">+ Business hours</button>';
    return h;
  }
  function opLabel(o) { return { eq: 'is', neq: 'is not', gt: 'is greater than', gte: 'is at least', lt: 'is less than', lte: 'is at most', contains: 'contains', not_contains: 'does not contain', starts_with: 'starts with', 'in': 'is one of', is_empty: 'is empty', is_not_empty: 'is not empty' }[o] || o; }

  // ---------- state ----------
  var state = { name: '', reEntry: 'AfterCompletion', cooldownHours: 0, maxStartsPerLeadPer30Days: 3, note: '', triggers: [], steps: [] };
  function load(def) {
    if (!def) { state.triggers = [{ eventType: 'lead.created', cond: { mode: 'all', rows: [] }, inactiveDays: null }]; state.steps = [newStep('Wait')]; return; }
    state.reEntry = def.reEntry; state.cooldownHours = def.cooldownHours; state.maxStartsPerLeadPer30Days = def.maxStartsPerLeadPer30Days; state.note = def.note || '';
    state.triggers = (def.triggers || []).map(function (t) { return { eventType: t.eventType, cond: fromCond(t.condition), inactiveDays: t.inactiveDays }; });
    state.steps = (def.steps || []).map(function (s) { return { key: s.key, type: s.type, name: s.name, config: cfgIn(s.type, s.config || {}), runIf: fromCond(s.condition), onFailure: s.onFailure, maxRetries: s.maxRetries, hasRunIf: !!s.condition }; });
  }
  function nextKey() { var n = 1; var keys = state.steps.map(function (s) { return s.key; }); while (keys.indexOf('s' + n) >= 0) n++; return 's' + n; }
  function typeInfo(t) { return catalog.actions.find(function (a) { return a.type === t; }); }
  function newStep(type) {
    var c = {};
    if (type === 'Wait') c = { amount: 1, unit: 'days' }; if (type === 'CreateFollowUp') c = { dueInHours: 24, note: '', assignToOwner: true };
    if (type === 'NotifyUser') c = { target: 'owner', title: '', message: '' }; if (type === 'Branch') c = { cond: { mode: 'all', rows: [newRow()] }, then: '', else: '' };
    if (type === 'UpdateLeadFields') c = { field: catalog.leadTextFields[0], value: '' }; if (type === 'ScheduleDemo') c = { withinDays: 7 };
    return { key: nextKey(), type: type, name: typeInfo(type).title, config: c, runIf: { mode: 'all', rows: [] }, onFailure: type === 'SendMessage' ? 'Stop' : 'Retry', maxRetries: 2, hasRunIf: false };
  }
  function cfgIn(type, c) { // stored config -> editable config
    if (type === 'Wait') { var m = c.minutes || 1; return m % 1440 === 0 ? { amount: m / 1440, unit: 'days' } : m % 60 === 0 ? { amount: m / 60, unit: 'hours' } : { amount: m, unit: 'minutes' }; }
    if (type === 'Branch') return { cond: fromCond(c.condition), then: c.then || '', else: c.else || '' };
    return JSON.parse(JSON.stringify(c));
  }
  function cfgOut(s) {
    var c = s.config;
    if (s.type === 'Wait') return { minutes: Math.round(Number(c.amount) * (c.unit === 'days' ? 1440 : c.unit === 'hours' ? 60 : 1)) };
    if (s.type === 'Branch') { var o = { condition: toCond(c.cond) }; if (c.then) o.then = c.then; if (c.else) o.else = c.else; return o; }
    if (s.type === 'CreateFollowUp') return { dueInHours: Number(c.dueInHours), note: c.note, assignToOwner: !!c.assignToOwner };
    if (s.type === 'ScheduleDemo') return { withinDays: Number(c.withinDays) };
    if (s.type === 'AssignLead') return c.unassign ? { unassign: true } : { userId: c.userId };
    return c;
  }
  function serialize() {
    return JSON.stringify({
      reEntry: state.reEntry, cooldownHours: Number(state.cooldownHours) || 0, maxStartsPerLeadPer30Days: Number(state.maxStartsPerLeadPer30Days) || 0, note: state.note || null,
      triggers: state.triggers.map(function (t) { var o = { eventType: t.eventType }; var c = toCond(t.cond); if (c) o.condition = c; if (t.eventType === 'lead.inactive') o.inactiveDays = Number(t.inactiveDays) || 7; return o; }),
      steps: state.steps.map(function (s) { var o = { key: s.key, type: s.type, name: s.name, config: cfgOut(s), onFailure: s.onFailure, maxRetries: Number(s.maxRetries) || 0 }; if (s.hasRunIf) { var c = toCond(s.runIf); if (c) o.condition = c; } return o; }),
    });
  }

  // ---------- rendering ----------
  function preview(text) { return String(text || '').replace(/\{\{\s*([A-Za-z_]\w*)\s*(?:\|([^{}|]*))?\}\}/g, function (m, v, fb) { var x = SAMPLE[v.toLowerCase()]; return x ? x : (fb ? fb.trim() : '⟦' + v + '⟧'); }); }
  function stepFields(s, i) {
    var c = s.config, p = 'steps.' + i + '.config.';
    var bind = function (k, label, type, extra) { return '<div class="col-12 col-md-6"><label class="form-label small">' + label + '</label><input class="form-control form-control-sm" type="' + (type || 'text') + '" data-path="' + p + k + '" data-bind="val" value="' + esc(c[k]) + '" ' + (extra || '') + '></div>'; };
    var sel = function (k, label, list, blank) { return '<div class="col-12 col-md-6"><label class="form-label small">' + label + '</label><select class="form-select form-select-sm" data-path="' + p + k + '" data-bind="val">' + opts(list, c[k], blank || 'Choose…') + '</select></div>'; };
    var vars = '<div class="small text-muted">Variables: ' + catalog.templateVariables.map(function (v) { return '{{' + v + '}}'; }).join(' ') + ' · fallback: {{city|your city}}</div>';
    switch (s.type) {
      case 'SendMessage': {
        var t = catalog.templates.find(function (x) { return x.value === c.templateId; });
        return sel('templateId', 'Approved message template', catalog.templates) + sel('channelAccountId', 'Send from WhatsApp account', catalog.accounts) +
          (t ? '<div class="col-12"><div class="border rounded p-2 bg-light small"><div class="text-muted">Template: ' + esc(t.detail) + '</div><div class="mt-1"><strong>Preview with sample data</strong> (' + esc(SAMPLE.name) + '): ' + esc(preview(t.detail)) + '</div></div></div>' : '') +
          '<div class="col-12 small text-muted">Sent only to contacts with recorded marketing consent who are not suppressed or opted out. This is checked again when the step runs and again before the message leaves.</div>';
      }
      case 'Wait': return '<div class="col-6 col-md-3"><label class="form-label small">Wait for</label><input type="number" min="1" class="form-control form-control-sm" data-path="' + p + 'amount" data-bind="val" value="' + esc(c.amount) + '"></div><div class="col-6 col-md-3"><label class="form-label small">Unit</label><select class="form-select form-select-sm" data-path="' + p + 'unit" data-bind="val">' + opts(['minutes', 'hours', 'days'], c.unit) + '</select></div>';
      case 'AssignLead': return '<div class="col-12 col-md-6"><label class="form-label small">Assign to</label><select class="form-select form-select-sm" data-path="' + p + 'userId" data-bind="assign">' + opts(catalog.users, c.unassign ? '__none' : c.userId, 'Choose…').replace('</select>', '') + '<option value="__none"' + (c.unassign ? ' selected' : '') + '>Nobody (remove the owner)</option></select></div>';
      case 'ChangeLeadStatus': return sel('status', 'New status', catalog.statuses);
      case 'ChangeLeadStage': return sel('stage', 'New stage', catalog.stages);
      case 'CreateFollowUp': return bind('dueInHours', 'Due in (hours)', 'number', 'min="1" max="720"') + '<div class="col-12 col-md-6"><div class="form-check mt-4"><input class="form-check-input" type="checkbox" data-path="' + p + 'assignToOwner" data-bind="chk"' + (c.assignToOwner ? ' checked' : '') + '> <label class="form-check-label small">Assign to the lead\'s owner</label></div></div><div class="col-12"><label class="form-label small">Task</label><textarea class="form-control form-control-sm" rows="2" maxlength="500" data-path="' + p + 'note" data-bind="val">' + esc(c.note) + '</textarea>' + vars + '<div class="small mt-1"><strong>Preview:</strong> ' + esc(preview(c.note)) + '</div></div>';
      case 'NotifyUser': return sel('target', 'Notify', [{ value: 'owner', label: 'The lead\'s owner' }].concat(catalog.users)) + bind('title', 'Title', 'text', 'maxlength="150"') + '<div class="col-12"><label class="form-label small">Message</label><textarea class="form-control form-control-sm" rows="2" maxlength="1000" data-path="' + p + 'message" data-bind="val">' + esc(c.message) + '</textarea>' + vars + '<div class="small mt-1"><strong>Preview:</strong> ' + esc(preview(c.title)) + ' — ' + esc(preview(c.message)) + '</div></div>';
      case 'EscalateToHuman': return bind('reason', 'Reason shown to the team', 'text', 'maxlength="200"');
      case 'UpdateLeadFields': return sel('field', 'Field', catalog.leadTextFields) + bind('value', 'New value', 'text', 'maxlength="100"');
      case 'AddTag': return bind('tag', 'Tag', 'text', 'maxlength="50"');
      case 'ScheduleDemo': return '<div class="col-12"><div class="alert alert-warning py-2 mb-2">Demo scheduling arrives in Phase 10. ' + (catalog.demoSchedulingConnected ? 'It is connected.' : 'It is not connected, so a workflow with this step cannot be activated yet.') + '</div></div>' + bind('withinDays', 'Look for a slot within (days)', 'number', 'min="1" max="14"');
      case 'Stop': return bind('reason', 'Reason (optional)', 'text', 'maxlength="200"');
      case 'Branch': {
        var others = state.steps.filter(function (x) { return x.key !== s.key; }).map(function (x) { return { value: x.key, label: x.name + ' (' + x.key + ')' }; });
        var targets = [{ value: '', label: 'Continue with the next step' }, { value: 'end', label: 'End the run' }].concat(others);
        return '<div class="col-12">' + condHtml('steps.' + i + '.config.cond', c.cond, 'If…') + '</div><div class="col-12 col-md-6"><label class="form-label small">If yes, go to</label><select class="form-select form-select-sm" data-path="' + p + 'then" data-bind="val">' + opts(targets, c.then) + '</select></div><div class="col-12 col-md-6"><label class="form-label small">Otherwise go to</label><select class="form-select form-select-sm" data-path="' + p + 'else" data-bind="val">' + opts(targets, c.else) + '</select></div>';
      }
    }
    return '';
  }
  function render() {
    var h = '<div class="card mb-3"><div class="card-header">1 · Starts when</div><div class="card-body">';
    state.triggers.forEach(function (t, i) {
      var info = catalog.triggers.find(function (x) { return x.key === t.eventType; });
      h += '<div class="border rounded p-3 mb-2"><div class="row g-2 align-items-end"><div class="col-12 col-md-6"><label class="form-label small">Trigger</label><select class="form-select" data-path="triggers.' + i + '.eventType" data-bind="val">' +
        catalog.triggers.map(function (x) { return '<option value="' + x.key + '"' + (x.key === t.eventType ? ' selected' : '') + '>' + esc(x.title) + (x.available ? '' : ' — available when Phase 10 ships') + '</option>'; }).join('') + '</select></div>' +
        (t.eventType === 'lead.inactive' ? '<div class="col-6 col-md-3"><label class="form-label small">Days without activity</label><input type="number" min="1" max="365" class="form-control" data-path="triggers.' + i + '.inactiveDays" data-bind="val" value="' + esc(t.inactiveDays || 7) + '"></div>' : '') +
        '<div class="col-6 col-md-3 text-end">' + (state.triggers.length > 1 ? '<button type="button" class="btn btn-sm btn-outline-danger" data-act="trig-del" data-i="' + i + '">Remove trigger</button>' : '') + '</div></div>' +
        '<div class="small text-muted mt-1">' + esc(info ? info.description : '') + '</div>' + (info && !info.available ? '<div class="alert alert-warning py-1 my-2 small">' + esc(info.unavailableReason) + '</div>' : '') +
        '<div class="mt-2">' + condHtml('triggers.' + i + '.cond', t.cond, 'Only start when… (optional)') + '</div></div>';
    });
    h += '<button type="button" class="btn btn-sm btn-outline-secondary" data-act="trig-add">+ Another trigger</button></div></div>';

    h += '<div class="card mb-3"><div class="card-header">2 · Steps (they run top to bottom)</div><div class="card-body">';
    if (state.steps.length === 0) h += '<div class="text-muted mb-2">No steps yet. Add the first one below.</div>';
    state.steps.forEach(function (s, i) {
      var info = typeInfo(s.type);
      h += '<div class="border rounded p-3 mb-2" data-step="' + i + '"><div class="d-flex justify-content-between gap-2 mb-2"><div><span class="badge text-bg-secondary me-1">' + (i + 1) + '</span><strong>' + esc(info.title) + '</strong> <span class="small text-muted">' + esc(info.description) + '</span></div>' +
        '<div class="btn-group btn-group-sm text-nowrap"><button type="button" class="btn btn-outline-secondary" data-act="up" data-i="' + i + '"' + (i === 0 ? ' disabled' : '') + ' aria-label="Move up">↑</button><button type="button" class="btn btn-outline-secondary" data-act="down" data-i="' + i + '"' + (i === state.steps.length - 1 ? ' disabled' : '') + ' aria-label="Move down">↓</button><button type="button" class="btn btn-outline-danger" data-act="del" data-i="' + i + '" aria-label="Remove step">Remove</button></div></div>' +
        '<div class="row g-2"><div class="col-12 col-md-6"><label class="form-label small">Step name</label><input class="form-control form-control-sm" maxlength="100" data-path="steps.' + i + '.name" data-bind="val" value="' + esc(s.name) + '"></div>' + stepFields(s, i) + '</div>' +
        '<details class="mt-2"' + (s.hasRunIf || s.onFailure !== 'Retry' ? ' open' : '') + '><summary class="small">Conditions and failure handling</summary><div class="mt-2"><div class="form-check mb-2"><input class="form-check-input" type="checkbox" data-path="steps.' + i + '.hasRunIf" data-bind="chk"' + (s.hasRunIf ? ' checked' : '') + '> <label class="form-check-label small">Run this step only if…</label></div>' + (s.hasRunIf ? condHtml('steps.' + i + '.runIf', s.runIf, 'Run only if') : '') +
        '<div class="row g-2 mt-1"><div class="col-12 col-md-6"><label class="form-label small">If the step fails</label><select class="form-select form-select-sm" data-path="steps.' + i + '.onFailure" data-bind="val"><option value="Retry"' + (s.onFailure === 'Retry' ? ' selected' : '') + '>Retry, then stop the run</option><option value="Skip"' + (s.onFailure === 'Skip' ? ' selected' : '') + '>Skip it and continue</option><option value="Stop"' + (s.onFailure === 'Stop' ? ' selected' : '') + '>Stop the run</option></select></div>' +
        (s.onFailure === 'Retry' ? '<div class="col-6 col-md-3"><label class="form-label small">Retries (0-5)</label><input type="number" min="0" max="5" class="form-control form-control-sm" data-path="steps.' + i + '.maxRetries" data-bind="val" value="' + esc(s.maxRetries) + '"></div>' : '') + '</div></div></details></div>';
    });
    h += '<div class="input-group" style="max-width:34rem"><select id="wf-add-type" class="form-select" aria-label="Action to add">' + catalog.actions.map(function (a) { return '<option value="' + a.type + '">' + esc(a.title) + (a.available ? '' : ' (Phase 10)') + '</option>'; }).join('') + '</select><button type="button" class="btn btn-outline-primary" data-act="add">Add step</button></div></div></div>';

    h += '<div class="card"><div class="card-header">3 · Rules for repeat runs</div><div class="card-body row g-3">' +
      '<div class="col-12 col-md-4"><label class="form-label small">Can the same lead enter again?</label><select class="form-select form-select-sm" data-path="reEntry" data-bind="val">' + opts([{ value: 'Once', label: 'Never again (once per lead)' }, { value: 'AfterCompletion', label: 'Only after the previous run ended' }, { value: 'Always', label: 'Yes, runs may overlap' }], state.reEntry) + '</select></div>' +
      '<div class="col-6 col-md-4"><label class="form-label small">Cool-down between runs (hours, 0 = none)</label><input type="number" min="0" class="form-control form-control-sm" data-path="cooldownHours" data-bind="val" value="' + esc(state.cooldownHours) + '"></div>' +
      '<div class="col-6 col-md-4"><label class="form-label small">At most per lead in 30 days (0 = no cap)</label><input type="number" min="0" max="100" class="form-control form-control-sm" data-path="maxStartsPerLeadPer30Days" data-bind="val" value="' + esc(state.maxStartsPerLeadPer30Days) + '"></div></div></div>';
    var active = document.activeElement && document.activeElement.getAttribute && document.activeElement.getAttribute('data-path');
    root.innerHTML = h;
    if (active) { var el = root.querySelector('[data-path="' + active + '"]'); if (el && el.tagName !== 'SELECT') { el.focus(); try { var l = el.value.length; el.setSelectionRange(l, l); } catch (e) { } } }
  }

  // ---------- events ----------
  function getRef(path) { var parts = path.split('.'), o = state; for (var i = 0; i < parts.length - 1; i++) o = o[parts[i]]; return { o: o, k: parts[parts.length - 1] }; }
  function getAt(path) { var parts = path.split('.'), o = state; for (var i = 0; i < parts.length; i++) o = o[parts[i]]; return o; }
  root.addEventListener('change', function (e) {
    var t = e.target, path = t.getAttribute('data-path'), bind = t.getAttribute('data-bind'); if (!path) return;
    var r = getRef(path);
    if (bind === 'chk') { r.o[r.k] = t.checked; render(); return; }
    if (bind === 'day') { var row = getAt(path); var d = t.value; var ix = row.days.indexOf(d); if (t.checked && ix < 0) row.days.push(d); if (!t.checked && ix >= 0) row.days.splice(ix, 1); return; }
    if (bind === 'assign') { var c = getAt(path.replace(/\.userId$/, '')); if (t.value === '__none') { c.unassign = true; delete c.userId; } else { delete c.unassign; c.userId = t.value; } return; }
    if (bind === 'field') { r.o[r.k] = t.value; var f = field(t.value); r.o.op = catalog.operators[f.kind][0]; r.o.value = f.kind === 'Bool' ? 'true' : ''; render(); return; }
    r.o[r.k] = t.value;
    if (/eventType$|\.type$|\.op$|\.unit$|onFailure$|templateId$|\.mode$/.test(path) || bind === 'mode') render();
  });
  root.addEventListener('input', function (e) { var t = e.target, path = t.getAttribute('data-path'); if (!path || t.getAttribute('data-bind') !== 'val' || t.tagName === 'SELECT') return; var r = getRef(path); r.o[r.k] = t.value; });
  root.addEventListener('click', function (e) {
    var b = e.target.closest('[data-act]'); if (!b) return; var act = b.getAttribute('data-act'), i = Number(b.getAttribute('data-i')), path = b.getAttribute('data-path');
    if (act === 'add') { state.steps.push(newStep(document.getElementById('wf-add-type').value)); }
    else if (act === 'del') { var gone = state.steps.splice(i, 1)[0]; state.steps.forEach(function (s) { if (s.type === 'Branch') { if (s.config.then === gone.key) s.config.then = ''; if (s.config.else === gone.key) s.config.else = ''; } }); }
    else if (act === 'up' && i > 0) { var a = state.steps[i]; state.steps[i] = state.steps[i - 1]; state.steps[i - 1] = a; }
    else if (act === 'down' && i < state.steps.length - 1) { var a2 = state.steps[i]; state.steps[i] = state.steps[i + 1]; state.steps[i + 1] = a2; }
    else if (act === 'trig-add') { var used = state.triggers.map(function (t) { return t.eventType; }); var free = catalog.triggers.find(function (t) { return used.indexOf(t.key) < 0; }); if (free) state.triggers.push({ eventType: free.key, cond: { mode: 'all', rows: [] }, inactiveDays: null }); }
    else if (act === 'trig-del') state.triggers.splice(i, 1);
    else if (act === 'row-add') getAt(path).rows.push(newRow());
    else if (act === 'win-add') getAt(path).rows.push({ kind: 'window', days: ['mon', 'tue', 'wed', 'thu', 'fri'], from: '09:00', to: '17:00' });
    else if (act === 'row-del') getAt(path).rows.splice(i, 1);
    else if (act === 'cond-clear') { var holder = getRef(path); holder.o[holder.k] = { mode: 'all', rows: [] }; }
    render();
  });

  // ---------- check + submit ----------
  var problems = document.getElementById('wf-problems');
  function show(r) {
    var h = '';
    if (r.errors && r.errors.length) h += '<div class="alert alert-danger"><strong>Fix these before saving:</strong><ul class="mb-0">' + r.errors.map(function (x) { return '<li>' + (x.path ? esc(x.path) + ': ' : '') + esc(x.message) + '</li>'; }).join('') + '</ul></div>';
    if (r.warnings && r.warnings.length) h += '<div class="alert alert-warning"><ul class="mb-0">' + r.warnings.map(function (x) { return '<li>' + esc(x.message) + '</li>'; }).join('') + '</ul></div>';
    if (!h) h = '<div class="alert alert-success">The workflow is valid.</div>';
    problems.innerHTML = h;
  }
  document.getElementById('wf-check').addEventListener('click', function () {
    var token = form.querySelector('input[name="__RequestVerificationToken"]');
    var body = new URLSearchParams(); body.set('definitionJson', serialize());
    fetch(form.getAttribute('action').replace(/Save$/, 'Validate'), { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'RequestVerificationToken': token ? token.value : '' }, body: body.toString() })
      .then(function (r) { return r.json(); }).then(show).catch(function () { problems.innerHTML = '<div class="alert alert-danger">The check could not be run. Try again.</div>'; });
  });
  form.addEventListener('submit', function () { out.value = serialize(); });

  load(initial); render();
})();
