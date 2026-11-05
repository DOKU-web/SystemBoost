"""Upscaler measurement: downscale a ground-truth image (area filter, like a game
rendering at lower resolution), upscale back, compare with the original.
  PSNR      : fidelity (higher = closer to native)
  sharpness : gradient energy relative to native (1.0 = same detail level)
  overshoot : mean amount pixels exceed the local source range (halo / ringing)
Usage: python3 -I measure_upscale.py <framegen_dir>"""
import sys, os
import numpy as np
root = sys.argv[1]
sys.path.insert(0, os.path.join(root, 'ref'))
sys.path.insert(0, os.path.join(root, 'tests'))
import upscale_ref as U
import scenes as S
from scipy import ndimage


def psnr(a, b):
    m = float(((a - b) ** 2).mean())
    return 10 * np.log10(1 / m)


def grad_energy(x):
    g = x.mean(-1)
    return float(np.hypot(np.diff(g, axis=0)[:, :-1], np.diff(g, axis=1)[:-1]).mean())


def area_down(img, f):
    h, w = img.shape[:2]
    return np.stack([ndimage.zoom(ndimage.uniform_filter(img[..., c], size=f), 1 / f, order=1)
                     for c in range(3)], -1).astype(np.float32)


# ground truth: smoother "game-like" image (shapes + gentle gradients + HUD text)
gt = S.make_texture(720, 1280, 5)
gt = ndimage.gaussian_filter(gt, (1.2, 1.2, 0))
gt[600:604, 40:440] = 1.0
for i in range(30):
    gt[560:576, 40 + i * 13:42 + i * 13] = 1.0
gt = np.clip(gt, 0, 1).astype(np.float32)
H, W = gt.shape[:2]
print(f"{'scale':6s} {'method':22s} {'PSNR':>6s} {'sharp':>6s} {'overshoot':>9s}")
for f in (1.5, 2.0):
    lo = np.clip(area_down(gt, f), 0, 1)
    ge = grad_energy(gt)
    up_nr = U.resample_bicubic_clamped(lo, H, W, anti_ring=False)
    methods = {
        'bilinear': U.bilinear_resize(lo, H, W),
        'bicubic (no clamp)': up_nr,
        'v0.6 sharp=0': U.upscale(lo, H, W, 0.0),
        'v0.6 sharp=0.5': U.upscale(lo, H, W, 0.5),
        'v0.6 sharp=1.0': U.upscale(lo, H, W, 1.0),
    }
    # local source range for overshoot measure
    rng_lo = ndimage.minimum_filter(U.bilinear_resize(lo, H, W), size=(5, 5, 1))
    rng_hi = ndimage.maximum_filter(U.bilinear_resize(lo, H, W), size=(5, 5, 1))
    for name, img in methods.items():
        over = np.maximum(img - rng_hi, 0) + np.maximum(rng_lo - img, 0)
        print(f"{f:<6} {name:22s} {psnr(img, gt):6.2f} {grad_energy(img) / ge:6.2f} {over.mean() * 1000:9.3f}")
