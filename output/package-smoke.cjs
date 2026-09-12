const fs=require('fs'),path=require('path'),os=require('os'),cp=require('child_process'),assert=require('assert');
const root=fs.mkdtempSync(path.join(os.tmpdir(),'findyou-package-')),exe=path.join(root,'FindYou.exe'),token='c'.repeat(32),port=53968;
fs.copyFileSync(path.resolve(process.argv[2]||'FindYou.exe'),exe);
fs.writeFileSync(path.join(root,'config.txt'),'FindYouConfig2\ncloud=0\nrelay=\nfw=1');
const p=cp.spawn(exe,['/noui','/data='+root,'/port='+port,'/udp='+(port+1),'/apitoken='+token],{windowsHide:true,stdio:'ignore'});
const wait=ms=>new Promise(r=>setTimeout(r,ms));
let origin;
(async()=>{try{
 for(let i=0;i<100;i++){try{const match=fs.readFileSync(path.join(root,'findyou.log'),'utf8').match(/本机控制服务 (http:\/\/[^\r\n]+)/);if(match){origin=match[1];break;}}catch{}await wait(100);}
 assert(origin,'service did not start');
 const info=await fetch(origin+'/api/info').then(r=>r.text());assert(info.includes('transfer=1')&&info.includes('stream=1'));
 const rootResponse=await fetch(origin+'/');assert.equal(rootResponse.status,403);assert(!(await rootResponse.text()).includes('<html'));
 const denied=await fetch(origin+'/api/local/native/state').then(r=>r.json());assert.equal(denied.ok,false);
 const state=await fetch(origin+'/api/local/native/state?k='+token).then(r=>r.json());assert.equal(state.ok,true);
 await fetch(origin+'/api/local/quit?k='+token,{method:'POST'});
 for(let i=0;i<100&&p.exitCode===null;i++)await wait(100);
 assert.notEqual(p.exitCode,null,'process did not exit');assert.equal(p.exitCode,0);
 console.log('PASS standalone native executable, protected local API, no embedded browser UI');
}finally{
 if(p.exitCode===null&&origin)try{await fetch(origin+'/api/local/quit?k='+token,{method:'POST'});}catch{}
 for(let i=0;i<20&&p.exitCode===null;i++)await wait(100);
 if(p.exitCode===null)p.kill();fs.rmSync(root,{recursive:true,force:true,maxRetries:5,retryDelay:200});
}})().catch(e=>{console.error(e);process.exitCode=1;});
