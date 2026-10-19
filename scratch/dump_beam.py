import math
import UnityPy

def euler(r):
    # quaternion (x,y,z,w) -> euler deg (Unity ZXY)
    x, y, z, w = r.x, r.y, r.z, r.w
    t0 = 2.0 * (w * x + y * z)
    t1 = 1.0 - 2.0 * (x * x + y * y)
    ex = math.degrees(math.atan2(t0, t1))
    t2 = 2.0 * (w * y - z * x)
    t2 = 1.0 if t2 > 1.0 else (-1.0 if t2 < -1.0 else t2)
    ey = math.degrees(math.asin(t2))
    t3 = 2.0 * (w * z + x * y)
    t4 = 1.0 - 2.0 * (y * y + z * z)
    ez = math.degrees(math.atan2(t3, t4))
    return (ex, ey, ez)

PATH = r"E:/SteamLibrary/steamapps/common/Hollow Knight 1578/hollow_knight_Data/level459"

env = UnityPy.load(PATH)

objs = list(env.objects)
print("objects:", len(objs))

gos = {}       # pathid -> GameObject data
trs = {}       # pathid -> Transform data
go_of_tr = {}  # tr pathid -> go pathid
tr_of_go = {}  # go pathid -> tr pathid

for o in objs:
    t = o.type.name
    if t == "GameObject":
        try:
            g = o.read()
            gos[o.path_id] = g
        except Exception as e:
            pass
    elif t == "Transform":
        try:
            tr = o.read()
            trs[o.path_id] = tr
        except Exception as e:
            pass

for trid, tr in trs.items():
    gid = tr.m_GameObject.path_id if tr.m_GameObject else 0
    go_of_tr[trid] = gid
    tr_of_go[gid] = trid

def name_of_go(gid):
    g = gos.get(gid)
    if g is None:
        return "<go %d ?>" % gid
    return g.m_Name

# find gameobjects whose name relates to beams
targets = [gid for gid, g in gos.items() if ("Radiant Beam" in g.m_Name or "Burst" in g.m_Name or "Eye Beam" in g.m_Name)]

def path_of_go(gid):
    names = []
    cur = gid
    seen = set()
    while cur and cur not in seen:
        seen.add(cur)
        names.append(name_of_go(cur))
        trid = tr_of_go.get(cur)
        if not trid:
            break
        tr = trs.get(trid)
        if tr is None or not tr.m_Father:
            break
        cur = go_of_tr.get(tr.m_Father.path_id, 0)
    return "/".join(reversed(names))

for gid in sorted(targets, key=lambda x: path_of_go(x)):
    trid = tr_of_go.get(gid)
    tr = trs.get(trid) if trid else None
    p = tr.m_LocalPosition if tr else None
    r = tr.m_LocalRotation if tr else None
    s = tr.m_LocalScale if tr else None
    nchild = len(tr.m_Children) if tr else -1
    print("\n%s" % path_of_go(gid))
    if tr:
        print("  pos  = (%.3f, %.3f, %.3f)" % (p.x, p.y, p.z))
        print("  rot  = (%.4f, %.4f, %.4f, %.4f)  euler=(%.2f, %.2f, %.2f)" % (r.x, r.y, r.z, r.w, *euler(r)))
        print("  scale= (%.3f, %.3f, %.3f)" % (s.x, s.y, s.z))
        print("  children:", nchild)
