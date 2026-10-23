from pathlib import Path
import json,sys
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

root=Path(sys.argv[1]).resolve()
output=root/'paired-latency.svg'
if output.exists():raise SystemExit('Refusing to overwrite completed chronology plot.')
fig=plt.figure(figsize=(13,10),layout='constrained')
grid=fig.add_gridspec(4,2,height_ratios=[1,1,1,1.2])
axes=[[fig.add_subplot(grid[row,col]) for col in range(2)] for row in range(3)]
all_values=[]
for col,version in enumerate(('baseline','candidate')):
    role=root/'counters' if version=='baseline' else root/'candidate/counters'
    for run in range(1,4):
        report=json.loads((role/f'run-{run:02}.json').read_text(encoding='utf-8-sig'))
        ax=axes[run-1][col]
        samples=next(s['samples'] for s in report['stages'] if s['stage']=='warm.done-layout')
        assert len(samples)==120
        values=[s['Milliseconds'] for s in samples];all_values+=values
        color='#a34431' if version=='baseline' else '#2376a8'
        ax.plot(range(1,121),values,color=color,linewidth=.9)
        over=[(i+1,value) for i,value in enumerate(values) if value>50]
        if over:ax.scatter(*zip(*over),color=color,s=12,zorder=3)
        ax.axhline(50,color='#777777',linestyle='--',linewidth=.8)
        ax.set_title(f'{version}, fresh process {run}; max {max(values):.2f} ms; {len(over)} >50 ms',fontsize=10)
        ax.grid(alpha=.15);ax.set_ylabel('Done CPU/layout total (ms)')
        if run==3:ax.set_xlabel('Warm transition pair, chronological order')
for row in axes:
    for ax in row:ax.set_ylim(0,max(55,max(all_values)*1.08))
cold=fig.add_subplot(grid[3,:]);cold_values=[]
for version,color,offset in (('baseline','#a34431',-.09),('candidate','#2376a8',.09)):
    role=root/'canonical' if version=='baseline' else root/'candidate/canonical'
    values=[]
    for run in range(1,4):
        report=json.loads((role/f'run-{run:02}.json').read_text(encoding='utf-8-sig'))
        samples=next(s['samples'] for s in report['stages'] if s['stage']=='cold.expanded-initial-refresh-layout')
        assert len(samples)==1
        values.append(samples[0]['Milliseconds'])
    cold_values+=values
    cold.scatter([x+offset for x in range(1,4)],values,color=color,label=version,s=30)
    for i,value in enumerate(values,1):cold.annotate(f'{value:.2f}',(i+offset,value),xytext=(0,7),textcoords='offset points',ha='center',fontsize=8)
cold.axhline(50,color='#777777',linestyle='--',linewidth=.8)
cold.set_xticks([1,2,3]);cold.set_xlabel('Fresh process');cold.set_ylabel('Cold initial CPU/layout (ms)');cold.set_ylim(0,max(60,max(cold_values)*1.2));cold.grid(alpha=.15)
cold.set_title('Original canonical A15D caller: all three cold initial layout samples',fontsize=10);cold.legend(fontsize=8)
fig.suptitle('Paired 154F baseline / final candidate: all samples retained\nUpper panels: 120-pair counter diagnostics without EventPipe; CPU/dispatcher/layout, not presented frames',fontsize=11)
fig.savefig(output)
fig.savefig(root/'paired-latency.png',dpi=150)
print(output)
