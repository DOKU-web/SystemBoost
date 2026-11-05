"""Run the real shaders (vk_parity, lavapipe) and the numpy reference on the same
inputs and compare. Inputs are quantized to 8 bits like captured frames.
Usage: python3 -I compare.py <framegen_dir> <vk_parity_exe> <spv_dir> <work_dir>"""
import sys, os, json, subprocess
import numpy as np
root, exe, spv, work = sys.argv[1:5]
sys.path.insert(0, os.path.join(root, 'ref'))
sys.path.insert(0, os.path.join(root, 'tests'))
import fg_ref, upscale_ref
import scenes as S

os.makedirs(work, exist_ok=True)


def q8(x):
    return (np.round(np.clip(x, 0, 1) * 255) / 255).astype(np.float32)


def rgba(x):
    return np.concatenate([x, np.ones(x.shape[:2] + (1,), np.float32)], -1).astype(np.float32)


def psnr(a, b):
    m = float(((a - b) ** 2).mean())
    return 99.0 if m < 1e-10 else 10 * np.log10(1 / m)


ts = [1 / 3, 0.5, 2 / 3]
H, W = S.H, S.W
rows = []
for name in S.SCENES:
    f = S.scene(name)
    A, B = q8(f(0.0)), q8(f(1.0))
    rgba(A).tofile(os.path.join(work, 'A.bin'))
    rgba(B).tofile(os.path.join(work, 'B.bin'))
    pre = os.path.join(work, name)
    out = subprocess.run([exe, 'fg', spv, str(W), str(H), '2', '2', '6',
                          os.path.join(work, 'A.bin'), os.path.join(work, 'B.bin'), pre] +
                         [f'{t:.6f}' for t in ts], capture_output=True, text=True, check=True)
    stats = json.loads(out.stdout.strip().splitlines()[-1])
    ref = fg_ref.FrameGen()
    st = ref.analyze(A, B)
    # motion field parity (finest forward field, block grid)
    gf = np.fromfile(pre + '_fwd.bin', np.float32).reshape(st['F'].shape[0], st['F'].shape[1], 4)
    vec_diff = np.abs(gf[..., :2] - st['F'])
    same_vec = float((vec_diff.max(-1) < 0.05).mean() * 100)
    for i, t in enumerate(ts):
        g = np.fromfile(f'{pre}_t{i}.bin', np.float32).reshape(H, W, 4)[..., :3]
        r = ref.interpolate(t)
        gt = f(t)
        rows.append(dict(scene=name, t=round(t, 3), gpu_vs_ref=psnr(g, r), gpu_vs_truth=psnr(g, gt),
                         ref_vs_truth=psnr(r, gt), same_vectors_pct=same_vec,
                         gpu_meanBest=stats['meanBest'], ref_meanBest=round(st['mean_err'], 5),
                         gpu_cut=stats['meanBest'] > 0.07 and stats['meanBest'] > 0.6 * stats['meanZero'],
                         ref_cut=st['scene_cut']))

print(f"{'scene':10s} {'t':>5s} {'GPU~ref':>8s} {'GPU~GT':>7s} {'ref~GT':>7s} {'vec%':>6s} {'cut g/r':>8s}")
for r in rows:
    print(f"{r['scene']:10s} {r['t']:5.2f} {r['gpu_vs_ref']:8.2f} {r['gpu_vs_truth']:7.2f} {r['ref_vs_truth']:7.2f} "
          f"{r['same_vectors_pct']:6.1f} {str(r['gpu_cut'])[0]}/{str(r['ref_cut'])[0]:>5s}")

# temporal candidate path: three consecutive frames (P, A, B)
print()
for name in ('pan_large', 'occlusion'):
    f = S.scene(name)
    P, A, B = q8(f(-1.0)), q8(f(0.0)), q8(f(1.0))
    for nm, im in (('P', P), ('A', A), ('B', B)):
        rgba(im).tofile(os.path.join(work, nm + '.bin'))
    pre = os.path.join(work, name + '_tmp')
    env = dict(os.environ, SBFG_PREV=os.path.join(work, 'P.bin'))
    subprocess.run([exe, 'fg', spv, str(W), str(H), '2', '2', '6', os.path.join(work, 'A.bin'),
                    os.path.join(work, 'B.bin'), pre, '0.5'], env=env, capture_output=True, check=True)
    ref = fg_ref.FrameGen()
    ref.analyze(P, A)
    ref.analyze(A, B)
    g = np.fromfile(pre + '_t0.bin', np.float32).reshape(H, W, 4)[..., :3]
    r = ref.interpolate(0.5)
    print(f"temporal {name:10s} t=0.50  GPU~ref {psnr(g, r):6.2f}  GPU~GT {psnr(g, f(0.5)):6.2f}  ref~GT {psnr(r, f(0.5)):6.2f}")

# upscaler parity
src = q8(S.make_texture(180, 320, 9))
rgba(src).tofile(os.path.join(work, 'up_in.bin'))
subprocess.run([exe, 'up', spv, '320', '180', '640', '360', '0.5', os.path.join(work, 'up_in.bin'),
                os.path.join(work, 'up_out.bin')], check=True)
g = np.fromfile(os.path.join(work, 'up_out.bin'), np.float32).reshape(360, 640, 4)[..., :3]
r = upscale_ref.upscale(src, 360, 640, 0.5)
print(f"\nupscale 320x180->640x360 sharp=0.5: GPU~ref PSNR {psnr(g, r):.2f} dB, max abs diff {np.abs(g - r).max():.4f}")
