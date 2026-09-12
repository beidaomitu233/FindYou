const fs=require('fs'),path=require('path'),os=require('os'),cp=require('child_process'),crypto=require('crypto'),assert=require('assert');
const exe=path.resolve(process.argv[2]||'output/FindYou.exe'), label=process.argv[3]||'sample';
const root=fs.mkdtempSync(path.join(os.tmpdir(),'findyou-throughput-')),procs=[],urls={};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
async function until(fn,msg){for(let i=0;i<2400;i++){const result=await fn();if(result)return result;await wait(50);}throw Error(msg);}
async function api(n,route,args){const r=await fetch(urls[n]+route+'?k='+n.repeat(32),args?{method:'POST',body:new URLSearchParams(args)}:{});const d=await r.json();assert(d.ok,d.error);return d;}
async function hash(p){const h=crypto.createHash('sha256');for await(const b of fs.createReadStream(p))h.update(b);return h.digest('hex');}
function file(p,bytes){const fd=fs.openSync(p,'wx'),block=crypto.randomBytes(1024*1024);try{while(bytes){const n=Math.min(bytes,block.length);fs.writeSync(fd,block,0,n);bytes-=n;}}finally{fs.closeSync(fd);}}
(async()=>{try{
 for(const [n,port] of [['a',54518],['b',54528]]){const d=path.join(root,n);fs.mkdirSync(d);fs.writeFileSync(path.join(d,'config.txt'),'FindYouConfig2\nid='+n.repeat(32)+'\ncloud=0\nrelay=\nfw=1\ndl='+encodeURIComponent(path.join(d,'received')));const p=cp.spawn(exe,['/noui','/data='+d,'/port='+port,'/udp='+(port+1),'/apitoken='+n.repeat(32)],{windowsHide:true,stdio:'ignore'});procs.push(p);urls[n]='http://127.0.0.1:'+port;await until(async()=>{try{return (await api(n,'/api/local/native/state')).ok;}catch{return false;}},'startup');}
 await api('a','/api/local/peers/add',{host:'127.0.0.1:54528'});await api('b','/api/local/peers/add',{host:'127.0.0.1:54518'});await api('a','/api/local/native/connect',{peerId:'b'.repeat(32)});
 const folder=path.join(root,'四个文档');fs.mkdirSync(folder);for(let i=0;i<4;i++)file(path.join(folder,i+'.bin'),Math.floor(130*1024*1024/4));
 const large=path.join(root,'large.bin');file(large,1024*1024*1024);
 const results=[];
 for(const [source,bytes] of [[folder,130*1024*1024],[large,1024*1024*1024]]){
  const start=performance.now(),job=await api('a','/api/local/native/send',{path:source});
  await until(async()=>{const s=(await api('a','/api/local/native/state')).sends.find(x=>x.id===job.id);if(s?.progress<0)throw Error(s.status);return s?.progress===100;},'send');
  const seconds=(performance.now()-start)/1000,dest=path.join(root,'b','received',path.basename(source));
  if(source===folder)for(const name of fs.readdirSync(folder))assert.equal(await hash(path.join(folder,name)),await hash(path.join(dest,name)));else assert.equal(await hash(source),await hash(dest));
  results.push({bytes,seconds,MBps:bytes/1e6/seconds,hash:'matched'});
 }
 const cancelled=await api('a','/api/local/native/send',{path:large});
 await until(async()=>{const s=(await api('a','/api/local/native/state')).sends.find(x=>x.id===cancelled.id);if(s?.progress===100)throw Error('Cancel test completed before interruption');return s?.progress>0;},'active transfer');
 await api('a','/api/local/native/cancel',{id:cancelled.id});
 await until(async()=>(await api('a','/api/local/native/state')).sends.find(x=>x.id===cancelled.id)?.progress<0,'cancel status');
 await until(()=>!fs.readdirSync(path.join(root,'b','received')).some(x=>x.startsWith('.findyou-')),'cancel cleanup');
 console.log('PASS cancellation during active HTTP transfer and receiver partial cleanup');
 console.log(JSON.stringify({label,environment:'same-host loopback; includes receiver commit; warm cache possible; random binary samples, not real Word files',results},null,2));
}finally{for(const n of ['a','b'])if(urls[n])await api(n,'/api/local/quit',{}).catch(()=>{});await wait(1000);for(const p of procs)if(p.exitCode===null)p.kill();fs.rmSync(root,{recursive:true,force:true,maxRetries:5,retryDelay:300});}})().catch(e=>{console.error(e);process.exitCode=1;});
