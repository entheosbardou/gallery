'use strict';
window.multiFilters=(()=>{
 const filters=new Map();
 for(const [id,noun,title] of [['site','sources','Source'],['status','statuses','Status'],['category','categories','Category']]){
  const select=document.getElementById(id),button=document.getElementById(id+'-trigger'),panel=document.getElementById(id+'-choices');
  filters.set(id,{select,button,panel,noun,title,all:id!=='status',values:new Set(id==='status'?['saved']:[]),options:[]});
  button.onclick=()=>{const open=panel.hidden;close();if(open){window.collectionMenu?.close();panel.hidden=false;button.setAttribute('aria-expanded','true');position(filters.get(id));panel.querySelector('input')?.focus()}};
  button.onkeydown=e=>{if(e.key==='ArrowDown'){e.preventDefault();if(panel.hidden)button.click();else panel.querySelector('input')?.focus()}};
  panel.onkeydown=e=>{const inputs=[...panel.querySelectorAll('input')],index=inputs.indexOf(document.activeElement);if(['ArrowDown','ArrowUp','Home','End'].includes(e.key)){e.preventDefault();inputs[e.key==='Home'?0:e.key==='End'?inputs.length-1:e.key==='ArrowDown'?(index+1)%inputs.length:(index-1+inputs.length)%inputs.length]?.focus()}};
 }
 function position(f){const rect=f.button.getBoundingClientRect();f.panel.style.left=Math.max(8,Math.min(rect.left,innerWidth-f.panel.offsetWidth-8))+'px';f.panel.style.maxHeight=Math.max(120,innerHeight-16)+'px';f.panel.style.top=Math.max(8,Math.min(rect.bottom+4,innerHeight-f.panel.offsetHeight-8))+'px'}
 function close(){for(const f of filters.values()){f.panel.hidden=true;f.button.setAttribute('aria-expanded','false')}}
 function sync(f){
  for(const option of f.select.options)option.selected=f.all||f.values.has(option.value);
  const count=f.options.filter(o=>f.values.has(o.value)).length;
  f.button.textContent=f.all?'All '+f.noun:count===0?'No '+f.noun:count===1?f.options.find(o=>f.values.has(o.value)).label:count+' '+f.noun+' selected';
  f.button.title=f.all?'All '+f.noun:f.options.filter(o=>f.values.has(o.value)).map(o=>o.label).join(', ')||'No '+f.noun;
  const all=f.panel.querySelector('[data-all]');if(all){all.checked=f.all;all.indeterminate=!f.all&&count>0;for(const check of f.panel.querySelectorAll('input:not([data-all])'))check.checked=f.all||f.values.has(check.value)}
 }
 function notify(f){sync(f);f.select.dispatchEvent(new Event('change',{bubbles:true}))}
 function update(id,options){
  const f=filters.get(id),signature=JSON.stringify(options);if(f.signature===signature){sync(f);return}f.signature=signature;f.options=options;
  f.values=new Set([...f.values].filter(value=>options.some(o=>o.value===value)));f.select.replaceChildren(...options.map(o=>new Option(o.label,o.value)));
  f.panel.replaceChildren();const add=(text,value,isAll=false)=>{const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.value=value;if(isAll)check.dataset.all='true';label.append(check,document.createTextNode(text));f.panel.append(label);check.onchange=()=>{if(isAll){f.all=check.checked;f.values=new Set(check.checked?f.options.map(o=>o.value):[])}else{if(f.all){f.values=new Set(f.options.map(o=>o.value));f.all=false}if(check.checked)f.values.add(value);else f.values.delete(value);f.all=f.options.length>0&&f.options.every(o=>f.values.has(o.value))}notify(f)}};
  add('All '+f.noun+' ('+options.length+')','',true);for(const o of options)add(o.label,o.value);sync(f);if(!f.panel.hidden)position(f);
 }
 function reset(){for(const [id,f] of filters){f.all=id!=='status';f.values=new Set(id==='status'?['saved']:[]);sync(f)}}
 document.addEventListener('pointerdown',e=>{if(![...filters.values()].some(f=>f.button.contains(e.target)||f.panel.contains(e.target)))close()});
 document.addEventListener('keydown',e=>{if(e.key==='Escape'){const f=[...filters.values()].find(f=>!f.panel.hidden);if(f){e.preventDefault();close();f.button.focus()}}});
 window.addEventListener('resize',close);window.addEventListener('scroll',e=>{if(![...filters.values()].some(f=>f.panel.contains(e.target)))close()},true);
 return {update,reset,close,matches:(id,value)=>{const f=filters.get(id);return f.all||f.values.has(value)},isAll:id=>filters.get(id).all,values:id=>[...filters.get(id).values]};
})();
