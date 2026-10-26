# Copyright (C) 2026 JeepNWilly
# Convertr is free software: you can redistribute it and/or modify it under the terms of the
# GNU General Public License as published by the Free Software Foundation, either version 3
# of the License, or (at your option) any later version. It comes with no warranty.
# See the LICENSE file in the repository root or <https://www.gnu.org/licenses/>.
import csv, math, sys, re
rows=[]
def pix(w,h): return w*h/(1280*720)
def load_sizes(path):
    for r in csv.DictReader(open(path)):
        info=r['Info'].split(',')
        if info[0]!='h264' or 'decode errors' in r['File'] : continue
        w,h=int(info[1]),int(info[2])
        rows.append(dict(name=r['File'][:40],w=w,h=h,src=float(r['ClipSrcKbps']),mode=r['Mode'],q=int(r['Q']),out=float(r['OutKbps'])))
load_sizes('sweep_sizes.csv')
# real library clips (res label only -> assume 1080p=1920x1080, 720p=1280x720)
import os
for fn in ['sweep_real.csv','sweep_real_partial.csv']:
    if os.path.exists(fn):
        for r in csv.DictReader(open(fn)):
            if any(x in r['File'] for x in ('decode errors','?','Series_M','Series_N')): continue
            w,h=(1920,1080) if r['Res']=='1080p' else (1280,720)
            rows.append(dict(name=r['File'][:40],w=w,h=h,src=float(r['ClipSrcKbps']),mode=r['Mode'],q=int(r['Q']),out=float(r['OutKbps'])))
        break
# earlier 90 s sweeps (720p clips, video only)
for fn,mode in [('../data/old_gpu.csv','GPU'),]:
    pass
old=[('Series_J',1280,640,3609,'CPU',21,1102),('Series_J',1280,640,3609,'GPU',24,2996),('Series_J',1280,640,3609,'GPU',27,1849),
     ('Series_C_S10E16',1280,720,7396,'CPU',21,2844),('Series_C_S10E16',1280,720,7396,'GPU',24,6711),('Series_C_S10E16',1280,720,7396,'GPU',27,4293)]
# these Series_J/Series_C_S10E16 clips duplicate sweep_sizes files (same sources, different clip) -> keep as additional clips
for n,w,h,s,m,q,o in old: rows.append(dict(name=n+'(90s)',w=w,h=h,src=s,mode=m,q=q,out=o))
def fit(mode,qref,minsrc=0):
    pts=[(math.log(r['src']/pix(r['w'],r['h'])), math.log(r['out']/pix(r['w'],r['h']))) for r in rows if r['mode']==mode and r['q']==qref and r['src']/pix(r['w'],r['h'])>=minsrc]
    n=len(pts); mx=sum(p[0] for p in pts)/n; my=sum(p[1] for p in pts)/n
    b=sum((p[0]-mx)*(p[1]-my) for p in pts)/sum((p[0]-mx)**2 for p in pts); a=my-b*mx
    res=[p[1]-(a+b*p[0]) for p in pts]; sd=(sum(x*x for x in res)/(n-2))**.5
    return n,math.exp(a),b,sd,sorted(res)
for mode,qr in [('CPU',21),('GPU',27),('GPU',24)]:
    n,A,B,sd,res=fit(mode,qr)
    print(mode,qr,'n=%d A=%.1f B=%.3f sd(ln)=%.3f  factors lo/hi(1sd)=%.2f/%.2f  min/max resid %.2f/%.2f'%(n,A,B,sd,math.exp(-sd),math.exp(sd),math.exp(res[0]),math.exp(res[-1])))
# quality scale per 3 steps: GPU 24->27 ratio of out
import statistics
g={}
for r in rows:
    g.setdefault((r['name'],r['mode']),{})[r['q']]=r['out']
k=[v[27]/v[24] for (nme,m),v in g.items() if m=='GPU' and 24 in v and 27 in v]
print('GPU CQ27/CQ24 out ratio per clip: mean %.3f  median %.3f  n=%d'%(statistics.mean(k),statistics.median(k),len(k)))
