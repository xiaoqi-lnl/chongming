import math
import UnityPy

def euler(r):
    x, y, z, w = r.x, r.y, r.z, r.w
    t0 = 2.0 * (w * x + y * z); t1 = 1.0 - 2.0 * (x * x + y * y)
    ex = math.degrees(math.atan2(t0, t1))
    t2 = 2.0 * (w * y - z * x)
    t2 = 1.0 if t2 > 1.0 else (-1.0 if t2 < -1.0 else t2)
    ey = math.degrees(math.asin(t2))
    t3 = 2.0 * (w * z + x * y); t4 = 1.0 - 2.0 * (y * y + z * z)
    ez = math.degrees(math.atan2(t3, t4))
    return (ex, ey, ez)

PATH = r"E:/SteamLibrary/steamapps/common/Hollow Knight 1578/hollow_knight_Data/level459"
env = UnityPy.load(PATH)

gos, trs = {}, {}
go_of_tr, tr_of_go = {}, {}
for o in env.objects:
    t = o.type.name
    if t == "GameObject":
        try: gos[o.path_id] = o.read()
        except Exception: pass
    elif t == "Transform":
        try: trs[o.path_id] = o.read()
        except Exception: pass
for trid, tr in trs.items():
    gid = tr.m_GameObject.path_id if tr.m_GameObject else 0
    go_of_tr[trid] = gid; tr_of_go[gid] = trid

def name_of(gid):
    g = gos.get(gid); return g.m_Name if g else ("<go %d ?>" % gid)

# find the Ascend Beam GameObject (exact name)
target = None
for gid, g in gos.items():
    if g.m_Name == "Ascend Beam":
        target = gid
        break
if target is None:
    print("no 'Ascend Beam'"); raise SystemExit

# component class name -> need raw read? UnityPy gives component .type
def comps(gid):
    g = gos[gid]
    out = []
    for c in g.m_Component:
        try:
            out.append(c.component.type.name)
        except Exception:
            out.append("?")
    return out

def dump(gid, depth=0):
    g = gos[gid]
    trid = tr_of_go.get(gid)
    tr = trs.get(trid)
    pad = "  " * depth
    line = pad + name_of(gid) + "   [" + ",".join(comps(gid)) + "]"
    if tr:
        p, s = tr.m_LocalPosition, tr.m_LocalScale
        line += "  pos=(%.3f,%.3f) sc=(%.3f,%.3f,%.3f)" % (p.x, p.y, s.x, s.y, s.z)
    print(line)
    if tr:
        for ch in tr.m_Children:
            cg = go_of_tr.get(ch.path_id, 0)
            if cg: dump(cg, depth + 1)

print("=== Ascend Beam subtree ===")
dump(target)

# also print BoxCollider2D / renderer details under the subtree
print()
print("=== component details ===")
def walk(gid):
    g = gos[gid]
    for c in g.m_Component:
        try:
            t = c.component.type.name
        except Exception:
            continue
        try:
            d = c.component.read()
        except Exception:
            continue
        if t == "BoxCollider2D":
            print(name_of(gid), t, "size=", d.m_Size, "offset=", d.m_Offset, "enabled=", d.m_Enabled)
        if t in ("MeshRenderer",):
            print(name_of(gid), t)
    trid = tr_of_go.get(gid); tr = trs.get(trid)
    if tr:
        for ch in tr.m_Children:
            cg = go_of_tr.get(ch.path_id, 0)
            if cg: walk(cg)
walk(target)
