// FindYou 5 discovery/signalling service. No file upload, relay, TURN, or storage endpoints.
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const PORT = Number(process.env.PORT || 8377);
const HOST = process.env.HOST || '0.0.0.0';
const MAX_BODY = 65536;
const clients = new Map();
const logs = [];
const stats = {startedAt:new Date().toISOString(),connections:0,signals:0,signalBytes:0,connected:0,failed:0,fileBytes:0};
function log(type,text){logs.unshift({time:new Date().toISOString(),type,text});if(logs.length>200)logs.pop();}
function equal(a,b){const x=Buffer.from(String(a)),y=Buffer.from(String(b));return x.length===y.length&&crypto.timingSafeEqual(x,y);}
function json(res,code,value){res.writeHead(code,{'Content-Type':'application/json; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(JSON.stringify(value));}
function authorized(req,res){
  if(!process.env.ADMIN_PASSWORD)return true;
  const expected='Basic '+Buffer.from('admin:'+process.env.ADMIN_PASSWORD).toString('base64');
  if(equal(req.headers.authorization||'',expected))return true;
  res.writeHead(401,{'WWW-Authenticate':'Basic realm="FindYou Monitor"'});res.end('Authentication required');return false;
}
function emit(client,data){
  if(client.res.destroyed||client.res.writableLength>262144){client.res.destroy();return false;}
  client.res.write('event: signal\ndata: '+data+'\n\n');return true;
}
function announce(id,c){return new URLSearchParams({type:'announce',id,name:c.name,ips:c.ips,port:c.port,files:'0',sharing:'1'}).toString();}
function body(req,res,callback){
  let bytes=0,done=false;const chunks=[];
  req.on('data',chunk=>{if(done)return;bytes+=chunk.length;if(bytes>MAX_BODY){done=true;json(res,413,{error:'Signalling messages must be at most 64 KiB'});req.resume();return;}chunks.push(chunk);});
  req.on('end',()=>{if(!done){done=true;try{callback(JSON.parse(Buffer.concat(chunks).toString('utf8')));}catch(e){if(!res.writableEnded)json(res,400,{error:'Invalid signalling message'});}}});
  req.on('error',()=>{done=true;});
}
const server=http.createServer((req,res)=>{
  let u;try{u=new URL(req.url,'http://localhost');}catch{json(res,400,{error:'Invalid URL'});return;}
  const route=u.pathname;
  if(route==='/health'){json(res,200,{ok:true,mode:'signalling-only'});return;}
  if((route==='/'||route==='/api/status')&&req.method==='GET'){
    if(!authorized(req,res))return;
    if(route==='/api/status'){
      const devices=[...clients].map(([id,c])=>({id,name:c.name,group:c.room?'配对码分组':'同一网络',address:c.ips,port:c.port,since:c.since,status:c.status,signals:c.signals}));
      json(res,200,{stats,devices,logs});return;
    }
    try{const html=fs.readFileSync(path.join(__dirname,'relay.html'));res.writeHead(200,{'Content-Type':'text/html; charset=utf-8','Cache-Control':'no-store','X-Content-Type-Options':'nosniff'});res.end(html);}catch{json(res,500,{error:'relay.html missing'});}return;
  }
  if(route==='/hub'&&req.method==='GET'){
    const id=u.searchParams.get('cid')||'',auth=u.searchParams.get('auth')||'',name=(u.searchParams.get('name')||'未命名设备').slice(0,24),room=(u.searchParams.get('room')||'').trim().toLowerCase();
    if(!/^[a-zA-Z0-9_-]{8,64}$/.test(id)||!/^\w{24,128}$/.test(auth)||room.length>64){json(res,400,{error:'Invalid client identity'});return;}
    const old=clients.get(id);if(old&&!equal(old.auth,auth)){json(res,409,{error:'Identity already connected'});return;}if(old)old.res.end();
    const group=room?'room:'+crypto.createHash('sha256').update(room).digest('hex'):'ip:'+req.socket.remoteAddress;
    res.writeHead(200,{'Content-Type':'text/event-stream; charset=utf-8','Cache-Control':'no-store','Connection':'keep-alive','X-Accel-Buffering':'no'});res.write(':connected\n\n');
    const c={res,auth,name,room:!!room,group,ips:(u.searchParams.get('ips')||'').slice(0,300),port:(u.searchParams.get('port')||'').slice(0,5),since:new Date().toISOString(),status:'等待连接',signals:0,window:Date.now(),rate:0};
    clients.set(id,c);stats.connections++;log('online',name+' 已上线');
    for(const [peerId,peer] of clients){if(peerId!==id&&peer.group===group){emit(c,announce(peerId,peer));emit(peer,announce(id,c));}}
    const timer=setInterval(()=>{
      if(!res.destroyed){res.write(':heartbeat\n\n');for(const [peerId,peer] of clients)if(peerId!==id&&peer.group===group)emit(c,announce(peerId,peer));}
    },5000);
    res.on('close',()=>{clearInterval(timer);if(clients.get(id)===c){clients.delete(id);log('offline',name+' 已离线');}});return;
  }
  if(route==='/signal'&&req.method==='POST'){
    body(req,res,j=>{
      const me=clients.get(String(j.from||''));
      if(!me||!equal(me.auth,j.auth||'')){json(res,401,{error:'Sender is not authenticated'});return;}
      if(Date.now()-me.window>60000){me.window=Date.now();me.rate=0;}if(++me.rate>240){json(res,429,{error:'Signalling rate exceeded'});return;}
      if(typeof j.data!=='string'||j.data.length>60000){json(res,400,{error:'Invalid signal'});return;}
      const data=new URLSearchParams(j.data),type=data.get('type');
      if(!['workspace','announce','status'].includes(type)){json(res,400,{error:'Only discovery and connection signals are supported'});return;}
      if(type==='status'){
        const status=data.get('status');if(!['connected','http-connected','failed','disconnected'].includes(status)){json(res,400,{error:'Invalid connection status'});return;}
        me.status={connected:'P2P 已连接','http-connected':'HTTP 直传已连接',failed:'连接失败',disconnected:'等待连接'}[status];
        if(status==='connected'||status==='http-connected')stats.connected++;if(status==='failed')stats.failed++;
        log(status,me.name+'：'+me.status+'（客户端上报）');json(res,200,{ok:true});return;
      }
      let targets=[];
      if(type==='announce')targets=[...clients].filter(([id,c])=>id!==j.from&&c.group===me.group).map(([,c])=>c);
      else{const peer=clients.get(String(j.to||''));if(!peer||peer.group!==me.group){log('failed',me.name+'：目标离线或配对码不一致');json(res,404,{error:'Target not in the same discovery group'});return;}targets=[peer];
        let payload;try{payload=JSON.parse(data.get('data'));}catch{json(res,400,{error:'Invalid connection signal'});return;}
        if(!['offer','answer','ice','bye','reject','http-open'].includes(payload.kind)||payload.id!==j.from){json(res,400,{error:'File payloads are not accepted'});return;}
        if(payload.kind==='offer'){me.status='正在连接';log('connecting',me.name+' 发起连接请求');}
        if(payload.kind==='reject'){log('failed',me.name+' 拒绝了连接请求');}
      }
      data.set('from',j.from);data.set('id',j.from);
      if(type==='announce'){
        me.name=(data.get('name')||me.name).slice(0,24);data.set('name',me.name);
      }
      const encoded=data.toString();for(const peer of targets)emit(peer,encoded);me.signals++;stats.signals++;stats.signalBytes+=Buffer.byteLength(encoded);json(res,200,{ok:true});
    });return;
  }
  if(route.startsWith('/relay/')||route.startsWith('/upload')){log('blocked','已拒绝文件中继请求');json(res,410,{error:'File relay is disabled. Use the P2P data channel.'});req.resume();return;}
  json(res,404,{error:'Not found'});
});
server.headersTimeout=15000;server.requestTimeout=30000;server.keepAliveTimeout=65000;
server.listen(PORT,HOST,()=>console.log('FindYou discovery monitor: http://'+HOST+':'+PORT+' (signalling only; file relay disabled)'));
