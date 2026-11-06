const node=id=>document.getElementById(id);
let authRedirecting=false;
window.galleryStatusAuthRedirect=()=>{if(authRedirecting)return;authRedirecting=true;location.replace('/auth/login?next=%2Fstatus.html')};
const number=value=>Number(value||0).toLocaleString();
const date=value=>{const serialized=typeof value==='string'&&value.match(/^\/Date\((-?\d+)/);return new Date(serialized?Number(serialized[1]):value);};
const stamp=value=>value&&!Number.isNaN(date(value).valueOf())?date(value).toLocaleString():'Not recorded';
const count=(rows,state)=>Number((rows||[]).find(row=>row.state===state)?.n||0);
function state(id,label,kind){const element=node(id);element.textContent=label;element.className='state '+kind;}
const components=['worker','inventory','work','moves','recovery','scan','downloads','services','issues'];
const busy={};
function show(component,data,checked){
 node(component+'-checked').textContent='Checked '+stamp(checked);
 if(component==='worker'){
  const worker=data||{},age=worker.updated_utc?Date.now()-date(worker.updated_utc).getTime():Infinity,stale=!Number.isFinite(age)||age>120000;
  node('worker-updated').textContent=stamp(worker.updated_utc);
  state('worker-state',stale?'No recent worker update':worker.phase==='error'?'Worker reported an error':Number(worker.pending_work)>0?`${number(worker.pending_work)} file jobs waiting`:Number(worker.recovery_total)>0?`Recovery pass: ${number(worker.recovery_processed)} of ${number(worker.recovery_total)}`:worker.phase==='complete'?'Last worker pass completed':'Current operation not recorded',stale||worker.phase==='error'?'bad':worker.phase==='scanning'?'busy':'ok');
  node('worker-message').textContent=`Last message: ${worker.message||'None recorded'}. File jobs waiting: ${number(worker.pending_work)}. Index changes waiting: ${number(worker.pending_index)}. Failed file jobs: ${number(worker.failed_count)}.`;
  node('freshness').textContent=stale?'No update from the worker in the last 2 minutes. This does not confirm it is running.':'Worker reported an update within the last 2 minutes.';
  const recovering=Number(worker.recovery_total||0)>0;node('recovery-progress').hidden=!recovering;node('recovery-progress-text').textContent=recovering?`File recovery pass: ${number(worker.recovery_processed)} of ${number(worker.recovery_total)} checked · ${number(worker.recovery_review)} need review`:'';if(recovering){node('recovery-progress').max=worker.recovery_total;node('recovery-progress').value=Math.min(worker.recovery_processed||0,worker.recovery_total)}
 }else if(component==='inventory'){
  node('saved-count').textContent=number(data.saved);node('total-count').textContent=number(data.records);node('collection-count').textContent=number(data.collections);
 }else if(component==='work'){
  node('pending').textContent=number(count(data.work_items,'pending')+count(data.work_items,'leased'));
  node('failed').textContent=number(count(data.work_items,'failed'));
  node('dirty').textContent=number(data.dirty_index);
  const kinds={hash_file:'Hash image files',match_file:'Match images and find duplicates',reconcile_file:'Reconcile file locations'};node('work-kinds').replaceChildren(...(data.tasks||[]).map(task=>{const row=document.createElement('div');row.className='row';const label=document.createElement('span');label.textContent=(kinds[task.task_kind]||task.task_kind)+' · '+(task.state==='leased'?'running':task.state);const value=document.createElement('strong');value.textContent=number(task.n);row.append(label,value);return row}));
 }else if(component==='moves'){
  node('moves-pending').textContent=number(count(data.counts,'pending'));node('moves-running').textContent=number(count(data.counts,'running'));node('moves-failed').textContent=number(count(data.counts,'failed'));
  const box=node('move-items');box.replaceChildren();if(!data.items?.length){const p=document.createElement('p');p.textContent='No queued, running, or failed collection moves.';box.append(p)}
  for(const job of data.items||[]){const section=document.createElement('section');section.className='row';const info=document.createElement('div');const title=document.createElement('strong');title.textContent=`${job.state==='pending'?'Queued':job.state==='running'?'Running':'Failed'} move #${job.id}: ${job.source_id||'entry '+job.entry_id} → ${job.target_collection}`;const detail=document.createElement('p');detail.textContent=`Files moved: ${number(job.processed)} of ${number(job.total)}${job.error?' · Reason: '+job.error:''}`;info.append(title,detail);section.append(info);if(job.state==='failed'){const retry=document.createElement('button');retry.type='button';retry.textContent='Retry move';retry.onclick=async()=>{retry.disabled=true;detail.textContent='Requesting retry…';try{const response=await fetch('/api/move-entry',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify({entry_id:job.entry_id,revision:job.current_revision,target_collection:job.target_collection})});const result=await response.json();if(!response.ok)throw Error(result.error||'Retry failed');detail.textContent='Retry queued.';refresh('moves')}catch(error){detail.textContent=error.message;retry.disabled=false}};section.append(retry)}box.append(section)}
  node('move-recent').replaceChildren(...(data.recent||[]).map(job=>{const li=document.createElement('li');li.textContent=`Move #${job.id} · entry ${job.source_id||job.entry_id} → ${job.target_collection} · completed ${stamp(job.completed_at)}`;return li}));
 }else if(component==='recovery'){
  const active=count(data,'planned')+count(data,'copied')+count(data,'linked'),errors=count(data,'error');
  node('operations').textContent=number(active+errors);node('operation-active').textContent=number(active);node('operation-errors').textContent=number(errors);
 }else if(component==='scan'){
  state('scan-state',data?String(data.state).replace(/^./,letter=>letter.toUpperCase()):'No scan recorded',data?.state==='failed'?'bad':data?.state==='running'?'busy':'ok');
  node('scan-detail').textContent=data?`Scan #${data.id} · started ${stamp(data.started_at)} · finished ${stamp(data.finished_at)} · ${number(data.files_processed)} files processed · ${number(data.errors)} errors recorded`:'No scan history was returned.';
 }else if(component==='downloads'){
  const item=data||{};state('download-state',item.phase?String(item.phase).replace(/^./,letter=>letter.toUpperCase()):'Idle',item.phase==='failed'?'bad':['queued','running'].includes(item.phase)?'busy':'ok');
  node('download-detail').textContent=item.phase&&item.phase!=='idle'?`${number(item.processed)} of ${number(item.total)} entries processed · ${number(item.saved)} saved · ${number(item.failed)} failed · updated ${stamp(item.updated_utc)}`:'No Gallery download job is active.';
  node('download-errors').replaceChildren(...(item.errors||[]).map(error=>{const li=document.createElement('li');li.textContent=error;return li}));
 }else if(component==='services'){
  node('wallhaven-state').textContent=data.wallhaven?'Available':'Unavailable';node('reddit-state').textContent=data.reddit?'Signed in':'Not signed in';
 }else{
  const issues=data?.issues||[];node('issue-summary').textContent=issues.length?`${number(issues.length)} issue examples reported by the worker. Full failed-job and recovery counts are above.`:'No issue examples in the latest worker status. Check the failed-job and recovery counts above.';
  node('issues').replaceChildren(...issues.map(issue=>{const item=document.createElement('li');item.textContent=[issue.file||issue.inventory,issue.error].filter(Boolean).join(': ');return item;}));
 }
}
async function refresh(component){
 if(busy[component])return;busy[component]=true;
 node(component+'-checked').textContent='Checking…';
 try{
  const response=await fetch('/api/system-status?component='+component,{headers:{'X-Gallery-Request':'1'},cache:'no-store'});
  if(response.status===401){window.galleryStatusAuthRedirect();return;}
  const body=await response.json();if(!response.ok)throw Error(body.error||`HTTP ${response.status}`);
  show(component,body.data,body.checked_utc);
 }catch(error){node(component+'-checked').textContent='Update failed: '+error.message;}
 finally{busy[component]=false;}
}
node('refresh').addEventListener('click',()=>components.forEach(refresh));
components.forEach((component,index)=>{setTimeout(()=>refresh(component),index*150);setInterval(()=>refresh(component),15000+index*3000);});
window.galleryStatusRefresh=refresh;
for(const button of document.querySelectorAll('[data-action]'))button.addEventListener('click',async()=>{const route=button.dataset.action,output=node(route==='retry-scan-issues'||route==='clear-scan-issues'?'issues-action-message':'maintenance-message');button.disabled=true;output.textContent='Requesting '+button.textContent.toLowerCase()+'…';try{const response=await fetch('/api/'+route,{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:'{}'});const result=await response.json();if(!response.ok)throw Error(result.error||'Request failed');output.textContent='Request accepted. Status will update as the worker reports progress.';refresh('worker');refresh('issues');}catch(error){output.textContent=error.message}finally{button.disabled=false}});
