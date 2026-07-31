# SPDX-License-Identifier: Apache-2.0
"""Kimodo <-> Unity bridge server.

A small FastAPI service that loads Kimodo diffusion models once and exposes a plain
JSON API so external apps (e.g. a Unity editor plugin) can request generated motion
in real time.

The generation path mirrors ``kimodo/scripts/generate.py`` exactly (same model call,
same conversion from global rotations to local rotations on the export skeleton) so the
motion sent to Unity is consistent with the official CLI / demo outputs.

Coordinate system note: all data is returned in *Kimodo* coordinates
(right-handed, Y-up, +Z forward, metres). The Unity client is responsible for
converting to Unity's left-handed space.

Run with::

    python -m kimodo_bridge            # or: kimodo-bridge (see run_bridge.py)

Endpoints:
    GET  /health          -> liveness + device + loaded models
    GET  /models          -> registry of available models
    POST /load_model      -> preload a model (optional; /generate loads on demand)
    POST /generate        -> generate motion, returns skeleton + per-frame clips
"""

from __future__ import annotations

import contextlib
import threading
import time
import traceback
from typing import Any, Dict, List, Optional

import numpy as np
import torch
from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel, Field

from kimodo import load_model
from kimodo.constraints import (
    TYPE_TO_CLASS,
    EndEffectorConstraintSet,
    FullBodyConstraintSet,
    LeftFootConstraintSet,
    LeftHandConstraintSet,
    RightFootConstraintSet,
    RightHandConstraintSet,
    create_pairs,
)
from kimodo.geometry import matrix_to_quaternion, quaternion_to_axis_angle, quaternion_to_matrix
from kimodo.model.registry import (
    DEFAULT_MODEL,
    MODEL_INFOS,
    get_model_info,
    resolve_model_name,
)
from kimodo.skeleton import SOMASkeleton30, global_rots_to_local_rots

# ---------------------------------------------------------------------------
# Global model cache (models are heavy: load once, reuse across requests).
# A single lock serialises generation because the model / GPU is not reentrant.
# ---------------------------------------------------------------------------
_DEVICE = "cuda:0" if torch.cuda.is_available() else "cpu"
_MODELS: dict[str, object] = {}          # resolved_short_key -> model
_MODEL_LOCK = threading.Lock()           # guards _MODELS mutation + generation

app = FastAPI(title="Kimodo Unity Bridge", version="0.1.0")


def _describe_exception(exc: BaseException) -> str:
    """One line the client can actually act on: what broke, and where in OUR code it broke.

    A bare 500 tells the user nothing — Starlette's default body is the string "Internal Server
    Error", which is what reaches Unity's error box. The exception type, its message and the last
    frame inside kimodo/kimodo_bridge are enough to say which part of the request is at fault.
    """
    frames = traceback.extract_tb(exc.__traceback__)
    where = ""
    for f in reversed(frames):
        path = f.filename.replace("\\", "/")
        if "/kimodo" in path:
            where = f" at {path.rsplit('/', 1)[-1]}:{f.lineno} in {f.name}()"
            break
    return f"{type(exc).__name__}: {exc}{where}"


@app.exception_handler(Exception)
async def _unhandled_exception(_request: Request, exc: Exception):
    """Anything that escapes a handler still comes back as JSON with a reason.

    Without this the client gets a 500 with no body and no way to tell a bad request from a broken
    model, which is exactly the position a "generation failed, HTTP 500" report leaves everyone in.
    """
    traceback.print_exc()
    _progress_end()
    return JSONResponse(status_code=500, content={"detail": _describe_exception(exc)})


# ---------------------------------------------------------------------------
# Request / response schemas
# ---------------------------------------------------------------------------
class LoadModelRequest(BaseModel):
    model: str = Field(default=DEFAULT_MODEL, description="Model name/short key, e.g. 'soma' or 'kimodo-soma-rp'.")


class GenerateRequest(BaseModel):
    prompt: str = Field(..., description="Motion prompt. Periods separate sequential segments.")
    model: str = Field(default=DEFAULT_MODEL, description="Model name/short key.")
    duration: str = Field(default="5.0", description="Seconds. Space-separated to give a duration per segment.")
    num_samples: int = Field(default=1, ge=1, le=8)
    diffusion_steps: int = Field(default=100, ge=1, le=1000)
    num_transition_frames: int = Field(default=5, ge=0)
    seed: Optional[int] = Field(default=None)
    cfg_weight: Optional[List[float]] = Field(
        default=None,
        description="Optional [text_weight, constraint_weight]. Defaults to Kimodo's own default.",
    )
    segment_cfg_weights: Optional[List[float]] = Field(
        default=None,
        description=(
            "Optional per-segment constraint weights: one value per prompt segment, overriding "
            "cfg_weight[1] for that segment only. Must have exactly one entry per segment."
        ),
    )
    postprocess: bool = Field(default=True, description="Foot-skate cleanup (ignored for G1).")
    has_first_heading: bool = Field(
        default=False,
        description="Whether first_heading_angle below is meaningful (JSON clients cannot send null floats).",
    )
    first_heading_angle: float = Field(
        default=0.0,
        description=(
            "Initial body heading in radians, Kimodo convention (0 = facing +Z). Used only when "
            "has_first_heading is true. Set it when a request continues from motion the client already "
            "has — e.g. the frames after a frozen timeline segment — so the model starts out facing the "
            "way that motion ended instead of the default 0."
        ),
    )
    include_positions: bool = Field(
        default=False,
        description="Also return posed_joints world positions per frame (larger payload, useful for debugging).",
    )
    constraints: Optional[List[Dict[str, Any]]] = Field(
        default=None,
        description=(
            "Optional keyframe constraints (pin hands/feet or the full body). Each item is "
            "{type, frameIndices:[N], localQuats:[N*J*4 wxyz], rootPositions:[N*3], jointNames?}, "
            "all in Kimodo coords (metres) — i.e. straight from a prior /generate clip. "
            "Supported types: left-hand, right-hand, left-foot, right-foot, fullbody, end-effector."
        ),
    )


def _model_summary(info) -> dict:
    loaded = info.short_key in _MODELS
    return {
        "shortKey": info.short_key,
        "displayName": info.display_name,
        "family": info.family,
        "skeleton": info.skeleton,
        "dataset": info.dataset_ui_label,
        "version": info.version,
        "loaded": loaded,
    }


# ---------------------------------------------------------------------------
# Model loading
# ---------------------------------------------------------------------------
def _get_or_load_model(name: str):
    """Return a cached model for ``name``, loading it only if not already resident.

    Loading a model also loads the heavy text encoder, so we MUST reuse a cached
    model across requests (otherwise every /generate reloads everything and is slow).
    Returns (model, resolved_short_key). Caller holds _MODEL_LOCK.
    """
    # Resolve the requested name to its canonical short key so cache hits work
    # regardless of how the client spelled it (e.g. "soma" vs "kimodo-soma-rp").
    try:
        resolved = resolve_model_name(name, "Kimodo")
    except Exception:
        resolved = name

    cached = _MODELS.get(resolved)
    if cached is not None:
        return cached, resolved

    model, resolved = load_model(
        name,
        device=_DEVICE,
        default_family="Kimodo",
        return_resolved_name=True,
    )
    _MODELS[resolved] = model
    return model, resolved


def _export_skeleton(model):
    """Skeleton whose joint order / neutral pose matches the model output arrays.

    For SOMA the model emits somaskel77 arrays even though model.skeleton is
    somaskel30, so we expose the 77-joint skeleton (full fingers/toes).
    """
    skel = model.skeleton
    if isinstance(skel, SOMASkeleton30):
        return skel.somaskel77.to(_DEVICE)
    return skel


def _skeleton_bones(skel) -> list[dict]:
    """Rest-pose bone list: name, parent index, local offset (metres, Kimodo coords)."""
    neutral = skel.neutral_joints.detach().cpu().numpy()  # [J, 3], root at origin (standard T-pose)
    parents = skel.joint_parents.detach().cpu().numpy().astype(int)
    names = list(skel.bone_order_names)
    bones = []
    for i, name in enumerate(names):
        p = int(parents[i])
        offset = neutral[i] if p < 0 else (neutral[i] - neutral[p])
        bones.append(
            {
                "name": name,
                "parent": p,
                "ox": float(offset[0]),
                "oy": float(offset[1]),
                "oz": float(offset[2]),
            }
        )
    return bones


# ---------------------------------------------------------------------------
# Constraints
# ---------------------------------------------------------------------------
def _build_constraint_dicts(
    unity_constraints: List[Dict[str, Any]], n_joints: int, total_frames: int
) -> list[dict]:
    """Translate the Unity constraint payload into the dict form load_constraints_lst expects.

    The Unity side sends poses straight from a prior /generate clip, so they are already in
    Kimodo coords (metres): ``localQuats`` are per-joint local rotations (wxyz), ``rootPositions``
    the pelvis path. kimodo.constraints, however, wants per-joint local rotations as *axis-angle*,
    so we convert here (quaternion -> axis-angle) and keep everything else as-is.

    Frames outside ``[0, total_frames)`` are dropped (the target motion may be shorter than the
    clip a keyframe was captured from), and constraints left with no frames are skipped.
    """
    dicts: list[dict] = []
    for c in unity_constraints:
        ctype = c.get("type")
        if ctype not in TYPE_TO_CLASS:
            raise HTTPException(status_code=400, detail=f"Unknown constraint type '{ctype}'.")

        frame_indices = [int(f) for f in c.get("frameIndices", [])]
        keep = [k for k, f in enumerate(frame_indices) if 0 <= f < total_frames]
        if not keep:
            continue
        kept_frames = [frame_indices[k] for k in keep]

        # root2d: pin the pelvis (x, z) ground path through the given frames (waypoints).
        if ctype == "root2d":
            try:
                path = torch.as_tensor(c["rootPath2d"], dtype=torch.float32).reshape(-1, 2)
            except (KeyError, RuntimeError) as exc:
                raise HTTPException(
                    status_code=400,
                    detail=f"root2d constraint needs 'rootPath2d' as N*2 (x,z) values: {exc}",
                ) from exc
            keep_idx = torch.as_tensor(keep, dtype=torch.long)
            d = {
                "type": "root2d",
                "frame_indices": kept_frames,
                "smooth_root_2d": path[keep_idx].tolist(),
            }
            # Optional per-frame facing: [cos, sin] of the Kimodo heading angle.
            heading = c.get("rootHeading2d")
            if heading:
                try:
                    h = torch.as_tensor(heading, dtype=torch.float32).reshape(-1, 2)
                    if h.shape[0] == len(frame_indices):
                        d["global_root_heading"] = h[keep_idx].tolist()
                except (RuntimeError, ValueError):
                    pass  # malformed heading -> position only
            dicts.append(d)
            continue

        # Direct effector target: explicit world->Kimodo position (+ optional rotation) at a frame.
        # (Empty/absent effectorPos falls through to the pose path, e.g. IK'd full-body poses.)
        if ctype in _EFFECTOR_JOINT and c.get("effectorPos"):
            keep_idx = torch.as_tensor(keep, dtype=torch.long)
            try:
                pos = torch.as_tensor(c["effectorPos"], dtype=torch.float32).reshape(-1, 3)
                rxz = torch.as_tensor(c["effectorRootXZ"], dtype=torch.float32).reshape(-1, 2)
            except (KeyError, RuntimeError) as exc:
                raise HTTPException(
                    status_code=400,
                    detail=f"Effector target '{ctype}' needs effectorPos [N*3] and effectorRootXZ [N*2]: {exc}",
                ) from exc
            td = {
                "type": ctype,
                "_target": True,
                "frame_indices": kept_frames,
                "positions": pos[keep_idx].tolist(),
                "root_xz": rxz[keep_idx].tolist(),
                "constrain_rot": bool(c.get("constrainRot", False)),
            }
            rot = c.get("effectorRot")
            if rot and td["constrain_rot"]:
                try:
                    q = torch.as_tensor(rot, dtype=torch.float32).reshape(-1, 4)  # wxyz
                    td["rot_quats"] = q[keep_idx].tolist()
                except RuntimeError:
                    td["constrain_rot"] = False
            dicts.append(td)
            continue

        try:
            quats = torch.as_tensor(c["localQuats"], dtype=torch.float32).reshape(-1, n_joints, 4)
            roots = torch.as_tensor(c["rootPositions"], dtype=torch.float32).reshape(-1, 3)
        except (KeyError, RuntimeError) as exc:
            raise HTTPException(
                status_code=400,
                detail=f"Constraint '{ctype}' pose arrays are missing or the wrong length "
                f"(expected {n_joints} joints): {exc}",
            ) from exc

        keep_idx = torch.as_tensor(keep, dtype=torch.long)
        aa = quaternion_to_axis_angle(quats[keep_idx])  # [N, J, 3] axis-angle
        d = {
            "type": ctype,
            "frame_indices": kept_frames,
            "local_joints_rot": aa.tolist(),
            "root_positions": roots[keep_idx].tolist(),
        }
        joint_names = c.get("jointNames")
        if joint_names:
            d["joint_names"] = list(joint_names)

        # Hand/foot keys: by default they behave exactly like the demo's end-effector keyframes —
        # the pose's root ground position, height and facing are pinned along with the limb, so the
        # generated motion matches the pose that was authored. "freeRoot" opts into the relaxed
        # variant below (limb + ground position only), for when the body should adapt to reach.
        if ctype in _EFFECTOR_JOINT and c.get("freeRoot"):
            d["_free_root"] = True

        # fullbody only: an active-joint subset -> constrain ONLY these joints' positions (partial
        # pose, e.g. upper body). Resolved to indices at load time (robust to SOMA 30/77).
        active = c.get("activeJoints")
        if ctype == "fullbody" and active:
            d["active_joint_names"] = list(active)

        # Optional per-keyframe target offsets (Kimodo coords): shift of the pinned joint from its
        # captured position, so the user can drag a hand/foot to a new target. Stashed under a
        # private key (from_dict ignores it) and applied after the constraint objects are built.
        offsets = c.get("targetOffsets")
        if offsets:
            try:
                off_t = torch.as_tensor(offsets, dtype=torch.float32).reshape(-1, 3)
                if off_t.shape[0] == len(frame_indices) and off_t.abs().sum() > 0:
                    d["_target_offsets"] = off_t[keep_idx].tolist()
            except (RuntimeError, ValueError):
                pass  # malformed offsets -> just pin as-is
        dicts.append(d)
    return dicts


def _apply_target_offsets(constraint_lst: list, con_dicts: list[dict]) -> None:
    """Shift pinned end-effector positions by the per-keyframe target offsets (in place).

    constraint_lst and con_dicts are index-aligned (load_constraints_lst preserves order). Only
    end-effector constraints have global_joints_positions + pos_indices; others are left untouched.
    Done before model() so multi_prompt's crop_move carries the shifted positions.
    """
    for c, d in zip(constraint_lst, con_dicts):
        offs = d.get("_target_offsets")
        if not offs:
            continue
        gjp = getattr(c, "global_joints_positions", None)
        pos_idx = getattr(c, "pos_indices", None)
        if gjp is None or pos_idx is None:
            continue
        off_t = torch.tensor(offs, dtype=gjp.dtype, device=gjp.device)  # [N, 3]
        if off_t.shape[0] != gjp.shape[0]:
            continue
        idx = pos_idx.to(gjp.device)  # local copy; keep the CPU pos_indices for create_pairs
        gjp[:, idx, :] += off_t[:, None, :]

        # Track the root horizontally with the effector so the body follows it (and the foot's
        # position relative to the root stays feasible instead of over-stretching the leg).
        sr = getattr(c, "smooth_root_2d", None)
        if sr is not None and sr.shape[0] == off_t.shape[0]:
            sr[:, 0] += off_t[:, 0]  # x
            sr[:, 1] += off_t[:, 2]  # z


# ---------------------------------------------------------------------------
# Position-only end-effector constraints ("free root", opt-in per key).
#
# Kimodo's stock end-effector constraint ALSO pins the root ground position
# (smooth_root_2d), root height (root_y_pos) and facing (global_root_heading) from
# the pose the keyframe carries. That is the right default — it is what the demo
# does, and with a pose authored in the editor those root values are exactly what
# the user posed. But when a limb has to reach somewhere the body must adapt to
# (stepping onto a box while the hips stay where the motion put them), the pinned
# height/facing fight the pin. These subclasses then constrain ONLY the effector
# joint position(s) + orientation, leaving height and facing free.
# ---------------------------------------------------------------------------
def _ee_position_only_update(self, data_dict, index_dict):
    crop = torch.arange(len(self.frame_indices), device=self.frame_indices.device)
    # Effector chain positions.
    pos_real = create_pairs(self.frame_indices, self.pos_indices)
    pos_crop = create_pairs(crop, self.pos_indices)
    data_dict["global_joints_positions"].append(self.global_joints_positions[tuple(pos_crop.T)])
    index_dict["global_joints_positions"].append(pos_real)
    # Effector base orientation (keeps the foot/hand naturally oriented).
    rot_real = create_pairs(self.frame_indices, self.rot_indices)
    rot_crop = create_pairs(crop, self.rot_indices)
    data_dict["global_joints_rots"].append(self.global_joints_rots[tuple(rot_crop.T)])
    index_dict["global_joints_rots"].append(rot_real)
    # smooth_root_2d (root XZ) is REQUIRED whenever global positions are constrained: the motion
    # rep stores joints relative to it. We keep it, but deliberately DROP root_y_pos (height) and
    # global_root_heading (facing) so the body can rise/turn to reach the pinned effector.
    data_dict["smooth_root_2d"].append(self.smooth_root_2d)
    index_dict["smooth_root_2d"].append(self.frame_indices)


class _LeftHandPO(LeftHandConstraintSet):
    update_constraints = _ee_position_only_update


class _RightHandPO(RightHandConstraintSet):
    update_constraints = _ee_position_only_update


class _LeftFootPO(LeftFootConstraintSet):
    update_constraints = _ee_position_only_update


class _RightFootPO(RightFootConstraintSet):
    update_constraints = _ee_position_only_update


class _EndEffPO(EndEffectorConstraintSet):
    update_constraints = _ee_position_only_update


_POSITION_ONLY_CLASSES = {
    "left-hand": _LeftHandPO,
    "right-hand": _RightHandPO,
    "left-foot": _LeftFootPO,
    "right-foot": _RightFootPO,
    "end-effector": _EndEffPO,
}


_EFFECTOR_JOINT = {
    "left-foot": "LeftFoot",
    "right-foot": "RightFoot",
    "left-hand": "LeftHand",
    "right-hand": "RightHand",
}


class _SingleJointTarget:
    """Constrain exactly one joint's global position (+ optional rotation) at given frames,
    plus the required smooth_root_2d. Built directly from an explicit UI target (no pose FK),
    so 'put this hand/foot here at frame F' maps straight through — like the demo's keyframe.
    """

    name = "effector-target"

    def __init__(self, skeleton, frame_indices, joint_idx, positions, rots, smooth_root_2d, constrain_rot=True):
        self.skeleton = skeleton
        self.frame_indices = frame_indices                # [N] (CPU)
        self.joint_idx = int(joint_idx)
        self.pos_indices = torch.tensor([self.joint_idx])  # (CPU, matches frame_indices)
        self.rot_indices = torch.tensor([self.joint_idx])
        self.positions = positions                         # [N, 3] (device)
        self.rots = rots                                   # [N, 3, 3] (device) or None
        self.smooth_root_2d = smooth_root_2d               # [N, 2] (device)
        self.constrain_rot = bool(constrain_rot and rots is not None)

    def update_constraints(self, data_dict, index_dict):
        data_dict["global_joints_positions"].append(self.positions)
        index_dict["global_joints_positions"].append(create_pairs(self.frame_indices, self.pos_indices))
        if self.constrain_rot:
            data_dict["global_joints_rots"].append(self.rots)
            index_dict["global_joints_rots"].append(create_pairs(self.frame_indices, self.rot_indices))
        # smooth_root_2d is required whenever global positions are constrained.
        data_dict["smooth_root_2d"].append(self.smooth_root_2d)
        index_dict["smooth_root_2d"].append(self.frame_indices)

    def crop_move(self, start, end):
        mask = (self.frame_indices >= start) & (self.frame_indices < end)
        rots = self.rots[mask] if self.rots is not None else None
        return _SingleJointTarget(
            self.skeleton, self.frame_indices[mask] - start, self.joint_idx,
            self.positions[mask], rots, self.smooth_root_2d[mask], self.constrain_rot,
        )

    def to(self, device=None, dtype=None):
        return self  # tensors are already placed by the builder


class _PartialFullBody:
    """A pose constraint restricted to a subset of joints (by index). It constrains ONLY the active
    joints' global positions (+ the required smooth_root_2d/root_y/heading) in the diffusion GUIDANCE,
    leaving the rest of the body free — so a pose key can drive, e.g., only the upper body.

    It is intentionally NOT a FullBodyConstraintSet subclass: postprocess (postprocess.py) rewrites the
    WHOLE body's rotations for any FullBody/EndEffector constraint (it can't do per-joint), which would
    snap the free joints back to the pose. Being a plain class, postprocess skips it (its else branch is
    a no-op), so the inactive joints stay free.
    """

    name = "fullbody-partial"

    def __init__(self, skeleton, frame_indices, global_joints_positions, global_joints_rots,
                 smooth_root_2d, root_y_pos, global_root_heading, active_indices):
        self.skeleton = skeleton
        self.frame_indices = frame_indices                      # [N] (CPU)
        self.global_joints_positions = global_joints_positions  # [N, J, 3]    (device)
        self.global_joints_rots = global_joints_rots            # [N, J, 3, 3] (device)
        self.smooth_root_2d = smooth_root_2d                    # [N, 2] (device)
        self.root_y_pos = root_y_pos                            # [N]    (device)
        self.global_root_heading = global_root_heading          # [N, 2] (device)
        self.active_indices = active_indices                    # [K] LongTensor (CPU)

    @classmethod
    def from_full(cls, base: FullBodyConstraintSet, active_indices: "torch.Tensor") -> "_PartialFullBody":
        return cls(base.skeleton, base.frame_indices, base.global_joints_positions, base.global_joints_rots,
                   base.smooth_root_2d, base.root_y_pos, base.global_root_heading, active_indices)

    def update_constraints(self, data_dict: dict, index_dict: dict) -> None:
        # Constrain the active joints' global POSITIONS and ROTATIONS (index tensors stay on CPU; the
        # data tensors live on their own device). Rotations are the primary pose channel — imputing
        # them is what makes a partial pose actually stick without the whole-body postprocess snap.
        active_cpu = self.active_indices
        dev = self.global_joints_positions.device
        active_dev = active_cpu.to(dev)
        pairs = create_pairs(self.frame_indices, active_cpu)  # CPU [N*K, 2] (t, j)

        data_dict["global_joints_positions"].append(self.global_joints_positions[:, active_dev, :].reshape(-1, 3))
        index_dict["global_joints_positions"].append(pairs)

        data_dict["global_joints_rots"].append(self.global_joints_rots[:, active_dev, :, :].reshape(-1, 3, 3))
        index_dict["global_joints_rots"].append(pairs)

        # Root channels are required whenever global positions are constrained.
        data_dict["smooth_root_2d"].append(self.smooth_root_2d)
        index_dict["smooth_root_2d"].append(self.frame_indices)
        data_dict["root_y_pos"].append(self.root_y_pos)
        index_dict["root_y_pos"].append(self.frame_indices)
        data_dict["global_root_heading"].append(self.global_root_heading)
        index_dict["global_root_heading"].append(self.frame_indices)

    def crop_move(self, start: int, end: int) -> "_PartialFullBody":
        mask = (self.frame_indices >= start) & (self.frame_indices < end)   # CPU bool
        md = mask.to(self.global_joints_positions.device)
        return _PartialFullBody(
            self.skeleton, self.frame_indices[mask] - start,
            self.global_joints_positions[md], self.global_joints_rots[md], self.smooth_root_2d[md],
            self.root_y_pos[md], self.global_root_heading[md], self.active_indices,
        )

    def to(self, device=None, dtype=None):
        return self  # tensors already placed by the builder


def _load_constraints(con_dicts: list[dict], skeleton) -> list:
    """Build constraint objects. Direct effector targets use _SingleJointTarget; a fullbody with an
    active-joint subset uses _PartialFullBody; a hand/foot key marked "_free_root" uses the
    position-only subclass; everything else uses Kimodo's own class for that type, which is what the
    demo does.
    """
    lst = []
    dev = skeleton.device if hasattr(skeleton, "device") else "cpu"
    for d in con_dicts:
        if d.get("_target"):
            jidx = skeleton.bone_index[_EFFECTOR_JOINT[d["type"]]]
            frames = torch.tensor(d["frame_indices"])
            pos = torch.tensor(d["positions"], dtype=torch.float32, device=dev).reshape(-1, 3)
            sr = torch.tensor(d["root_xz"], dtype=torch.float32, device=dev).reshape(-1, 2)
            rots = None
            if d.get("constrain_rot") and "rot_quats" in d:
                q = torch.tensor(d["rot_quats"], dtype=torch.float32, device=dev).reshape(-1, 4)  # wxyz
                rots = quaternion_to_matrix(q)  # [N, 3, 3]
            lst.append(_SingleJointTarget(skeleton, frames, jidx, pos, rots, sr, bool(d.get("constrain_rot"))))
        elif d["type"] == "fullbody" and d.get("active_joint_names"):
            # Partial pose: keep only the named joints that exist in this skeleton (30/77-safe).
            idx = [skeleton.bone_index[n] for n in d["active_joint_names"] if n in skeleton.bone_index]
            if not idx:
                continue
            base = FullBodyConstraintSet.from_dict(skeleton, d)
            lst.append(_PartialFullBody.from_full(base, torch.tensor(idx, dtype=torch.long)))
        elif d.get("_free_root"):
            cls = _POSITION_ONLY_CLASSES.get(d["type"], TYPE_TO_CLASS[d["type"]])
            lst.append(cls.from_dict(skeleton, d))
        else:
            lst.append(TYPE_TO_CLASS[d["type"]].from_dict(skeleton, d))
    return lst


# ---------------------------------------------------------------------------
# Generation progress
#
# A generate call takes tens of seconds, and until it returns the client has nothing to show. Kimodo's
# own denoising loop takes a `progress_bar` callable that wraps the step iterator (kimodo_model.py:
# `for i in progress_bar(indices)`), which is a real hook rather than a guess, so we pass one that
# records where it is. /progress then reports it.
#
# Two details worth knowing when reading the numbers:
#   * multi-prompt generation runs ONE denoising loop per segment, so progress is (segment + step/steps)
#     over the segment count;
#   * before the first loop the text encoder runs, which on CPU is a big slice of the wall clock. That
#     phase reports as "encoding" with no step count, rather than pretending to be at 0 % of denoising.
# ---------------------------------------------------------------------------
_PROGRESS_LOCK = threading.Lock()
_PROGRESS = {
    "running": False,
    "phase": "idle",      # idle | encoding | denoising | postprocess
    "segment": 0,         # 0-based, of segments
    "segments": 0,
    "step": 0,            # within the current segment
    "steps": 0,
    "loops": 0,           # denoising loops seen so far == segments started
    "startedAt": 0.0,
}


def _progress_begin(segments: int) -> None:
    with _PROGRESS_LOCK:
        _PROGRESS.update(
            running=True, phase="encoding", segment=0, segments=max(1, segments),
            step=0, steps=0, loops=0, startedAt=time.time(),
        )


def _progress_end() -> None:
    with _PROGRESS_LOCK:
        _PROGRESS.update(running=False, phase="idle", step=0, steps=0)


def _progress_bar():
    """A stand-in for tqdm: wraps the denoising step iterator and records the position.

    Kimodo only ever uses the return value as an iterable, so a generator is enough. Each call marks
    the start of another segment's denoising loop.
    """
    def bar(iterable, *_args, **_kwargs):
        steps = list(iterable)
        with _PROGRESS_LOCK:
            # Count the loops rather than looking at the phase: by the time the next segment starts,
            # the previous loop has already moved the phase on, so a phase test never advanced past
            # segment 0 and the bar stalled halfway.
            _PROGRESS["loops"] += 1
            _PROGRESS["segment"] = min(_PROGRESS["loops"] - 1, _PROGRESS["segments"] - 1)
            _PROGRESS.update(phase="denoising", step=0, steps=len(steps))
        for i, value in enumerate(steps):
            with _PROGRESS_LOCK:
                _PROGRESS["step"] = i
            yield value
        with _PROGRESS_LOCK:
            _PROGRESS["step"] = len(steps)
            _PROGRESS["phase"] = "postprocess"
    return bar


# ---------------------------------------------------------------------------
# Endpoints
# ---------------------------------------------------------------------------
@app.get("/progress")
def progress() -> dict:
    """How far the generation in flight has got. Cheap and lock-free with respect to _MODEL_LOCK, so it
    answers while /generate is still working."""
    with _PROGRESS_LOCK:
        p = dict(_PROGRESS)
    steps, segments = p["steps"], max(1, p["segments"])
    done = (p["segment"] + (p["step"] / steps if steps else 0.0)) / segments
    p["fraction"] = round(min(1.0, max(0.0, done)), 4) if p["running"] else 0.0
    p["elapsed"] = round(time.time() - p["startedAt"], 1) if p["running"] else 0.0
    return p


@app.get("/health")
def health() -> dict:
    return {
        "status": "ok",
        "device": _DEVICE,
        "cudaAvailable": torch.cuda.is_available(),
        "loadedModels": sorted(_MODELS.keys()),
        "defaultModel": DEFAULT_MODEL,
    }


@app.get("/models")
def models() -> dict:
    return {"models": [_model_summary(info) for info in MODEL_INFOS if info.family == "Kimodo"]}


@app.get("/skeleton")
def skeleton(model: str = DEFAULT_MODEL) -> dict:
    """Return the model's rest skeleton (bones only, no motion) so the Unity client can author
    constraints and draw gizmos BEFORE the first /generate. Loads/caches the model if needed."""
    with _MODEL_LOCK:
        try:
            m, resolved = _get_or_load_model(model)
        except Exception as exc:  # noqa: BLE001
            raise HTTPException(status_code=400, detail=f"Failed to load model '{model}': {exc}") from exc
        skel = _export_skeleton(m)
        bones = _skeleton_bones(skel)
    info = get_model_info(resolved)
    return {
        "model": resolved,
        "displayName": info.display_name if info else resolved,
        "skeletonName": skel.name,
        "coordSystem": "kimodo_rh_yup_zfwd_meters",
        "quatOrder": "wxyz",
        "fps": float(m.fps),
        "frameCount": 0,
        "jointCount": int(skel.nbjoints),
        "rootIndex": int(skel.root_idx),
        "bones": bones,
    }


@app.post("/load_model")
def load(req: LoadModelRequest) -> dict:
    with _MODEL_LOCK:
        try:
            _, resolved = _get_or_load_model(req.model)
        except Exception as exc:  # noqa: BLE001 - surface load errors to the client
            raise HTTPException(status_code=400, detail=f"Failed to load model '{req.model}': {exc}") from exc
    info = get_model_info(resolved)
    return {"loaded": resolved, "displayName": info.display_name if info else resolved}


@contextlib.contextmanager
def _generate_hooks(model, weights: Optional[list[float]], progress_bar):
    """Shadow ``model._generate`` for the duration of one request.

    Kimodo's ``_multiprompt`` generates one segment at a time, calling ``model._generate(...)`` once
    per segment. Two things we need are only reachable there:

    * **per-segment guidance strength** — every segment is handed the *same* ``cfg_weight`` and there
      is no public knob, so the wrapper rewrites ``cfg_weight[1]`` (the constraint weight;
      ``cfg_weight[0]``, the text weight, is left alone) with the Nth entry of ``weights``;
    * **progress** — ``_multiprompt`` accepts a ``progress_bar`` and never passes it on, so the
      denoising loop always falls back to its own tqdm. The wrapper injects ours instead.

    Both live in ONE wrapper on purpose: as two nested context managers they would each try to
    restore ``_generate`` on exit, and the inner one's restore would delete the outer one's.

    Deliberately defensive: if Kimodo ever changes how many times ``_generate`` is called, calls past
    the end of ``weights`` keep the caller's own weight and the mismatch is logged, so the worst case
    is "the global weight was used" rather than a crash or silently shifted weights.
    """
    original = model._generate
    had_own = "_generate" in model.__dict__
    state = {"calls": 0}

    def wrapped(*args, **kwargs):
        i = state["calls"]
        state["calls"] += 1
        kwargs = dict(kwargs)
        if weights:
            cw = kwargs.get("cfg_weight")
            if i < len(weights) and isinstance(cw, (list, tuple)) and len(cw) == 2:
                kwargs["cfg_weight"] = [cw[0], float(weights[i])]
        if progress_bar is not None and "progress_bar" not in kwargs:
            kwargs["progress_bar"] = progress_bar
        return original(*args, **kwargs)

    model._generate = wrapped
    try:
        yield state
    finally:
        if had_own:
            model._generate = original
        else:
            model.__dict__.pop("_generate", None)
        if weights and state["calls"] != len(weights):
            print(
                f"[kimodo-bridge] per-segment constraint weights: expected {len(weights)} segment "
                f"generations but saw {state['calls']}; some segments used the global weight."
            )


def _texts_and_frames(prompt: str, duration: str, fps: float) -> tuple[list[str], list[int]]:
    """Replicate generate.py: split prompt on periods, map durations to frame counts."""
    texts = [t.strip() for t in prompt.split(".")]
    texts = [t + "." for t in texts if t]
    if not texts:
        raise HTTPException(status_code=400, detail="Prompt is empty.")
    if " " not in duration.strip():
        num_frames = [int(float(duration) * fps)] * len(texts)
    else:
        parts = duration.split()
        if len(parts) != len(texts):
            raise HTTPException(
                status_code=400,
                detail=f"Got {len(parts)} durations for {len(texts)} prompt segments; counts must match.",
            )
        num_frames = [int(float(p) * fps) for p in parts]
    return texts, num_frames


@app.post("/generate")
def generate(req: GenerateRequest) -> dict:
    t_start = time.time()
    with _MODEL_LOCK:
        try:
            model, resolved = _get_or_load_model(req.model)
        except Exception as exc:  # noqa: BLE001
            raise HTTPException(status_code=400, detail=f"Failed to load model '{req.model}': {exc}") from exc

        fps = float(model.fps)
        texts, num_frames = _texts_and_frames(req.prompt, req.duration, fps)

        # seed >= 0 sets a reproducible seed; None or negative means "random".
        if req.seed is not None and req.seed >= 0:
            from kimodo.tools import seed_everything

            seed_everything(req.seed)

        # G1 postprocessing is disabled upstream; mirror that.
        use_postprocess = False if "g1" in resolved else req.postprocess
        cfg_weight = req.cfg_weight if req.cfg_weight else [2.0, 2.0]

        # Export skeleton (SOMA emits somaskel77); its joint count is what the client's
        # constraint poses are laid out against. Compute it up front so we can build the
        # constraint list before generating.
        skel = _export_skeleton(model)
        root_idx = int(skel.root_idx)

        constraint_lst = []
        if req.constraints:
            con_dicts = _build_constraint_dicts(req.constraints, int(skel.nbjoints), sum(num_frames))
            if con_dicts:
                try:
                    # Build on model.skeleton (e.g. SOMA30); from_dict auto-converts 77->30.
                    # NOTE: do NOT pass device= here. from_dict already puts the constraint DATA
                    # tensors on skeleton.device (cuda), while frame_indices / pos_indices /
                    # rot_indices stay on CPU. Kimodo's multi_prompt path calls crop_move, which
                    # recreates pos_indices/rot_indices on CPU (torch.tensor([...])) regardless; if
                    # we'd moved frame_indices to cuda the two would mismatch in create_pairs
                    # ("Expected all tensors to be on the same device"). Keeping indices on CPU
                    # matches how the demo (compute_model_constraints_lst) builds them.
                    # Effector pins are position-only (don't lock the root); root2d/fullbody default.
                    constraint_lst = _load_constraints(con_dicts, model.skeleton)
                    # Apply user-authored target offsets (drag a hand/foot to a new position).
                    _apply_target_offsets(constraint_lst, con_dicts)
                except Exception as exc:  # noqa: BLE001 - surface constraint errors to the client
                    raise HTTPException(status_code=400, detail=f"Failed to build constraints: {exc}") from exc

        # Optional per-segment constraint weight (one value per prompt segment).
        seg_weights = req.segment_cfg_weights
        if seg_weights:
            if len(seg_weights) != len(texts):
                raise HTTPException(
                    status_code=400,
                    detail=(
                        f"Got {len(seg_weights)} segment constraint weights for {len(texts)} prompt "
                        "segments; counts must match."
                    ),
                )
            seg_weights = [float(w) for w in seg_weights]

        _progress_begin(len(texts))
        bar = _progress_bar()
        try:
            with torch.no_grad():
                with _generate_hooks(model, seg_weights, bar):
                    output = model(
                        texts,
                        num_frames,
                        num_denoising_steps=req.diffusion_steps,
                        constraint_lst=constraint_lst,
                        num_samples=req.num_samples,
                        multi_prompt=True,
                        num_transition_frames=req.num_transition_frames,
                        cfg_weight=cfg_weight,
                        post_processing=use_postprocess,
                        return_numpy=True,
                        first_heading_angle=(
                            float(req.first_heading_angle) if req.has_first_heading else None
                        ),
                        progress_bar=bar,   # only reached on the single-prompt path
                    )
        except Exception as exc:  # noqa: BLE001 - surface the real error to the client + logs
            _progress_end()
            traceback.print_exc()
            kinds = ", ".join(sorted({str(c.get("type")) for c in (req.constraints or [])})) or "none"
            raise HTTPException(
                status_code=500,
                detail=f"model() failed — {_describe_exception(exc)}. Request: {len(texts)} segment(s), "
                f"{sum(num_frames)} frames, {req.num_samples} sample(s), {len(constraint_lst)} "
                f"constraint(s) [{kinds}], postprocess={use_postprocess}. Full traceback in the "
                f"server console.",
            ) from exc

        _progress_end()
        try:
            return _build_response(req, resolved, skel, root_idx, output, num_frames, texts, t_start, fps)
        except HTTPException:
            raise
        except Exception as exc:  # noqa: BLE001
            traceback.print_exc()
            raise HTTPException(
                status_code=500,
                detail=f"Building the response failed — {_describe_exception(exc)}.",
            ) from exc


def _build_response(req, resolved, skel, root_idx, output, num_frames, texts, t_start, fps) -> dict:
    """Turn the model output into the JSON the client expects (kept separate so a failure here is
    reported as such rather than as a generation failure)."""
    with torch.no_grad():
        bones = _skeleton_bones(skel)

        posed = output["posed_joints"]        # [B, T, J, 3]
        global_rot = output["global_rot_mats"]  # [B, T, J, 3, 3]
        foot = output.get("foot_contacts")     # [B, T, C] or None
        n_samples = int(posed.shape[0])
        T = int(posed.shape[1])
        J = int(posed.shape[2])

        clips = []
        for i in range(n_samples):
            joints_pos = torch.from_numpy(posed[i]).to(_DEVICE)          # [T, J, 3]
            g_rot = torch.from_numpy(global_rot[i]).to(_DEVICE)          # [T, J, 3, 3]
            local_rot = global_rots_to_local_rots(g_rot, skel)          # [T, J, 3, 3]
            quats = matrix_to_quaternion(local_rot)                      # [T, J, 4] wxyz
            root_pos = joints_pos[:, root_idx, :]                        # [T, 3]

            clip = {
                "rootPositions": root_pos.reshape(-1).cpu().numpy().round(6).tolist(),  # T*3
                "localQuats": quats.reshape(-1).cpu().numpy().round(6).tolist(),        # T*J*4
            }
            if foot is not None:
                # foot_contacts may be a bool array; cast to float before rounding.
                clip["footContacts"] = np.asarray(foot[i], dtype=np.float32).reshape(-1).round(4).tolist()
            if req.include_positions:
                clip["posedJoints"] = joints_pos.reshape(-1).cpu().numpy().round(6).tolist()  # T*J*3
            clips.append(clip)

    info = get_model_info(resolved)
    foot_channels = int(np.asarray(foot).shape[-1]) if foot is not None else 0
    return {
        "model": resolved,
        "displayName": info.display_name if info else resolved,
        "skeletonName": skel.name,
        "coordSystem": "kimodo_rh_yup_zfwd_meters",
        "quatOrder": "wxyz",
        "fps": fps,
        "frameCount": T,
        "jointCount": J,
        "rootIndex": root_idx,
        "footContactChannels": foot_channels,
        "bones": bones,
        "clips": clips,
        "prompts": texts,
        "numFramesPerSegment": num_frames,
        "generationSeconds": round(time.time() - t_start, 3),
    }
