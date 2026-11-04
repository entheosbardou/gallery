'use strict';
window.RufflePlayer={config:{publicPath:'/vendor/ruffle/',polyfills:false,allowScriptAccess:false,allowNetworking:'none',openUrlMode:'deny',autoplay:'off',allowFullscreen:true}};
(async()=>{
 const $=id=>document.getElementById(id),message=$('message'),controls=$('controls');
 let player,api,ready=false,busy=false,muted=false,volume=1,lastVolume=1,timer;
 try{const saved=JSON.parse(localStorage.getItem('gallery-flash-audio')||'null');if(saved&&Number.isFinite(saved.volume)){volume=Math.max(0,Math.min(1,saved.volume));lastVolume=volume||1;muted=!!saved.muted}}catch{}
 function saveAudio(){try{localStorage.setItem('gallery-flash-audio',JSON.stringify({volume,muted}))}catch{}}
 function applyAudio(){if(ready)api.volume=muted?0:volume;$('volume').value=String(Math.round(volume*100));$('volume-value').textContent=Math.round(volume*100)+'%';$('mute').textContent=muted?'Unmute':'Mute';$('mute').setAttribute('aria-pressed',String(muted));saveAudio()}
 function sync(){if(!ready)return;const playing=api.isPlaying;$('play-pause').textContent=playing?'Pause':'Play';$('play-pause').setAttribute('aria-label',playing?'Pause playback':'Play playback');if(!busy){const state=playing?'Playing':'Paused';if($('playback-state').textContent!==state)$('playback-state').textContent=state;}for(const id of ['play-pause','restart','mute','volume'])$(id).disabled=busy}
 $('play-pause').onclick=()=>{if(!ready||busy)return;if(api.isPlaying)api.suspend();else api.resume();sync()};
 $('restart').onclick=async()=>{if(!ready||busy)return;busy=true;$('playback-state').textContent='Restarting…';sync();try{await api.reload();applyAudio();api.resume()}catch{message.hidden=false;message.textContent='Could not restart this Flash file. Close it and try opening it again.'}finally{busy=false;sync()}};
 $('mute').onclick=()=>{if(busy)return;muted=!muted;if(!muted&&volume===0)volume=lastVolume||1;applyAudio()};
 $('volume').oninput=()=>{volume=Number($('volume').value)/100;if(volume>0)lastVolume=volume;muted=volume===0;applyAudio()};
 $('fullscreen').onclick=async()=>{try{if(document.fullscreenElement)await document.exitFullscreen();else await document.documentElement.requestFullscreen()}catch{$('playback-state').textContent='Fullscreen unavailable'}};
 document.addEventListener('fullscreenchange',()=>{$('fullscreen').textContent=document.fullscreenElement?'Exit fullscreen':'Fullscreen'});
 window.addEventListener('pagehide',()=>{clearInterval(timer);if(ready)api.suspend()});
 try{
  const url=new URL(new URLSearchParams(location.search).get('file')||'',location.origin);
  if(url.origin!==location.origin||url.pathname!=='/api/image')throw Error('Choose a saved Flash file from the gallery.');
  const response=await fetch(url.href,{credentials:'same-origin'});if(!response.ok)throw Error('This file is unavailable or you no longer have permission to view it.');
  const bytes=await response.arrayBuffer(),header=new Uint8Array(bytes,0,Math.min(3,bytes.byteLength));if(!['FWS','CWS','ZWS'].includes(String.fromCharCode(...header)))throw Error('This is not a valid SWF file.');
  await new Promise((resolve,reject)=>{const script=document.createElement('script');script.src='/vendor/ruffle/ruffle.js';script.onload=resolve;script.onerror=()=>reject(Error('The Flash player could not load.'));document.head.append(script)});
  player=window.RufflePlayer.newest().createPlayer();$('player').append(player);api=player.ruffle();message.hidden=true;
  await api.load({data:new Uint8Array(bytes),swfFileName:(url.searchParams.get('file')||'animation.swf').split('/').pop(),allowScriptAccess:false,allowNetworking:'none',openUrlMode:'deny',autoplay:'off',allowFullscreen:true});
  ready=true;controls.hidden=false;applyAudio();sync();timer=setInterval(sync,250);
  $('fullscreen').disabled=!document.fullscreenEnabled;
 }catch(error){clearInterval(timer);$('player').replaceChildren();controls.hidden=true;message.hidden=false;message.textContent=error.message||'This Flash file could not be played.'}
})();
