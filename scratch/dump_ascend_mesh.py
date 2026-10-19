import UnityPy

PATH = r"E:/SteamLibrary/steamapps/common/Hollow Knight 1578/hollow_knight_Data/level459"
env = UnityPy.load(PATH)

gos = {}
for o in env.objects:
    if o.type.name == "GameObject":
        try: gos[o.path_id] = o.read()
        except Exception: pass

target = None
for gid, g in gos.items():
    if g.m_Name == "Ascend Beam":
        target = gid; break

g = gos[target]
for c in g.m_Component:
    if c.component.type.name == "MeshFilter":
        mf = c.component.read()
        mesh = mf.m_Mesh.read()
        print("mesh name:", mesh.m_Name)
        print("vertexCount:", mesh.m_VertexCount)
        aabb = mesh.m_LocalAABB
        print("AABB center:", aabb.m_Center)
        print("AABB extent:", aabb.m_Extent)
        # sample min/max of vertices
        try:
            verts = mesh.m_Vertices
            xs = verts[0::3]; ys = verts[1::3]
            print("x range: %.4f .. %.4f" % (min(xs), max(xs)))
            print("y range: %.4f .. %.4f" % (min(ys), max(ys)))
        except Exception as e:
            print("verts err", e, "n=", len(mesh.m_Vertices))
