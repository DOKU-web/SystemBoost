"""Measure interpolation quality (PSNR vs exact ground truth) for:
   blend   - plain 50/50 mix of the two frames
   simple  - single-scale forward-only block matching (typical 1st implementation)
   v0.6    - new bidirectional hierarchical pipeline (ref/fg_ref.py)
Usage: python3 -I measure_quality.py <framegen_dir> [out_dir]"""
import sys, os, json, time
import numpy as np

root = sys.argv[1]
sys.path.insert(0, os.path.join(root, 'ref'))
sys.path.insert(0, os.path.join(root, 'tests'))
import fg_ref
import scenes as S

out_dir = sys.argv[2] if len(sys.argv) > 2 else None


def psnr(a, b, mask=None):
    d = (a - b) ** 2
    if mask is not None:
        d = d[mask]
    mse = float(d.mean())
    return 99.0 if mse < 1e-10 else 10 * np.log10(1.0 / mse)


methods = {
    'blend': fg_ref.BlendFG,
    'simple': fg_ref.SimpleBlockFG,
    'v0.6': fg_ref.FrameGen,
}
ts = [1 / 3, 0.5, 2 / 3]
hud = S.HUD_A > 0
occ = S.occlusion_band()
results = {}
for name in S.SCENES:
    f = S.scene(name)
    A, B = f(0.0), f(1.0)
    gts = {t: f(t) for t in ts}
    results[name] = {}
    for mname, cls in methods.items():
        m = cls()
        t0 = time.time()
        st = m.analyze(A, B)
        outs = {t: m.interpolate(t) for t in ts}
        row = {
            'psnr': np.mean([psnr(outs[t], gts[t]) for t in ts]),
            'psnr_hud': np.mean([psnr(outs[t], gts[t], hud) for t in ts]),
        }
        if name == 'occlusion':
            row['psnr_occ'] = np.mean([psnr(outs[t], gts[t], occ) for t in ts])
        if mname == 'v0.6':
            row['scene_cut_detected'] = bool(st['scene_cut'])
            row['mean_err'] = round(st['mean_err'], 4)
        row['cpu_s'] = round(time.time() - t0, 2)
        results[name][mname] = row
        if out_dir and name in ('pan_large', 'occlusion'):
            from PIL import Image
            os.makedirs(out_dir, exist_ok=True)
            Image.fromarray((outs[0.5] * 255).astype(np.uint8)).save(
                os.path.join(out_dir, f'{name}_{mname}.png'))
    if out_dir and name in ('pan_large', 'occlusion'):
        from PIL import Image
        Image.fromarray((gts[0.5] * 255).astype(np.uint8)).save(
            os.path.join(out_dir, f'{name}_truth.png'))

print(json.dumps(results, indent=1, default=lambda x: round(float(x), 2)))
print('\nscene        method  PSNR  HUD   OCC  cut')
for n, r in results.items():
    for m, v in r.items():
        print(f"{n:12s} {m:6s} {v['psnr']:5.1f} {v['psnr_hud']:5.1f} {v.get('psnr_occ', float('nan')):5.1f} {v.get('scene_cut_detected','')}")
