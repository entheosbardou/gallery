'use strict';
// Keep the existing select as the gallery's filter value; expose a keyboard-friendly nested menu.
window.collectionMenu=(()=>{
 const select=document.getElementById('collection'),trigger=document.getElementById('collection-trigger'),host=document.getElementById('collection-menu');
 let root={children:new Map()},panels=[],collectionIds=new Set(),hoverTimer;
 function make(tag,text,cls){const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n}
 function close(focus=false){clearTimeout(hoverTimer);host.replaceChildren();panels=[];trigger.setAttribute('aria-expanded','false');if(focus)trigger.focus()}
 function trim(depth){while(panels.length>depth){const removed=panels.pop();removed.anchor.querySelector('.collection-submenu')?.setAttribute('aria-expanded','false');removed.element.remove()};if(panels.length)panels[panels.length-1].element.style.visibility=''}
 function choose(value){select.value=value;updateLabel();close(true);select.dispatchEvent(new Event('change',{bubbles:true}))}
 function updateLabel(){const option=select.selectedOptions[0];trigger.textContent=option?option.textContent:'All collections';trigger.title=select.value||'All collections'}
 function items(panel){return [...panel.querySelectorAll('button:not(:disabled)')]}
 function open(node,depth,anchor,focus=false){
  clearTimeout(hoverTimer);if(panels[depth]?.node===node){if(focus)items(panels[depth].element)[0]?.focus();return}trim(depth);
  const panel=make('div',undefined,'collection-panel');panel.setAttribute('role','menu');panel.setAttribute('aria-label',node.path?node.path+' collections':'Collections');
  const narrow=window.innerWidth<700;
  if(depth&&narrow){const back=make('button','‹ Back','collection-back');back.type='button';back.setAttribute('role','menuitem');back.onclick=()=>{trim(depth);items(panels[depth-1].element)[0]?.focus()};panel.append(back)}
  if(node.path)panel.append(make('div',node.path,'collection-menu-heading'));
  if(!depth){const all=make('button','All collections','collection-choice');all.type='button';all.setAttribute('role','menuitem');all.onclick=()=>choose('');panel.append(all)}
  for(const child of [...node.children.values()].sort((a,b)=>a.name.localeCompare(b.name,undefined,{numeric:true,sensitivity:'base'}))){
   const row=make('div',undefined,'collection-menu-row'),button=make('button',child.name+(child.count!==undefined?' ('+child.count.toLocaleString()+')':''),'collection-choice');button.type='button';button.setAttribute('role','menuitem');button.title=child.path;
   if(child.id===select.value)button.classList.add('selected');
   button.onclick=()=>child.id!==undefined?choose(child.id):open(child,depth+1,row,true);
   row.append(button);
   if(child.children.size){const more=make('button','›','collection-submenu');more.type='button';more.setAttribute('role','menuitem');more.setAttribute('aria-haspopup','menu');more.setAttribute('aria-expanded','false');more.setAttribute('aria-label','Open '+child.name+' subcollections');more.onclick=()=>open(child,depth+1,row,true);row.append(more);button.onkeydown=e=>{if(e.key==='ArrowRight'){e.preventDefault();open(child,depth+1,row,true)}};
    row.onpointerenter=e=>{if(e.pointerType==='mouse'&&!narrow){clearTimeout(hoverTimer);hoverTimer=setTimeout(()=>open(child,depth+1,row),180)}};
   }else row.onpointerenter=()=>{clearTimeout(hoverTimer);trim(depth+1)};
   panel.append(row);
  }
  host.append(panel);panels.push({node,element:panel,anchor});anchor.querySelector('.collection-submenu')?.setAttribute('aria-expanded','true');
  const rect=anchor.getBoundingClientRect(),width=panel.offsetWidth;
  let left=depth&&!narrow?rect.right+4:trigger.getBoundingClientRect().left;
  if(left+width>window.innerWidth-8)left=depth&&!narrow?rect.left-width-4:window.innerWidth-width-8;
  left=Math.max(8,Math.min(left,window.innerWidth-width-8));
  let top=depth&&!narrow?rect.top:trigger.getBoundingClientRect().bottom+4;
  panel.style.maxHeight=Math.max(120,window.innerHeight-16)+'px';
  top=Math.max(8,Math.min(top,window.innerHeight-panel.offsetHeight-8));panel.style.left=left+'px';panel.style.top=top+'px';panel.style.zIndex=1000+depth;
  if(narrow)panels.slice(0,-1).forEach(p=>p.element.style.visibility='hidden');
  panel.onkeydown=e=>{const list=items(panel),i=list.indexOf(document.activeElement);if(['ArrowDown','ArrowUp','Home','End'].includes(e.key)){e.preventDefault();list[e.key==='Home'?0:e.key==='End'?list.length-1:e.key==='ArrowDown'?(i+1)%list.length:(i-1+list.length)%list.length]?.focus()}else if(e.key==='ArrowLeft'&&depth){e.preventDefault();trim(depth);panels[depth-1].element.style.visibility='';items(panels[depth-1].element)[0]?.focus()}};
  if(focus)items(panel)[0]?.focus();trigger.setAttribute('aria-expanded','true');
 }
 function update(collections,selected){
  close();root={children:new Map()};collectionIds=new Set(collections.map(c=>c.id));
  for(const collection of collections){let node=root;const parts=collection.id==='.'?['.']:collection.id.split('/');let path='';for(const part of parts){path=path?path+'/'+part:part;if(!node.children.has(part))node.children.set(part,{name:part==='.'?'Root collection':part,path,children:new Map()});node=node.children.get(part)}node.id=collection.id;node.count=collections.filter(c=>c.id===collection.id||collection.id==='.'||c.id.startsWith(collection.id+'/')).reduce((sum,c)=>sum+c.records,0)}
  if(root.children.has('.')){const base=root.children.get('.');for(const [key,value] of root.children)if(key!=='.')base.children.set(key,value);root.children=new Map([['.',base]])}
  select.value=collectionIds.has(selected)?selected:'';updateLabel();
 }
 trigger.onclick=()=>panels.length?close():open(root,0,trigger,true);
 trigger.onkeydown=e=>{if(e.key==='ArrowDown'){e.preventDefault();open(root,0,trigger,true)}};
 select.addEventListener('change',updateLabel);
 document.addEventListener('pointerdown',e=>{if(!host.contains(e.target)&&!trigger.contains(e.target))close()});
 document.addEventListener('keydown',e=>{if(e.key==='Escape'&&panels.length){e.preventDefault();e.stopPropagation();close(true)}});
 window.addEventListener('resize',()=>close());window.addEventListener('scroll',e=>{if(!host.contains(e.target))close()},true);
 return {update,updateLabel,close};
})();
