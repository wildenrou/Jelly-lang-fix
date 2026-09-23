#!/usr/bin/env python3
"""Isolated Jellyfin integration check. Uses generated media and the TEST provider.
Run only against a disposable directory; never point at an existing server's data.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

p=argparse.ArgumentParser()
p.add_argument('--dotnet',required=True);p.add_argument('--server',required=True);p.add_argument('--work',required=True)
args=p.parse_args()
root=Path(args.work).resolve()
if root.exists() and any(root.iterdir()):raise RuntimeError('The smoke-test work directory must be empty; existing data is never overwritten.')
root.mkdir(parents=True,exist_ok=True)
source=Path(__file__).resolve().parents[1]
(root/'config').mkdir()
(root/'config'/'network.xml').write_text('<NetworkConfiguration><InternalHttpPort>18096</InternalHttpPort><PublicHttpPort>18096</PublicHttpPort><EnableHttps>false</EnableHttps><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses></NetworkConfiguration>')
plugin_version=ET.parse(source/'src/Jellyfin.Plugin.FrenchOriginals/Jellyfin.Plugin.FrenchOriginals.csproj').findtext('.//Version')
for dll,plugin_id,name,version in [
    (source/'src/Jellyfin.Plugin.FrenchOriginals/bin/Release/net10.0/Jellyfin.Plugin.FrenchOriginals.dll','8a18225a-22b8-4e18-9921-b8f1b5900bbc','French Originals',plugin_version),
    (source/'tests/SmokeProvider/bin/Release/net10.0/SmokeProvider.dll','edb4b3f5-c6d6-4c46-9ba3-e71034a6b7a7','TEST provider','1.0.0.0')]:
    dest=root/'data'/'plugins'/(dll.stem+'_'+version);dest.mkdir(parents=True)
    shutil.copy2(dll,dest/dll.name)
    (dest/'meta.json').write_text(json.dumps({'guid':plugin_id,'name':name,'targetAbi':'12.0.0.0','version':version,'status':'Active','autoUpdate':False,'assemblies':[dll.name]}))
base='http://127.0.0.1:18096'
token=None
identity='MediaBrowser Client="FrenchOriginalsTests", Device="Tests", DeviceId="isolated-test", Version="1.0"'
def normalize(value):
    if isinstance(value,list):return [normalize(x) for x in value]
    if isinstance(value,dict):return {k[:1].upper()+k[1:]:normalize(v) for k,v in value.items()}
    return value
def api(method,path,body=None):
    headers={'Authorization':identity+(f', Token="{token}"' if token else ''),'Content-Type':'application/json','Accept':'application/json; profile="PascalCase"'}
    req=urllib.request.Request(base+path,data=json.dumps(body).encode() if body is not None else (b'' if method=='POST' else None),headers=headers,method=method)
    with urllib.request.urlopen(req,timeout=30) as r:
        raw=r.read()
        if raw and r.headers.get_content_type()=='text/html':return raw.decode()
        return normalize(json.loads(raw)) if raw else None
def wait_for(check,seconds=55):
    end=time.monotonic()+seconds
    while time.monotonic()<end:
        try:
            result=check()
            if result:return result
        except urllib.error.URLError:pass
        time.sleep(.3)
    raise RuntimeError('Timeout waiting for test server condition')
def current():
    return api('GET','/Items?Recursive=true&IncludeItemTypes=Movie,Series,Season,Episode&Fields=Overview,ProviderIds,Settings&CollapseBoxSetItems=false')['Items']
def idle():
    return not any(t['State'] in ['Running','Cancelling'] for t in api('GET','/ScheduledTasks'))
def run_task(task_id):
    api('POST','/ScheduledTasks/Running/'+task_id)
    time.sleep(.5)
    wait_for(lambda:next(t for t in api('GET','/ScheduledTasks') if t['Id']==task_id)['State']=='Idle')
    return api('GET','/FrenchOriginals/Report')

media=root/'media';movies=media/'Movies';movies.mkdir(parents=True,exist_ok=True)
sample=root/'sample.mkv'
subprocess.run(['ffmpeg','-hide_banner','-loglevel','error','-y','-f','lavfi','-i','color=c=black:s=64x64:d=1','-c:v','libx264','-threads','1',str(sample)],check=True)
for name,external_id in [('FrenchFixture','900000001'),('EnglishFixture','900000002')]:
    folder=movies/name;folder.mkdir(exist_ok=True);shutil.copy2(sample,folder/(name+'.mkv'))
    (folder/'movie.nfo').write_text(f'<movie><title>{name}</title><plot>Original English summary.</plot><uniqueid type="tmdb" default="true">{external_id}</uniqueid><language>en</language><tag>PreserveMe</tag><mpaa>PG</mpaa></movie>')
shows=media/'Shows';series=shows/'FrenchSeries';season=series/'Season 01';season.mkdir(parents=True)
(series/'tvshow.nfo').write_text('<tvshow><title>French series in English</title><plot>English series overview</plot><uniqueid type="tmdb" default="true">900000010</uniqueid><language>en</language></tvshow>')
(season/'season.nfo').write_text('<season><title>Season in English</title><seasonnumber>1</seasonnumber><uniqueid type="tmdb" default="true">900000011</uniqueid></season>')
for number in [1,2]:
    name=f'FrenchSeries S01E{number:02d}'
    shutil.copy2(sample,season/(name+'.mkv'))
    override='<language>en</language>' if number==2 else ''
    (season/(name+'.nfo')).write_text(f'<episodedetails><title>Episode {number} in English</title><plot>English episode overview</plot><season>1</season><episode>{number}</episode><uniqueid type="tmdb" default="true">90000001{number+1}</uniqueid>{override}</episodedetails>')

env=os.environ.copy();env['DOTNET_PROCESSOR_COUNT']='2'
log=(root/'server-console.log').open('w')
server=subprocess.Popen([args.dotnet,args.server,'--nowebclient','--nonetchange','--datadir',str(root/'data'),'--configdir',str(root/'config'),'--cachedir',str(root/'cache'),'--logdir',str(root/'log'),'--ffmpeg',shutil.which('ffmpeg')],stdout=log,stderr=subprocess.STDOUT,env=env)
try:
    def ready():
        # 12.x briefly hosts a separate startup app that also returns System/Info.
        # Wait for a real controller before attempting to configure the server.
        api('GET','/Startup/Configuration')
        result=api('GET','/System/Info/Public')
        return result if 'Version' in result else None
    info=wait_for(ready)
    print('Server:',info['Version'],flush=True)
    api('GET','/Startup/User')
    api('POST','/Startup/User',{'Name':'SmokeAdmin','Password':'TemporaryTestPass!123'})
    api('POST','/Startup/Complete')
    auth=api('POST','/Users/AuthenticateByName',{'Username':'SmokeAdmin','Pw':'TemporaryTestPass!123'})
    token=auth['AccessToken']
    plugins=api('GET','/Plugins')
    plugin=next(x for x in plugins if x['Id'].replace('-','')=='8a18225a22b84e189921b8f1b5900bbc')
    assert plugin['Status']=='Active',plugin
    print('PASS native plugin loaded Active',flush=True)
    page=api('GET','/web/ConfigurationPage?name=FrenchOriginals')
    assert 'FrenchOriginalsForm' in page and 'RunFrenchTask' in page
    print('PASS Jellyfin serves the embedded configuration page',flush=True)
    old_token=token;token=None
    try:api('GET','/FrenchOriginals/Report');raise AssertionError('Unauthenticated report was accepted')
    except urllib.error.HTTPError as e:assert e.code in (401,403)
    token=old_token
    print('PASS report requires authentication',flush=True)
    library_options={'PreferredMetadataLanguage':'en','EnableRealtimeMonitor':False,'EnableLUFSScan':False,
        'EnableChapterImageExtraction':False,'EnableTrickplayImageExtraction':False,'AutomaticRefreshIntervalDays':0,
        'TypeOptions':[{'Type':t,'MetadataFetchers':['Smoke French'],'MetadataFetcherOrder':['Smoke French'],
                        'ImageFetchers':[],'ImageFetcherOrder':[]} for t in ['Movie','Series','Season','Episode']]}
    api('POST','/Library/VirtualFolders?'+urllib.parse.urlencode({'name':'Smoke Movies','collectionType':'movies','paths':str(movies),'refreshLibrary':'false'}),{'LibraryOptions':library_options})
    api('POST','/Library/VirtualFolders?'+urllib.parse.urlencode({'name':'Smoke Shows','collectionType':'tvshows','paths':str(shows),'refreshLibrary':'false'}),{'LibraryOptions':library_options})
    wait_for(idle)
    api('POST','/Library/Refresh')
    wait_for(lambda:len(current())>=6)
    wait_for(idle)
    before=current()
    print('Fixture metadata:',[(i['Name'],i.get('OriginalLanguage')) for i in before],flush=True)
    assert {i.get('OriginalLanguage') for i in before if i['Type']=='Movie'}=={'fr','en'}
    folders=api('GET','/Library/VirtualFolders')
    plugin_id='8a18225a-22b8-4e18-9921-b8f1b5900bbc'
    config=api('GET','/Plugins/'+plugin_id+'/Configuration')
    config.update(LibraryIds=[v['ItemId'] for v in folders],PreviewOnly=True,ItemLimit=25)
    api('POST','/Plugins/'+plugin_id+'/Configuration',config)
    task=next(t for t in api('GET','/ScheduledTasks') if t['Key']=='FrenchOriginals.UpdateMetadata')
    assert task['Triggers']==[],task['Triggers']
    report=run_task(task['Id']);assert report['Status']=='Preview complete',report
    assert report['Selected']==4 and report['Changed']==0,report
    assert [(x['Id'],x['Name'],x.get('Overview')) for x in current()]==[(x['Id'],x['Name'],x.get('Overview')) for x in before]
    print('PASS native preview selects only French original and does not change text',flush=True)
    config['PreviewOnly']=False;api('POST','/Plugins/'+plugin_id+'/Configuration',config)
    report=run_task(task['Id']);assert report['Status']=='Completed' and report['Changed']==4,report
    after=current();fr=next(i for i in after if i['Type']=='Movie' and i['OriginalLanguage']=='fr');en=next(i for i in after if i['Type']=='Movie' and i['OriginalLanguage']=='en')
    assert fr['Name']=='Titre français de test' and fr['Overview']=='Résumé français de test.',fr
    assert fr['PreferredMetadataLanguage']=='fr',fr
    assert en['Name']==next(i for i in before if i['Id']==en['Id'])['Name'],en
    assert fr.get('OfficialRating')=='PG'
    print('PASS native provider lookup and persisted French title/summary/language; English title and rating preserved',flush=True)
    for item in after:
        if item['Type'] in ('Series','Season') or (item['Type']=='Episode' and item['IndexNumber']==1):
            assert item['Name']=='Titre français de test' and item['PreferredMetadataLanguage']=='fr',item
        if item['Type']=='Episode' and item['IndexNumber']==2:
            assert item['Name']=='Episode 2 in English' and item['PreferredMetadataLanguage']=='en',item
    print('PASS series, season, episode localization and explicit non-French episode override',flush=True)
    report=run_task(task['Id']);assert report['Changed']==0 and report['Selected']==0,report
    print('PASS repeat native task does no further work',flush=True)
    (root/'result.json').write_text(json.dumps({'ServerVersion':info['Version'],'Status':'Passed','LastReport':report},indent=2))
finally:
    server.terminate()
    try:server.wait(timeout=15)
    except subprocess.TimeoutExpired:server.kill();server.wait()
    log.close()
