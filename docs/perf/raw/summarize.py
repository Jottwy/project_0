import csv, glob, gzip, json, os, statistics as st, sys, re
try: sys.stdout.reconfigure(encoding="utf-8")
except Exception: pass

root = sys.argv[1] if len(sys.argv) > 1 else r"J:\Unity\BackroomsSurvivalMMO\.claude\worktrees\game-alpha-1-progress-bf3cd3\docs\perf\raw\probe"
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(root), "summary_tables.md")

def pct(v, q):
    if not v: return float('nan')
    s = sorted(v); return s[int(round((len(s) - 1) * q))]

def load_csv(path):
    op = gzip.open(path, "rt", encoding="utf-8") if path.endswith(".gz") else open(path, encoding="utf-8")
    rows = list(csv.DictReader(op))
    for r in rows:
        for k, v in r.items():
            try: r[k] = float(v)
            except: pass
    return rows

MARKERS = ["Update_ScriptRunBehaviourUpdate_ms", "PreLateUpdate_ScriptRunBehaviourLateUpdate_ms", "Physics_Simulate_ms",
           "FixedUpdate_PhysicsFixedUpdate_ms", "Animators_Update_ms", "RenderPipelineManager_DoRenderLoop_Internal()_ms",
           "PostLateUpdate_FinishFrameRendering_ms", "Culling_ms", "CullResults_CreateSharedRendererScene_ms",
           "Gfx_WaitForRenderThread_ms", "Gfx_WaitForPresentOnGfxThread_ms", "WaitForTargetFPS_ms", "Gfx_PresentFrame_ms",
           "Semaphore_WaitForSignal_ms", "GC_Collect_ms", "Shader_CreateGPUProgram_ms", "AudioManager_Update_ms",
           "PostLateUpdate_UpdateAllRenderers_ms", "PostLateUpdate_UpdateAllSkinnedMeshes_ms", "Loading_ReadObject_ms",
           "Update_ScriptRunDelayedDynamicFrameRate_ms", "PostLateUpdate_PlayerUpdateCanvases_ms", "PlayerLoop_ms"]

lines = []
def w(s=""): lines.append(s)

csvs = sorted(glob.glob(os.path.join(root, "*.csv")) + glob.glob(os.path.join(root, "*.csv.gz")))
runs = []
for path in csvs:
    name = os.path.basename(path).replace(".csv.gz", "").replace(".csv", "")
    m = re.match(r"(.+?)_(perf_[^_]+.*?|solo|playtest\d*)_?(\d{8}_\d{6})$", name)
    label = name
    rows = load_csv(path)
    rec = [r for r in rows if r["phase"] == 3.0]
    if len(rec) < 50: continue
    allr = rows
    def col(k, rs=rec): return [r[k] for r in rs if k in r and isinstance(r[k], float) and r[k] >= 0]
    dt = col("dt_ms"); cm = col("cpuMain_ms"); rt = col("cpuRender_ms"); gpu = col("gpu_ms")
    gpu_bound = 100.0 * sum(1 for r in rec if r["gpu_ms"] > 0 and r["gpu_ms"] > r["cpuMain_ms"]) / len(rec)
    gc = col("GC_Allocated_In_Frame"); gcc = col("GC_Allocation_In_Frame_Count")
    gccol = [r for r in rec if r.get("GC_Collect_ms", 0) > 0.01]
    med = pct(dt, .5)
    spikes = [r for r in rec if r["dt_ms"] > 2 * med]
    summary = dict(label=label, frames=len(rec), seconds=rec[-1]["t"] - rec[0]["t"],
                   dt50=pct(dt,.5), dt95=pct(dt,.95), dt99=pct(dt,.99), dtmax=max(dt),
                   cm50=pct(cm,.5), cm95=pct(cm,.95), rt50=pct(rt,.5), gpu50=pct(gpu,.5), gpu95=pct(gpu,.95), gpu_bound=gpu_bound,
                   gc_mean=st.mean(gc) if gc else -1, gc_p95=pct(gc,.95), gcc_mean=st.mean(gccol and [1]*len(gccol) or [0]),
                   gc_collects=len(gccol), gc_collect_max=max([r["GC_Collect_ms"] for r in rec] or [0]),
                   draw=pct(col("Draw_Calls_Count"),.5), setpass=pct(col("SetPass_Calls_Count"),.5), tris=pct(col("Triangles_Count"),.5),
                   verts=pct(col("Vertices_Count"),.5),
                   mem_total=pct(col("Total_Used_Memory"),.5)/1e6, mem_sys=pct(col("System_Used_Memory"),.5)/1e6,
                   mem_gc=pct(col("GC_Used_Memory"),.5)/1e6, mem_tex=pct(col("Texture_Memory"),.5)/1e6,
                   mem_mesh=pct(col("Mesh_Memory"),.5)/1e6, mem_rt=pct(col("Render_Textures_Bytes"),.5)/1e6,
                   mem_buf=pct(col("Used_Buffers_Bytes"),.5)/1e6, spikes=len(spikes), remotes=pct(col("remotes"),.5),
                   chunks=pct(col("builtChunks"),.5), rows=rec, allrows=allr, spikerows=spikes, path=path)
    for mk in MARKERS:
        v = col(mk)
        summary[mk] = (st.mean(v) if v else -1, pct(v,.95) if v else -1)
    runs.append(summary)

w("# Tablas generadas por summarize.py (fase 3 = grabación)")
w()
w("## Frametime (ms) y cuello")
w("| run | frames | dt p50 | p95 | p99 | max | cpuMain p50 | p95 | renderThr p50 | gpu p50 | gpu p95 | frames GPU>CPU | spikes >2×med | GC.Collect frames | GC.Collect max ms |")
w("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|")
for s in runs:
    w(f"| {s['label']} | {s['frames']} | {s['dt50']:.2f} | {s['dt95']:.2f} | {s['dt99']:.2f} | {s['dtmax']:.1f} | {s['cm50']:.2f} | {s['cm95']:.2f} | {s['rt50']:.2f} | {s['gpu50']:.2f} | {s['gpu95']:.2f} | {s['gpu_bound']:.0f} % | {s['spikes']} | {s['gc_collects']} | {s['gc_collect_max']:.2f} |")
w()
w("## Render y GC")
w("| run | draw calls | SetPass | tris | verts | GC alloc/frame (KB) mean | p95 | remotos | chunks |")
w("|---|---|---|---|---|---|---|---|---|")
for s in runs:
    w(f"| {s['label']} | {s['draw']:.0f} | {s['setpass']:.0f} | {s['tris']/1000:.0f} k | {s['verts']/1000:.0f} k | {s['gc_mean']/1024:.1f} | {s['gc_p95']/1024:.1f} | {s['remotes']:.0f} | {s['chunks']:.0f} |")
w()
w("## Memoria (MB, mediana de la grabación)")
w("| run | Total Used | System Used (WS) | GC heap | Texturas | Mallas | Render targets | Buffers GPU |")
w("|---|---|---|---|---|---|---|---|")
for s in runs:
    w(f"| {s['label']} | {s['mem_total']:.0f} | {s['mem_sys']:.0f} | {s['mem_gc']:.0f} | {s['mem_tex']:.0f} | {s['mem_mesh']:.0f} | {s['mem_rt']:.0f} | {s['mem_buf']:.0f} |")
w()
w("## Marcadores del hilo principal (ms, media / p95)")
short = {m: m.replace("_ms","").replace("Update_ScriptRunBehaviourUpdate","ScriptUpdate").replace("PreLateUpdate_ScriptRunBehaviourLateUpdate","ScriptLateUpdate").replace("RenderPipelineManager_DoRenderLoop_Internal()","SRP loop").replace("PostLateUpdate_FinishFrameRendering","FinishFrameRendering").replace("CullResults_CreateSharedRendererScene","CullResults").replace("Gfx_WaitForRenderThread","WaitRenderThread").replace("Gfx_WaitForPresentOnGfxThread","WaitPresent").replace("FixedUpdate_PhysicsFixedUpdate","PhysicsFixed").replace("PostLateUpdate_UpdateAllRenderers","UpdateAllRenderers").replace("PostLateUpdate_UpdateAllSkinnedMeshes","UpdateSkinned").replace("Update_ScriptRunDelayedDynamicFrameRate","Coroutines").replace("PostLateUpdate_PlayerUpdateCanvases","Canvases") for m in MARKERS}
w("| run | " + " | ".join(short[m] for m in MARKERS) + " |")
w("|---|" + "---|" * len(MARKERS))
for s in runs:
    cells = []
    for m in MARKERS:
        a, b = s[m]
        cells.append("n/a" if a < 0 else f"{a:.2f}/{b:.2f}")
    w(f"| {s['label']} | " + " | ".join(cells) + " |")
w()
w("## Tirones (dt > 2× mediana): los 8 mayores por run, con el marcador dominante")
for s in runs:
    sp = sorted(s["spikerows"], key=lambda r: -r["dt_ms"])[:8]
    if not sp: w(f"- {s['label']}: sin tirones"); continue
    w(f"- **{s['label']}** ({s['spikes']} tirones sobre mediana {s['dt50']:.2f} ms):")
    prev = {r["frame"]: i for i, r in enumerate(s["rows"])}
    for r in sp:
        mk = sorted(((r.get(m, -1) or 0), m) for m in MARKERS if isinstance(r.get(m, -1), float) and r.get(m, -1) > 0.3)
        mk = mk[::-1][:3]
        i = prev[r["frame"]]
        cd = int(r["builtChunks"] - s["rows"][i-1]["builtChunks"]) if i > 0 else 0
        w(f"  - t={r['t']:.1f}s dt={r['dt_ms']:.1f} ms cpuMain={r['cpuMain_ms']:.1f} gpu={r['gpu_ms']:.1f} GCalloc={r['GC_Allocated_In_Frame']/1024:.0f} KB chunkΔ={cd} " + ", ".join(f"{short[m]}={v:.1f}" for v, m in mk))
w()
w("## Carga y streaming (fases 1-2 = espera del mundo y warmup; eventos = frames donde builtChunks sube)")
for s in runs:
    allr = s["allrows"]
    pre = [r for r in allr if r["phase"] < 3]
    ev = [(allr[i], allr[i]["builtChunks"] - allr[i-1]["builtChunks"]) for i in range(1, len(allr)) if allr[i]["builtChunks"] > allr[i-1]["builtChunks"] and allr[i]["builtChunks"] >= 0 and allr[i-1]["builtChunks"] >= 0]
    evr = [(r, d) for r, d in ev if r["phase"] == 3]
    dts = [r["dt_ms"] for r, d in evr]
    w(f"- **{s['label']}**: frames antes de grabar={len(pre)}, dt max en carga/warmup={max([r['dt_ms'] for r in pre] or [0]):.0f} ms; eventos de chunk durante la grabación={len(evr)}" + (f", dt en esos frames: media {st.mean(dts):.1f} ms, max {max(dts):.1f} ms, chunks por evento media {st.mean([d for r,d in evr]):.1f}" if dts else ""))
    if evr:
        top = sorted(evr, key=lambda x: -x[0]["dt_ms"])[:5]
        for r, d in top:
            w(f"  - t={r['t']:.1f}s +{int(d)} chunk(s) dt={r['dt_ms']:.1f} ms cpuMain={r['cpuMain_ms']:.1f} GCalloc={r['GC_Allocated_In_Frame']/1024:.0f} KB ScriptUpdate={r.get('Update_ScriptRunBehaviourUpdate_ms',0):.1f} Loading={r.get('Loading_ReadObject_ms',0):.1f}")
w()
# renderer census files
for path in sorted(glob.glob(os.path.join(root, "*_renderers.tsv"))):
    w(f"## Censo de renderers: {os.path.basename(path)}")
    txt = open(path, encoding="utf-8").read().splitlines()
    w("```")
    for l in txt[:2] + txt[2:22] + txt[-2:]: w(l[:170])
    w("```")
open(out, "w", encoding="utf-8").write("\n".join(lines))
print("\n".join(lines))
