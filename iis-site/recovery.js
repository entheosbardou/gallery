// Administrator recovery controls for the maintenance page.
(()=>{
const $=id=>document.getElementById(id);
const el=(tag,value)=>{const item=document.createElement(tag);if(value!=null)item.textContent=value;return item};
async function api(route,payload){const response=await fetch('/api/'+route,{method:payload===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},...(payload===undefined?{}:{body:JSON.stringify(payload)})});if(response.status===401){window.galleryStatusAuthRedirect();throw Error('Sign in again.')}const result=await response.json();if(!response.ok)throw Error(result.error||'Request failed');return result}
let recoveryRows=new Map(),recoveryPollBusy=false,recoveryRemaining=0;
async function loadRecoveryOperations(){
 const selected=$('recovery-find-id').value.trim();
 const result=await api('recovery-operations'+(selected?'?id='+encodeURIComponent(selected):''));
 recoveryRemaining=result.total;
 $('recovery-summary').textContent=selected?(result.items.length?'Reviewing operation #'+selected+'.':'No open move or file-operation error remains for #'+selected+'.'):result.total.toLocaleString()+' file-operation errors remain. Choose a resolution below.';
 const box=$('recovery-items');box.replaceChildren();recoveryRows.clear();
 for(const item of result.items){
  const section=el('section');section.style.cssText='padding:16px 0;border-bottom:1px solid var(--line)';
  const shared=!!item.survivor_path;
  const keepBoth=item.operation_kind==='deduplicate'&&item.source_catalog_present&&item.target_catalog_present&&!item.source_canonical_file_id&&!item.target_canonical_file_id&&item.current_source_path===item.source_path&&item.current_target_path===item.target_path;
  section.append(el('h3','Issue #'+item.id+' · '+(item.operation_kind==='organize'?'Choose where to keep the file':'Choose which copies to keep')));

  const status=el('p');status.setAttribute('role','status');status.style.cssText='white-space:pre-wrap;overflow-wrap:anywhere';
  const actions=el('div');actions.style.cssText='display:flex;flex-wrap:wrap;gap:8px;margin:12px 0';
  const isMove=item.operation_kind==='organize';
  const permission=/permission denied|access is denied|access denied/i.test(item.error||'');
  const exactDuplicate=item.operation_kind==='deduplicate'&&item.expected_source_sha256&&item.expected_source_sha256===item.expected_target_sha256;
  const explanation=permission?'The worker was denied access to an image. This can be temporary. Retry this cleanup to check access again; a successful retry clears the issue.':isMove?'Choose whether to finish moving this file or leave the verified original in its original location.':exactDuplicate?'These files have the same recorded SHA-256. Gallery verifies both saved copies, updates their links, and removes the extra file. No backup copy is retained.':'Choose which image to retain. Gallery verifies both files and updates their references before removing one.';
  section.append(el('p',explanation));
  async function runChoice(route,payload){
   for(const button of actions.querySelectorAll('button'))button.disabled=true;
   status.textContent='Sending your choice to the worker...';
   try{const answer=await api(route,{id:item.id,source_id:item.source_file_id,target_id:item.target_file_id,...payload});status.textContent=answer.message;recoveryRows.get(String(item.id)).waiting=true;await pollRecoveryActions();}
   catch(e){status.textContent=e.message;for(const button of actions.querySelectorAll('button'))button.disabled=false;}
  }
  function action(label,choice){const button=el('button',label);button.type='button';let reviewed=null;button.onclick=async()=>{
   if(choice==='retry'){await runChoice('recovery-action',{choice});return;}
   if(reviewed){await runChoice('recovery-action',{choice,reviewed_source_sha256:reviewed.source?.sha256||'',reviewed_target_sha256:reviewed.target?.sha256||''});return;}
   button.disabled=true;status.textContent='Checking the copies for your review…';
   try{const proof=await api('recovery-copies-proof?id='+encodeURIComponent(item.id));if(!proof.source||(!['cancel_move','finish_move_manual'].includes(choice)&&!proof.target))throw Error('A required copy is missing. Use the saved copy or restore a missing copy.');reviewed=proof;status.textContent=`Review the copies above.\nFirst: ${proof.source.path} · ${Number(proof.source.size).toLocaleString()} bytes · SHA-256 ${proof.source.sha256}`+(proof.target?`\nSecond: ${proof.target.path} · ${Number(proof.target.size).toLocaleString()} bytes · SHA-256 ${proof.target.sha256}`:'')+(choice==='finish_move_manual'?'\nYour choice will use the first copy, replace the second copy if present, update the file links and category, and remove the original. The current bytes shown here take precedence over the earlier worker checks.':'\nYour decision will override the automated match verdict.');button.textContent='Confirm: '+label;}
   catch(e){status.textContent=e.message;}finally{button.disabled=false;}
  };actions.append(button);return button;}
  if(isMove){action('Use first copy and finish move','finish_move_manual');action('Keep original location','cancel_move');}
  else{action('Keep first copy','first');action('Keep second copy','second');if(!exactDuplicate)action('Keep both copies','both');}
  action(isMove?'Retry move with automatic checks':'Retry this cleanup','retry');
  if(shared){const resolve=el('button','Use saved copy and resolve');resolve.type='button';let reviewed=null;resolve.onclick=async()=>{
   if(reviewed){await runChoice('resolve-superseded-recovery',{reviewed_sha256:reviewed.sha256,reviewed_size:reviewed.size});return;}
   resolve.disabled=true;status.textContent='Checking the selected saved file…';
   try{const proof=await api('recovery-survivor-proof?id='+encodeURIComponent(item.id));if(proof.path!==item.survivor_path)throw Error('The saved file changed. Refresh this issue.');reviewed=proof;status.textContent=`Review the saved copy above. File: ${proof.path}\nSize: ${Number(proof.size).toLocaleString()} bytes\nSHA-256: ${proof.sha256}\nYour confirmation will close this cleanup using this file, even if automated hash or pixel checks disagree.`;resolve.textContent='Confirm my saved copy';}
   catch(e){status.textContent=e.message;}finally{resolve.disabled=false;}
  };actions.append(resolve);}
  const restore=el('details');restore.append(el('summary','Restore or locate a missing copy'));
  const side=el('select');side.setAttribute('aria-label','Copy to restore');for(const [value,label] of [['source','First copy'],['target','Second copy']]){const option=el('option',label);option.value=value;side.append(option)}
  const restoreButton=el('button','Restore selected copy from backup');restoreButton.type='button';restoreButton.onclick=()=>runChoice('recovery-action',{choice:'restore',side:side.value});
  const location=el('input');location.placeholder='Location relative to wallpaper storage';location.setAttribute('aria-label','Existing copy location');location.style.width='min(100%,600px)';
  const locateButton=el('button','Use this file to restore the missing copy');locateButton.type='button';locateButton.onclick=()=>runChoice('recovery-action',{choice:'locate',side:side.value,path:location.value});
  restore.append(el('p','Gallery copies the file you select into the missing location and checks that the copy matches your selected source. It will not replace an existing file.'),side,restoreButton,location,locateButton);actions.append(restore);
  recoveryRows.set(String(item.id),{section,status,actions,waiting:false,lastError:item.error});
  // Each preview uses the normal permission-checked image endpoint.
  const previews=el('div');
  const candidates=[['First copy',item.current_source_path,item.source_collection],['Second copy',isMove?item.target_path:item.current_target_path,item.target_collection],['Saved copy',item.survivor_path,item.survivor_collection]];
  for(const [label,path,owner] of candidates){
   if(!path||!owner||(owner!=='.'&&!path.startsWith(owner+'/')))continue;
   const url='/api/image?'+new URLSearchParams({collection:owner,file:owner==='.'?path:path.slice(owner.length+1)});
   const a=el('a','Open '+label.toLowerCase());a.href=url;a.target='_blank';a.rel='noopener';a.style.cssText='display:inline-grid;gap:6px;margin:8px 16px 8px 0';
   const img=el('img');img.src=url+'&thumb=1';img.alt=label;img.loading='lazy';img.style.cssText='width:200px;height:130px;object-fit:contain';a.prepend(img);previews.append(a);
  }
  section.append(previews,actions,status);
  const technical=el('details');technical.append(el('summary','Technical details'));
  const lines=[['Original first file',item.source_path],['Original second file',item.target_path],['Saved copy',item.survivor_path],['Recorded error',item.error],['First file ID',item.source_file_id],['Second file ID',item.target_file_id],['Expected first SHA-256',item.expected_source_sha256],['Expected second SHA-256',item.expected_target_sha256],['Entries linked to first file',item.source_entry_links],['Downloader links to first file',item.source_downloader_links]];
  const detail=el('pre',lines.map(([name,value])=>name+': '+(value==null?'—':value)).join('\n'));detail.style.cssText='white-space:pre-wrap;overflow-wrap:anywhere';technical.append(detail);
  const held=(item.error||'').startsWith('On hold: ');const hold=el('button',held?'Resume automatic checks':'Pause automatic checks');hold.type='button';
  hold.onclick=async()=>{hold.disabled=true;try{const answer=await api('hold-recovery-operation',{id:item.id,hold:!held});await loadRecoveryOperations();$('recovery-summary').textContent=answer.message}catch(e){status.textContent=e.message;hold.disabled=false}};
  technical.append(hold);section.append(technical);box.append(section);
 }
}
async function pollRecoveryActions(){
 if(recoveryPollBusy||!recoveryRows.size)return;
 recoveryPollBusy=true;
 try{
  const result=await api('recovery-action-status?ids='+encodeURIComponent([...recoveryRows.keys()].join(',')));
  for(const item of result.items){
   const row=recoveryRows.get(String(item.id));if(!row)continue;
   if(['complete','cancelled'].includes(item.state)){row.status.textContent='Resolved. This issue was cleared.';row.section.replaceChildren(el('p','Issue #'+item.id+' resolved.'));recoveryRows.delete(String(item.id));recoveryRemaining=Math.max(0,recoveryRemaining-1);$('recovery-summary').textContent=recoveryRemaining.toLocaleString()+' issues remain. Completed issues are cleared automatically.';continue;}
   let action=item.action;try{if(typeof action==='string')action=JSON.parse(action)}catch{action=null}
   if(action&&['pending','working'].includes(action.status)){row.waiting=true;row.status.textContent=action.status==='working'?'The worker is verifying files and applying your choice...':action.message;for(const button of row.actions.querySelectorAll('button'))button.disabled=true;}
   else if(action&&['done','failed'].includes(action.status)){row.waiting=false;row.status.textContent=action.message;for(const button of row.actions.querySelectorAll('button'))button.disabled=false;}
  }
 }catch{}finally{recoveryPollBusy=false;}
}
setInterval(()=>{if($('recovery-dialog').open)pollRecoveryActions()},5000);
$('review-recovery').onclick=async()=>{try{await loadRecoveryOperations();$('recovery-dialog').showModal()}catch(e){$('recovery-summary').textContent=e.message}};
$('recovery-find').onclick=async()=>{try{await loadRecoveryOperations()}catch(e){$('recovery-summary').textContent=e.message}};$('recovery-show-all').onclick=async()=>{$('recovery-find-id').value='';try{await loadRecoveryOperations()}catch(e){$('recovery-summary').textContent=e.message}};$('recovery-close').onclick=()=>$('recovery-dialog').close();
$('recovery-retry').onclick=async()=>{try{await api('retry-recovery-operation',{});$('recovery-summary').textContent='Recovery recheck requested. The worker will review unfinished operations.';window.galleryStatusRefresh?.('recovery')}catch(e){$('recovery-summary').textContent=e.message}};

})();
