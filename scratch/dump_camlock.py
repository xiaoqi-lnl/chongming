import UnityPy

def quat_mul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b
    return (aw*bx + ax*bw + ay*bz - az*by,
            aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw,
            aw*bw - ax*bx - ay*by - az*bz)

def quat_rot(q, v):
    x, y, z, w = q
    vx, vy, vz = v
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty),
            vy + w*ty + (z*tx - x*tz),
            vz + w*tz + (x*ty - y*tx))

PATH = r"E:/SteamLibrary/steamapps/common/Hollow Knight 1578/hollow_knight_Data/level459"
env = UnityPy.load(PATH)
gos = {}; tr_by_go = {}; tr_by_id = {}; col_by_go = {}
for o in env.objects:
    t = o.type.name
    if t == "GameObject":
        try: gos[o.path_id] = o.read()
        except Exception: pass
    elif t == "Transform":
        try:
            d = o.read(); tr_by_go[d.m_GameObject.path_id] = d; tr_by_id[o.path_id] = d
        except Exception: pass
    elif t == "BoxCollider2D":
        try:
            d = o.read(); col_by_go[d.m_GameObject.path_id] = (d.m_Size.x, d.m_Size.y, d.m_Offset.x, d.m_Offset.y)
        except Exception: pass

def world_of(gid):
    chain = []; cur = gid
    while cur:
        tr = tr_by_go.get(cur)
        if tr is None: break
        chain.append(tr)
        f = tr.m_Father
        nxt = 0
        if f and f.path_id:
            ftr = tr_by_id.get(f.path_id)
            if ftr is not None:
                nxt = ftr.m_GameObject.path_id
        cur = nxt
    pos = (0.0, 0.0, 0.0); rot = (0.0, 0.0, 0.0, 1.0); scl = (1.0, 1.0, 1.0)
    for tr in reversed(chain):
        lp = tr.m_LocalPosition; lr = tr.m_LocalRotation; ls = tr.m_LocalScale
        sp = (lp.x * scl[0], lp.y * scl[1], lp.z * scl[2])
        rp = quat_rot(rot, sp)
        pos = (pos[0] + rp[0], pos[1] + rp[1], pos[2] + rp[2])
        rot = quat_mul(rot, (lr.x, lr.y, lr.z, lr.w))
        scl = (scl[0]*ls.x, scl[1]*ls.y, scl[2]*ls.z)
    return pos, scl

want = ("CamLock A1", "CamLock A2", "CamLock A2 (1)", "CamLock Main", "CamLock Final", "CamLock Intro")
for gid, g in gos.items():
    if g.m_Name in want:
        p, s = world_of(gid)
        line = "%-16s world=(%.2f, %.2f)" % (g.m_Name, p[0], p[1])
        if gid in col_by_go:
            sx, sy, ox, oy = col_by_go[gid]
            line += "  X[%.1f..%.1f] Y[%.1f..%.1f]" % (
                p[0]+(ox-sx/2)*abs(s[0]), p[0]+(ox+sx/2)*abs(s[0]),
                p[1]+(oy-sy/2)*abs(s[1]), p[1]+(oy+sy/2)*abs(s[1]))
        print(line)
