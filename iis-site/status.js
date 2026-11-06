const node=id=>document.getElementById(id);
const number=value=>Number(value||0).toLocaleString();
const date=value=>{const serialized=typeof value==='string'&&value.match(/^\/Date\((-?\d+)/);return new Date(serialized?Number(serialized[1]):value);};
const stamp=value=>value&&!Number.isNaN(date(value).valueOf())?date(value).toLocaleString():'Not recorded';
const count=(rows,state)=>Number((rows||[]).find(row=>row.state===state)?.n||0);
function state(id,label,kind){const element=node(id);element.textContent=label;element.className='state '+kind;}
const components=['worker','work','recovery','scan','downloads','issues'];
const busy={};
function show(component,data,checked){
 node(component+'-checked').textContent='Checked '+stamp(checked);
 if(component==='worker'){
  const worker=data||{},age=worker.updated_utc?Date.now()-date(worker.updated_utc).getTime():Infinity,stale=!Number.isFinite(age)||age>120000;
  node('worker-updated').textContent=stamp(worker.updated_utc);
  state('worker-state',stale?'Status is stale':worker.phase==='error'?'Worker reported an error':worker.phase==='scanning'?'Processing changes':worker.phase==='complete'?'Last pass completed':'Worker status: '+(worker.phase||'unknown'),stale||worker.phase==='error'?'bad':worker.phase==='scanning'?'busy':'ok');
  node('worker-message').textContent=worker.message||'No worker message recorded.';
  node('freshness').textContent=stale?'No update from the worker in the last 2 minutes. This does not confirm it is running.':'Worker reported an update within the last 2 minutes.';
 }else if(component==='work'){
  node('pending').textContent=number(count(data.work_items,'pending')+count(data.work_items,'leased'));
  node('failed').textContent=number(count(data.work_items,'failed'));
  node('dirty').textContent=number(data.dirty_index);
 }else if(component==='recovery'){
  const active=count(data,'planned')+count(data,'copied')+count(data,'linked'),errors=count(data,'error');
  node('operations').textContent=number(active+errors);node('operation-active').textContent=number(active);node('operation-errors').textContent=number(errors);
 }else if(component==='scan'){
  state('scan-state',data?String(data.state).replace(/^./,letter=>letter.toUpperCase()):'No scan recorded',data?.state==='failed'?'bad':data?.state==='running'?'busy':'ok');
  node('scan-detail').textContent=data?`Scan #${data.id} · started ${stamp(data.started_at)} · finished ${stamp(data.finished_at)} · ${number(data.files_processed)} files processed · ${number(data.errors)} errors recorded`:'No scan history was returned.';
 }else if(component==='downloads'){
  const item=data||{};state('download-state',item.phase?String(item.phase).replace(/^./,letter=>letter.toUpperCase()):'Idle',item.phase==='failed'?'bad':['queued','running'].includes(item.phase)?'busy':'ok');
  node('download-detail').textContent=item.phase&&item.phase!=='idle'?`${number(item.processed)} of ${number(item.total)} entries processed · ${number(item.saved)} saved · ${number(item.failed)} failed · updated ${stamp(item.updated_utc)}`:'No Gallery download job is active.';
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
  if(response.status===401){location.href='/auth/login';return;}
  const body=await response.json();if(!response.ok)throw Error(body.error||`HTTP ${response.status}`);
  show(component,body.data,body.checked_utc);
 }catch(error){node(component+'-checked').textContent='Update failed: '+error.message;}
 finally{busy[component]=false;}
}
node('refresh').addEventListener('click',()=>components.forEach(refresh));
components.forEach((component,index)=>{setTimeout(()=>refresh(component),index*150);setInterval(()=>refresh(component),15000+index*3000);});
