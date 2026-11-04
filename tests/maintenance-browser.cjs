// Inert HTTP fixtures only. Never starts the product, a Windows service, or a driver.
const fs=require('node:fs'), path=require('node:path'), http=require('node:http'), assert=require('node:assert/strict');
const {chromium}=require('playwright');
const web=path.resolve(process.env.I18N_WEB_ROOT||path.join(__dirname,'../Core/wwwroot'));
const out=path.resolve(process.env.MAINTENANCE_TEST_OUTPUT||path.join(__dirname,'../artifacts/maintenance'));fs.mkdirSync(out,{recursive:true});
const server=http.createServer((req,res)=>{const file=path.resolve(web,'.'+new URL(req.url,'http://localhost').pathname.replace(/\/$/,'/index.html'));if(!file.startsWith(web+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}res.setHeader('Content-Type',({'.js':'text/javascript','.css':'text/css','.json':'application/json','.html':'text/html'})[path.extname(file)]||'text/plain');fs.createReadStream(file).pipe(res);});
(async()=>{await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;const browser=await chromium.launch({channel:'msedge',headless:true});let checks=0;
try {for(const file of fs.readdirSync(path.join(web,'locales')).filter(f=>f.endsWith('.json')))for(const mode of ['Safe','Normal']) {
 const lang=file.slice(0,-5), table=JSON.parse(fs.readFileSync(path.join(web,'locales',file),'utf8'));
 const context=await browser.newContext({viewport:{width:['ru','de','ja','ko','zh','uk'].includes(lang)?375:1280,height:900},reducedMotion:'reduce',locale:lang.replaceAll('_','-')});const page=await context.newPage();const errors=[],writes=[];let policy={activeMode:mode,nextMode:mode,configuredMode:mode,revision:1,restartRequired:false};
 page.on('pageerror',e=>errors.push(e.message));await page.route('**/api/**',async route=>{const req=route.request(),url=new URL(req.url());let body=[];
  if(url.pathname==='/api/settings/operation-mode') {if(req.method()==='PUT'){const data=req.postDataJSON();assert.equal(data.confirmNormal,true);writes.push(url.pathname);policy={...policy,nextMode:data.operationMode,revision:2,restartRequired:true};}body=policy;}
  else {if(req.method()!=='GET')writes.push(url.pathname);if(url.pathname==='/api/settings')body={logLevel:'Error'};if(url.pathname.endsWith('/status'))body={installed:false,active:false,busy:false};if(url.pathname.endsWith('/log'))body={lines:[],busy:false};if(url.pathname==='/api/seats/inventory')body={monitors:[],audioOutputs:[],audioInputs:[],devices:[]};}
  await route.fulfill({json:body});});
 await page.goto(origin);
 try {await page.waitForFunction(()=>typeof maintenanceStatus!=='undefined'&&maintenanceStatus!==null&&!document.documentElement.classList.contains('i18n-loading'));}
 catch(error) {throw new Error(lang+':'+mode+' initialization failed; JS errors='+JSON.stringify(errors)+'; state='+await page.evaluate(()=>JSON.stringify({status:typeof maintenanceStatus==='undefined'?'missing':maintenanceStatus,i18n:typeof I18n,body:document.body?.className})),{cause:error});}
 await page.locator('#maintenance-banner').waitFor({state:mode==='Safe'?'visible':'hidden'});assert.equal(await page.locator('#maintenance-banner').isVisible(),mode==='Safe',lang+':'+mode);checks++;
 await page.getByRole('button',{name:table['nav.more'],exact:true}).click();await page.locator('#more-pop').getByRole('button',{name:table['settings.title'],exact:true}).click();await page.locator('#settings-modal').waitFor({state:'visible'});
 assert.equal(await page.locator('#maintenance-current').textContent(),table['maintenance.'+mode.toLowerCase()]);assert.equal(await page.locator('#maintenance-mode').inputValue(),mode);checks++;
 assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);checks++;
 if(mode==='Safe') {
  await page.evaluate(()=>{maintenanceStatus.residualState={usbCapture:'pending',consoleLock:'unknown',needsAttention:true};renderMaintenance();});
  assert.equal(await page.locator('#maintenance-recovery').textContent(),table['maintenance.recovery']);
  assert.equal(await page.locator('#maintenance-recovery').isVisible(),true);assert.deepEqual(writes,[]);checks++;
  assert.equal(await page.locator('#maintenance-recovery').evaluate(el=>el.scrollWidth<=el.clientWidth+1),true);checks++;
  if(['ru','en','de','ja','ko','zh','uk'].includes(lang))await page.screenshot({path:path.join(out,lang+'-Safe-recovery.png'),fullPage:true});
  await page.evaluate(()=>{maintenanceStatus.residualState={needsAttention:false};renderMaintenance();});assert.equal(await page.locator('#maintenance-recovery').isVisible(),false);checks++;
 }
 if(lang==='ru') {
  await page.locator('#maintenance-mode').selectOption(mode==='Safe'?'Normal':'Safe');
  await page.evaluate(()=>I18n.setLanguage('en'));
  assert.equal(await page.locator('#maintenance-mode').inputValue(),mode==='Safe'?'Normal':'Safe');
  assert.equal(await page.locator('#maintenance-current').textContent(),mode==='Safe'?'Safe / Maintenance':'Normal');
  await page.evaluate(()=>I18n.setLanguage('ru'));checks+=2;
 }
 if(mode==='Safe') {
  await page.evaluate(()=>hide('settings-modal'));await page.evaluate(()=>openRdpModal());await page.waitForTimeout(60);assert.equal(await page.locator('#rdp-btn-install').isDisabled(),true);await page.evaluate(()=>closeRdpModal());
  await page.evaluate(()=>openDriverModal('vigem'));await page.waitForTimeout(60);assert.equal(await page.locator('#vigem-btn-install').isDisabled(),true);await page.evaluate(()=>closeDriverModal('vigem'));checks+=2;
  const blocked=await page.evaluate(async()=>{const r=await api('POST','/api/sessions/fixture/start');return r.status;});assert.equal(blocked,409);assert.deepEqual(writes,[]);checks++;
  await page.evaluate(()=>openSettingsModal());await page.locator('#maintenance-mode').selectOption('Normal');await page.locator('#maintenance-save').click();await page.locator('#confirm-modal').waitFor({state:'visible'});assert.equal(writes.length,0);checks++;
  // Cancel the synthetic confirmation: even fixture writes must require an explicit choice.
  await page.evaluate(()=>closeModal('confirm-modal'));await page.evaluate(()=>hide('settings-modal'));
 }
 assert.deepEqual(errors,[],lang+':'+mode);checks++;
 if(['ru','en','de','ja','ko','zh','uk'].includes(lang)){await page.evaluate(async()=>{hide('toast');await openSettingsModal();});await page.locator('#settings-modal').waitFor({state:'visible'});await page.screenshot({path:path.join(out,lang+'-'+mode+'.png'),fullPage:true});}
 await context.close();
}
fs.writeFileSync(path.join(out,'result.json'),JSON.stringify({status:'PASS',checks,languages:22,modes:2,realSystemMutations:0},null,2));console.log('PASS '+checks+' maintenance browser checks across 22 languages and 2 modes');
}finally{await browser.close();server.close();}})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
