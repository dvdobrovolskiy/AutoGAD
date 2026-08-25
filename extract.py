# SPDX-License-Identifier: AGPL-3.0-or-later
# Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

import json, collections, io, re
f=r"C:\Users\dima\Downloads\АХП-2.2025.РД\К2.АХП-2.2025.РД_dump.json"
d=json.load(open(f,encoding="utf-8"))
out=io.open(r"C:\MEGA\Hobby\AutoGAD\tables.txt","w",encoding="utf-8")
def p(*a): print(*a,file=out)

def strip_mtext(s):
    if s is None: return ""
    s=str(s)
    s=re.sub(r"\\[Ff][^;]*;","",s)                 # font defs
    s=re.sub(r"\\[A-Za-z][^;\\]*;","",s)           # \C1; \H1.5x; \pxqc; etc
    s=s.replace("\\P","\n").replace("\\~"," ")
    s=s.replace("{","").replace("}","")
    s=re.sub(r"\\[A-Za-z]","",s)
    return s.strip()

ms=next(s for s in d["spaces"] if s["name"]=="*Model_Space")
tables=[e for e in ms["entities"] if e["type"]=="Table"]
p("TABLES in model space:", len(tables))
for i,t in enumerate(tables):
    rows=t.get("cells") or []
    bb=t.get("bbox")
    loc=[round(x) for x in bb["min"]] if bb else None
    p("\n===== TABLE %d  (%sx%s)  at %s =====" % (i, t.get("numRows"), t.get("numCols"), loc))
    for r in rows:
        cells=[strip_mtext(c).replace("\n"," / ") for c in r]
        if any(cells):
            p("  | " + " | ".join(cells))
out.close()
print("ok tables", len(tables))
