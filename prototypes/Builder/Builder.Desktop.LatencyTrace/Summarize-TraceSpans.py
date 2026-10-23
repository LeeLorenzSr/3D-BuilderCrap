from pathlib import Path
from bisect import bisect_left,bisect_right
import json,sys
root=Path(sys.argv[1]).resolve();output=root/'trace-spans.json'
if output.exists():raise SystemExit('Refusing to overwrite trace spans.')
result=dict(schemaVersion=1,definition='A contiguous run is consecutive captured UI-thread samples whose managed stacks contain System.Windows.Documents.TextStore. Span is last timestamp minus first, not exact native call duration or CPU time.',versions={})
for version,folder in (('baseline',root/'traced'),('candidate',root/'candidate/traced')):
    rows=[]
    for run in range(1,4):
        r=json.loads((folder/f'run-{run:02}.json').read_text(encoding='utf-8-sig'));e=json.loads((folder/f'run-{run:02}.events.json').read_text(encoding='utf-8-sig'))
        samples=[x for x in e['samples'] if x['tid']==r['trace']['UiNativeThreadId'] and x['pid']==r['trace']['ProcessId']];ticks=[x['qpc'] for x in samples]
        for s in r['trace']['samples']:
            if s['stage']!='Done':continue
            m=s['metrics'];matched=samples[bisect_left(ticks,m['startTicks']):bisect_right(ticks,m['endTicks'])]
            groups=[];active=[]
            for sample in matched+[None]:
                if sample and any('System.Windows.Documents.TextStore.' in frame for frame in sample['stack']):active.append(sample)
                elif active:
                    groups.append(dict(count=len(active),startMs=active[0]['ms'],endMs=active[-1]['ms'],spanMilliseconds=active[-1]['ms']-active[0]['ms']));active=[]
            largest=max(groups,key=lambda x:x['spanMilliseconds']) if groups else None
            rows.append(dict(run=run,sequence=s['sequence'],wallMilliseconds=m['wallMilliseconds'],gen0=m['gen0'],longestTextStoreRun=largest))
    groups=[x['longestTextStoreRun'] for x in rows if x['longestTextStoreRun']]
    result['versions'][version]=dict(doneCount=len(rows),doneOver50=sum(x['wallMilliseconds']>50 for x in rows),gen0ContainingDone=sum(x['gen0']>0 for x in rows),maximumContiguousTextStoreSpanMilliseconds=max(x['spanMilliseconds'] for x in groups),maximumContiguousTextStoreSampleCount=max(x['count'] for x in groups),chronology=rows)
output.write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
print(json.dumps({version:{key:value for key,value in data.items() if key!='chronology'} for version,data in result['versions'].items()},indent=2))
