"""
Шрифты для снимков интерфейса под Wine (build/screenshots-wine.sh).
Segoe UI и Segoe Fluent Icons в Linux недоступны, а WPF под Wine падает, если для символа нет шрифта.
Поэтому в префикс Wine кладутся: Open Sans (+ недостающие символы из DejaVu) под именем «Segoe UI»,
DejaVu с заглушками значков под именем «Segoe Fluent Icons» и DejaVu под системными именами (Arial, Tahoma…).
Только для локальных снимков: метрики близки к Segoe UI, но не совпадают, значки — квадраты.
Использование: python3 build/wine-fonts.py <папка Fonts в префиксе Wine>
"""
import sys, os
from fontTools.ttLib import TTFont
from fontTools import merge
out=sys.argv[1]
dv='/usr/share/fonts/truetype/dejavu/'
os_='/usr/share/fonts/truetype/open-sans/'
def rename(f, fam, style, legacy_family=None, weight=None):
    n=f['name']
    for rec in list(n.names):
        if rec.nameID in (1,2,3,4,6,16,17,21,22): n.removeNames(nameID=rec.nameID)
    lf = legacy_family or fam
    ls = style if style in ('Regular','Bold') else 'Regular'
    n.setName(lf,1,3,1,0x409); n.setName(ls,2,3,1,0x409)
    n.setName(fam+' '+style,3,3,1,0x409)
    n.setName(fam+('' if style=='Regular' else ' '+style),4,3,1,0x409)
    n.setName((fam+'-'+style).replace(' ',''),6,3,1,0x409)
    n.setName(fam,16,3,1,0x409); n.setName(style,17,3,1,0x409)
    if weight: f['OS/2'].usWeightClass=weight
for osf, dvf, style, w in [('OpenSans-Regular.ttf','DejaVuSans.ttf','Regular',400),('OpenSans-Semibold.ttf','DejaVuSans-Bold.ttf','Semibold',600),('OpenSans-Bold.ttf','DejaVuSans-Bold.ttf','Bold',700)]:
    try:
        m=merge.Merger(options=merge.Options(drop_tables=['vhea','vmtx','GSUB','GPOS','GDEF','kern','hdmx','VDMX','LTSH','prep','fpgm','cvt ','gasp']))
        f=m.merge([os_+osf, dv+dvf])
    except Exception as e:
        print('merge failed',style,repr(e)[:200]); f=TTFont(dv+dvf)
    rename(f,'Segoe UI',style, None if style in ('Regular','Bold') else 'Segoe UI '+style, w)
    f.save(os.path.join(out,'segoeui-'+style.lower()+'.ttf')); print('segoe',style,len(f.getBestCmap()))
f=TTFont(dv+'DejaVuSans.ttf')
box=f.getBestCmap()[0x25A1]
for t in f['cmap'].tables:
    if t.isUnicode() and t.format in (4,12):
        for cp in range(0xE700,0xF900): t.cmap[cp]=box
rename(f,'Segoe Fluent Icons','Regular')
f.save(os.path.join(out,'segoefluenticons.ttf')); print('icons ok')
for fam in ['Arial','Tahoma','Times New Roman','Segoe UI Symbol','Consolas','Courier New']:
    for fn, style in [('DejaVuSans.ttf','Regular'),('DejaVuSans-Bold.ttf','Bold')]:
        g=TTFont(dv+fn); rename(g,fam,style); g.save(os.path.join(out, fam.replace(' ','').lower()+'-'+style.lower()+'.ttf'))
print('done')
