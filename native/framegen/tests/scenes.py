"""Synthetic test scenes with exact ground truth at any time tau.
render(tau) returns float32 RGB (H,W,3) in [0,1]; source frames are tau=0 and 1."""
import numpy as np
from scipy import ndimage

H, W = 360, 640
RNG = np.random.default_rng(7)


def make_texture(h, w, seed):
    r = np.random.default_rng(seed)
    img = np.zeros((h, w, 3), np.float32)
    for octave, amp in ((8, 0.45), (24, 0.25), (64, 0.15), (160, 0.08)):
        n = r.random((h // octave + 3, w // octave + 3, 3)).astype(np.float32)
        img += amp * ndimage.zoom(n, (octave, octave, 1), order=3)[:h, :w]
    # hard-edged shapes (buildings / props)
    for _ in range(60):
        y, x = r.integers(0, h), r.integers(0, w)
        hh, ww = r.integers(8, 70), r.integers(8, 70)
        img[y:y + hh, x:x + ww] = r.random(3) * 0.9
    yy, xx = np.mgrid[0:h, 0:w]
    for _ in range(25):
        cy, cx, rad = r.integers(0, h), r.integers(0, w), r.integers(5, 30)
        m = (yy - cy) ** 2 + (xx - cx) ** 2 < rad ** 2
        img[m] = r.random(3)
    img -= img.min()
    img /= img.max()
    return img.astype(np.float32)


BG = make_texture(H * 2, W * 2, 1)
BG2 = make_texture(H * 2, W * 2, 2)
FG = make_texture(140, 140, 3) * 0.6 + 0.4


def warp_affine(src, sx, sy, scale=1.0, cx=None, cy=None):
    """Sample src (big texture) so that output pixel (x,y) shows src at
    ((x-cx)/scale+cx + ox, ...). Cubic interpolation = ground truth renderer."""
    h, w = H, W
    cx = w / 2 if cx is None else cx
    cy = h / 2 if cy is None else cy
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float64)
    X = (xx - cx) / scale + cx + W / 2 - sx
    Y = (yy - cy) / scale + cy + H / 2 - sy
    out = np.stack([ndimage.map_coordinates(src[..., c], [Y, X], order=3, mode='reflect')
                    for c in range(3)], -1)
    return np.clip(out, 0, 1).astype(np.float32)


def hud_layer():
    """Static HUD: crosshair, health bar, text-like strokes. Returns (rgb, alpha)."""
    rgb = np.zeros((H, W, 3), np.float32)
    a = np.zeros((H, W), np.float32)
    # health bar
    a[330:342, 20:220] = 1
    rgb[330:342, 20:220] = (0.95, 0.55, 0.1)
    # "text": short strokes
    r = np.random.default_rng(11)
    for i in range(14):
        x = 20 + i * 11
        for _ in range(3):
            y = 300 + r.integers(0, 14)
            a[y:y + 2, x:x + 8] = 1
            rgb[y:y + 2, x:x + 8] = 1
        a[300:316, x:x + 2] = 1
        rgb[300:316, x:x + 2] = 1
    # crosshair
    a[178:182, 300:340] = 1
    a[160:200, 318:322] = 1
    rgb[178:182, 300:340] = 1
    rgb[160:200, 318:322] = 1
    # minimap frame
    a[16:18, 520:624] = a[118:120, 520:624] = 1
    a[16:120, 520:522] = a[16:120, 622:624] = 1
    rgb[a > 0] = np.maximum(rgb[a > 0], 0.9)
    return rgb, a


HUD_RGB, HUD_A = hud_layer()


def with_hud(img):
    return img * (1 - HUD_A[..., None]) + HUD_RGB * HUD_A[..., None]


def scene(name):
    if name == 'pan_small':
        return lambda t: with_hud(warp_affine(BG, 4 * t, 1 * t))
    if name == 'pan_large':
        return lambda t: with_hud(warp_affine(BG, 40 * t, 6 * t))
    if name == 'zoom':
        return lambda t: with_hud(warp_affine(BG, 0, 0, 1 + 0.04 * t))
    if name == 'occlusion':
        def f(t):
            bg = warp_affine(BG, -6 * t, 0)
            x0 = int(round(200 + 24 * t))  # GT uses exact sub-pixel via shift below
            fg_shift = 200 + 24 * t
            canvas = np.zeros((H, W, 3), np.float32)
            mask = np.zeros((H, W), np.float32)
            fx = fg_shift - np.floor(fg_shift)
            fgs = ndimage.shift(FG, (0, fx, 0), order=3, mode='nearest')
            xi = int(np.floor(fg_shift))
            canvas[110:250, xi:xi + 140] = fgs
            m = np.ones((140, 140), np.float32)
            m = ndimage.shift(m, (0, fx), order=1, mode='constant', cval=0)
            mask[110:250, xi:xi + 140] = m
            out = bg * (1 - mask[..., None]) + canvas * mask[..., None]
            return with_hud(np.clip(out, 0, 1))
        return f
    if name == 'scene_cut':
        return lambda t: with_hud(warp_affine(BG if t < 0.5 else BG2, 0, 0))
    raise KeyError(name)


def occlusion_band():
    """Pixels near the moving foreground's edges (where occlusion happens)."""
    m = np.zeros((H, W), bool)
    m[100:260, 190:200 + 140 + 34] = True
    inner = np.zeros((H, W), bool)
    inner[120:240, 236:330] = True
    return m & ~inner


SCENES = ['pan_small', 'pan_large', 'zoom', 'occlusion', 'scene_cut']
