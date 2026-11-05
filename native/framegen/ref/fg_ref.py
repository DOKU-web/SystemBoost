"""
SystemBoost FrameGen v0.6 - CPU reference implementation (numpy).

This file is the *specification* of the GPU pipeline in ../shaders/*.hlsl.
Every step here has a 1:1 counterpart in a compute shader, so quality can be
measured on Linux/CI without a GPU. Constants live in PARAMS and mirror
FgConstants in shaders/common.hlsli.

Pipeline (per pair of captured frames A=prev, B=cur):
  1. luma at processing resolution (proc_scale of capture size)
  2. image pyramid (2x box downsample)
  3. bidirectional hierarchical block motion estimation (A->B and B->A)
       coarsest: full search +-R
       finer   : candidates (parent, parent neighbours, temporal, zero) + 3x3 refine
       each    : 3x3 component median filter
       finest  : parabolic sub-pixel refinement
  4. scene-cut test (mean best-match error)
  5. per output pixel, for time t:
       fixed-point lookup of vector at time t (2 iterations) for both directions
       forward/backward consistency -> per-direction confidence (occlusion)
       static/HUD protection (zero-motion test)
       confidence-weighted blend of the two warped samples
"""
import numpy as np

PARAMS = dict(
    proc_scale=0.5,      # processing resolution relative to capture
    block=8,             # block size in pixels at every pyramid level
    min_level_w=64,      # stop building pyramid below this width
    coarse_radius=6,     # full-search radius at coarsest level
    refine_iters=2,      # 3x3 refinement rounds at finer levels
    lam=0.004,           # smoothness penalty per pixel of deviation from predictor
    zero_bias=0.002,     # tiny preference for the zero vector (static scenes)
    fp_iters=2,          # fixed-point iterations for vector lookup at time t
    fb_sigma=1.5,        # consistency tolerance (capture pixels)
    fb_sigma_k=0.10,     # + tolerance proportional to motion length
    static_thr=0.02,     # HUD / static protection threshold (luma 0..1)
    cut_err=0.07,        # scene cut: mean best-match error
    cut_ratio=0.60,      # scene cut: best error / zero-motion error
)

LUMA = np.array([0.299, 0.587, 0.114], np.float32)


# ----------------------------------------------------------------- helpers
def to_luma(rgb):
    return (rgb[..., :3] @ LUMA).astype(np.float32)


def resize_area(img, scale):
    """Box downscale by an integer factor 1/scale (scale = 0.5, 0.25 ...)."""
    f = int(round(1 / scale))
    if f == 1:
        return img.copy()
    h, w = img.shape[:2]
    h2, w2 = h // f, w // f
    img = img[:h2 * f, :w2 * f]
    sh = (h2, f, w2, f) + img.shape[2:]
    return img.reshape(sh).mean(axis=(1, 3)).astype(np.float32)


def down2(img):
    return resize_area(img, 0.5)


def sample_bilinear(img, x, y):
    """Clamp-to-edge bilinear sampling. Returns (values, inside_mask)."""
    h, w = img.shape[:2]
    inside = (x >= 0) & (x <= w - 1) & (y >= 0) & (y <= h - 1)
    xc = np.clip(x, 0, w - 1)
    yc = np.clip(y, 0, h - 1)
    x0 = np.floor(xc).astype(np.int32)
    y0 = np.floor(yc).astype(np.int32)
    x1 = np.minimum(x0 + 1, w - 1)
    y1 = np.minimum(y0 + 1, h - 1)
    fx = (xc - x0).astype(np.float32)
    fy = (yc - y0).astype(np.float32)
    if img.ndim == 3:
        fx = fx[..., None]
        fy = fy[..., None]
    a = img[y0, x0] * (1 - fx) + img[y0, x1] * fx
    b = img[y1, x0] * (1 - fx) + img[y1, x1] * fx
    return (a * (1 - fy) + b * fy).astype(np.float32), inside


def box3(img):
    p = np.pad(img, 1, mode='edge')
    h, w = img.shape
    s = np.zeros_like(img)
    for dy in range(3):
        for dx in range(3):
            s += p[dy:dy + h, dx:dx + w]
    return s / 9.0


# ------------------------------------------------------- block matching
class BlockGrid:
    def __init__(self, h, w, b):
        self.h, self.w, self.b = h, w, b
        self.ny = (h + b - 1) // b
        self.nx = (w + b - 1) // b
        iy, ix = np.mgrid[0:self.ny, 0:self.nx]
        py, px = np.mgrid[0:b, 0:b]
        # absolute pixel coords of each block pixel, clamped (edge blocks)
        self.Y = np.minimum(iy[..., None, None] * b + py, h - 1)
        self.X = np.minimum(ix[..., None, None] * b + px, w - 1)


def block_cost(src, dst, grid, V):
    """Mean abs diff of each src block vs dst displaced by integer V (ny,nx,2)."""
    h, w = dst.shape
    vx = V[..., 0][..., None, None]
    vy = V[..., 1][..., None, None]
    X2 = grid.X + vx
    Y2 = grid.Y + vy
    out = (X2 < 0) | (X2 >= w) | (Y2 < 0) | (Y2 >= h)
    X2 = np.clip(X2, 0, w - 1)
    Y2 = np.clip(Y2, 0, h - 1)
    d = np.abs(src[grid.Y, grid.X] - dst[Y2, X2])
    d = np.where(out, 0.25, d)          # out-of-frame pixels count as poor match
    return d.mean(axis=(2, 3))


def median3_field(V):
    p = np.pad(V, ((1, 1), (1, 1), (0, 0)), mode='edge')
    ny, nx = V.shape[:2]
    stack = np.stack([p[dy:dy + ny, dx:dx + nx] for dy in range(3) for dx in range(3)], 0)
    return np.median(stack, axis=0)


def estimate_level(src, dst, grid, cands, pred, lam, zero_bias):
    """Pick best of candidate fields (list of (ny,nx,2) int arrays)."""
    best_v = None
    best_c = None
    best_e = None
    for C in cands:
        e = block_cost(src, dst, grid, C)
        c = e + lam * np.abs(C - pred).sum(-1) + zero_bias * (np.abs(C).sum(-1) > 0)
        if best_c is None:
            best_v, best_c, best_e = C.copy(), c, e
        else:
            m = c < best_c
            best_v[m] = C[m]
            best_c = np.where(m, c, best_c)
            best_e = np.where(m, e, best_e)
    return best_v, best_c, best_e


def motion_pyramid(src_pyr, dst_pyr, temporal=None, P=PARAMS):
    """Hierarchical block ME from src to dst. Returns per-level fields (finest first)
    of float vectors (sub-pixel at finest) and the finest best-match error."""
    b = P['block']
    L = len(src_pyr)
    V = None
    err = None
    for lv in range(L - 1, -1, -1):
        s, d = src_pyr[lv], dst_pyr[lv]
        g = BlockGrid(*s.shape, b)
        zero = np.zeros((g.ny, g.nx, 2), np.int32)
        if V is None:
            R = P['coarse_radius']
            cands = [np.full_like(zero, 0) + np.array([dx, dy], np.int32)
                     for dy in range(-R, R + 1) for dx in range(-R, R + 1)]
            if temporal is not None:
                cands.append(_resample_field(temporal, g, lv, P))
            pred = zero
        else:
            # parent field upsampled (nearest) and scaled x2
            iy = np.minimum(np.arange(g.ny) // 2, V.shape[0] - 1)
            ix = np.minimum(np.arange(g.nx) // 2, V.shape[1] - 1)
            Vp = np.round(V).astype(np.int32)
            par = Vp[iy][:, ix] * 2
            cands = [par, zero]
            for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                jy = np.clip(iy + dy, 0, V.shape[0] - 1)
                jx = np.clip(ix + dx, 0, V.shape[1] - 1)
                cands.append(Vp[jy][:, jx] * 2)
            if temporal is not None:
                cands.append(_resample_field(temporal, g, lv, P))
            pred = par
        best, cost, err = estimate_level(s, d, g, cands, pred, P['lam'], P['zero_bias'])
        # 3x3 refinement
        iters = P['refine_iters'] if V is not None else 1
        for _ in range(iters):
            rc = [best + np.array([dx, dy], np.int32)
                  for dy in (-1, 0, 1) for dx in (-1, 0, 1)]
            best, cost, err = estimate_level(s, d, g, rc, pred, P['lam'], P['zero_bias'])
        best = np.round(median3_field(best)).astype(np.int32)
        err = block_cost(s, d, g, best)
        V = best.astype(np.float32)
    # sub-pixel parabolic refinement at the finest level
    s, d = src_pyr[0], dst_pyr[0]
    g = BlockGrid(*s.shape, b)
    Vi = V.astype(np.int32)
    c0 = block_cost(s, d, g, Vi)
    for axis in (0, 1):
        o = np.zeros(2, np.int32)
        o[axis] = 1
        cm = block_cost(s, d, g, Vi - o)
        cp = block_cost(s, d, g, Vi + o)
        den = cm - 2 * c0 + cp
        off = np.where(den > 1e-6, 0.5 * (cm - cp) / np.maximum(den, 1e-6), 0.0)
        V[..., axis] += np.clip(off, -0.5, 0.5)
    return V, c0


def _resample_field(F, g, lv, P):
    """Temporal candidate: previous finest field resampled to level lv grid."""
    s = 2 ** lv
    iy = np.minimum((np.arange(g.ny) * s), F.shape[0] - 1)
    ix = np.minimum((np.arange(g.nx) * s), F.shape[1] - 1)
    return np.round(F[iy][:, ix] / s).astype(np.int32)


def build_pyramid(luma, P=PARAMS):
    pyr = [luma]
    while pyr[-1].shape[1] // 2 >= P['min_level_w']:
        pyr.append(down2(pyr[-1]))
    return pyr


# ------------------------------------------------------------ dense field
def dense_field(V, proc_shape, out_shape, P=PARAMS):
    """Bilinear upsample of block vectors to capture resolution (in capture px)."""
    b = P['block']
    H, W = out_shape
    ph, pw = proc_shape
    sx = pw / W
    sy = ph / H
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    # capture px -> block-grid coordinates (block centres at (i+0.5)*b)
    gx = ((xx + 0.5) * sx) / b - 0.5
    gy = ((yy + 0.5) * sy) / b - 0.5
    vx, _ = sample_bilinear(V[..., 0], gx, gy)
    vy, _ = sample_bilinear(V[..., 1], gx, gy)
    return np.stack([vx / sx, vy / sy], -1).astype(np.float32)


class FieldSampler:
    """Samples a coarse block field directly at arbitrary capture-px positions
    (what the GPU does with a bilinear sampler on the small vector texture)."""

    def __init__(self, V, proc_shape, out_shape, P=PARAMS):
        self.V = V
        self.b = P['block']
        self.sx = proc_shape[1] / out_shape[1]
        self.sy = proc_shape[0] / out_shape[0]

    def __call__(self, x, y):
        gx = ((x + 0.5) * self.sx) / self.b - 0.5
        gy = ((y + 0.5) * self.sy) / self.b - 0.5
        vx, _ = sample_bilinear(self.V[..., 0], gx, gy)
        vy, _ = sample_bilinear(self.V[..., 1], gx, gy)
        return vx / self.sx, vy / self.sy


# --------------------------------------------------------------- frame gen
class FrameGen:
    def __init__(self, P=PARAMS):
        self.P = dict(P)
        self.prev_fwd = None

    def analyze(self, A, B):
        """A, B: float32 RGB in [0,1], shape (H,W,3)."""
        P = self.P
        la = resize_area(to_luma(A), P['proc_scale'])
        lb = resize_area(to_luma(B), P['proc_scale'])
        pa, pb = build_pyramid(la, P), build_pyramid(lb, P)
        F, eF = motion_pyramid(pa, pb, self.prev_fwd, P)
        # temporal candidate only for the forward field (same as the GPU path)
        Bk, eB = motion_pyramid(pb, pa, None, P)
        self.prev_fwd = F
        g = BlockGrid(*pa[0].shape, P['block'])
        zero_err = block_cost(pa[0], pb[0], g, np.zeros_like(F, np.int32))
        mean_best = float(eF.mean())
        mean_zero = float(zero_err.mean())
        cut = mean_best > P['cut_err'] and mean_best > P['cut_ratio'] * mean_zero
        self.state = dict(A=A, B=B, F=F, Bk=Bk, proc_shape=pa[0].shape,
                          scene_cut=cut, mean_err=mean_best)
        return self.state

    def interpolate(self, t):
        st = self.state
        A, B = st['A'], st['B']
        H, W = A.shape[:2]
        if st['scene_cut']:
            return (A if t < 0.5 else B).copy()
        P = self.P
        fF = FieldSampler(st['F'], st['proc_shape'], (H, W), P)
        fB = FieldSampler(st['Bk'], st['proc_shape'], (H, W), P)
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)

        # forward: point p in A moves to p + t*F(p) at time t
        fx, fy = fF(xx, yy)
        for _ in range(P['fp_iters']):
            fx, fy = fF(xx - t * fx, yy - t * fy)
        pax, pay = xx - t * fx, yy - t * fy
        # backward: point q in B was at q + (1-t)*Bk(q) at time t
        gx, gy = fB(xx, yy)
        for _ in range(P['fp_iters']):
            gx, gy = fB(xx - (1 - t) * gx, yy - (1 - t) * gy)
        pbx, pby = xx - (1 - t) * gx, yy - (1 - t) * gy

        sa, ina = sample_bilinear(A, pax, pay)
        sb, inb = sample_bilinear(B, pbx, pby)

        # static map: pixels identical in A and B at the same place (HUD, UI,
        # letterbox). Computed once per pair; the GPU stores it in a R8 texture.
        smap = st.get('smap')
        if smap is None:
            smap = (np.abs(A - B).max(-1) < P['static_thr']).astype(np.float32)
            st['smap'] = smap

        # forward/backward consistency (occlusion detection)
        bx, by = fB(pax + fx, pay + fy)
        errA = np.hypot(fx + bx, fy + by)
        ax_, ay_ = fF(pbx + gx, pby + gy)
        errB = np.hypot(gx + ax_, gy + ay_)
        sigA = P['fb_sigma'] + P['fb_sigma_k'] * np.hypot(fx, fy)
        sigB = P['fb_sigma'] + P['fb_sigma_k'] * np.hypot(gx, gy)
        cA = np.exp(-(errA / sigA) ** 2) * ina
        cB = np.exp(-(errB / sigB) ** 2) * inb
        # don't drag static (HUD) pixels along with moving background
        movA = np.minimum(np.hypot(fx, fy) * t, 1.0)
        movB = np.minimum(np.hypot(gx, gy) * (1 - t), 1.0)
        sA, _ = sample_bilinear(smap, pax, pay)
        sB, _ = sample_bilinear(smap, pbx, pby)
        cA = cA * (1 - sA * movA)
        cB = cB * (1 - sB * movB)

        wA = (1 - t) * cA + 1e-3 * (1 - t)
        wB = t * cB + 1e-3 * t
        out = (sa * wA[..., None] + sb * wB[..., None]) / (wA + wB)[..., None]

        # static / HUD protection: content that did not move stays put
        static = smap > 0.5
        blend = A * (1 - t) + B * t
        out = np.where(static[..., None], blend, out)
        self.last_conf = (cA, cB, static)
        return np.clip(out, 0, 1).astype(np.float32)


# --------------------------------------------- "v0.5-style" comparison
class SimpleBlockFG:
    """Single-scale, forward-only block matching (+-8 at proc res), no occlusion
    handling. Stand-in for a typical first implementation; used only as a
    comparison baseline in tests (the real v0.5 code was not available here)."""

    def __init__(self, P=PARAMS):
        self.P = P

    def analyze(self, A, B):
        P = self.P
        la = resize_area(to_luma(A), P['proc_scale'])
        lb = resize_area(to_luma(B), P['proc_scale'])
        g = BlockGrid(*la.shape, P['block'])
        zero = np.zeros((g.ny, g.nx, 2), np.int32)
        R = 8
        cands = [zero + np.array([dx, dy], np.int32)
                 for dy in range(-R, R + 1) for dx in range(-R, R + 1)]
        V, _, _ = estimate_level(la, lb, g, cands, zero, 0.0, 0.0)
        self.A, self.B, self.V, self.ps = A, B, V.astype(np.float32), la.shape

    def interpolate(self, t):
        H, W = self.A.shape[:2]
        F = dense_field(self.V, self.ps, (H, W), self.P)
        yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
        sa, _ = sample_bilinear(self.A, xx - t * F[..., 0], yy - t * F[..., 1])
        sb, _ = sample_bilinear(self.B, xx + (1 - t) * F[..., 0], yy + (1 - t) * F[..., 1])
        return np.clip(sa * (1 - t) + sb * t, 0, 1)


class BlendFG:
    def analyze(self, A, B):
        self.A, self.B = A, B

    def interpolate(self, t):
        return self.A * (1 - t) + self.B * t
