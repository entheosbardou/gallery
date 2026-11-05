'use strict';
const gallerySession=window.gallerySession||{admin:false,role:'users',name:''},isAdmin=!!gallerySession.admin,legacyDownloads=!!gallerySession.legacy_downloads;
document.body.dataset.role=isAdmin?'admins':'users';

const $=id=>document.getElementById(id),fields=['collection','search','site','status','category','kind','view'];
let pageSize=60;
let pageCursor="",nextCursor=null,cursorHistory=[],pageMeta=null,matchingTotal=0,refreshSequence=0,refreshAbort=null,filterTimer=null;
function pageParams(cursor=pageCursor){const q=new URLSearchParams({limit:String(pageSize),collection:$("collection").value,search:$("search").value,kind:$("kind").value,duplicates:$("show-duplicates").checked?"1":"0"});for(const [id,key] of [["site","sites"],["status","statuses"],["category","categories"]])if(!window.multiFilters.isAll(id))q.set(key,JSON.stringify(window.multiFilters.values(id)));if(cursor)q.set("cursor",cursor);return q}
function firstPage(){pageCursor="";nextCursor=null;cursorHistory=[];current=1;refresh(false)}
let data=[],filtered=[],current=1,downloading=false,deleting=false,previewBusy=false,previewRevision=0,lastPreviewRequest=0,serverKeyAvailable=false;
const previews=new Map(),attemptedPreviews=new Set();
const names={no_images:'No downloadable images',ready:'Ready to download',saved:'Saved',downloaded:'Saved',existing:'Saved',duplicate:'Duplicate',partial:'Partially saved',missing:'Missing on disk',failed:'Download failed',inventory_only:'Inventory only',excluded:'Excluded',excluded_unrelated:'Excluded — unrelated',post_record:'Post record',available:'Album available',unavailable:'Album unavailable',sfw:'SFW',sketchy:'Sketchy',nsfw:'NSFW',not_provided:'Not labelled by source'};
function label(s){return names[String(s).toLowerCase()]||s||'Not labelled by source'}
function el(tag,text,cls){const e=document.createElement(tag);if(text!==undefined)e.textContent=text;if(cls)e.className=cls;return e}
function link(text,url){const a=el('a',text);try{const parsed=new URL(url,location.href);if(!['http:','https:'].includes(parsed.protocol))return el('span',text);a.href=parsed.href}catch{return el('span',text)}a.target='_blank';a.rel='noopener noreferrer';return a}
let pointerSelecting=false,pendingRender=false;
const pendingText=new Map();
function selectionBusy(){const selection=window.getSelection();return pointerSelecting||!!(selection&&!selection.isCollapsed)}
function liveText(id,value){const node=$(id);value=String(value);if(selectionBusy()){pendingText.set(id,value);return}pendingText.delete(id);if(node.textContent!==value)node.textContent=value}
function flushBackgroundUpdates(){if(selectionBusy())return;for(const [id,value] of pendingText){const node=$(id);if(node&&node.textContent!==value)node.textContent=value}pendingText.clear();if(pendingRender){pendingRender=false;render(true)}}
document.addEventListener('pointerdown',()=>{pointerSelecting=true});
document.addEventListener('pointerup',()=>{pointerSelecting=false;setTimeout(flushBackgroundUpdates,0)});
document.addEventListener('pointercancel',()=>{pointerSelecting=false;setTimeout(flushBackgroundUpdates,0)});
document.addEventListener('selectionchange',()=>{if(!selectionBusy())setTimeout(flushBackgroundUpdates,0)});
function rowKey(r){return String(r.entry_id||JSON.stringify([r.collection,r.site,r.id,r.inventory_index]))}
const selectedEntries=new Map();let bulkMoveBusy=false;
function updateSelection(){if(!isAdmin)return;$('selected-count').textContent=selectedEntries.size.toLocaleString()+' selected';$('bulk-move-open').disabled=!selectedEntries.size||bulkMoveBusy;$('clear-selection').disabled=!selectedEntries.size||bulkMoveBusy;$('select-page').disabled=bulkMoveBusy;}
function selectionControl(r){const label=el('label',undefined,'select-entry');const box=el('input');box.type='checkbox';box.checked=selectedEntries.has(rowKey(r));box.setAttribute('aria-label','Select '+r.site+' '+r.id);box.onchange=()=>{if(box.checked)selectedEntries.set(rowKey(r),r);else selectedEntries.delete(rowKey(r));updateSelection()};label.append(box,el('span','Select'));return label}
function remote(r){return previews.get(rowKey(r))}
function options(id,values){for(const value of [...new Set(values.filter(Boolean))].sort()){if([...$(id).options].some(o=>o.value===value))continue;const option=el('option',label(value));option.value=value;$(id).append(option)}}
async function api(route,payload,signal){const response=await fetch('/api/'+route,{method:payload===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Gallery-Request':'1'},signal,...(payload===undefined?{}:{body:JSON.stringify(payload)})});if(response.status===401){location.href='/auth/login';throw Error('Sign in again to continue.')}let result;try{result=await response.json()}catch{throw Error('The gallery could not complete this request. Reload and retry.')}if(!response.ok)throw Error(result.error||'Request failed');return result}
function updateStats(){liveText('saved-count',(pageMeta?.saved||0).toLocaleString());liveText('total-count',(pageMeta?.total||0).toLocaleString());$('collection-count').textContent=(pageMeta?.collections.length||0).toLocaleString()}
let inventoryRefreshBusy=false,inventoryRefreshAgain=false;
async function refresh(check=true){
 const sequence=++refreshSequence;if(refreshAbort)refreshAbort.abort();refreshAbort=new AbortController();inventoryRefreshBusy=true;
 $('refresh').disabled=true;liveText('load-state','Loading entries…');
 try{
  const result=await api('inventory?'+pageParams(),undefined,refreshAbort.signal);
  if(sequence!==refreshSequence)return;
  matchingTotal=result.total;data=result.records;nextCursor=result.next_cursor;data.forEach(r=>{r.category=(r.category||'not_provided').toLowerCase();r.search=[r.id,r.description,r.file,r.site,r.category,r.collection].join(' ').toLowerCase()});
  updateStats();render(true);liveText('load-state','Entries loaded.');
  if(!pageMeta||check){
   pageMeta=await api('inventory-meta',undefined,refreshAbort.signal);if(sequence!==refreshSequence)return;
   const selected=$('collection').value;$('collection').replaceChildren(el('option','All collections'));$('collection').firstChild.value='';for(const c of pageMeta.collections){const option=el('option',c.id+' ('+c.records.toLocaleString()+')');option.value=c.id;$('collection').append(option)}window.collectionMenu.update(pageMeta.collections,selected);$('collection').value=selected;
   const opts=values=>values.filter(Boolean).map(value=>({value,label:label(value)}));window.multiFilters.update('site',opts(pageMeta.sites));window.multiFilters.update('status',opts(pageMeta.statuses));window.multiFilters.update('category',opts(pageMeta.categories));
  }
  $('category').closest('.multi-filter').hidden=result.categories.length===1;if(!isAdmin)$('kind').closest('label').hidden=!!result.restricted;
  liveText('load-state','Updated at '+new Date().toLocaleTimeString()+'.');updateStats();render(true);
 }catch(e){if(e.name!=='AbortError')liveText('load-state',e.message)}finally{if(sequence===refreshSequence){inventoryRefreshBusy=false;$('refresh').disabled=downloading}}
}
function collectionIncludes(selected,collection){return !selected||selected==='.'||collection===selected||collection.startsWith(selected+'/')}
function matchesScope(r){return r.status!=='duplicate'}
function matches(r){return matchesScope(r)&&(window.multiFilters.isAll('status')||window.multiFilters.values('status').some(status=>status==='saved'?!!r.local:r.status===status))}
function isFlash(r){try{const path=r.file||new URL(r.local||'',location.href).searchParams.get('file')||'';return /\.swf$/i.test(path)}catch{return false}}
function show(r){const path=r.local||remote(r)?.path;if(!path)return;const flash=isFlash(r);$('modal-title').textContent=r.collection+' · '+r.site+' '+r.id;$('large').hidden=flash;$('flash-preview').hidden=!flash;if(flash){$('large').removeAttribute('src');$('flash-preview').src='/flash-player.html?file='+encodeURIComponent(path)}else{$('flash-preview').src='about:blank';$('large').src=path}$('modal-links').replaceChildren(link(flash?'Download original SWF':'Open original image',path));if(r.related_image_links?.length>1)r.related_image_links.forEach((image,index)=>$('modal-links').append(link('Saved image '+(index+1),image.url)));if(r.source)$('modal-links').append(link('View source post',r.source));if(r.related_has_more){const more=el('button','Browse all saved images','small');more.onclick=()=>loadRelatedImages(r,0,more);$('modal-links').append(more)}$('modal').showModal()}
$('modal').addEventListener('close',()=>{$('flash-preview').src='about:blank';$('large').removeAttribute('src')});

function actions(r){const a=el('div',undefined,'actions');if(r.local){a.append(link('Open original',r.local));const save=link('Download',r.local);save.download=(r.file||r.id||'wallpaper').split('/').pop();save.target='_self';a.append(save)}else if(isAdmin&&legacyDownloads&&r.record_type==='image'){const button=el('button',(r.status==='excluded_unrelated'||/(^|\/)other$/i.test(r.download_collection||''))&&r.download_collection!==r.collection?'Download to other':'Download image','small');button.disabled=downloading||deleting;button.onclick=()=>downloadRows([r]);a.append(button)}if(isAdmin&&legacyDownloads&&r.site==='Reddit'&&!r.local&&['post','album','post_or_album'].includes(r.record_type)){const b=el('button',(r.status==='excluded_unrelated'||/(^|\/)other$/i.test(r.download_collection||''))&&r.download_collection!==r.collection?'Download to other':'Retry Reddit post','small');b.disabled=downloading||deleting;b.onclick=()=>downloadRows([r]);a.append(b)}if(!r.local){const b=el('button',remote(r)?'View preview':'Load preview','small');b.disabled=downloading||deleting;b.onclick=()=>remote(r)?show(r):loadPreview(r,true);a.append(b)}if(r.source)a.append(link('View source',r.source));if(isAdmin){const move=el('button','Move to collection','small');move.onclick=()=>openCollectionMove(r);a.append(move);const merge=el('button',r.local?'Link to existing entry':'Link duplicate','small link-local');merge.disabled=deleting;merge.onclick=()=>openLocalLink(r);a.append(merge);const remove=el('button','Delete entry','small delete-entry');remove.disabled=deleting;remove.onclick=()=>deleteEntry(r);a.append(remove)}return a}
function picture(r,small=false){const p=el('button',undefined,small?'':'picture');p.setAttribute('aria-label','View '+r.site+' '+r.id);if(isFlash(r)){p.append(el('span','▶ Play Flash (.swf)','flash-tile'));p.onclick=()=>show(r);return p}const im=el('img',undefined,small?'smallthumb':'');im.src=r.thumb||remote(r)?.thumb||r.local;im.referrerPolicy='no-referrer';im.alt=r.description||r.id;im.loading='lazy';im.onerror=()=>{im.onerror=null;im.src=r.local||remote(r)?.path||''};p.append(im);p.onclick=()=>show(r);return p}
function card(r){const c=el('article',undefined,'card');if(r.local||remote(r))c.append(picture(r));else c.append(el('div',label(r.status),'picture empty'));const b=el('div',undefined,'body');if(isAdmin)b.append(selectionControl(r));b.append(el('div',r.collection,'collection-name'),el('div',r.site+' · '+r.id,'title'));const badges=el('div',undefined,'badges');for(const x of [r.site,label(r.status),label(r.category)])badges.append(el('span',x,'badge'));b.append(badges);if((r.file_collection||r.duplicate_collection)&&(r.file_collection||r.duplicate_collection)!==r.collection)b.append(el('div','Shared file from '+(r.file_collection||r.duplicate_collection),'sub'));if(r.duplicate_record_of)b.append(el('div','Same saved image as '+(r.duplicate_record_site||r.site)+' · '+r.duplicate_record_of,'sub'));if(r.content_duplicate)b.append(el('div','Identical file: '+r.duplicate_primary_path,'sub'));if((r.status==='excluded_unrelated'||/(^|\/)other$/i.test(r.download_collection||''))&&r.download_collection!==r.collection)b.append(el('div','Downloads to '+(r.download_collection||'the nearest other collection'),'sub'));if(r.resolution)b.append(el('div',r.resolution,'sub'));if(r.file||r.duplicate_of)b.append(el('div',r.file||r.duplicate_of,'sub'));if(r.description){const d=el('details',undefined,'details');d.append(el('summary','Description'),el('p',r.description));b.append(d)}if(r.reason||r.error)b.append(el('p',r.reason||r.error,'sub'));b.append(actions(r));c.append(b);return c}
function render(background=false){
 if(background&&selectionBusy()){pendingRender=true;return}
 filtered=data;const rows=data;
 $('count').textContent=matchingTotal.toLocaleString()+' matching records'+(rows.length?' · showing '+((current-1)*pageSize+1).toLocaleString()+'–'+((current-1)*pageSize+rows.length).toLocaleString():'');$('page').textContent='Page '+current+' of '+Math.max(1,Math.ceil(matchingTotal/pageSize)).toLocaleString();$('prev').disabled=cursorHistory.length===0;$('next').disabled=!nextCursor;
 const root=$('results');root.replaceChildren();if(!rows.length){root.append(el('p','No matching records. Try changing a filter.'));return}
 if($('view').value==='grid'){const grid=el('div',undefined,'grid');for(const r of rows)grid.append(card(r));root.append(grid);return}
 const wrap=el('div',undefined,'tablewrap'),table=el('table'),head=el('thead'),tr=el('tr');for(const title of [...(isAdmin?['Select']:[]),'Preview','Collection / result','Status / category','Description','Links'])tr.append(el('th',title));head.append(tr);table.append(head);const body=el('tbody');
 for(const r of rows){const row=el('tr'),pic=el('td');if(r.local||remote(r))pic.append(picture(r,true));else pic.textContent='—';const meta=el('td');meta.append(el('div',r.collection,'collection-name'),el('strong',r.site+' · '+r.id),el('div',r.resolution||r.record_type,'sub'));const state=el('td');state.append(el('div',label(r.status)),el('div',label(r.category),'sub'));const links=el('td');links.append(actions(r));if(isAdmin){const select=el('td');select.append(selectionControl(r));row.append(select)}row.append(pic,meta,state,el('td',r.description||''),links);body.append(row)}table.append(body);wrap.append(table);root.append(wrap)
}
async function loadPreview(r,showAfter=false){
 if(previewBusy)return;if(r.site==='Wallhaven'&&!serverKeyAvailable)return;
 previewBusy=true;lastPreviewRequest=Date.now();attemptedPreviews.add(rowKey(r));const revision=previewRevision;
 try{const result=await api('preview',{collection:r.collection,entry_id:r.entry_id,revision:r.revision,id:r.id,site:r.site});if(revision!==previewRevision)return;previews.set(rowKey(r),result);liveText('auth-message','Preview ready for '+r.id+'.');render(true);if(showAfter)show(r)}catch(e){if(revision===previewRevision&&!/HTTP (401|403)/.test(e.message))$('auth-message').textContent=e.message}finally{previewBusy=false}
}
setInterval(()=>{if(previewBusy||Date.now()-lastPreviewRequest<1800)return;const r=data.find(r=>!r.local&&(r.site!=='Wallhaven'||serverKeyAvailable)&&!remote(r)&&!attemptedPreviews.has(rowKey(r)));if(r)loadPreview(r)},1800);
async function deleteEntry(r){
 if(deleting)return;
 if(!confirm('Delete '+r.site+' '+r.id+' from '+r.collection+'?\n\nThis removes only this inventory entry. Image files and other entries are kept.'))return;
 deleting=true;for(const id of ['download-filtered','retry-failed','refresh'])$(id).disabled=true;render();
 $('entry-message').textContent='Deleting entry…';
 try{await api('delete-entry',{collection:r.collection,entry_id:r.entry_id,revision:r.revision,id:r.id,site:r.site,token:r.entry_token});previews.clear();attemptedPreviews.clear();$('entry-message').textContent='Deleted '+r.site+' '+r.id+'. Image files were kept.';await refresh()}
 catch(e){$('entry-message').textContent=e.message;await refresh(false)}
 finally{deleting=false;for(const id of ['download-filtered','retry-failed','refresh'])$(id).disabled=downloading;render()}
}
async function downloadFiltered(retry=false){
 if(downloading||deleting)return;
 const params=pageParams('');if(retry===true)params.set('statuses',JSON.stringify(['failed']));
 try{showDownloadJob(await api('downloads-filtered?'+params,{}))}catch(e){liveText('download-progress',e.message)}
}
async function downloadRows(selection){
 if(downloading||deleting)return;
 try{const job=await api('downloads',{entries:selection.map(r=>({collection:r.collection,entry_id:r.entry_id,revision:r.revision,id:r.id,site:r.site,kind:r.record_type,url:r.url||''}))});showDownloadJob(job)}catch(e){$('download-progress').textContent=e.message}
}
let lastDownloadPhase='',queuePollBusy=false,lastInventoryDownloadRefresh=0,lastShownSaved=-1;
function showDownloadJob(job){
 const active=['queued','running'].includes(job.phase);downloading=active;
 for(const id of ['download-filtered','retry-failed'])$(id).disabled=active||deleting;
 $('download-stop').hidden=!active&&job.phase!=='stopped';liveText('download-stop',active?'Stop downloads':'Resume downloads');
 if(job.phase!=='idle')liveText('download-progress',(active?(job.stop?'Stopping after the current image. ':'Downloading on the server. '):job.phase==='stopped'?'Stopped. ':'Finished. ')+`${job.processed}/${job.total} processed · ${job.saved} saved · ${job.resolved||0} posts resolved · ${job.existing} already saved · ${job.skipped} skipped · ${job.failed} failed`+(active&&job.current?' · '+job.current:''));
 if(active&&job.saved!==lastShownSaved&&!inventoryRefreshBusy&&Date.now()-lastInventoryDownloadRefresh>15000){lastShownSaved=job.saved;lastInventoryDownloadRefresh=Date.now();refresh(false)}
 liveText('download-errors',(job.errors||[]).join('\n'));$('download-errors').hidden=!(job.errors||[]).length;
 if(lastDownloadPhase!==job.phase){const previous=lastDownloadPhase;lastDownloadPhase=job.phase;render(true);if(!active&&['queued','running'].includes(previous))refresh()}
}
async function pollDownloads(){if(!isAdmin||!legacyDownloads)return;if(queuePollBusy)return;queuePollBusy=true;try{showDownloadJob(await api('downloads'))}catch(e){liveText('download-progress','Could not read download progress. The server queue is retained; retrying...')}finally{queuePollBusy=false}}
setInterval(pollDownloads,2000);
try{localStorage.removeItem('spice-and-wolf-wallhaven-api-key')}catch{}
if(isAdmin)api('wallhaven-status').then(result=>{serverKeyAvailable=result.available;$('auth-message').textContent=result.available?'Wallhaven API key loaded automatically from the server.':'The server could not read C:\\downloads\\wallhaven-api-key.txt.'}).catch(()=>{$('auth-message').textContent='Could not check the server Wallhaven key.'});
for(const field of fields)$(field).addEventListener(['search','tags'].includes(field)?'input':'change',()=>{if(field==='view'){render();return}clearTimeout(filterTimer);filterTimer=setTimeout(firstPage,['search','tags'].includes(field)?300:0)});
$('show-duplicates').checked=false;
$('show-duplicates').onchange=()=>{firstPage()};
try{const saved=Number(localStorage.getItem('wallpaper-gallery-page-size'));if(Number.isInteger(saved)&&saved>=1&&saved<=5000)pageSize=saved}catch{}
$('page-size').value=pageSize;
$('page-size').onchange=()=>{const value=Number($('page-size').value);if(!Number.isInteger(value)||value<1||value>5000){$('page-size').value=pageSize;return}pageSize=value;try{localStorage.setItem('wallpaper-gallery-page-size',String(value))}catch{}firstPage()};
$('prev').onclick=()=>{if(!cursorHistory.length)return;pageCursor=cursorHistory.pop();current--;refresh(false);scrollTo({top:0})};$('next').onclick=()=>{if(!nextCursor)return;cursorHistory.push(pageCursor);pageCursor=nextCursor;current++;refresh(false);scrollTo({top:0})};$('reset').onclick=()=>{for(const f of fields.filter(f=>!['site','status','category'].includes(f)))$(f).value='';window.multiFilters.reset();$('view').value='grid';$('show-duplicates').checked=false;window.collectionMenu.updateLabel();firstPage()};
$('close').onclick=()=>{$('modal').close();$('large').removeAttribute('src')};$('modal').addEventListener('click',e=>{if(e.target===$('modal'))$('close').click()});
$('refresh').onclick=async()=>{if(isAdmin)await api('inventory?rescan=1');await refresh(false)};$('check-duplicates').onclick=checkDuplicates;$('download-stop').onclick=async()=>{try{showDownloadJob(await api(downloading?'download-stop':'download-resume',{}))}catch(e){$('download-progress').textContent=e.message}};$('download-filtered').onclick=()=>downloadFiltered();$('retry-failed').onclick=()=>downloadFiltered(true);
$('export').onclick=()=>{const q=pageParams('');q.delete('cursor');location.href='/api/export-csv?'+q};
if(isAdmin&&legacyDownloads)api('reddit-status').then(result=>{$('reddit-status').textContent=result.available?'The dedicated Reddit downloader Edge profile is signed in. You can close its browser window.':'Sign in once through Sign in to Reddit downloader.cmd in C:\\downloads\\gallery. The downloader uses its separate saved Edge profile.'}).catch(()=>{});
let duplicatePolling=false;
function showDuplicateResult(result){
 if(result.message){liveText('duplicate-progress',result.message+(result.total?' '+result.processed.toLocaleString()+' / '+result.total.toLocaleString():'')+(result.phase==='scanning'?' · You can close this page.':''));$('duplicate-results').hidden=true;return;}
 if(result.phase==='scanning'){liveText('duplicate-progress',result.total?'Checking exact duplicates: '+result.processed.toLocaleString()+' / '+result.total.toLocaleString()+' images…':'Finding image files for the duplicate check…');return}
 if(result.phase==='error'){liveText('duplicate-progress',result.error);return}
 if(result.phase!=='complete')return;
 liveText('duplicate-count',result.duplicate_files.toLocaleString());liveText('duplicate-record-count',(result.duplicate_records||0).toLocaleString());liveText('removed-count',(result.deleted_files||0).toLocaleString());
 liveText('duplicate-progress','Checked '+result.total.toLocaleString()+' files · '+result.duplicate_groups.toLocaleString()+' exact duplicate groups · '+result.duplicate_files.toLocaleString()+' extra file copies · '+(result.duplicate_records||0).toLocaleString()+' duplicate inventory entries · '+(result.deleted_files||0).toLocaleString()+' duplicate files removed. Posts link to the retained images.'+(result.issues.length?' '+result.issues.length+' scan issues.':''));
 const groups=$('duplicate-groups');groups.replaceChildren();liveText('duplicate-summary','Duplicate files and cleanup history');$('duplicate-results').hidden=!result.groups.length&&!result.issues.length&&!(result.deletions||[]).length;
 for(const [index,group] of result.groups.entries()){const detail=el('details',undefined,'details');detail.append(el('summary','Group '+(index+1)+' · '+group.files.length+' identical files'));const list=el('ul');for(const file of group.files){const item=el('li');item.append(file.url?link(file.path,file.url):el('span',file.path));list.append(item)}detail.append(list);groups.append(detail)}
 for(const removed of result.deletions||[])groups.append(el('p','Removed '+removed.file+' → kept '+removed.kept_file,'sub'));
 for(const issue of result.issues)groups.append(el('p',issue,'sub'));
}
async function checkDuplicates(){
 if(downloading)return;
 if(duplicatePolling){try{await api('duplicates',{})}catch{}return}
 try{await api('duplicates',{});duplicatePolling=true;$('check-duplicates').disabled=true;
  for(;;){const result=await api('duplicates');showDuplicateResult(result);if(result.phase==='complete'){await refresh(false);break}if(result.phase==='error'||result.phase==='idle')break;await new Promise(resolve=>setTimeout(resolve,1200))}
 }catch(e){$('duplicate-progress').textContent=e.message}finally{duplicatePolling=false;$('check-duplicates').disabled=downloading}
}
pollDownloads().finally(()=>refresh());

let organizationPolling=false;
function showOrganization(job){
 const active=['running','scanning'].includes(job.phase);$('organize').disabled=active;
 if(active)liveText('organize-progress',(job.message||'Organizing category folders...')+(job.total?' '+job.processed+'/'+job.total:'')+' You can close this page.');
 if(job.phase==='complete')liveText('organize-progress',job.message||'Category folders organized. Collection links updated.');
 if(job.phase==='error')liveText('organize-progress',job.error||job.message);
 return active;
}
async function pollOrganization(){if(organizationPolling)return;organizationPolling=true;try{for(;;){const job=await api('organize');if(!showOrganization(job)){if(job.phase==='complete')await refresh(false);break}await new Promise(resolve=>setTimeout(resolve,1500))}}catch(e){$('organize-progress').textContent=e.message;$('organize').disabled=false}finally{organizationPolling=false}}
$('organize').onclick=async()=>{if(organizationPolling)return;$('organize').disabled=true;try{showOrganization(await api('organize',{}));pollOrganization()}catch(e){$('organize-progress').textContent=e.message;$('organize').disabled=false}};
if(isAdmin)api('organize').then(job=>{if(showOrganization(job))pollOrganization()}).catch(()=>{});

let manualScanPolling=false;
async function pollManualScan(){if(manualScanPolling)return;manualScanPolling=true;try{for(;;){const job=await api('manual-scan');$('scan-manual').disabled=['running','scanning'].includes(job.phase);liveText('manual-scan-progress',job.message||'');if(!['running','scanning'].includes(job.phase)){if(job.phase==='complete')await refresh(false);break}await new Promise(resolve=>setTimeout(resolve,1500))}}catch(e){liveText('manual-scan-progress',e.message);$('scan-manual').disabled=false}finally{manualScanPolling=false}}
$('scan-manual').onclick=async()=>{if(manualScanPolling)return;$('scan-manual').disabled=true;try{await api('manual-scan',{});pollManualScan()}catch(e){liveText('manual-scan-progress',e.message);$('scan-manual').disabled=false}};
if(isAdmin)api('manual-scan').then(job=>{if(['running','scanning'].includes(job.phase))pollManualScan()}).catch(()=>{});

let linkingSource=null,linkingTarget=null,linkingBusy=false;
function linkIdentity(row){return {collection:row.collection,entry_id:row.entry_id,revision:row.revision,id:row.id,site:row.site,token:row.entry_token}}
let linkSearchSequence=0;
async function renderLinkMatches(){
 const query=$('link-search').value.trim().toLowerCase(),collection=$('link-collection').value;
 const sequence=++linkSearchSequence;let found;try{const params=new URLSearchParams({limit:'50',collection,search:query});found=await api('inventory?'+params)}catch(e){$('link-message').textContent=e.message;return}if(sequence!==linkSearchSequence)return;const matches=found.records.filter(r=>rowKey(r)!==rowKey(linkingSource)&&['image','post','album','post_or_album'].includes(r.record_type)&&(linkingSource.local||r.local));
 const results=$('link-results');results.replaceChildren();
 for(const row of matches){const button=el('button',undefined,'link-match');button.style.cssText='display:block;width:100%;text-align:left;margin:6px 0';button.append(el('strong',row.collection+' · '+row.site+' '+row.id),el('div',(row.description||row.file||'').slice(0,200),'sub'),el('div',label(row.status),'sub'));button.onclick=()=>{linkingTarget=row;$('link-selected').textContent='Selected: '+row.collection+' · '+row.site+' '+row.id;$('link-confirm').disabled=linkingBusy};results.append(button)}
 if(!matches.length)results.append(el('p','No matching entries. Try another search or collection.'));
}
function openLocalLink(row){linkingSource=row;linkingTarget=null;$('link-source').textContent=row.local?'Saved image: '+(row.file||row.duplicate_of||row.id)+(row.site==='Local'?'. The matching entry will use this file and the redundant Local entry will be removed.':'. The matching entry will link to this file. Both source entries are kept.'):'Choose the saved entry containing this image. This entry will link to that file as a duplicate.';$('link-search').value='';$('link-message').textContent='';$('link-selected').textContent='Choose the matching entry above.';$('link-confirm').disabled=true;$('link-collection').replaceChildren(el('option','All collections'));$('link-collection').firstChild.value='';for(const value of (pageMeta?.collections||[]).map(c=>c.id)){const option=el('option',value);option.value=value;$('link-collection').append(option)}$('link-collection').value=row.collection;renderLinkMatches();$('link-dialog').showModal();$('link-search').focus()}
$('link-search').oninput=renderLinkMatches;$('link-collection').onchange=renderLinkMatches;$('link-cancel').onclick=()=>{if(!linkingBusy)$('link-dialog').close()};
$('link-confirm').onclick=async()=>{if(linkingBusy||!linkingSource||!linkingTarget)return;linkingBusy=true;$('link-confirm').disabled=true;$('link-message').textContent='Linking saved image...';try{await api('link-local',{local:linkIdentity(linkingSource.local?linkingSource:linkingTarget),target:linkIdentity(linkingSource.local?linkingTarget:linkingSource)});$('link-dialog').close();previews.clear();attemptedPreviews.clear();$('entry-message').textContent='Image linked successfully. Image files were kept.';await refresh(false)}catch(e){$('link-message').textContent=e.message}finally{linkingBusy=false;$('link-confirm').disabled=!linkingTarget}};

let lastDatabaseCompletion='';
async function pollDatabase(){if(!isAdmin)return;try{const job=await api('database-status');const recovery=$('recovery-progress-wrap');recovery.hidden=!(job.recovery_total>0);if(!recovery.hidden){$('recovery-progress').max=job.recovery_total;$('recovery-progress').value=Math.min(job.recovery_processed||0,job.recovery_total);liveText('recovery-progress-label','Recovering file operations · '+(job.recovery_processed||0).toLocaleString()+' / '+job.recovery_total.toLocaleString()+(job.recovery_review?' · '+job.recovery_review.toLocaleString()+' need review':''));}liveText('database-progress',(job.message||'')+(job.total?' · '+job.processed.toLocaleString()+' / '+job.total.toLocaleString():'')+(job.phase==='scanning'?' · You can close this page.':''));if(job.issues?.length){liveText('database-progress',(job.message||'Collection check finished')+' · '+job.issues.length+' items need attention.');liveText('scan-issues',job.issues.map(x=>(x.file||x.inventory||'')+': '+x.error).join('\n'));$('scan-issues').hidden=false;$('retry-scan-issues').hidden=false;$('clear-scan-issues').hidden=false;}else{$('scan-issues').hidden=true;$('retry-scan-issues').hidden=true;$('clear-scan-issues').hidden=true;}if(job.pending_updates?.length)liveText('database-progress',job.message+' · '+job.pending_updates.length+' inventory updates are waiting for import.');if(job.phase==='complete'&&job.updated_utc!==lastDatabaseCompletion){lastDatabaseCompletion=job.updated_utc;await refresh(false)}}catch{}}
$('import-updates').onclick=async()=>{try{await api('import-updates',{});await pollDatabase()}catch(e){liveText('database-progress',e.message)}};
$('retry-scan-issues').onclick=async()=>{try{await api('retry-scan-issues',{});await pollDatabase()}catch(e){liveText('database-progress',e.message)}};
$('clear-scan-issues').onclick=async()=>{try{await api('clear-scan-issues',{});await pollDatabase()}catch(e){liveText('database-progress',e.message)}};
let recoveryRows=new Map(),recoveryPollBusy=false,recoveryRemaining=0;
async function loadRecoveryOperations(){
 const selected=$('recovery-find-id').value.trim();
 const result=await api('recovery-operations'+(selected?'?id='+encodeURIComponent(selected):''));
 recoveryRemaining=result.total;
 $('recovery-summary').textContent=selected&&!result.items.length?'No open error remains for issue #'+selected+'.':result.total.toLocaleString()+' issues remain. Choose a resolution below.';
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
  const explanation=permission?'The worker was denied access to an image. This can be temporary. Retry this cleanup to check access again; a successful retry clears the issue.':isMove?'Choose whether to finish moving this file or leave the verified original in its original location.':'Choose which image to retain. Keeping one copy backs up the other copy and updates Gallery and downloader references before removing it.';
  section.append(el('p',explanation));
  async function runChoice(route,payload){
   for(const button of actions.querySelectorAll('button'))button.disabled=true;
   status.textContent='Sending your choice to the worker...';
   try{const answer=await api(route,{id:item.id,source_id:item.source_file_id,target_id:item.target_file_id,...payload});status.textContent=answer.message;recoveryRows.get(String(item.id)).waiting=true;await pollRecoveryActions();}
   catch(e){status.textContent=e.message;for(const button of actions.querySelectorAll('button'))button.disabled=false;}
  }
  function action(label,choice){const button=el('button',label);button.type='button';button.onclick=()=>runChoice('recovery-action',{choice});actions.append(button);return button;}
  if(isMove){action('Finish move','retry');action('Keep original location','cancel_move');}
  else{action('Keep first copy','first');action('Keep second copy','second');action('Keep both copies','both');}
  action('Retry this cleanup','retry');
  if(shared){const resolve=el('button','Use saved copy and resolve');resolve.type='button';resolve.onclick=()=>runChoice('resolve-superseded-recovery',{});actions.append(resolve);}
  const restore=el('details');restore.append(el('summary','Restore or locate a missing copy'));
  const side=el('select');side.setAttribute('aria-label','Copy to restore');for(const [value,label] of [['source','First copy'],['target','Second copy']]){const option=el('option',label);option.value=value;side.append(option)}
  const restoreButton=el('button','Restore selected copy from backup');restoreButton.type='button';restoreButton.onclick=()=>runChoice('recovery-action',{choice:'restore',side:side.value});
  const location=el('input');location.placeholder='Location relative to wallpaper storage';location.setAttribute('aria-label','Existing copy location');location.style.width='min(100%,600px)';
  const locateButton=el('button','Use this file to restore the missing copy');locateButton.type='button';locateButton.onclick=()=>runChoice('recovery-action',{choice:'locate',side:side.value,path:location.value});
  restore.append(el('p','Gallery verifies that the backup or located file exactly matches the missing image, then restores it without replacing an existing file.'),side,restoreButton,location,locateButton);actions.append(restore);
  recoveryRows.set(String(item.id),{section,status,actions,waiting:false,lastError:item.error});
  // Each preview uses the normal permission-checked image endpoint.
  const previews=el('div');
  const candidates=[['First copy',item.current_source_path,item.source_collection],['Second copy',item.current_target_path,item.target_collection],['Saved copy',item.survivor_path,item.survivor_collection]];
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
$('review-recovery').onclick=async()=>{try{await loadRecoveryOperations();$('recovery-dialog').showModal()}catch(e){liveText('database-progress',e.message)}};
$('recovery-find').onclick=async()=>{try{await loadRecoveryOperations()}catch(e){$('recovery-summary').textContent=e.message}};$('recovery-show-all').onclick=async()=>{$('recovery-find-id').value='';try{await loadRecoveryOperations()}catch(e){$('recovery-summary').textContent=e.message}};$('recovery-close').onclick=()=>$('recovery-dialog').close();
$('recovery-retry').onclick=async()=>{try{await api('retry-recovery-operation',{});$('recovery-summary').textContent='Recovery recheck requested. The worker will review unfinished operations.';await pollDatabase()}catch(e){$('recovery-summary').textContent=e.message}};
pollDatabase();setInterval(pollDatabase,5000);

$('export-json').onclick=()=>{const collection=$('collection').value;if(!collection){liveText('entry-message','Choose a collection to export.');return}location.href='/api/export-json?'+new URLSearchParams({collection})};
const adminControls=["export","import-updates","scan-manual","organize","check-duplicates","export-json","retry-failed","download-filtered","download-stop","reddit-status","duplicate-progress","database-progress","scan-issues","retry-scan-issues","clear-scan-issues","manual-scan-progress","organize-progress","download-progress","download-errors"];
for(const id of adminControls){const node=$(id);if(node)node.setAttribute("data-admin-only","")}
$("account-name").textContent=gallerySession.name+(isAdmin?" · Administrator":" · User");
$("sign-out").onclick=async()=>{try{await fetch("/auth/logout",{method:"POST",headers:{"X-Gallery-Request":"1"}});location.href="/auth/signed-out"}catch{location.reload()}};
if(!isAdmin)$("auth-message").textContent="Browse the collections or use Download to save an image to your device.";

async function loadRelatedImages(r,after,button){button.disabled=true;try{const result=await api('entry-files?'+new URLSearchParams({entry_id:r.entry_id,after}));const known=new Set([...$('modal-links').querySelectorAll('a')].map(a=>a.href));for(const item of result.links){const url=new URL(item.url,location.href).href;if(!known.has(url)){known.add(url);$('modal-links').insertBefore(link(item.file,item.url),button)}}if(result.next_after){button.textContent='More saved images';button.onclick=()=>loadRelatedImages(r,result.next_after,button);button.disabled=false}else button.remove()}catch(e){button.textContent=e.message;button.disabled=false}}
if(!legacyDownloads){for(const id of ['download-filtered','retry-failed','download-stop','download-queue','download-progress','download-errors','reddit-status','auth-message']){const node=$(id);if(node)node.hidden=true}}



let movingEntry=null,moveTimer=null,moveBusy=false;
async function openCollectionMove(row){
 if(!pageMeta){try{pageMeta=await api("inventory-meta")}catch(e){liveText("entry-message",e.message);return}}
 movingEntry=row;$('move-confirm').textContent='Move entry and files';clearTimeout(moveTimer);moveBusy=false;$('move-confirm').disabled=false;$('move-collection').disabled=false;$('move-message').textContent='';$('move-source').textContent=row.site+' · '+row.id+' — currently in '+row.collection;
 $('move-collection').replaceChildren();const placeholder=el('option','Choose a collection');placeholder.value='';$('move-collection').append(placeholder);for(const c of pageMeta?.collections||[]){if(c.id===row.collection)continue;const option=el('option',c.id);option.value=c.id;$('move-collection').append(option)}
 $('move-confirm').disabled=true;$('move-dialog').showModal();
}
async function pollCollectionMove(id){
 try{const job=await api('collection-move?id='+id);$('move-message').textContent=job.state==='completed'?'Moved to '+job.target_collection+'.':job.state==='failed'?'Move needs attention: '+job.error:'Moving entry and files: '+job.processed+' / '+job.total+' files. You can close this dialog; the move continues on the server.';
 if(job.state==='failed'&&job.current_revision){movingEntry.revision=job.current_revision;$('move-confirm').textContent='Retry move'}
 if(job.state==='completed'||job.state==='failed'){moveBusy=false;$('move-collection').disabled=false;$('move-confirm').disabled=job.state==='completed';if(job.state==='completed'){pageCursor='';cursorHistory=[];current=1;pageMeta=null;await refresh(true)}return}
 moveTimer=setTimeout(()=>pollCollectionMove(id),2000);
 }catch(e){$('move-message').textContent=e.message;moveTimer=setTimeout(()=>pollCollectionMove(id),5000)}
}
$('move-collection').onchange=()=>{$('move-confirm').disabled=moveBusy||!$('move-collection').value};
$('move-confirm').onclick=async()=>{
 if(moveBusy||!movingEntry||!$('move-collection').value)return;moveBusy=true;$('move-confirm').disabled=true;$('move-collection').disabled=true;$('move-message').textContent='Queuing move…';
 try{const job=await api('move-entry',{entry_id:movingEntry.entry_id,revision:movingEntry.revision,target_collection:$('move-collection').value});pollCollectionMove(job.move_id)}catch(e){moveBusy=false;$('move-confirm').disabled=false;$('move-collection').disabled=false;$('move-message').textContent=e.message}
};
$('move-cancel').onclick=()=>{$('move-dialog').close();clearTimeout(moveTimer)};

$('select-page').onclick=()=>{for(const r of data)selectedEntries.set(rowKey(r),r);updateSelection();render(true)};
$('clear-selection').onclick=()=>{selectedEntries.clear();updateSelection();render(true)};
$('bulk-move-open').onclick=async()=>{if(!selectedEntries.size||bulkMoveBusy)return;if(!pageMeta)pageMeta=await api('inventory-meta');const select=$('bulk-move-collection');select.replaceChildren();const blank=el('option','Choose a collection');blank.value='';select.append(blank);for(const c of pageMeta.collections){const option=el('option',c.id);option.value=c.id;select.append(option)}$('bulk-move-summary').textContent=selectedEntries.size.toLocaleString()+' selected entries';$('bulk-move-progress').textContent='';$('bulk-move-confirm').disabled=true;$('bulk-move-dialog').showModal()};
$('bulk-move-collection').onchange=()=>{$('bulk-move-confirm').disabled=!$('bulk-move-collection').value||bulkMoveBusy};
$('bulk-move-close').onclick=()=>$('bulk-move-dialog').close();
$('bulk-move-confirm').onclick=async()=>{if(bulkMoveBusy)return;const target=$('bulk-move-collection').value;if(!target)return;const entries=[...selectedEntries.values()].filter(r=>r.collection!==target);if(!entries.length){$('bulk-move-progress').textContent='All selected entries are already in that collection.';return}bulkMoveBusy=true;$('bulk-move-confirm').disabled=true;$('bulk-move-collection').disabled=true;updateSelection();const jobs=[];const errors=[];for(let i=0;i<entries.length;i++){const r=entries[i];$('bulk-move-progress').textContent='Queuing '+(i+1)+' / '+entries.length+' entries…';try{const job=await api('move-entry',{entry_id:r.entry_id,revision:r.revision,target_collection:target});jobs.push(job.move_id);selectedEntries.delete(rowKey(r))}catch(e){errors.push(r.site+' '+r.id+': '+e.message)}}updateSelection();render(true);$('bulk-move-progress').textContent=jobs.length+' moves queued. '+errors.length+' could not be queued.'+(errors.length?' '+errors.slice(0,3).join(' · '):'')+' Moves continue on the server; refresh to see their results.';bulkMoveBusy=false;$('bulk-move-collection').disabled=false;updateSelection();pageMeta=null;await refresh(true)};
