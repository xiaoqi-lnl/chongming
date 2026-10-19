import math
import UnityPy

def euler(r):
    x, y, z, w = r.x, r.y, r.z, r.w
    t0 = 2 * (w * x + y * z); t1 = 1 - 2 * (x * x + y * y)
    ex = math.degrees(math.atan2(t0, t1))
    t2 = max(-1, min(1, 2 * (w * y - z * x))); ey = math.degrees(math.asin(t2))
    t3 = 2 * (w * z + x * y); t4 = 1 - 2 * (y * y + z * z)
    ez = math.degrees(math.atan2(t3, t4))
    return (ex, ey, ez)

PATH = r"E:/SteamLibrary/steamapps/common/Hollow Knight 1578/hollow_knight_Data/level459"
env = UnityPy.load(PATH)

gos = {}; tr_by_go = {}; col_by_go = {}
for o in env.objects:
    t = o.type.name
    if t == "GameObject":
        try: gos[o.path_id] = o.read()
        except Exception: pass
    elif t == "Transform":
        try:
            d = o.read(); tr_by_go[d.m_GameObject.path_id] = d
        except Exception: pass
    elif t == "BoxCollider2D":
        try:
            d = o.read(); col_by_go[d.m_GameObject.path_id] = (d.m_Size.x, d.m_Size.y, d.m_Offset.x, d.m_Offset.y, d.m_Enabled)
        except Exception: pass

for gid, g in gos.items():
    if g.m_Name in ("Ascend Beam", "Radiant Beam", "Beam Sweeper"):
        tr = tr_by_go.get(gid)
        if not tr: continue
        s = tr.m_LocalScale; r = tr.m_LocalRotation
        print("%-14s pos=(%.2f,%.2f) eZ=%.1f sc=(%.2f,%.2f) ch=%d" % (
            g.m_Name, tr.m_LocalPosition.x, tr.m_LocalPosition.y, euler(r)[2], s.x, s.y, len(tr.m_Children)))
        if gid in col_by_go:
            sx, sy, ox, oy, en = col_by_go[gid]
            print("     col size=(%.3f,%.3f) off=(%.3f,%.3f) en=%s" % (sx, sy, ox, oy, en))
