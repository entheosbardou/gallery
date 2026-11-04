'use strict';
const {chromium}=require('playwright-core');
const path=require('path');
async function withPage(context,read){
 const page=await context.newPage();
 try{return await read(page)}finally{await page.close({runBeforeUnload:false}).catch(()=>{})}
}
async function closeContext(context){
 // Close tabs explicitly so the persistent profile does not save used tabs for restoration.
 await Promise.all(context.pages().map(page=>page.close({runBeforeUnload:false}).catch(()=>{})));
 await context.close();
}
(async()=>{
 let context;
 try{
  context=await chromium.launchPersistentContext(path.join(process.env.LOCALAPPDATA,'WallpaperGallery','RedditEdge'),{
   channel:'msedge',headless:true,timeout:15000,args:['--no-first-run','--disable-background-mode']
  });
  if(process.argv.includes('--page-status')){
   const id=process.argv[process.argv.indexOf('--page-status')+1];if(!/^[a-z0-9]+$/.test(id))throw Error('Invalid post');
   const result=await withPage(context,async page=>{
   const response=await page.goto('https://www.reddit.com/comments/'+id+'/',{waitUntil:'domcontentloaded',timeout:15000});
   await page.locator('shreddit-post[post-id="t3_'+id+'"]').first().waitFor({state:'attached',timeout:15000}).catch(()=>{});
   const info=await page.locator('shreddit-post[post-id="t3_'+id+'"]').evaluateAll(posts=>posts.map(post=>({hasImage:!!post.getAttribute('content-href'),type:post.getAttribute('post-type')})));
   const diagnostics=await page.evaluate(()=>({host:location.host,postElements:document.querySelectorAll('shreddit-post').length,images:document.images.length,ageGate:/verify your age|confirm your age|mature content|over 18/i.test(document.body.innerText),loginGate:/log in to continue|log in to view/i.test(document.body.innerText),blocked:/blocked|access denied/i.test(document.body.innerText),removed:/removed|deleted/i.test(document.body.innerText)}));
   return {status:response.status(),posts:info,diagnostics};
   });
   await closeContext(context);context=null;
   process.stdout.write(JSON.stringify(result)+'\n');return;
  }
  const postFlag=process.argv.includes('--post')?'--post':process.argv.includes('--post-status')?'--post-status':null;
  if(postFlag){
   const id=process.argv[process.argv.indexOf(postFlag)+1];if(!/^[a-z0-9]+$/.test(id))throw Error('Invalid post');
   const result=await withPage(context,async page=>{
   const response=await page.goto('https://www.reddit.com/comments/'+id+'.json?raw_json=1&limit=1',{waitUntil:'domcontentloaded',timeout:15000});
   const status=response.status(),body=await response.text();
   return postFlag==='--post-status'?{status,json:body.trim().startsWith('[')}:{status,body};
   });
   await closeContext(context);context=null;
   process.stdout.write(JSON.stringify(result)+'\n');return;
  }
  const cookies=(await context.cookies()).filter(c=>/^(?:.*\.)?(?:reddit\.com|redd\.it)$/.test(c.domain.replace(/^\./,''))).map(c=>({domain:c.domain,path:c.path,name:c.name,value:c.value}));
  await closeContext(context);context=null;
  const result={browser:'Dedicated Edge',available:cookies.some(c=>c.name==='reddit_session')};
  if(!process.argv.includes('--status'))result.cookies=cookies;
  process.stdout.write(JSON.stringify(result)+'\n');
 }catch(error){
  if(process.argv.includes('--page-status')||process.argv.includes('--post-status'))process.stderr.write(String(error.message).split('\n')[0].slice(0,300)+'\n');
  // Profile locks during interactive sign-in are expected. Never print cookie data or raw browser errors.
  process.stdout.write(JSON.stringify({browser:'Dedicated Edge',available:false,cookies:[],error:String(error.message).includes('ProcessSingleton')?'profile_in_use':error.name==='TimeoutError'?'timeout':'browser_error'})+'\n');
 }finally{if(context)await closeContext(context).catch(()=>{})}
})().catch(()=>process.exitCode=1);
