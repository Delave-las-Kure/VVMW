"""Actual support/Core/Shell bodies with controlled player availability; no media I/O."""
from pathlib import Path
import argparse,hashlib,json,shutil,subprocess
p=argparse.ArgumentParser()
p.add_argument('--source',type=Path,default=Path(__file__).resolve().parents[2]/'Packages/idv.jlchntoz.vvmw/Runtime/VVMW')
p.add_argument('--video-source',type=Path)
p.add_argument('--shell-source',type=Path,default=Path(r'D:/unityhub/worlds/Uran Fracture Cinema/Assets/UFC/Udon/UfcVideoShell.cs'))
p.add_argument('--out',type=Path,required=True)
p.add_argument('--expect-old-bug',action='store_true')
p.add_argument('--android',action='store_true')
a=p.parse_args();out=a.out.resolve();out.mkdir(parents=True,exist_ok=False)
paths={n:a.source/n for n in ['VideoPlayerHandler.cs','ImageViewerHandler.cs','Core.cs']}
if a.video_source:paths['VideoPlayerHandler.cs']=a.video_source
paths['UfcVideoShell.cs']=a.shell_source
raw={n:path.read_bytes() for n,path in paths.items()};source={n:b.decode('utf-8-sig') for n,b in raw.items()}
def member(s,sig):
    start=s.index(sig);brace=s.find('{',start);arrow=s.find('=>',start)
    if arrow>=0 and (brace<0 or arrow<brace):return s[start:s.index(';',arrow)+1]
    depth=0
    for end in range(brace,len(s)):
        if s[end]=='{':depth+=1
        elif s[end]=='}':depth-=1
        if depth==0:return s[start:end+1]
    raise ValueError(sig)
video=member(source['VideoPlayerHandler.cs'],'override int IsSupported(string urlStr)').replace('override int','public override int',1)
protocol=member(source['VideoPlayerHandler.cs'],'bool IsRealTimeProtocolS(string urlStr)')
image=member(source['ImageViewerHandler.cs'],'override int IsSupported(string urlStr)').replace('override int','public override int',1)
core=member(source['Core.cs'],'public byte GetSuitablePlayerType(VRCUrl url)')
shell=member(source['UfcVideoShell.cs'],'byte ResolveInputBackend(VRCUrl url)').replace('byte ResolveInputBackend','public byte ResolveInputBackend',1)
code=('#define UNITY_ANDROID\n' if a.android else '')+r'''
using System;using System.Collections.Generic;
namespace ServiceDomainBoundary {
public class VRCUrl{string value;public VRCUrl(string v){value=v;}public string Get()=>value;public static bool IsNullOrEmpty(VRCUrl u)=>u==null||string.IsNullOrEmpty(u.value);}
public static class Utilities{public static bool IsValid(object o)=>o!=null;}
public abstract class Handler{public abstract int IsSupported(string s);}
public class Video:Handler{public bool isAvPro,isLowLatency;string[] realTimeProtocols={"rtsp","rtmp","rtspt","rtspu","rtmps","rtsps"};
'''+video+'\n'+protocol+r'''
}
public class Image:Handler{
'''+image+r'''
}
public class Core{public Handler[] playerHandlers;
'''+core+r'''
}
public class Shell{public Core core;public byte inputBackend,unityBackend=2,avProBackend=1;
'''+shell+r'''
}
public static class Tests{
static int checks,urls;static List<object> examples=new List<object>();static void Need(bool x,string why){checks++;if(!x)throw new Exception(why);}
static Core Shape()=>new Core{playerHandlers=new Handler[]{new Video{isAvPro=true},new Video(),new Image()}};
static void Original27(bool old){var c=Shape();var s=new Shell{core=c};foreach(string host in new[]{"notyoutube.com","mytwitch.tv","oursoundcloud.com","cdn.example","notyoutu.be","youtube.com","www.youtube.com","twitch.tv","soundcloud.com"})foreach(string ext in new[]{"png","jpg","jpeg"}){
string value="https://"+host+"/picture."+ext+"?token=x#view";var url=new VRCUrl(value);Need(Uri.TryCreate(value,UriKind.Absolute,out var uri)&&uri.DnsSafeHost==host,"Valid authority host");Need(c.playerHandlers[2].IsSupported(value)==1,"Image extension recognized");bool falseHost=host=="notyoutube.com"||host=="mytwitch.tv"||host=="oursoundcloud.com",ordinary=host=="cdn.example"||host=="notyoutu.be";byte expected=(byte)((ordinary||falseHost&&!old)?3:1);Need(c.GetSuitablePlayerType(url)==expected&&s.ResolveInputBackend(url)==expected,"Baseline regression / corrected initial image route");urls++;
if(falseHost)examples.Add(new{host,ext,expected,actual=s.ResolveInputBackend(url)});
}}
static int Generic(string ext,bool avpro){if(ext=="png"||ext=="jpg"||ext=="jpeg")return 0;if(ext=="mp4")return 1;if(ext=="m3u8")return avpro?1:0;return 0;}
static void FixedMatrix(){
var entries=new (string host,int kind)[]{("youtube.com",1),("www.youtube.com",1),("a.b.youtube.com",1),("YouTuBe.CoM",1),("WWW.YouTube.Com",1),("youtu.be",2),("a.youtu.be",2),("YoUtU.Be",2),("twitch.tv",3),("www.twitch.tv",3),("a.b.TwItCh.TV",3),("soundcloud.com",4),("a.soundcloud.com",4),("SOUNDCloud.COM",4),("notyoutube.com",0),("mytwitch.tv",0),("oursoundcloud.com",0),("notyoutu.be",0),("x.notyoutube.com",0),("youtube.com.evil.test",0),("youtu.be.evil.test",0),("twitch.tv.evil.test",0),("soundcloud.com.evil.test",0),("youtube.com:443",0),("www.youtube.com:443",0),("youtu.be:443",0),("twitch.tv:8443",0),("soundcloud.com:443",0),("cdn.example",0)};
foreach(var entry in entries)foreach(string ext in new[]{"png","jpg","jpeg","mp4","m3u8","unknown"})foreach(string suffix in new[]{"","?token=x","#view","?token=x#view","#view?token=x"}){
string value="https://"+entry.host+"/picture."+ext+suffix;var c=Shape();var s=new Shell{core=c};int av=entry.kind==1?1:entry.kind==2?0:entry.kind==3||entry.kind==4?1:Generic(ext,true),unity=entry.kind==1?1:entry.kind==2||entry.kind==3?0:entry.kind==4?-1:Generic(ext,false),im=ext=="png"||ext=="jpg"||ext=="jpeg"?1:-1;
Need(c.playerHandlers[0].IsSupported(value)==av&&c.playerHandlers[1].IsSupported(value)==unity&&c.playerHandlers[2].IsSupported(value)==im,"Exact service/generic support scores preserved");byte expected=av>0?(byte)1:unity>0?(byte)2:im>0?(byte)3:(byte)0;var url=new VRCUrl(value);Need(c.GetSuitablePlayerType(url)==expected,"Core keeps authored score/order policy");Need(s.ResolveInputBackend(url)==(expected==0?(byte)1:expected),"Actual Shell Auto returns corrected route");s.inputBackend=2;Need(s.ResolveInputBackend(url)==2,"Explicit Unity selection preserved");s.inputBackend=1;Need(s.ResolveInputBackend(url)==1,"Explicit AVPro selection preserved");urls++;
}
foreach(string host in new[]{"youtube.com","www.YouTube.com","notyoutube.com"})foreach(string suffix in new[]{"","?x=1#view"}){
string value="https://"+host+"/live"+suffix;bool real=host!="notyoutube.com";var av=new Video{isAvPro=true};var low=new Video{isAvPro=true,isLowLatency=true};var unity=new Video();Need(av.IsSupported(value)==(real?1:0)&&low.IsSupported(value)==(real?2:0)&&unity.IsSupported(value)==(real?-1:0),"YouTube live/low-latency policy applies only to exact or dot-subdomain host");urls++;
}
foreach(string protocol in new[]{"rtsp","rtmp","rtsps","rtmps","rtspt","rtspu"}){string value=protocol+"://cdn.example/live";Need(new Video{isAvPro=true}.IsSupported(value)==1&&new Video{isAvPro=true,isLowLatency=true}.IsSupported(value)==2&&new Video().IsSupported(value)==-1,"Existing real-time protocol scoring preserved");urls++;}
}
public static object Run(bool old){Original27(old);if(!old)FixedMatrix();return new{pass=true,checks,urlCases=urls,expectOldBug=old,originalReproductionCases=27,originalReproductionChecks=81,examples,scope="Four actual current Video/Image/Core/Shell routing bodies plus unchanged real-time protocol helper. Authored AVPro1/Unity2/Image3 shape and explicit-backend controls; PC/UNITY_ANDROID symbols. Port-bearing authorities preserve previous generic-extension policy; no authority/URI normalization, HTTP/SDK/Udon/Photon or physical Android claim."};}
}
}
'''
(out/'actual.cs').write_text(code,encoding='utf-8')
ps="$ErrorActionPreference='Stop'\nAdd-Type -Path '"+str(out/'actual.cs')+"'\n[ServiceDomainBoundary.Tests]::Run($"+str(a.expect_old_bug).lower()+")|ConvertTo-Json -Depth 8|Set-Content -Encoding utf8 -LiteralPath '"+str(out/'report.json')+"'\n"
(out/'run.ps1').write_text(ps,encoding='utf-8')
runtime=Path(r'C:/Users/t_evg/.cache/codex-runtimes/codex-primary-runtime/dependencies/native/powershell/pwsh.exe')
exe=str(runtime) if runtime.is_file() else shutil.which('pwsh')
if not exe:raise RuntimeError('PowerShell 7 with Add-Type required')
run=subprocess.run([exe,'-NoProfile','-File',str(out/'run.ps1')],capture_output=True)
(out/'run.log').write_bytes(run.stdout+run.stderr)
if run.returncode:raise RuntimeError((run.stdout+run.stderr).decode('utf-8','replace'))
report=json.loads((out/'report.json').read_text(encoding='utf-8-sig'))
report.update(sourcePins={n:hashlib.sha256(data).hexdigest() for n,data in raw.items()},sourceUnchanged=all(data==paths[n].read_bytes() for n,data in raw.items()),compiledPlatform='UNITY_ANDROID-controlled' if a.android else 'PC-controlled')
(out/'report.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps({k:v for k,v in report.items() if k!='examples'},indent=2))
