const cp=require('child_process'),assert=require('assert'),http=require('http'),path=require('path');
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const p=cp.spawn(process.execPath,[path.resolve('findyou-relay.js')],{env:{...process.env,PORT:'18389',HOST:'127.0.0.1'},windowsHide:true,stdio:'ignore'});
const base='http://127.0.0.1:18389',auth='a'.repeat(32),bAuth='b'.repeat(32),sse=[];
(async()=>{try{
 for(let i=0;i<100;i++){try{if((await fetch(base+'/health')).ok)break;}catch{}await sleep(100);}
 for(const [id,token] of [['aaaaaaaa',auth],['bbbbbbbb',bAuth]])sse.push(http.get(base+'/hub?cid='+id+'&auth='+token+'&room=test&name='+id,r=>r.on('data',()=>{})));
 for(let i=0;i<50;i++){if((await fetch(base+'/api/status').then(r=>r.json())).devices.length===2)break;await sleep(100);}
 const post=data=>fetch(base+'/signal',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({from:'aaaaaaaa',to:'bbbbbbbb',auth,data:new URLSearchParams(data).toString()})});
 assert((await post({type:'workspace',data:JSON.stringify({kind:'http-open',id:'aaaaaaaa',sid:'test'})})).ok);
 assert((await post({type:'status',status:'http-connected'})).ok);
 const d=await fetch(base+'/api/status').then(r=>r.json());assert.equal(d.stats.connected,1);assert.equal(d.stats.fileBytes,0);assert.equal(d.devices.find(x=>x.id==='aaaaaaaa').status,'HTTP 直传已连接');
 assert.equal((await fetch(base+'/relay/no',{method:'POST',body:'no'})).status,410);
 console.log('PASS HTTP connection signals/status accepted, dashboard distinguishes HTTP, file relay remains disabled');
}finally{for(const req of sse)req.destroy();p.kill();}})().catch(e=>{console.error(e);process.exitCode=1;});
