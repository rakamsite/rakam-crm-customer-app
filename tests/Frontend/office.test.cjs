const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const html = fs.readFileSync('ui/index.html','utf8');
const script = fs.readFileSync('ui/app.js','utf8') + '\n' + fs.readFileSync('ui/office.js','utf8');
const wait = async fn => { for(let i=0;i<100;i++){if(fn())return;await new Promise(r=>setTimeout(r,5));}throw Error('Timed out'); };
async function fixture(manager=false,late=false){
 const {Window}=await import(require('node:url').pathToFileURL(require.resolve('happy-dom')).href);
 const window=new Window({url:'https://app.rakam.local/'});
 window.document.write(html.replace(/<script[^>]*><\/script>/g,''));
 const state={metadata:{user:{id:7,name:'کارمند آزمایشی'},sources:{unknown:'نامشخص'},channels:{in_person:'حضوری'},purposes:{vehicle:'خودرو'},fields:[],sales:false,office:true},visits:[],contacts:[],configured:true,server:'https://crm.test',sync_message:'قطع ارتباط؛ ثبت محلی ادامه دارد',data_folder:'test',office:{sections:{settings:[{id:1,user_id:7,manager,users:[{id:7,name:'کارمند'}]}],topics:[],entries:[],reports:[],announcements:[]},queue:[],last_sync:null}};
 let listener;const calls=[];
 window.chrome={webview:{addEventListener:(event,fn)=>{listener=fn},postMessage:message=>{
  calls.push(message);
  let result=structuredClone(state);
  if(message.action==='office.pick')result=[{id:'file-id',name:'رسید.txt',size:4}];
  else if(message.action==='office.save'){state.office.queue.push({command:message.payload,state:'queued',error:''});result=structuredClone(state);}
  queueMicrotask(()=>listener({data:{id:message.id,ok:true,result}}));
 }}};
 window.confirm=()=>true;
 if(late){
  state.office.sections.topics.push({id:1,title:'درخواست دریافت‌شده',next_step:'منتظر پاسخ مدیریت'});
  window.eval(fs.readFileSync('ui/app.js','utf8') + '\nsetTimeout(() => {\n' + fs.readFileSync('ui/office.js','utf8') + '\n}, 20);');
  await wait(()=>window.document.getElementById('office-list').textContent.includes('درخواست دریافت‌شده'));
 }else window.eval(script);
 await wait(()=>window.document.querySelector('[data-page=office]').hidden===false);
 return {window,state,calls,document:window.document};
}
test('office-only employee registers a durable request with attachments without sales controls',async()=>{
 const {window,state,calls,document}=await fixture();
 assert.equal(document.querySelector('[data-page=register]').hidden,true);
 assert.equal(document.getElementById('office').hidden,false);
 document.getElementById('office-new').click();
 const form=document.getElementById('office-form');
 assert.equal(form.querySelector('[name=assignee_id]'),null);
 form.elements.title.value='چک رسید';form.elements.body.value='متن محفوظ';
 form.dispatchEvent(new window.Event('input',{bubbles:true}));
 assert.equal(JSON.parse(window.localStorage.getItem('office-draft:7')).values.body,'متن محفوظ');
 [...form.querySelectorAll('button')].find(x=>x.textContent==='افزودن فایل').click();
 await wait(()=>document.querySelector('.office-file-list').textContent.includes('رسید.txt'));
 form.dispatchEvent(new window.Event('submit',{bubbles:true,cancelable:true}));
 await wait(()=>calls.some(x=>x.action==='office.save'));
 const command=calls.find(x=>x.action==='office.save').payload;
 assert.equal(command.kind,'topic.create');assert.equal(command.data.title,'چک رسید');assert.deepEqual([...command.files],['file-id']);
 assert.match(command.uuid,/^[0-9a-f-]{36}$/);assert.equal(command.target_id,null);
 await wait(()=>!document.getElementById('office-dialog').open);
 assert.equal(window.localStorage.getItem('office-draft:7'),null);
 assert.equal(state.office.queue.length,1);
 await window.happyDOM.abort();
});
test('manager sees assignment controls while employee reports stay collapsed in the list',async()=>{
 const {window,document}=await fixture(true);
 document.getElementById('office-new').click();
 assert.ok(document.querySelector('[name=assignee_id]'));
 assert.ok(document.querySelector('[name=due_at]'));
 document.getElementById('office-close').click();
 document.querySelector('[data-office-tab=reports]').click();
 assert.equal(document.getElementById('office-new').hidden,true);
 document.querySelector('[data-office-tab=announcements]').click();
 assert.equal(document.getElementById('office-new').textContent,'انتشار اطلاعیه');
 await window.happyDOM.abort();
});


test('cached office records render even when metadata arrives before the office script',async()=>{
 const {window,document}=await fixture(false,true);
 assert.ok(document.getElementById('office-list').textContent.includes('درخواست دریافت‌شده'));
 assert.equal(document.querySelector('[data-office-tab=topics]').getAttribute('aria-pressed'),'true');
 await window.happyDOM.abort();
});
