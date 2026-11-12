'use strict';
const vscode=require('vscode'),http=require('http'),fs=require('fs'),path=require('path'),crypto=require('crypto');
let server;
exports.activate=async function(context){
  const root=process.env.RAMFLOW_APP_DIR,token=process.env.RAMFLOW_APP_TOKEN;
  if(!root||!token)throw new Error('Isolated research environment missing');
  const document=await vscode.workspace.openTextDocument(path.join(root,'workspace','fixture.ts'));
  const editor=await vscode.window.showTextDocument(document);
  let busy=false;
  server=http.createServer(async(req,res)=>{
    if(req.method!=='POST'||req.headers.authorization!==`Bearer ${token}`){res.writeHead(403).end();return;}
    if(busy){res.writeHead(409).end();return;}busy=true;
    try{
      let body='';for await(const piece of req){body+=piece;if(body.length>4096)throw new Error('Request too large');}
      const {feature,id}=JSON.parse(body);if(!['completion','save'].includes(feature))throw new Error('Unknown feature');
      const line=document.lineCount-1,range=document.lineAt(line).range,start=performance.now();
      const text=feature==='completion'?'item.':`// saved ${id}`;
      if(!await editor.edit(edit=>edit.replace(range,text)))throw new Error('Edit failed');
      let correct=false,checksum;
      if(feature==='completion'){
        const answer=await vscode.commands.executeCommand('vscode.executeCompletionItemProvider',document.uri,new vscode.Position(line,5),'.');
        const labels=(answer?.items||[]).map(x=>typeof x.label==='string'?x.label:x.label.label);
        correct=['id','label','score','group'].every(x=>labels.includes(x));checksum=labels.sort().join('|');
      }else{
        correct=await document.save();checksum=crypto.createHash('sha256').update(document.getText()).digest('hex');
      }
      const latency_ms=performance.now()-start;
      if(feature==='save')correct=correct&&fs.readFileSync(document.uri.fsPath,'utf8')===document.getText();
      res.writeHead(200,{'Content-Type':'application/json'}).end(JSON.stringify({id,feature,latency_ms,correct,checksum,
        endpoint:feature==='completion'?'editor.edit to completion provider API return':'editor.edit to document.save API return; disk contents verified separately'}));
    }catch(error){res.writeHead(500,{'Content-Type':'application/json'}).end(JSON.stringify({error:String(error)}));}
    finally{busy=false;}
  });
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  fs.writeFileSync(path.join(root,'extension-ready.json'),JSON.stringify({port:server.address().port,pid:process.pid,version:vscode.version}));
  context.subscriptions.push({dispose(){server.close();}});
};
exports.deactivate=()=>server?.close();
