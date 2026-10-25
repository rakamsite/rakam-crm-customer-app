'use strict';
const $ = id => document.getElementById(id);
const pending = new Map();
let state, metaSignature = '', chosenSource = 'unknown', editingUuid = null, editingOccurred = null;
let toastTimer;
const fa = value => new Intl.NumberFormat('fa-IR').format(value);
const text = (tag, value, className = '') => {
  const node = document.createElement(tag); node.textContent = value ?? ''; node.className = className; return node;
};
function call(action, payload = {}) {
  return new Promise((resolve, reject) => {
    if (!window.chrome?.webview) { reject(new Error('این صفحه باید از داخل برنامه ویندوز باز شود.')); return; }
    const id = crypto.randomUUID();
    pending.set(id, { resolve, reject });
    window.chrome.webview.postMessage({ id, action, payload });
  });
}
window.chrome?.webview?.addEventListener('message', event => {
  const message = event.data;
  if (message.type === 'state') { render(message.data); return; }
  const task = pending.get(message.id);
  if (!task) return;
  pending.delete(message.id);
  message.ok ? task.resolve(message.result) : task.reject(new Error(message.error));
});
function toast(message, error = false) {
  clearTimeout(toastTimer); $('toast').textContent = message; $('toast').classList.toggle('error', error);
  $('toast').hidden = false;
  if (!error) toastTimer = setTimeout(() => $('toast').hidden = true, 5000);
}
$('toast').addEventListener('click', () => $('toast').hidden = true);
function page(id) {
  document.querySelectorAll('.page').forEach(node => node.hidden = node.id !== id);
  document.querySelectorAll('.nav').forEach(node => node.classList.toggle('active', node.dataset.page === id));
  $('page-title').textContent = { register: 'ثبت مراجعه جدید', history: 'سوابق مراجعان', contacts: 'مخاطبان CRM', office: 'اتوماسیون داخلی', settings: 'اتصال و پشتیبان' }[id];
}
document.querySelectorAll('.nav').forEach(node => node.addEventListener('click', () => page(node.dataset.page)));
function options(select, values, selected) {
  select.replaceChildren(...Object.entries(values).map(([value, label]) => {
    const option = text('option', label); option.value = value; return option;
  }));
  if (Object.hasOwn(values, selected)) select.value = selected;
}
function sourceOptions(meta) {
  $('source-options').replaceChildren(...Object.entries(meta.sources).map(([key, label]) => {
    const button = text('button', label, 'choice'); button.type = 'button'; button.dataset.key = key;
    button.setAttribute('aria-pressed', String(chosenSource === key));
    button.classList.toggle('selected', chosenSource === key);
    button.addEventListener('click', () => { chosenSource = key; sourceOptions(state.metadata); });
    return button;
  }));
}
function dynamicFields(meta) {
  const old = new Map([...document.querySelectorAll('[data-field]')].map(input => [input.dataset.field,
    input.multiple ? [...input.selectedOptions].map(x => x.value) : input.value]));
  $('dynamic-fields').replaceChildren();
  for (const field of meta.fields.filter(f => !['mobile', 'full_name'].includes(f.key))) {
    const label = text('label', field.label + (field.required ? ' *' : ''));
    let input;
    if (['select', 'multiselect', 'boolean'].includes(field.type) || field.options.length) {
      input = document.createElement('select');
      if (field.type === 'multiselect') input.multiple = true;
      else { const option = text('option', 'انتخاب کنید'); option.value = ''; input.append(option); }
      for (const option of field.options) {
        const node = text('option', option.label); node.value = option.value; input.append(node);
      }
    } else {
      input = document.createElement(field.type === 'textarea' ? 'textarea' : 'input');
      if (field.type === 'date') { input.placeholder = '۱۴۰۵/۰۷/۲۸'; input.dir = 'ltr'; }
      if (field.type === 'email') { input.type = 'email'; input.dir = 'ltr'; }
      if (field.type === 'number') input.inputMode = 'decimal';
    }
    input.dataset.field = field.key; input.dataset.fieldType = field.type;
    input.dataset.required = String(field.required); input.dataset.contactTypes = JSON.stringify(field.contact_types);
    const previous = old.get(field.key);
    if (previous !== undefined) {
      if (Array.isArray(previous)) [...input.options].forEach(x => x.selected = previous.includes(x.value));
      else input.value = previous;
    }
    label.append(input); $('dynamic-fields').append(label);
  }
  $('extra-fields').hidden = $('dynamic-fields').children.length === 0;
  updateContactHint();
}
function normalizePhone(value) {
  let number = value.replace(/[۰-۹]/g, c => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(c)))
    .replace(/[٠-٩]/g, c => String('٠١٢٣٤٥٦٧٨٩'.indexOf(c))).replace(/\D/g, '');
  if (number.length === 12 && number.startsWith('98')) number = '0' + number.slice(2);
  if (number.length === 10 && number.startsWith('9')) number = '0' + number;
  return number;
}
function updateContactHint() {
  if (!state) return;
  const phone = normalizePhone($('mobile').value);
  const contact = state.contacts.find(c => c.mobile === phone);
  $('contact-hint').classList.toggle('found', Boolean(contact));
  if (contact) {
    $('contact-hint').textContent = 'این شماره در مخاطبان دریافتی موجود است؛ مراجعه جدید به همان پرونده متصل می‌شود.';
    if (!$('full-name').value.trim()) $('full-name').value = contact.full_name;
  } else {
    const local = state.visits.find(x => x.visit.mobile === phone && phone);
    $('contact-hint').textContent = local ? 'این شماره قبلاً روی این دستگاه ثبت شده است؛ این بار یک مراجعه جدید ثبت می‌شود.' :
      'بدون شماره هم می‌توانید مراجعه را ثبت کنید؛ پرونده مخاطب در CRM ساخته نمی‌شود.';
  }
  const type = document.querySelector('[data-field="contact_type"]')?.value ?? '';
  $('full-name').required = !contact && Boolean(phone) && Boolean(state.metadata.fields.find(f => f.key === 'full_name')?.required);
  for (const input of document.querySelectorAll('[data-field]')) {
    const types = JSON.parse(input.dataset.contactTypes);
    const applies = !types.length || types.includes(type);
    input.parentElement.hidden = !applies;
    input.required = !contact && Boolean(phone) && applies && input.dataset.required === 'true';
  }
}
$('mobile').addEventListener('input', updateContactHint);
$('visit-form').addEventListener('invalid', event => {
  if (event.target.closest('#extra-fields')) $('extra-fields').open = true;
}, true);
$('dynamic-fields').addEventListener('change', updateContactHint);
function render(next) {
  if (!next) return;
  state = next;
  $('agent-name').textContent = state.metadata.user.name;
  $('avatar').textContent = state.metadata.user.name[0] || 'ر';
  const signature = JSON.stringify(state.metadata);
  if (signature !== metaSignature) {
    options($('entry-channel'), state.metadata.channels, $('entry-channel').value || 'in_person');
    options($('purpose'), state.metadata.purposes, $('purpose').value || 'vehicle');
    sourceOptions(state.metadata); dynamicFields(state.metadata); metaSignature = signature;
  }
  const counts = state.visits.reduce((result, row) => { result[row.state]++; return result; }, { queued: 0, synced: 0, error: 0 });
  for (const row of state.office?.queue || []) { if (row.state === 'queued') counts.queued++; if (row.state === 'error') counts.error++; }
  const today = new Date().toLocaleDateString('en-CA', { timeZone: 'Asia/Tehran' });
  $('stat-today').textContent = fa(state.visits.filter(x => new Date(x.visit.occurred_at).toLocaleDateString('en-CA', { timeZone: 'Asia/Tehran' }) === today).length);
  $('stat-pending').textContent = fa(counts.queued);
  $('stat-synced').textContent = fa(counts.synced);
  $('stat-errors').textContent = fa(counts.error);
  $('history-count').textContent = fa(state.visits.length);
  const badge = $('sync-badge');
  badge.className = 'sync-badge' + (counts.error ? ' error' : counts.queued || !state.configured ? ' waiting' : '');
  badge.querySelector('span').textContent = state.syncing ? 'در حال همگام‌سازی' : counts.error ? 'نیازمند بررسی' :
    counts.queued ? fa(counts.queued) + ' منتظر ارسال' : state.configured ? 'آماده ثبت' : 'ثبت آفلاین';
  $('connection-note').textContent = state.sync_message;
  $('sync-button').disabled = state.syncing;
  $('server-url').value = state.server;
  $('connection-state').textContent = state.configured ? 'اتصال ذخیره شده و محدودیت زمانی ندارد؛ ورود روزانه لازم نیست.' : 'بدون اتصال هم می‌توانید مراجعه ثبت کنید.';
  $('data-folder').textContent = state.data_folder;
  renderHistory(); renderContacts();
  document.querySelector('[data-page="office"]').hidden = !state.metadata.office;
  for (const id of ['register', 'history', 'contacts']) document.querySelector('[data-page="'+id+'"]').hidden = !state.metadata.sales;
  if (!state.metadata.sales && document.querySelector('.nav.active')?.dataset.page !== 'settings') page(state.metadata.office ? 'office' : 'settings');
  document.querySelector('.stats').hidden = !state.metadata.sales;
  window.renderOffice?.();
}
const date = value => new Date(value).toLocaleString('fa-IR', { timeZone: 'Asia/Tehran', dateStyle: 'medium', timeStyle: 'short' });
function renderHistory() {
  if (!state) return;
  const search = normalizePhone($('history-search').value) || $('history-search').value.trim().toLowerCase();
  const filter = $('history-state').value;
  const rows = state.visits.filter(x => (!filter || x.state === filter) &&
    (!search || [x.visit.full_name, x.visit.mobile, x.visit.subject].join(' ').toLowerCase().includes(search)));
  const nodes = rows.slice(0, 200).map(row => {
    const v = row.visit, record = text('div', '', 'record'), main = text('div', '', 'record-main');
    const heading = text('strong', v.full_name || 'بدون نام');
    heading.append(text('span', v.mobile || 'بدون شماره', 'record-phone'));
    main.append(heading, text('p', [state.metadata.sources[v.acquisition_source], state.metadata.channels[v.entry_channel], v.subject].filter(Boolean).join(' · ')),
      text('p', date(v.occurred_at)));
    if (row.error) main.append(text('p', row.error, 'error-text'));
    record.append(main, text('span', { queued: 'منتظر ارسال', synced: 'همگام‌شده', error: 'نیازمند بررسی' }[row.state], 'tag ' + row.state));
    const details = text('button', 'جزئیات', 'btn subtle'); details.addEventListener('click', () => detail(row)); record.append(details);
    if (row.state === 'error' && !row.server_id) {
      const edit = text('button', 'اصلاح', 'btn'); edit.addEventListener('click', () => editVisit(v)); record.append(edit);
    }
    const again = text('button', 'مراجعه جدید', 'btn'); again.addEventListener('click', () => newFor(v.full_name, v.mobile)); record.append(again);
    return record;
  });
  if (!nodes.length) nodes.push(text('div', 'موردی برای نمایش وجود ندارد.', 'empty'));
  if (rows.length > 200) nodes.push(text('p', '۲۰۰ مورد اول نمایش داده شده؛ برای یافتن مورد دلخواه جست‌وجو کنید.', 'hint'));
  $('history-list').replaceChildren(...nodes);
}
function renderContacts() {
  if (!state) return;
  const query = $('contact-search').value.trim();
  const digits = normalizePhone(query);
  const rows = state.contacts.filter(c => !query || c.full_name.includes(query) || (digits && c.mobile.includes(digits)));
  const nodes = rows.slice(0, 100).map(c => {
    const record = text('div', '', 'record'), main = text('div', '', 'record-main');
    main.append(text('strong', c.full_name), text('span', c.mobile, 'record-phone'));
    const button = text('button', 'ثبت مراجعه', 'btn'); button.addEventListener('click', () => newFor(c.full_name, c.mobile));
    record.append(main, button); return record;
  });
  if (!nodes.length) nodes.push(text('div', state.contacts.length ? 'مخاطبی پیدا نشد.' : 'پس از اتصال، مخاطبان مجاز شما اینجا نمایش داده می‌شوند.', 'empty'));
  if (rows.length > 100) nodes.push(text('p', '۱۰۰ مورد اول نمایش داده شده؛ از جست‌وجو استفاده کنید.', 'hint'));
  $('contacts-list').replaceChildren(...nodes);
}
function detail(row) {
  const v = row.visit, box = $('detail-body'); box.replaceChildren(text('h2', v.full_name || 'جزئیات مراجعه'));
  const grid = text('div', '', 'detail-grid');
  for (const [label, value] of [['موبایل', v.mobile || 'ثبت نشده'], ['زمان مراجعه', date(v.occurred_at)],
    ['منبع آشنایی', state.metadata.sources[v.acquisition_source]], ['روش مراجعه', state.metadata.channels[v.entry_channel]],
    ['موضوع', state.metadata.purposes[v.purpose]], ['پیگیری', v.follow_up_at ? date(v.follow_up_at) : 'تاریخ ثبت نشده']]) {
    const item = text('div', ''); item.append(text('small', label), text('span', value)); grid.append(item);
  }
  box.append(grid, text('p', v.subject), text('p', v.notes));
  if (row.error) box.append(text('p', row.error, 'error-text'));
  box.append(text('p', v.uuid, 'hint'));
  $('detail-dialog').showModal();
}
function reset() {
  $('visit-form').reset(); editingUuid = editingOccurred = null; chosenSource = 'unknown'; $('edit-note').hidden = true;
  $('follow-up-label').hidden = true; if (state) sourceOptions(state.metadata); updateContactHint();
}
function newFor(name, phone) { reset(); $('full-name').value = name || ''; $('mobile').value = phone || ''; page('register'); updateContactHint(); }
function editVisit(v) {
  reset(); editingUuid = v.uuid; editingOccurred = v.occurred_at; chosenSource = v.acquisition_source;
  for (const key of ['full_name', 'mobile', 'subject', 'notes', 'entry_channel', 'purpose']) $('visit-form').elements[key].value = v[key] || '';
  for (const input of document.querySelectorAll('[data-field]')) {
    const value = v.fields[input.dataset.field];
    if (input.multiple) [...input.options].forEach(o => o.selected = (value || []).includes(o.value));
    else input.value = input.dataset.fieldType === 'date' && value ? new Date(value).toLocaleDateString('fa-IR', { timeZone: 'Asia/Tehran' }) : value ?? '';
  }
  $('needs-follow-up').checked = v.needs_follow_up;
  if (v.follow_up_at) $('visit-form').elements.follow_up_date.value = new Date(v.follow_up_at).toLocaleDateString('fa-IR', { timeZone: 'Asia/Tehran' });
  $('follow-up-label').hidden = !v.needs_follow_up; $('edit-note').hidden = false;
  sourceOptions(state.metadata); updateContactHint(); page('register');
}
$('visit-form').addEventListener('submit', async event => {
  event.preventDefault(); const form = event.currentTarget; const data = new FormData(form);
  const fields = {};
  for (const input of document.querySelectorAll('[data-field]')) if (!input.parentElement.hidden)
    fields[input.dataset.field] = input.multiple ? [...input.selectedOptions].map(x => x.value) : input.value;
  const payload = { uuid: editingUuid || crypto.randomUUID(), full_name: data.get('full_name'), mobile: data.get('mobile'),
    acquisition_source: chosenSource, entry_channel: data.get('entry_channel'), purpose: data.get('purpose'),
    subject: data.get('subject'), notes: data.get('notes'), occurred_at: editingOccurred || new Date().toISOString(),
    needs_follow_up: data.has('needs_follow_up'), follow_up_at: null, fields };
  $('save-button').disabled = true;
  try {
    // Persian date conversion lives in the shared .NET domain, not browser locale parsing.
    const result = await call('save', { ...payload, follow_up_date: data.get('follow_up_date') });
    render(result); reset(); $('mobile').focus(); toast('مراجعه روی این دستگاه ذخیره شد.');
  } catch (error) { toast(error.message, true); }
  finally { $('save-button').disabled = false; }
});
$('needs-follow-up').addEventListener('change', () => $('follow-up-label').hidden = !$('needs-follow-up').checked);
$('reset-form').addEventListener('click', reset);
$('close-detail').addEventListener('click', () => $('detail-dialog').close());
$('history-search').addEventListener('input', renderHistory); $('history-state').addEventListener('change', renderHistory);
$('contact-search').addEventListener('input', renderContacts);
document.addEventListener('keydown', event => { if (event.ctrlKey && event.key === 'Enter' && !$('register').hidden) $('visit-form').requestSubmit(); });
for (const [id, action] of [['sync-button', 'sync'], ['export-button', 'export'], ['backup-button', 'backup'], ['restore-button', 'restore']])
  $(id).addEventListener('click', async () => { try { const result = await call(action); if (typeof result === 'string') toast(result); else if (result) render(result); } catch (error) { toast(error.message, true); } });
$('connection-form').addEventListener('submit', async event => {
  event.preventDefault(); $('pair-button').disabled = true;
  try {
    render(await call('pair', { server: $('server-url').value, token: $('connection-token').value }));
    $('connection-token').value = ''; toast('اتصال ذخیره شد؛ همگام‌سازی در پس‌زمینه شروع می‌شود.');
  } catch (error) { toast(error.message, true); }
  finally { $('pair-button').disabled = false; }
});
$('today').textContent = new Date().toLocaleDateString('fa-IR', { timeZone: 'Asia/Tehran', weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' });
call('init').then(render).catch(error => toast(error.message, true));

