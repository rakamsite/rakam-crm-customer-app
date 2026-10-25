'use strict';
let officeTab = 'topics', officeEditing = null, officeFiles = [], officeDraftTimer;
const officeKinds = { 'topic.create': 'درخواست جدید', 'topic.act': 'پاسخ و پیگیری', 'report.save': 'گزارش روزانه', 'report.reply': 'پاسخ مدیریت', 'notice.create': 'اطلاعیه جدید', 'notice.update': 'ویرایش اطلاعیه' };
const officeSettings = () => state?.office?.sections?.settings?.[0] || {};
const officeRows = name => state?.office?.sections?.[name] || [];
const officeStatus = { decision: 'در حال بررسی', action: 'در حال اقدام', confirmation: 'منتظر تأیید', closed: 'خاتمه‌یافته', submitted: 'ارسال شده', seen: 'دیده شده', responded: 'پاسخ داده شده' };
const officeDraftKey = () => 'office-draft:' + (state?.metadata?.user?.id || 0);
function officeButton(label, fn, primary = false) {
  const button = text('button', label, 'btn' + (primary ? ' primary' : '')); button.type = 'button';
  button.addEventListener('click', fn); return button;
}
function officePanel(title) {
  $('office-dialog-title').textContent = title; $('office-content').replaceChildren();
  if (!$('office-dialog').open) $('office-dialog').showModal();
  return $('office-content');
}
async function officeSend(kind, target, version, data, files = [], uuid = crypto.randomUUID()) {
  const result = await call('office.save', { uuid, kind, target_id: target, version, data, files });
  render(result); return uuid;
}
async function officeRead(kind, row, version, data = {}) {
  const duplicate = state.office.queue.some(x => x.command.kind === kind && x.command.target_id === row.id && JSON.stringify(x.command.version) === JSON.stringify(version));
  if (duplicate) return;
  try { await officeSend(kind, row.id, version, data); } catch (error) { toast(error.message, true); }
}
window.renderOffice = function () {
  if (!state) return;
  const settings = officeSettings();
  for (const tab of document.querySelectorAll('[data-office-tab]')) {
    tab.setAttribute('aria-pressed', String(tab.dataset.officeTab === officeTab));
    tab.classList.toggle('primary',tab.dataset.officeTab === officeTab);
  }
  $('office-new').textContent = officeTab === 'topics' ? 'درخواست جدید' : officeTab === 'reports' ? 'ثبت گزارش روزانه' : 'انتشار اطلاعیه';
  $('office-new').hidden = !state.metadata.office || (officeTab === 'announcements' && !settings.manager) || (officeTab === 'reports' && settings.manager);
  $('office-last-sync').textContent = state.office?.last_sync ? 'آخرین دریافت: ' + date(state.office.last_sync) + ' · هنگام قطعی، آخرین اطلاعات دریافت‌شده نمایش داده می‌شود.' : 'پس از اولین اتصال، درخواست‌ها و وظایف شما اینجا دریافت می‌شود.';
  const queue = state.office?.queue || [];
  $('office-queue').hidden = queue.length === 0;
  $('office-queue').replaceChildren(text('h3', 'ارسال‌های این دستگاه'));
  for (const row of queue) {
    const box = text('div', '', 'office-pending');
    box.append(text('strong', row.command.data.title || officeKinds[row.command.kind] || 'ثبت مشاهده'), text('span', row.state === 'error' ? 'نیازمند بررسی' : row.state === 'delivered' ? 'ارسال شد؛ دریافت آخرین اطلاعات در انتظار اتصال' : 'منتظر ارسال', 'tag ' + row.state));
    if (row.error) box.append(text('p', row.error, 'error-text'));
    if (row.state === 'error' && officeKinds[row.command.kind]) box.append(officeButton('بررسی و اصلاح', () => officeRetry(row)));
    if (row.state === 'error') box.append(officeButton('حذف از صف', async () => {
      if (!confirm('این ثبت ناموفق از صف حذف شود؟')) return;
      try { render(await call('office.dismiss', { uuid: row.command.uuid })); } catch (e) { toast(e.message, true); }
    }));
    $('office-queue').append(box);
  }
  const query = $('office-search').value.trim();
  const items = officeRows(officeTab).filter(row => !query || [row.title, row.body, row.user_name, row.completed_work, ...(officeTab === 'topics' ? officeRows('entries').filter(x => x.topic_id === row.id).map(x => x.body) : [])].filter(Boolean).join(' ').includes(query));
  const list = items.slice().sort((a,b) => b.id-a.id).map(row => {
    const box = text('div', '', 'record'), main = text('div', '', 'record-main');
    main.append(text('strong', row.title || ('گزارش ' + row.user_name)), text('p', officeTab === 'topics' ? row.next_step : officeTab === 'reports' ? officeStatus[row.status] : row.is_read || row.created_by_user_id === state.metadata.user.id ? 'مطالعه شده' : 'خوانده‌نشده'));
    if (officeTab === 'topics') main.append(text('small', 'ثبت‌کننده: ' + row.creator_name + ' · مسئول: ' + (row.assignee_name || 'مدیریت')));
    if (officeTab === 'reports') main.append(text('small', new Date(row.report_date).toLocaleDateString('fa-IR', { timeZone: 'Asia/Tehran' })));
    box.append(main, officeButton('مشاهده', () => officeOpen(row))); return box;
  });
  $('office-list').replaceChildren(...(list.length ? list : [text('p', 'موردی برای نمایش وجود ندارد.', 'empty')]));
};
function officeOpen(row) {
  const panel = officePanel(row.title || 'گزارش ' + row.user_name);
  if (officeTab === 'topics') {
    for (const entry of officeRows('entries').filter(x => x.topic_id === row.id)) {
      const item = text('article', '', 'office-entry');
      item.append(text('strong', entry.actor_name), text('small', date(entry.created_at)), text('p', entry.body || ({assign:'ارجاع کار',result:'اعلام انجام کار',close:'خاتمه درخواست',reopen:'بازگشایی',return:'درخواست تکمیل کار'}[entry.kind] || 'ثبت درخواست')));
      for (const file of entry.attachments || []) item.append(officeButton('دریافت ' + file.name, async () => {
        try { await call('office.download', file); } catch (e) { toast(e.message, true); }
      }));
      panel.append(item);
    }
    if (row.status !== 'closed') {
      panel.append(officeButton('ارسال پاسخ', () => officeForm('topic.act', row, { action: 'message' }), true));
      if (row.status === 'action' && row.assignee_id === state.metadata.user.id) panel.append(officeButton('اعلام انجام کار', () => officeForm('topic.act', row, { action: 'result' })));
      if (officeSettings().manager) {
        panel.append(officeButton('ارجاع به همکار', () => officeForm('topic.act', row, { action: 'assign' })));
        if (row.status === 'confirmation') panel.append(officeButton('درخواست تکمیل کار', () => officeForm('topic.act', row, { action: 'return' })));
        panel.append(officeButton(row.status === 'confirmation' ? 'تأیید و خاتمه' : 'پاسخ و خاتمه', () => officeForm('topic.act', row, { action: 'close' })));
      }
    } else if (officeSettings().manager) panel.append(officeButton('بازگشایی درخواست', () => officeForm('topic.act', row, { action: 'reopen' })));
    if ((row.last_read_entry_id || 0) < row.last_entry_id) void officeRead('topic.read', row, row.last_entry_id);
  } else if (officeTab === 'reports') {
    for (const [key,label] of [['completed_work','کارهای انجام‌شده'],['unfinished_work','کارهای نیمه‌تمام'],['problems','مشکلات'],['tomorrow_plan','برنامه فردا'],['manager_response','پاسخ مدیریت']]) {
      if (row[key]) panel.append(text('strong',label),text('p',row[key]));
    }
    if (officeSettings().manager) panel.append(officeButton('ثبت پاسخ', () => officeForm('report.reply', row), true));
    else panel.append(officeButton('ویرایش گزارش', () => officeForm('report.save', row, row)));
    void officeRead('report.read', row, row.revision, { revision: row.revision });
  } else {
    panel.append(text('p', row.body),text('p', 'منتشرکننده: ' + row.created_by_name));
    if (row.created_by_user_id !== state.metadata.user.id && !row.is_read)
      panel.append(officeButton('خواندم', async () => { await officeRead('notice.read',row,row.version); $('office-dialog').close(); },true));
    if (row.created_by_user_id === state.metadata.user.id || officeSettings().manager) panel.append(text('small','مطالعه‌شده توسط ' + fa(row.read_count) + ' نفر'));
    if (row.created_by_user_id === state.metadata.user.id) {
      panel.append(officeButton('ویرایش اطلاعیه',()=>officeForm('notice.update',row,row)), officeButton('حذف اطلاعیه',async()=>{
        if (!confirm('اطلاعیه حذف شود؟')) return;
        try { await officeSend('notice.delete',row.id,row.version,{}); $('office-dialog').close(); toast('حذف اطلاعیه در صف ارسال ثبت شد.'); } catch(e){toast(e.message,true);}
      }));
    }
  }
}
function officeForm(kind, row = null, values = {}, files = [], failed = null, uuid = crypto.randomUUID()) {
  officeEditing = { kind, target_id: row?.id || null, version: kind.startsWith('topic.') ? row?.last_entry_id ?? null : kind.startsWith('notice.') ? row?.version ?? null : row?.revision ?? null, failed, uuid };
  officeFiles = files.slice();
  const panel = officePanel(officeKinds[kind]);
  if (failed && row) {
    const current = document.createElement('details'); current.open = true;
    current.append(text('summary','آخرین وضعیت دریافت‌شده؛ پیش از ارسال بررسی کنید'));
    current.append(text('p',row.next_step || officeStatus[row.status] || row.title));
    if(row.assignee_name)current.append(text('p','مسئول فعلی: '+row.assignee_name));
    const recent = kind.startsWith('topic.') ? officeRows('entries').filter(x=>x.topic_id===row.id).slice(-3).map(x=>x.body).filter(Boolean) : [row.body,row.completed_work,row.manager_response].filter(Boolean);
    for(const body of recent)current.append(text('p',body));
    panel.append(current);
  }
  const form = document.createElement('form'); form.id = 'office-form'; form.className='office-form';
  const add = (key,label,type='textarea',required=false,selected='') => {
    const wrap=text('label',label);let input;
    if (type==='select') {
      input=document.createElement('select');input.name=key;
      input.append(Object.assign(text('option','انتخاب کنید'),{value:''}));
      for (const person of officeSettings().users || []) input.append(Object.assign(text('option',person.name),{value:String(person.id)}));
    } else { input=document.createElement(type==='textarea'?'textarea':'input');input.name=key;if(type==='checkbox')input.type=type;if(type==='date'){input.dir='ltr';input.placeholder='۱۴۰۵/۰۸/۰۳';} }
    input.required=required;
    if(type==='checkbox')input.checked=Boolean(values[key] || selected);else input.value=values[key] ?? selected;
    if(type==='textarea'){input.rows=4;input.maxLength=kind==='report.save'||kind==='report.reply'?10000:15000;}
    if(type==='text')input.maxLength=180;
    wrap.append(input);form.append(wrap);return input;
  };
  if(kind==='topic.create'||kind.startsWith('notice.'))add('title','عنوان','text',true);
  if(kind.startsWith('topic.')) {
    if(kind==='topic.act') {const action=document.createElement('input');action.type='hidden';action.name='action';action.value=values.action || 'message';form.append(action);}
    const action=values.action || 'message';
    add('body',kind==='topic.create'?'متن درخواست':'متن پاسخ یا توضیح اقدام','textarea',kind==='topic.create'||['message','result','return'].includes(action)||(action==='close'&&row?.status!=='confirmation'&&row?.kind!=='information'));
    if(officeSettings().manager&&(kind==='topic.create'||action==='assign')) {
      add('assignee_id',kind==='topic.create'?'واگذاری به همکار (اختیاری)':'مسئول اجرا','select',action==='assign',row?.assignee_id || '');
      add('due_at','مهلت انجام (اختیاری)','date',false);
    }
    const help=text('p','پیوست‌ها ابتدا روی دستگاه کپی می‌شوند و پس از اتصال، یکی‌یکی ارسال می‌شوند.','hint');form.append(help);
    form.append(officeButton('افزودن فایل',async()=>{try{officeFiles.push(...await call('office.pick'));drawFiles();saveOfficeDraft();}catch(e){toast(e.message,true);}}));
    const list=text('div','','office-file-list');form.append(list);
    function drawFiles(){list.replaceChildren(...officeFiles.map(file=>{const item=text('div',file.name);item.append(officeButton('حذف',async()=>{await call('office.remove',{id:file.id});officeFiles=officeFiles.filter(x=>x.id!==file.id);drawFiles();saveOfficeDraft();}));return item;}));}
    drawFiles();
  } else if(kind==='report.save') {
    const today=new Date().toLocaleDateString('fa-IR',{timeZone:'Asia/Tehran',year:'numeric',month:'2-digit',day:'2-digit'});
    const dateInput=add('report_date','تاریخ گزارش','date',true,row?.report_date?new Date(row.report_date).toLocaleDateString('fa-IR',{timeZone:'Asia/Tehran'}):today);
    if(row){dateInput.value=new Date(row.report_date).toLocaleDateString('fa-IR',{timeZone:'Asia/Tehran'});dateInput.readOnly=true;}
    for(const [key,label] of [['completed_work','کارهای انجام‌شده'],['unfinished_work','کارهای نیمه‌تمام'],['problems','مشکلات'],['tomorrow_plan','برنامه فردا']])add(key,label,'textarea',key==='completed_work');
    add('needs_manager_attention','نیازمند توجه مدیریت','checkbox');
  } else if(kind==='report.reply')add('manager_response','پاسخ مدیریت','textarea',true);
  else {
    add('body','متن اطلاعیه','textarea',true);
    const label=text('label','مخاطبان');const select=document.createElement('select');select.name='audience';
    for(const [value,title] of [['all','همه همکاران'],['selected','همکاران مشخص']])select.append(Object.assign(text('option',title),{value}));
    select.value=values.audience||'all';label.append(select);form.append(label);
    const recipients=document.createElement('div');recipients.className='office-recipients';
    for(const person of officeSettings().users || []) {
      const label=text('label',person.name);const input=document.createElement('input');input.type='checkbox';input.name='recipient';input.value=person.id;input.checked=(values.audience_user_ids||[]).includes(person.id);label.prepend(input);recipients.append(label);
    }
    const show=()=>{recipients.hidden=select.value!=='selected';};select.addEventListener('change',show);show();form.append(recipients);
  }
  const error=text('p','','error-text');error.setAttribute('role','alert');form.append(error);
  const submit=text('button','ذخیره و ارسال خودکار','btn primary');submit.type='submit';form.append(submit);
  form.addEventListener('input',saveOfficeDraft);form.addEventListener('change',saveOfficeDraft);
  form.addEventListener('submit',async event=>{
    event.preventDefault();if(!form.reportValidity())return;submit.disabled=true;error.textContent='';
    try{
      const data=Object.fromEntries(new FormData(form));
      if(data.assignee_id==='')delete data.assignee_id;else if(data.assignee_id)data.assignee_id=Number(data.assignee_id);
      if(kind==='report.save'){data.needs_manager_attention=Boolean(form.elements.needs_manager_attention.checked);if(row){data.report_id=row.id;data.revision=row.revision;}}
      if(kind==='report.reply')data.revision=row.revision;
      if(data.audience==='selected'){data.audience_user_ids=[...form.querySelectorAll('[name=recipient]:checked')].map(x=>Number(x.value));if(!data.audience_user_ids.length)throw Error('حداقل یک همکار را انتخاب کنید.');}delete data.recipient;
      await officeSend(kind,officeEditing.target_id,officeEditing.version,data,officeFiles.map(x=>x.id),officeEditing.uuid);
      if(failed)render(await call('office.dismiss',{uuid:failed}));
      localStorage.removeItem(officeDraftKey());officeEditing=null;$('office-dialog').close();toast('روی این دستگاه ذخیره شد؛ ارسال خودکار انجام می‌شود.');
    }catch(e){error.textContent=e.message;}finally{submit.disabled=false;}
  });
  panel.append(form);saveOfficeDraft();
}
function saveOfficeDraft(){
  if(!officeEditing||!$('office-form'))return;
  clearTimeout(officeDraftTimer);
  try{
    const form=$('office-form');const values=Object.fromEntries(new FormData(form));
    values.needs_manager_attention=Boolean(form.elements.needs_manager_attention?.checked);
    values.audience_user_ids=[...form.querySelectorAll('[name=recipient]:checked')].map(x=>Number(x.value));
    localStorage.setItem(officeDraftKey(),JSON.stringify({...officeEditing,values,files:officeFiles}));
  }catch{/* Saving the submitted operation does not depend on browser draft storage. */}
}
function officeRetry(row){
  const section=row.command.kind.startsWith('topic.')?'topics':row.command.kind.startsWith('report.')?'reports':'announcements';
  const latest=officeRows(section).find(x=>x.id===row.command.target_id);
  if(row.command.target_id&&!latest){toast('ابتدا همگام‌سازی کنید؛ این مورد ممکن است حذف شده یا از دسترس شما خارج شده باشد.',true);return;}
  toast('آخرین وضعیت را بررسی کنید؛ متن قبلی شما برای اصلاح باز شده است.');
  const files=row.command.files.map(id=>({id,name:'پیوست ذخیره‌شده'}));
  officeForm(row.command.kind,latest,row.command.data,files,row.command.uuid);
}
for(const button of document.querySelectorAll('[data-office-tab]'))button.addEventListener('click',()=>{officeTab=button.dataset.officeTab;window.renderOffice();});
$('office-search').addEventListener('input',()=>window.renderOffice());
$('office-new').addEventListener('click',()=>{
  try{const saved=JSON.parse(localStorage.getItem(officeDraftKey()));if(saved&&confirm('پیش‌نویس قبلی باز شود؟')){
    const section=saved.kind.startsWith('topic.')?'topics':saved.kind.startsWith('report.')?'reports':'announcements';
    const row=officeRows(section).find(x=>x.id===saved.target_id);
    if(saved.target_id&&!row){toast('پیش‌نویس محفوظ است؛ ابتدا همگام‌سازی کنید.',true);return;}
    officeForm(saved.kind,row,saved.values,saved.files,saved.failed,saved.uuid);return;
  }}catch{}
  officeForm(officeTab==='topics'?'topic.create':officeTab==='reports'?'report.save':'notice.create');
});
$('office-close').addEventListener('click',()=>{saveOfficeDraft();$('office-dialog').close();officeEditing=null;});
$('office-dialog').addEventListener('cancel',()=>{saveOfficeDraft();officeEditing=null;});
window.addEventListener('beforeunload',saveOfficeDraft);

// Metadata may arrive before this deferred script has loaded.
if (state) window.renderOffice();
