from pathlib import Path
from bisect import bisect_left,bisect_right
from collections import Counter
from statistics import median
import json,sys,hashlib

root=Path(sys.argv[1]).resolve()
output=root/'trace-comparison.json'
if output.exists():raise SystemExit('Refusing to overwrite trace comparison.')
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def stats(v):
    v=sorted(v)
    if not v:return dict(count=0)
    def q(p):
        r=p*(len(v)-1);i=int(r);return v[i]+(v[min(i+1,len(v)-1)]-v[i])*(r-i)
    return dict(count=len(v),minimum=min(v),median=q(.5),p95=q(.95),maximum=max(v),over50=sum(x>50 for x in v))
result=dict(schemaVersion=1,diagnosticOnly=True,notCanonicalPerformanceGate=True,versions={},files=[],method='Matched three fixed120-pair EventPipe processes per version; native WindowsQPC and UIthread matching. All samples retained. GC-containing Done samples included even when fast.')
for version,role in (('baseline',root/'traced'),('candidate',root/'candidate/traced')):
    rows=[];processes=[]
    for run in range(1,4):
        report_path=role/f'run-{run:02}.json';event_path=role/f'run-{run:02}.events.json'
        r=load(report_path);e=load(event_path);assert not r['failures'] and not any(not b['passed'] for b in r['behavior'])
        assert e['eventsLost']==0
        assert r['trace']['Frequency']==10000000
        assert len(r['trace']['samples'])==243
        if version=='baseline':assert r['desktop']['sha256']=='154F83F6F275AA3CC6051794F1E0FC3AE42C9965DDA497E1A1464DE6DE4CBD94'
        assert r['core']['sha256']=='941FBC0759107880BAD2E0CD95BF21A871315917A86C590BBB3DFF0B84461EF5'
        for path in (report_path,event_path,role/f'run-{run:02}.nettrace'):
            result['files'].append(dict(file=path.relative_to(root).as_posix(),sha256=hashlib.sha256(path.read_bytes()).hexdigest().upper(),bytes=path.stat().st_size))
        pid=r['trace']['ProcessId'];tid=r['trace']['UiNativeThreadId']
        ui=[s for s in e['samples'] if s['pid']==pid and s['tid']==tid];ticks=[s['qpc'] for s in ui]
        chronology=[];details=[]
        for s in r['trace']['samples']:
            if s['stage']!='Done':continue
            m=s['metrics'];a=m['startTicks'];b=m['endTicks'];matching=ui[bisect_left(ticks,a):bisect_right(ticks,b)]
            text=[x for x in matching if any('System.Windows.Documents.TextStore.' in f for f in x['stack'])]
            item=dict(run=run,pid=pid,uiThread=tid,sequence=s['sequence'],wallMilliseconds=m['wallMilliseconds'],gen0=m['gen0'],gcPauseMilliseconds=m['gcPauseMilliseconds'],uiSamples=len(matching),textStoreSamples=len(text))
            chronology.append(item);rows.append(item)
            if m['gen0']>0 or m['wallMilliseconds']>50:
                details.append(dict(**item,phases=s['phases'],uiSamplesDetail=matching,runtimeEvents=[x for x in e['runtime'] if x['pid']==pid and a<=x['qpc']<=b]))
        assert len(chronology)==121
        processes.append(dict(run=run,pid=pid,eventsLost=e['eventsLost'],customMarkers=len(e['markers']),desktopSha256=r['desktop']['sha256'],sourceSha256=r['harness']['sourceSha256'],phaseSourceSha256=r['trace']['phaseSourceSha256'],done=stats([s['wallMilliseconds'] for s in chronology[1:]]),chronologicalDone=chronology,gcOrSlowDone=details))
    assert len({p['desktopSha256'] for p in processes})==1
    gc=[x for x in rows if x['gen0']>0];slow=[x for x in rows if x['wallMilliseconds']>50]
    result['versions'][version]=dict(processes=processes,allDone=stats([x['wallMilliseconds'] for x in rows]),slowDoneCount=len(slow),gcContainingDoneCount=len(gc),gcContainingDoneWall=stats([x['wallMilliseconds'] for x in gc]),textStoreSamplesInGcDone=sum(x['textStoreSamples'] for x in gc),uiSamplesInGcDone=sum(x['uiSamples'] for x in gc),gcWithoutSlowDone=sum(x['wallMilliseconds']<=50 for x in gc))
baseline=result['versions']['baseline'];candidate=result['versions']['candidate']
assert baseline['processes'][0]['desktopSha256']!=candidate['processes'][0]['desktopSha256']
for field in ('sourceSha256','phaseSourceSha256'):
    assert len({p[field] for v in result['versions'].values() for p in v['processes']})==1
result['limitations']=['EventPipe sampled managed stacks do not establish native wait ownership or exact CPU time.','GC counters/sampled stacks are correlated by native WindowsQPC; custom EventSource markers are absent in the original trace.','WPRnative CPU profiling was denied; OScontext switches unavailable.','Fixed synthetic workload and one workstation; absence of a tail here does not guarantee all workloads.']
output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps({v:{k:x[k] for k in ('allDone','slowDoneCount','gcContainingDoneCount','textStoreSamplesInGcDone','gcWithoutSlowDone')} for v,x in result['versions'].items()},indent=2))
