// DOM behavior test; stubbed API, no live credentials or browser styling.
const fs = require('fs');
const path = require('path');
const { JSDOM } = require(process.env.FRENCH_TEST_NODE_MODULES
  ? path.join(process.env.FRENCH_TEST_NODE_MODULES, 'jsdom') : 'jsdom');
async function waitFor(check) {
  for (let i=0;i<200;i++) { if(check())return; await new Promise(r=>setTimeout(r,10)); }
  throw new Error('DOM test timed out');
}
(async () => {
  const calls=[];
  const config={previewOnly:true,libraryIds:[],updateTitlesAndSummaries:true,includeSeasonsAndEpisodes:true,
    recheckCompletedItems:false,itemIds:'',itemLimit:25,maxChanges:80,delayMilliseconds:200,
    providerTimeoutSeconds:30,journalRetentionDays:30};
  const dom=new JSDOM(fs.readFileSync(path.join(__dirname,'../src/Jellyfin.Plugin.FrenchOriginals/Configuration/configPage.html'),'utf8'),{
    runScripts:'dangerously',url:'http://localhost/',beforeParse(w){
      w.Dashboard={showLoadingMsg(){},hideLoadingMsg(){},processPluginConfigurationUpdateResult(){}};
      w.ApiClient={getUrl:p=>p,ajax:async o=>{
        calls.push(o);if(o.type==='POST')return undefined;
        if(o.url.endsWith('/Configuration'))return config;
        if(o.url==='Library/VirtualFolders')return [{itemId:'11111111111111111111111111111111',name:'Movies'}];
        if(o.url==='ScheduledTasks')return [{id:'test-task',key:'FrenchOriginals.UpdateMetadata',state:'Idle'}];
        if(o.url==='FrenchOriginals/Report')return {status:'Preview complete',items:[
          {title:'<img src=x onerror="window.injected=true">',type:'Movie',action:'Would update title',newTitle:'Titre français'},
          {title:'Les Aimants',type:'Movie',action:'No change needed',newTitle:'Les Aimants'},
          {title:'Titre conservé',type:'Movie',action:'Updated summary',newTitle:'Titre conservé',detail:'Provider: fake'}
        ]};
        throw new Error('Unexpected request: '+o.url);
      }};
    }
  });
  try {
    const w=dom.window;const q=s=>w.document.querySelector(s);
    q('#FrenchOriginalsPage').dispatchEvent(new w.Event('pageshow'));
    await waitFor(()=>q('.frenchLibrary')&&q('#FrenchRows tr'));
    if(!q('#PreviewOnly').checked)throw new Error('Preview default not loaded');
    if(q('#FrenchRows img')||w.injected)throw new Error('Report inserted executable HTML');
    if(w.document.querySelectorAll('#FrenchRows tr').length!==2||q('#FrenchRows').textContent.includes('Les Aimants'))throw new Error('Unchanged item was not hidden');
    const summaryRow=w.document.querySelectorAll('#FrenchRows tr')[1];
    if(summaryRow.children[3].textContent!=='Provider: fake')throw new Error('Unchanged title was repeated as a rename');
    q('#RefreshFrenchReport').click();
    await waitFor(()=>calls.filter(x=>x.url==='FrenchOriginals/Report').length===2);
    if(w.document.querySelectorAll('#FrenchRows tr').length!==2)throw new Error('Refreshing the report duplicated or restored hidden rows');
    q('.frenchLibrary').checked=true;q('#ItemLimit').value='5';
    q('#FrenchOriginalsForm').dispatchEvent(new w.Event('submit',{cancelable:true}));
    await waitFor(()=>calls.some(x=>x.type==='POST'&&x.url.endsWith('/Configuration')));
    const saved=JSON.parse(calls.find(x=>x.type==='POST'&&x.url.endsWith('/Configuration')).data);
    if(!saved.PreviewOnly||saved.ItemLimit!==5||saved.LibraryIds.length!==1)throw new Error('Incorrect saved settings');
    q('#RunFrenchTask').click();
    await waitFor(()=>calls.some(x=>x.url==='ScheduledTasks/Running/test-task'));
    console.log('PASS configuration page loads and saves settings, starts the native task, hides unchanged rows on refresh, avoids duplicate titles, and renders untrusted report text safely');
  } finally {dom.window.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
