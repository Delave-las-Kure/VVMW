"""Compile actual frontend bodies with explicit native/ownership callback boundaries."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess

p=argparse.ArgumentParser()
p.add_argument('--source',type=Path,default=Path(__file__).resolve().parents[2]/'Packages/idv.jlchntoz.vvmw/Runtime/VVMW')
p.add_argument('--out',required=True,type=Path)
p.add_argument('--expect-old-bug',action='store_true')
p.add_argument('--android',action='store_true')
args=p.parse_args();out=args.out.resolve();out.mkdir(parents=True,exist_ok=False)
names=['FrontendHandler.cs','FrontendHandler_PlayList.cs','FrontendHandler_QueueList.cs']
raw={name:(args.source/name).read_bytes() for name in names}
source={name:b.decode('utf-8-sig') for name,b in raw.items()}
def member(s,sig):
    start=s.index(sig);brace=s.find('{',start);arrow=s.find('=>',start)
    if arrow>=0 and (brace<0 or arrow<brace):return s[start:s.index(';',arrow)+1]
    depth=0
    for end in range(brace,len(s)):
        if s[end]=='{':depth+=1
        elif s[end]=='}':depth-=1
        if depth==0:return s[start:end+1]
    raise ValueError(sig)
selections={
 'FrontendHandler.cs':['public void PlayAt(int playListIndex, int entryIndex, bool deleteOnly)','bool RequestSync()','void MarkLocalQueuePlaybackForUfc()','void RestoreReceivedQueueOriginForUfc()','bool CanRotateCurrentQueuePlaybackForUfc()','bool QueuePlaybackUrlEqualsForUfc(VRCUrl a, VRCUrl b)','public override void OnVideoReady()','public override void OnVideoEnd()','void _OnVideoBeginLoad()','void ScheduleAdvanceForUfc(bool manual)','void ScheduleAdvanceEventForUfc()','void _RunPendingAdvanceForUfc()','public void _PlayNext()','void _AutoPlayNext()','bool IsArrayNullOrEmpty(Array array)'],
 'FrontendHandler_PlayList.cs':['void RefreshPlayListQueue(int startIndex)','void PlayPlayList(int index)'],
 'FrontendHandler_QueueList.cs':['public void PlayUrl(VRCUrl pcUrl, VRCUrl questUrl, string queuedTitle, byte index)','void PlayQueueList(int index, bool deleteOnly)','void UpdateTitle()','void GetLastPlayedUrl(out VRCUrl pcUrl, out VRCUrl questUrl, out byte playerIndex)']}
actual=[]
for name,sigs in selections.items():
    for sig in sigs:
        body=member(source[name],sig).replace('public override ','public ')
        if not body.startswith('public '):body='public '+body
        actual.append(body)
code=(('#define UNITY_ANDROID\n') if args.android else '')+r'''
using System;
using System.Collections.Generic;
namespace PlaylistReplacementRegression {
public class VRCUrl {public string value;public VRCUrl(string s){value=s;}public string Get()=>value;public static VRCUrl Empty=new VRCUrl("");public static bool IsNullOrEmpty(VRCUrl u)=>u==null||string.IsNullOrEmpty(u.value);public override bool Equals(object o)=>o is VRCUrl u&&u.value==value;public override int GetHashCode()=>value.GetHashCode();}
public static class Utilities{public static bool IsValid(object o)=>o!=null;}
public static class Networking{public static Player LocalPlayer=new Player();public static HashSet<object> owners=new HashSet<object>();public static int claims;public static bool IsOwner(object g)=>owners.Contains(g);public static void SetOwner(Player p,object g){owners.Add(g);claims++;}}
public class Player{public string displayName="Host";}
public class Filter{public VRCUrl pcUrl,questUrl;public bool reject;public void _ValidateUrls(){if(reject)pcUrl=VRCUrl.Empty;}}
namespace UnityEngine{public static class Random{public static int Range(int a,int b)=>a;}}
public static class Mathf{public static int Min(int a,int b)=>Math.Min(a,b);}
public enum IdleMode{DoNothing,PlayDefault}
public class Core {
public object gameObject=new object();public Frontend frontend;public bool IsReady,IsLoading,IsLocalTransportActiveForUfc,HasLocalTransportErrorForUfc,IsSynced=true;public VRCUrl Url=VRCUrl.Empty,AltUrl=VRCUrl.Empty,LastUrl=VRCUrl.Empty,LastAltUrl=VRCUrl.Empty;public byte ActivePlayer,LastActivePlayer;public int LocalStopRevisionForUfc,loads,stops,launches;public string SharedPlaybackIdForUfc="";public List<string> loaded=new List<string>();public void SetTitle(string t,string a){}public void _ResetTitle(){}
public void PlayUrl(VRCUrl pc,VRCUrl q,byte b){loads++;launches++;loaded.Add(pc.Get());
#if UNITY_ANDROID
Url=VRCUrl.IsNullOrEmpty(q)?pc:q;AltUrl=pc;
#else
Url=pc;AltUrl=VRCUrl.IsNullOrEmpty(q)?pc:q;
#endif
ActivePlayer=b;IsReady=false;IsLoading=true;SharedPlaybackIdForUfc="launch-"+launches;frontend?._OnVideoBeginLoad();}
public void Ready(){IsLoading=false;IsReady=true;frontend.OnVideoReady();}
public void Stop(){stops++;LocalStopRevisionForUfc++;LastUrl=Url;LastAltUrl=AltUrl;LastActivePlayer=ActivePlayer;Url=VRCUrl.Empty;AltUrl=VRCUrl.Empty;ActivePlayer=0;IsReady=false;IsLoading=false;SharedPlaybackIdForUfc="";frontend.OnVideoEnd();}
}
public class Frontend {
public object gameObject=new object(),coreGO;public Core core;public bool enableQueueList=true,RepeatAll,Shuffle,synced=true,forceStop,advanceEventScheduledForUfc;public Filter urlInputFilter;public int localPlayListIndex,localPlayingPlaylistIndex=-1,pendingAdvanceStopRevisionForUfc,syncs;public ushort localPlayingIndex,localLastPlayingIndex;public ushort[] localPlayListOrder;public VRCUrl[] localQueuedUrls,localQueuedQuestUrls;public byte[] localQueuedPlayerIndex;public string[] localQueuedTitles;public string localCurrentTitle,localQueuePlaybackIdForUfc,failedQueuePlaybackIdForUfc,queuePlaybackIdForUfc;public VRCUrl localQueuePcUrlForUfc,localQueueQuestUrlForUfc;public byte localQueuePlayerForUfc;public byte[] pendingAdvancesForUfc=new byte[0];public IdleMode idleMode=IdleMode.DoNothing;
public int[] playListUrlOffsets=new[]{0};public VRCUrl[] playListUrls=new[]{new VRCUrl("https://example.com/A.mp4")},playListUrlsQuest=new[]{new VRCUrl("https://example.com/A-mobile.mp4")};public byte[] playListPlayerIndex=new byte[]{1};public string[] playListEntryTitles=new[]{"A"},playListTitles=new[]{"Authored aligned fixture"};public List<string> history=new List<string>();
public Frontend(){core=new Core{frontend=this};coreGO=core.gameObject;Networking.owners.Add(gameObject);Networking.owners.Add(coreGO);}public void SeedRandomBeforeShuffle(){}public void UpdateState(){}public void RecordPlaybackHistory(VRCUrl pc,VRCUrl q,byte b,string t){history.Add(pc.Get());}public string UnescapeUrl(VRCUrl u)=>u.Get();public void PlayHistory(int i){throw new Exception("Unused history branch");}public void RequestSerialization(){syncs++;}public void SendCustomEventDelayedFrames(string e,int frames){}public void _AutoPlay(){throw new Exception("Unused idle policy");}
'''+ '\n'.join(actual) + r'''
}
public static class Tests {
static int checks;static bool old;static void Need(bool ok,string why){checks++;if(!ok)throw new Exception(why);}
static string Canonical(Core c)=>c.loaded.Count==0?"":c.loaded[c.loaded.Count-1];
static Frontend Playlist(bool enabled=true,bool ready=true,bool loading=false,bool repeat=false){var f=new Frontend{enableQueueList=enabled,RepeatAll=repeat};f.PlayAt(1,0,false);Need(Canonical(f.core).EndsWith("A.mp4")&&f.localPlayListIndex==1&&f.core.loads==1,"Supported playlist ingress starts A");f.core.IsReady=ready;f.core.IsLoading=loading;if(ready)f.OnVideoReady();return f;}
static void B(Frontend f)=>f.PlayUrl(new VRCUrl("https://example.com/B.mp4"),new VRCUrl("https://example.com/B-mobile.mp4"),"B",2);
static void C(Frontend f)=>f.PlayUrl(new VRCUrl("https://example.com/C.mp4"),null,"C",1);
static object Basic(string mode,bool enabled,bool ready,bool loading,bool playlist){var f=playlist?Playlist(enabled,ready,loading):new Frontend{enableQueueList=enabled};if(!playlist&&(ready||loading)){f.core.PlayUrl(new VRCUrl("https://example.com/A.mp4"),null,1);f.core.IsReady=ready;f.core.IsLoading=loading;}int loads=f.core.loads,stops=f.core.stops;B(f);bool queued=f.localQueuedUrls!=null&&f.localQueuedUrls.Length==1;bool b=Canonical(f.core).EndsWith("B.mp4");
bool bug=playlist&&enabled&&(ready||loading)&&old;bool append=!playlist&&enabled&&(ready||loading);
if(bug||append){Need(!b&&queued&&f.core.loads==loads,"Expected append/baseline bug");}else{Need(b&&!queued&&f.core.loads==loads+1,"Replacement/idle directly loads B without queuing it");Need(f.core.ActivePlayer==2,"Replacement uses requested backend");Need(f.localPlayingPlaylistIndex==0&&f.localPlayListIndex==0,"Replacement adopts ordinary queue origin");Need(f.localCurrentTitle.EndsWith("B"),"Requested title belongs to B");}
Need(f.core.stops-stops==(playlist&&!enabled?1:0),"Existing disabled-queue Stop retained; no new Stop/End introduced");return new{mode,bLoaded=b,queued,loadDelta=f.core.loads-loads,stopDelta=f.core.stops-stops};}
static void FilterControl(){var f=Playlist();f.urlInputFilter=new Filter{reject=true};int loads=f.core.loads;B(f);Need(f.localPlayListIndex==1&&f.core.loads==loads&&f.core.stops==0,"Rejected filtered URL leaves playlist A intact");}
static void RepeatAndStopControls(){
var f=Playlist(repeat:true);B(f);Need(Canonical(f.core).EndsWith("B.mp4")&&f.localPlayListIndex==0,"Repeat playlist still replaced immediately");Need(f.core.stops==0&&f.pendingAdvancesForUfc.Length==0,"Replacement adds no End or deferred advance");f.core.Ready();C(f);Need(f.localQueuedUrls.Length==1&&f.localQueuedUrls[0].Get().EndsWith("C.mp4"),"Subsequent C uses ordinary append");f._PlayNext();Need(Canonical(f.core).EndsWith("C.mp4")&&f.localQueuedUrls.Length==1&&f.localQueuedUrls[0].Get().EndsWith("B.mp4"),"RepeatAll rotates replacement B, never playlist A");Need(f.localQueuedQuestUrls[0].Get().EndsWith("B-mobile.mp4")&&f.localQueuedPlayerIndex[0]==2,"Rotated B preserves mobile URL and backend");Need(f.history.Count==2&&f.history[0].EndsWith("B.mp4")&&f.history[1].EndsWith("C.mp4"),"History records new queue media, not retired playlist A");
f=Playlist();B(f);f.core.Ready();C(f);f.ScheduleAdvanceForUfc(false);f.forceStop=true;f.core.Stop();Need(f.core.ActivePlayer==0&&f.localQueuedUrls.Length==1,"Explicit queue-preserving Stop retains waiting C");f._RunPendingAdvanceForUfc();Need(f.pendingAdvancesForUfc.Length==0&&f.core.loads==2,"Stop revision cancels older advance without starting C");f._PlayNext();Need(Canonical(f.core).EndsWith("C.mp4"),"Fresh Next after Stop still starts C");
f=Playlist();B(f);C(f);Networking.owners.Remove(f.gameObject);int loads=f.core.loads;f._PlayNext();Need(f.core.loads==loads&&f.localQueuedUrls.Length==1,"Non-owner Next still obeys actual frontend guard");
f=Playlist();Networking.owners.Remove(f.gameObject);int claims=Networking.claims;B(f);Need(Networking.IsOwner(f.gameObject)&&Networking.claims==claims+1,"Direct replacement retains ordinary RequestSync ownership route");
}
public static object Run(bool expectedOld){old=expectedOld;var cases=new object[]{Basic("playlist-ready",true,true,false,true),Basic("playlist-loading",true,false,true,true),Basic("disabledqueue-ready",false,true,false,true),Basic("idle",true,false,false,false),Basic("ordinary-append",true,true,false,false),Basic("ended-playlist",true,false,false,true)};FilterControl();if(!old)RepeatAndStopControls();return new{pass=true,checks,expectedOldBug=old,cases,extractedMethods=21,scope="Actual frontend ingress/playlist/queue/origin/scheduler/ownership method bodies. Core load, readiness, synchronous Stop End and revision are deterministic native boundaries; no full Core, SDK, Unity, Photon or physical Quest claim."};}
}
}
'''
(out/'actual.cs').write_text(code,encoding='utf-8')
ps="$ErrorActionPreference='Stop'\nAdd-Type -Path '"+str(out/'actual.cs')+"'\n[PlaylistReplacementRegression.Tests]::Run($"+str(args.expect_old_bug).lower()+")|ConvertTo-Json -Depth 8|Set-Content -Encoding utf8 -LiteralPath '"+str(out/'report.json')+"'\n"
(out/'run.ps1').write_text(ps,encoding='utf-8')
pwsh=Path(r'C:/Users/t_evg/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe')
exe=str(pwsh) if pwsh.is_file() else shutil.which('pwsh')
if not exe:raise RuntimeError('PowerShell 7 with Add-Type required')
run=subprocess.run([exe,'-NoProfile','-File',str(out/'run.ps1')],capture_output=True)
(out/'run.log').write_bytes(run.stdout+run.stderr)
if run.returncode:raise RuntimeError((run.stdout+run.stderr).decode('utf-8','replace'))
report=json.loads((out/'report.json').read_text(encoding='utf-8-sig'))
report.update(sourcePins={n:hashlib.sha256(raw[n]).hexdigest() for n in names},platform='UNITY_ANDROID-controlled' if args.android else 'PC-controlled',sourceUnchanged=all(raw[n]==(args.source/n).read_bytes() for n in names))
(out/'report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,indent=2))
