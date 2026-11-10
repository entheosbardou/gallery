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
  state('worker-state',stale?'No recent worker update':worker.phase==='error'?'Worker reported an error':worker.message||'No current stage reported',stale||worker.phase==='error'?'bad':worker.phase==='scanning'?'busy':'ok');
  node('worker-message').textContent=`${worker.files_checked!=null?`${number(worker.files_checked)} saved files checked · `:''}${worker.total?`${number(worker.processed)} of ${number(worker.total)} processed · `:''}File jobs waiting: ${number(worker.pending_work)} · Index changes waiting: ${number(worker.pending_index)} · Failed file jobs: ${number(worker.failed_count)}.`;
  const pipeline=worker.visual_worker_limit!=null;node('pipeline-status').hidden=!pipeline;node('pipeline-status').textContent=pipeline?`Visual comparisons: ${number(worker.visual_active)} active of ${number(worker.visual_worker_limit)} allowed, ${number(worker.visual_waiting)} waiting${worker.visual_per_minute!=null?' · '+number(worker.visual_per_minute)+'/min completed':''}. File changes: ${number(worker.file_changes_active)} active of ${number(worker.file_change_worker_limit)} allowed, ${number(worker.file_changes_waiting)} waiting${worker.file_changes_per_minute!=null?' · '+number(worker.file_changes_per_minute)+'/min completed':''}. Indexing: ${number(worker.index_active)} active of ${number(worker.index_worker_limit)} workers, ${number(worker.pending_index)} changes waiting${worker.indexed_per_minute!=null?' · '+number(worker.indexed_per_minute)+'/min completed':''}.`:'';
  node('freshness').textContent=stale?'No update from the worker in the last 2 minutes. This does not confirm it is running.':'Worker reported an update within the last 2 minutes.';
  const recovering=Number(worker.recovery_total||0)>0;node('recovery-progress').hidden=!recovering;node('recovery-progress-text').textContent=recovering?`File recovery pass: ${number(worker.recovery_processed)} of ${number(worker.recovery_total)} checked · ${number(worker.recovery_review)} need review`:'';if(recovering){node('recovery-progress').max=worker.recovery_total;node('recovery-progress').value=Math.min(worker.recovery_processed||0,worker.recovery_total)}
 }else if(component==='inventory'){
  node('saved-count').textContent=number(data.saved);node('total-count').textContent=number(data.records);node('collection-count').textContent=number(data.collections);
 }else if(component==='work'){
  node('pending').textContent=number(count(data.work_items,'pending')+count(data.work_items,'leased'));
  node('failed').textContent=number(count(data.work_items,'failed'));
  node('failed-jobs-open').disabled=count(data.work_items,'failed')===0;
  node('dirty').textContent=number(data.dirty_index);
  const kinds={hash_file:'Hash image files',match_file:'Match images and find duplicates',reconcile_file:'Reconcile file locations'};node('work-kinds').replaceChildren(...(data.tasks||[]).map(task=>{const row=document.createElement('div');row.className='row';const label=document.createElement('span');label.textContent=(kinds[task.task_kind]||task.task_kind)+' · '+(task.state==='leased'?'claimed by worker':task.state);const value=document.createElement('strong');value.textContent=number(task.n);row.append(label,value);return row}));
 }else if(component==='moves'){
  node('moves-pending').textContent=number(count(data.counts,'pending'));node('moves-running').textContent=number(count(data.counts,'running'));node('moves-failed').textContent=number(count(data.counts,'failed'));
  const box=node('move-items');box.replaceChildren();if(!data.items?.length){const p=document.createElement('p');p.textContent='No queued, running, or failed collection moves.';box.append(p)}
  for(const job of data.items||[]){const section=document.createElement('section');section.className='row';const info=document.createElement('div');const title=document.createElement('strong');title.textContent=`${job.state==='pending'?'Queued':job.state==='running'?'Running':'Failed'} move #${job.id}: ${job.source_id||'entry '+job.entry_id} → ${job.target_collection}`;const detail=document.createElement('p');detail.textContent=`Files moved: ${number(job.processed)} of ${number(job.total)}${job.error?' · Reason: '+job.error:''}`;info.append(title,detail);section.append(info);if(job.state==='failed'){const retry=document.createElement('button');retry.type='button';retry.textContent='Retry move';retry.onclick=async()=>{retry.disabled=true;detail.textContent='Requesting retry…';try{const response=await fetch('/api/retry-failed-move',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify({id:job.id})});const result=await response.json();if(!response.ok)throw Error(result.error||'Retry failed');detail.textContent=result.message;refresh('moves')}catch(error){detail.textContent=error.message;retry.disabled=false}};section.append(retry)}box.append(section)}
  (data.items||[]).forEach((job,index)=>{if(job.state!=='failed')return;const section=box.children[index];if(!section)return;const button=document.createElement('button');button.type='button';button.textContent='Withdraw unstarted move';button.onclick=async()=>{button.disabled=true;const detail=section.querySelector('p');detail.textContent='Checking whether this move changed any files…';try{const response=await fetch('/api/withdraw-failed-move',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify({id:job.id})});const result=await response.json();if(!response.ok)throw Error(result.error||'Withdrawal failed');detail.textContent=result.message;refresh('moves')}catch(error){detail.textContent=error.message;button.disabled=false}};section.append(button)});
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
  const failures=node('download-failures');failures.replaceChildren();
  for(const failure of item.failed_entries||[]){if(failure.resolved_utc)continue;
   const row=document.createElement('div');row.className='failed-job';const title=document.createElement('strong');title.textContent=`${failure.site} ${failure.source_id} · ${failure.collection}`;
   const reason=document.createElement('p');reason.className='error';reason.textContent=failure.error||'Download failed';
   const instructions=document.createElement('p');instructions.textContent='If you saved this image manually, link it to this entry in Gallery. Then verify the saved link here.';
   const link=document.createElement('a');link.href='/';link.textContent='Open Gallery';
   const verify=document.createElement('button');verify.type='button';verify.textContent='Verify manual saved file';verify.onclick=async()=>{verify.disabled=true;reason.textContent='Checking the saved file link…';try{const response=await fetch('/api/resolve-download-failure',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify({id:failure.id})});const result=await response.json();if(!response.ok)throw Error(result.error||'Verification failed');reason.textContent=result.message;refresh('downloads')}catch(error){reason.textContent=error.message;verify.disabled=false}};
   row.append(title,reason,instructions,link,document.createTextNode(' '),verify);failures.append(row);
  }
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
let failedJobsAfter=0,failedJobsBusy=false;
const failedJobKinds={hash_file:'Hash image files',match_file:'Match images and find duplicates',reconcile_file:'Reconcile file locations'};
async function reviewFailedJob(job,item){
 let reviewBox=item.querySelector('.manual-review');if(reviewBox){reviewBox.remove();return}
 reviewBox=document.createElement('section');reviewBox.className='manual-review';reviewBox.style.cssText='margin-top:12px;padding:12px;border:1px solid #64765e;border-radius:7px';
 const message=document.createElement('p');message.textContent='Checking the saved file and its recorded evidence…';reviewBox.append(message);item.append(reviewBox);
 try{
  const response=await fetch('/api/failed-file-job-review?id='+job.id,{headers:{'X-Gallery-Request':'1'},cache:'no-store'});
  if(response.status===401){window.galleryStatusAuthRedirect();return}
  const review=await response.json();if(!response.ok)throw Error(review.error||`HTTP ${response.status}`);
  reviewBox.replaceChildren();
  const preview=document.createElement('img');preview.src=review.preview_url;preview.alt='Saved file preview';preview.style.cssText='display:block;max-width:min(100%,500px);max-height:350px;object-fit:contain';
  const open=document.createElement('a');open.href=review.preview_url;open.target='_blank';open.rel='noopener noreferrer';open.textContent='Open original image';
  const proof=document.createElement('p');proof.className='detail';proof.textContent=`Saved file: ${number(review.byte_size)} bytes · SHA-256 ${review.actual_sha256} · MD5 ${review.actual_md5}`;
  reviewBox.append(preview,open,proof);
  if(review.choice==='correct_checksum'){
   const recorded=document.createElement('p');recorded.className='detail';recorded.textContent=`Downloader file #${review.external_file_id}: recorded MD5 ${review.recorded_md5||'missing'}, SHA-256 ${review.recorded_sha256||'missing'}, size ${review.recorded_size==null?'missing':number(review.recorded_size)+' bytes'} · ${number(review.media_links)} linked media record(s).`;
   reviewBox.append(recorded);
   if(review.duplicate_of_file_id){const duplicate=document.createElement('p');duplicate.className='detail';duplicate.textContent=`Downloader file #${review.duplicate_of_file_id} already records this SHA-256, MD5 ${review.duplicate_recorded_md5||'missing'}, and ${number(review.duplicate_recorded_size)} bytes. Gallery will verify both saved copies, merge their media links, and remove the extra file through the recovery worker. One saved copy will remain; no duplicate backup is retained.`;reviewBox.append(duplicate)}
  }else if(review.choice==='accept_partial'){
   const explanation=document.createElement('p');explanation.textContent='Accepting this exact file allows Gallery to decode its recoverable image data. The original bytes are kept.';reviewBox.append(explanation);
  }else{
   const explanation=document.createElement('p');explanation.textContent='Close this job only after you have handled its work manually. Gallery will keep the saved file and stop retrying this processing stage. Hashing, matching, or reconciliation that this job would have performed will remain incomplete.';reviewBox.append(explanation);
  }
  const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';label.append(check,document.createTextNode(' I reviewed this file and the values above.'));
  const apply=document.createElement('button');apply.type='button';apply.textContent=review.duplicate_of_file_id?'Merge verified duplicate records':review.choice==='correct_checksum'?'Correct downloader checksum':review.choice==='accept_partial'?'Accept partial image':'Close after manual handling';apply.disabled=true;check.onchange=()=>{apply.disabled=!check.checked};
  const result=document.createElement('p');result.setAttribute('role','status');
  apply.onclick=async()=>{apply.disabled=true;result.textContent='Applying your decision…';try{
   const payload={id:job.id,choice:review.choice,sha256:review.actual_sha256,recorded_md5:review.recorded_md5,recorded_sha256:review.recorded_sha256,recorded_size:review.recorded_size,duplicate_of_file_id:review.duplicate_of_file_id};
   const response=await fetch(review.choice==='close_after_manual_handling'?'/api/close-reviewed-file-job':'/api/resolve-failed-file-job',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify(payload)});
   if(response.status===401){window.galleryStatusAuthRedirect();return}
   const body=await response.json();if(!response.ok)throw Error(body.error||`HTTP ${response.status}`);
   result.textContent=body.message||'Decision saved. The job was queued for processing.';apply.remove();refresh('work');
  }catch(error){result.textContent='Could not apply decision: '+error.message;apply.disabled=false}};
  reviewBox.append(label,apply,result);
 }catch(error){message.textContent='Manual review unavailable: '+error.message}
}
async function loadFailedJobs(reset=false){
 if(failedJobsBusy)return;failedJobsBusy=true;
 if(reset){failedJobsAfter=0;node('failed-jobs-list').replaceChildren()}
 node('failed-jobs-summary').textContent='Loading failed jobs…';node('failed-jobs-more').disabled=true;
 try{
  const response=await fetch('/api/failed-file-jobs?after='+failedJobsAfter,{headers:{'X-Gallery-Request':'1'},cache:'no-store'});
  if(response.status===401){window.galleryStatusAuthRedirect();return}
  const result=await response.json();if(!response.ok)throw Error(result.error||`HTTP ${response.status}`);
  for(const job of result.items||[]){
   const item=document.createElement('article');item.className='failed-job';
   const title=document.createElement('strong');title.textContent=`Job #${job.id} · ${failedJobKinds[job.task_kind]||job.task_kind} · file #${job.file_id}`;
   const path=document.createElement('p');path.textContent=job.relative_path||'File location is no longer available';
   const attempts=document.createElement('p');attempts.className='muted';attempts.textContent=`Failed attempts: ${number(job.failure_count)}`;
   const error=document.createElement('p');error.className='error';error.textContent=job.error||'No error details were recorded.';
   const retry=document.createElement('button');retry.type='button';retry.textContent='Retry this job';
   const manual=document.createElement('button');manual.type='button';manual.textContent='Review manually';manual.onclick=()=>reviewFailedJob(job,item);
   retry.onclick=async()=>{
    retry.disabled=true;error.textContent='Queuing retry…';
    try{
     const response=await fetch('/api/retry-failed-file-job',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify({id:job.id})});
     if(response.status===401){window.galleryStatusAuthRedirect();return}
     const result=await response.json();if(!response.ok)throw Error(result.error||`HTTP ${response.status}`);
     error.textContent='Retry queued. The worker will process this job.';retry.remove();refresh('work');
    }catch(failure){error.textContent='Retry failed: '+failure.message;retry.disabled=false}
   };
   item.append(title,path,attempts,error,retry,document.createTextNode(' '),manual);node('failed-jobs-list').append(item);
  }
  failedJobsAfter=result.next_after||0;node('failed-jobs-more').hidden=!result.has_more;
  node('failed-jobs-summary').textContent=node('failed-jobs-list').childElementCount?`${number(node('failed-jobs-list').childElementCount)} failed jobs shown${result.has_more?' · more available':''}.`:'No failed file jobs remain.';
 }catch(error){node('failed-jobs-summary').textContent='Could not load failed jobs: '+error.message}
 finally{failedJobsBusy=false;node('failed-jobs-more').disabled=false}
}
node('failed-jobs-open').onclick=()=>{node('failed-jobs-dialog').showModal();loadFailedJobs(true)};
node('failed-jobs-close').onclick=()=>node('failed-jobs-dialog').close();
node('failed-jobs-more').onclick=()=>loadFailedJobs();
let moveQueueBefore='',moveQueueBusy=false;
async function loadMoveQueue(reset=false){
 if(moveQueueBusy)return;moveQueueBusy=true;
 if(reset){moveQueueBefore='';node('move-queue-items').replaceChildren()}
 const state=node('move-queue-state').value;node('move-queue-summary').textContent='Loading moves…';
 try{
  const response=await fetch('/api/move-queue?'+new URLSearchParams({state,...(moveQueueBefore?{before:moveQueueBefore}:{})}),{headers:{'X-Gallery-Request':'1'},cache:'no-store'});
  if(response.status===401){window.galleryStatusAuthRedirect();return}
  const result=await response.json();if(!response.ok)throw Error(result.error||'Could not load moves');
  const box=node('move-queue-items');
  for(const job of result.items){
   const row=document.createElement('section');row.className='failed-job';
   const title=document.createElement('strong');title.textContent=`${job.state.toUpperCase()} move #${job.id} · ${job.site||'Entry'} ${job.source_id||job.entry_id} → ${job.target_collection}${job.target_category?' / '+job.target_category:''}`;
   const detail=document.createElement('p');detail.textContent=`${number(job.completed_files)} of ${number(job.total_files)} files moved · Created ${stamp(job.created_at)}${job.error?' · Reason: '+job.error:''}`;
   const actions=document.createElement('div');actions.className='toolbar';actions.style.justifyContent='flex-start';
   const send=async(route,payload,button)=>{button.disabled=true;detail.textContent='Applying your choice…';try{const response=await fetch('/api/'+route,{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:JSON.stringify(payload)});const answer=await response.json();if(!response.ok)throw Error(answer.error||'Action failed');detail.textContent=answer.message;refresh('moves');loadMoveQueue(true)}catch(error){detail.textContent=error.message;button.disabled=false}};
   if(job.state==='failed'){
    const retry=document.createElement('button');retry.type='button';retry.textContent='Retry move';retry.onclick=()=>send('retry-failed-move',{id:job.id},retry);actions.append(retry);
    if(Number(job.total_files)>0&&Number(job.completed_files)===Number(job.total_files)){
     const finish=document.createElement('button');finish.type='button';finish.textContent='Finish completed files';let reviewed=false;finish.onclick=()=>{if(!reviewed){reviewed=true;finish.textContent='Confirm finish';detail.textContent+=` Your decision will finish this move’s entry details for the ${number(job.completed_files)} files already moved.`;return}send('finish-failed-move',{id:job.id,revision:job.current_revision},finish)};actions.append(finish);
    }
    if(Number(job.started_operations)===0){const withdraw=document.createElement('button');withdraw.type='button';withdraw.textContent='Withdraw unstarted move';withdraw.onclick=()=>send('withdraw-failed-move',{id:job.id},withdraw);actions.append(withdraw)}
    if(job.first_operation_id){const review=document.createElement('button');review.type='button';review.textContent='Review file operation #'+job.first_operation_id;review.onclick=()=>{node('move-queue-dialog').close();node('recovery-find-id').value=job.first_operation_id;node('review-recovery').click()};actions.append(review)}
   }
   row.append(title,detail,actions);box.append(row);
  }
  moveQueueBefore=result.next_before?String(result.next_before):'';node('move-queue-more').hidden=!moveQueueBefore;
  node('move-queue-summary').textContent=`${number(result.total)} ${state==='all'?'open':state} moves · ${number(box.children.length)} shown.`;
  node('move-queue-retry-all').hidden=state!=='failed'||Number(result.total)===0;
  if(!result.items.length&&!box.children.length)box.append(document.createTextNode('No moves in this state.'));
 }catch(error){node('move-queue-summary').textContent=error.message}
 finally{moveQueueBusy=false}
}
function resetRetryAll(){const button=node('move-queue-retry-all');button.dataset.confirm='';button.textContent='Retry all failed moves'}
node('move-queue-open').onclick=()=>{resetRetryAll();node('move-queue-state').value='failed';node('move-queue-dialog').showModal();loadMoveQueue(true)};
node('move-queue-close').onclick=()=>{resetRetryAll();node('move-queue-dialog').close()};
node('move-queue-state').onchange=()=>{resetRetryAll();loadMoveQueue(true)};
node('move-queue-more').onclick=()=>loadMoveQueue();
node('move-queue-retry-all').onclick=async()=>{
 const button=node('move-queue-retry-all'),summary=node('move-queue-summary');
 if(button.dataset.confirm!=='yes'){button.dataset.confirm='yes';button.textContent='Confirm retry all failed moves';summary.textContent='This queues every failed collection and category move for the automated worker to check again. Reviewed manual finish decisions stay queued separately.';return}
 button.disabled=true;summary.textContent='Queuing all eligible failed moves…';
 try{const response=await fetch('/api/retry-all-failed-moves',{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:'{}'});const result=await response.json();if(!response.ok)throw Error(result.error||'Could not retry moves');button.dataset.confirm='';button.textContent='Retry all failed moves';summary.textContent=result.message;refresh('moves');await loadMoveQueue(true)}
 catch(error){summary.textContent=error.message}
 finally{button.disabled=false}
};
for(const button of document.querySelectorAll('[data-action]'))button.addEventListener('click',async()=>{const route=button.dataset.action,output=node(route==='retry-scan-issues'||route==='clear-scan-issues'?'issues-action-message':'maintenance-message');button.disabled=true;output.textContent='Requesting '+button.textContent.toLowerCase()+'…';try{const response=await fetch('/api/'+route,{method:'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},body:'{}'});const result=await response.json();if(!response.ok)throw Error(result.error||'Request failed');output.textContent='Request accepted. Status will update as the worker reports progress.';refresh('worker');refresh('issues');}catch(error){output.textContent=error.message}finally{button.disabled=false}});
