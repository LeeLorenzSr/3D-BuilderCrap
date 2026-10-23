from pathlib import Path
from collections import Counter
from statistics import median
from bisect import bisect_left,bisect_right
import json,sys,hashlib,platform

root=Path(sys.argv[1]).resolve()
def load(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def stats(v):
    s=sorted(v)
    def q(p):
        r=p*(len(s)-1);i=int(r);return s[i]+(s[min(i+1,len(s)-1)]-s[i])*(r-i)
    return dict(count=len(s),minimum=min(s),median=q(.5),p95=q(.95),maximum=max(s),over50=sum(x>50 for x in s))
def selected(v,a,b):
    return v[bisect_left([x['qpc'] for x in v],a):bisect_right([x['qpc'] for x in v],b)]
summary=dict(schemaVersion=1,tool=dict(python=platform.python_version()),roles={},traces=[])
for role in ('canonical','counters','traced'):
    reports=[load(root/role/f'run-{i:02}.json') for i in range(1,4)]
    assert all(not r['failures'] for r in reports)
    assert all(not any(not b['passed'] for b in r['behavior']) for r in reports)
    stages={s['stage'] for r in reports for s in r['stages'] if 'samples' in s}
    summary['roles'][role]=dict(stages={stage:stats([v['Milliseconds'] for r in reports for s in r['stages'] if s['stage']==stage for v in s['samples']]) for stage in sorted(stages)},
      behaviorObservations=sum(len(r['behavior']) for r in reports))
    if role=='canonical':continue
    summary['roles'][role]['coldInitial']=[r['trace']['samples'][0] for r in reports]
    summary['roles'][role]['chronologicalDone']=[dict(run=i,sequence=s['sequence'],metrics=s['metrics'],phases=s['phases']) for i,r in enumerate(reports,1) for s in r['trace']['samples'] if s['stage']=='Done']
    summary['roles'][role]['chronologicalEnter']=[dict(run=i,sequence=s['sequence'],metrics=s['metrics'],phases=s['phases']) for i,r in enumerate(reports,1) for s in r['trace']['samples'] if s['stage']=='Enter']
    summary['roles'][role]['outliers']=[s for s in summary['roles'][role]['chronologicalDone'] if s['metrics']['wallMilliseconds']>50]
    if role!='traced':continue
    for i,r in enumerate(reports,1):
        ev=load(root/role/f'run-{i:02}.events.json');tid=r['trace']['UiNativeThreadId'];pid=r['trace']['ProcessId'];freq=r['trace']['Frequency']
        assert ev['eventsLost']==0
        assert all(x['pid']==pid for x in ev['samples'])
        runtime=ev['runtime'];samples=[s for s in ev['samples'] if s['tid']==tid]
        intervals=[];pending=None
        for e in runtime:
            if e['name']=='GC/SuspendEEStart':pending=dict(start=e,suspended=None,restarting=None)
            elif e['name']=='GC/SuspendEEStop' and pending:pending['suspended']=e
            elif e['name']=='GC/RestartEEStart' and pending:pending['restarting']=e
            elif e['name']=='GC/RestartEEStop' and pending:
                a=pending['start'];b=pending['suspended'];c=pending['restarting']
                intervals.append(dict(start=a['qpc'],end=e['qpc'],reason=a['payload'].get('Reason'),milliseconds=(e['qpc']-a['qpc'])*1000/freq,startMs=a['ms'],suspendedMs=b['ms'] if b else None,restartingMs=c['ms'] if c else None,endMs=e['ms'],
                  rendezvousMilliseconds=(b['qpc']-a['qpc'])*1000/freq if b else None,suspendedMilliseconds=(c['qpc']-b['qpc'])*1000/freq if b and c else None,restartMilliseconds=(e['qpc']-c['qpc'])*1000/freq if c else None));pending=None
        trace=dict(run=i,pid=pid,uiThread=tid,eventsLost=ev['eventsLost'],customMarkers=len(ev['markers']),interesting=[])
        for s in r['trace']['samples']:
            if not(s['stage']=='Initial' or(s['stage'] in ('Enter','Done') and s['metrics']['wallMilliseconds']>50)):continue
            a=s['metrics']['startTicks'];b=s['metrics']['endTicks']
            stacks=selected(samples,a,b)
            frames=Counter(f for sample in stacks for f in set(sample['stack']))
            events=selected(runtime,a,b)
            phase_stacks=[]
            for phase in s['phases']:
                pm=phase['metrics'];matched=selected(samples,pm['startTicks'],pm['endTicks'])
                meaningful=Counter(f for sample in matched for f in set(sample['stack']) if f.startswith('Builder.') or 'System.Windows.Documents.TextStore.' in f or 'TextServicesDisplayAttribute' in f or 'CompositionEngineLock' in f)
                phase_stacks.append(dict(phase=phase['phase'],uiSampleCount=len(matched),meaningfulInclusiveFrames=meaningful.most_common(15)))
            trace['interesting'].append(dict(sequence=s['sequence'],stage=s['stage'],metrics=s['metrics'],phases=s['phases'],phaseStacks=phase_stacks,
              uiSamples=stacks,inclusiveFrames=frames.most_common(25),runtimeEvents=events,
              textStoreSamples=sum(any('System.Windows.Documents.TextStore.' in f for f in sample['stack']) for sample in stacks),
              textStoreSampleTimes=[sample['ms'] for sample in stacks if any('System.Windows.Documents.TextStore.' in f for f in sample['stack'])],
              suspensionIntervals=[x for x in intervals if x['start']<=b and x['end']>=a]))
        summary['traces'].append(trace)
(root/'phase-summary.json').write_text(json.dumps(summary,indent=2)+'\n',encoding='utf-8')
for role,value in summary['roles'].items():
    print(role,'initial',value['stages']['cold.expanded-initial-refresh-layout'],'Done',value['stages']['warm.done-layout'])
for t in summary['traces']:
    for s in t['interesting'][:3]:
        print('trace',t['run'],s['sequence'],s['stage'],'wall',s['metrics']['wallMilliseconds'],'frames',s['inclusiveFrames'][:6])
