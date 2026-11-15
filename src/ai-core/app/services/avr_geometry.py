"""Photo-derived optic-disc proposal, root-branch tracing and native-pixel edge calibers."""
from __future__ import annotations

import numpy as np
from PIL import Image
from scipy.ndimage import binary_dilation, find_objects, gaussian_filter1d, label, map_coordinates
from skimage.morphology import skeletonize

from .vision_runtime import cv2


def estimate_optic_disc(rgb, min_contrast=0.08):
    if rgb.ndim != 3 or rgb.shape[2] != 3 or rgb.dtype != np.uint8:
        raise ValueError("Expected uint8 RGB photograph.")
    height, width = rgb.shape[:2]
    scale = min(1.0, 752 / max(height, width))
    small = np.asarray(Image.fromarray(rgb).resize((max(1, round(width * scale)), max(1, round(height * scale)))))
    h, w = small.shape[:2]
    intensity = (0.65 * small[:, :, 0] + 0.35 * small[:, :, 1]).astype(np.float32) / 255
    smooth = cv2.GaussianBlur(intensity, (0, 0), 3)
    raw_fov = (small.max(axis=2) > 20).astype(np.float32)
    sigma = max(12, min(h, w) * 0.06)
    # Normalize by FOV support so the black border cannot manufacture a bright candidate.
    broad = cv2.GaussianBlur(intensity * raw_fov, (0, 0), sigma) / np.maximum(cv2.GaussianBlur(raw_fov, (0, 0), sigma), 1e-5)
    fov = cv2.erode(raw_fov.astype(np.uint8), np.ones((9, 9), np.uint8)) > 0
    score = np.where(fov, smooth - broad, -1).copy()
    candidates = []
    yy, xx = np.ogrid[:h, :w]
    for _ in range(5):
        y, x = np.unravel_index(np.argmax(score), score.shape)
        if score[y, x] <= 0:
            break
        local = (xx - x) ** 2 + (yy - y) ** 2
        annulus = (local > (0.08 * min(h, w)) ** 2) & (local < (0.15 * min(h, w)) ** 2) & fov
        background = float(np.median(smooth[annulus])) if annulus.any() else float(smooth[y, x])
        contrast = float(smooth[y, x] - background)
        score[local < (0.15 * min(h, w)) ** 2] = -1
        if contrast < min_contrast:
            continue
        proposals = []
        for fraction in (0.15, 0.25, 0.35, 0.45, 0.55, 0.65):
            bright = ((smooth > background + fraction * contrast) & (local < (0.15 * min(h, w)) ** 2)).astype(np.uint8)
            bright = cv2.morphologyEx(bright, cv2.MORPH_CLOSE, np.ones((9, 9), np.uint8))
            _, components = cv2.connectedComponents(bright)
            region_id = components[y, x]
            if region_id == 0:
                continue
            region = (components == region_id).astype(np.uint8)
            contours, _ = cv2.findContours(region, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
            contour = max(contours, key=cv2.contourArea)
            area = cv2.contourArea(contour)
            perimeter = cv2.arcLength(contour, True)
            radius = np.sqrt(area / np.pi)
            if not 0.025 * min(h, w) <= radius <= 0.12 * min(h, w) or len(contour) < 5:
                continue
            (cx, cy), axes, _ = cv2.fitEllipse(contour)
            circularity = 4 * np.pi * area / max(perimeter * perimeter, 1)
            if min(axes) / max(axes) < 0.6 or circularity < 0.7:
                continue
            proposals.append((cx, cy, radius, circularity))
        if len(proposals) < 2:
            continue
        stable_pairs = [(left, right) for left, right in zip(proposals, proposals[1:])
                        if abs(left[2] - right[2]) / max(left[2], right[2]) <= 0.3
                        and np.linalg.norm(np.array(left[:2]) - right[:2]) <= 0.3 * min(left[2], right[2])]
        if not stable_pairs:
            continue
        # Keep the outer stable compact boundary rather than the brightest optic cup.
        cx, cy, radius, circularity = max((p for pair in stable_pairs for p in pair), key=lambda p: p[2])
        candidates.append({"center_x": cx / scale, "center_y": cy / scale, "radius_px": radius / scale,
                           "contrast": contrast, "shape_score": circularity,
                           "method": "MULTITHRESHOLD_BRIGHT_REGION_ELLIPSE", "expert_landmark_validated": False})
    if not candidates:
        return None
    return max(candidates, key=lambda p: p["contrast"] * p["shape_score"])


def native_edge_diameter(green, center_y, center_x, normal, estimated_radius):
    # A fixed 40-pixel cap misses both edges of wide vessels in native high-resolution photos.
    reach = max(5.0, 2.5 * estimated_radius)
    positions = np.arange(-reach, reach + 0.125, 0.25, dtype=np.float32)
    # Suppress pixel noise along the vessel without averaging across a wide curved segment.
    offsets = np.arange(-2, 3, dtype=np.float32)[:, None]
    ys = center_y + positions[None, :] * normal[0] + offsets * normal[1]
    xs = center_x + positions[None, :] * normal[1] - offsets * normal[0]
    if ys.min() < 1 or xs.min() < 1 or ys.max() >= green.shape[0] - 1 or xs.max() >= green.shape[1] - 1:
        return None
    samples = map_coordinates(green, [ys, xs], order=1, mode="nearest")
    profile = np.array([1, 4, 6, 4, 1], dtype=np.float32) @ samples / 16
    profile = gaussian_filter1d(profile, 0.75 / 0.25)
    derivative = np.gradient(profile, 0.25)
    # Exclude the central light reflex, which is not an outer vessel wall.
    center_margin = max(0.5, 0.5 * estimated_radius)
    left = np.flatnonzero((positions < -center_margin) & (positions > -reach + 0.5))
    right = np.flatnonzero((positions > center_margin) & (positions < reach - 0.5))
    li, ri = left[np.argmin(derivative[left])], right[np.argmax(derivative[right])]
    width = float(positions[ri] - positions[li])
    background = float((profile[:4].mean() + profile[-4:].mean()) / 2)
    center = float(np.min(profile[(positions >= positions[li]) & (positions <= positions[ri])]))
    if derivative[li] >= -0.003 or derivative[ri] <= 0.003 or background - center < 0.015:
        return None
    if not max(1.5, estimated_radius * 0.7) <= width <= estimated_radius * 3.5:
        return None
    return width


def trace_root_calibers(rgb, vessel_mask, artery_probability, optic_disc):
    """Distinct vessels in Zone B, including branches whose parent is outside the annulus."""
    from scipy.ndimage import distance_transform_edt, center_of_mass

    # Normalize PIL boolean masks: noncanonical true bytes can crash native skeletonization.
    vessel_mask = vessel_mask != 0
    height, width = vessel_mask.shape
    cx, cy, radius = optic_disc["center_x"], optic_disc["center_y"], optic_disc["radius_px"]
    yy, xx = np.ogrid[:height, :width]
    radial = np.hypot(xx - cx, yy - cy)
    zone = (radial >= 2 * radius) & (radial <= 3 * radius)
    skeleton = skeletonize(vessel_mask)
    # Cyclic transitions count arms; raw neighbor counts misclassify diagonal staircases.
    padded = np.pad(skeleton, 1)
    ring = [padded[1 + dy:1 + dy + height, 1 + dx:1 + dx + width]
            for dy, dx in ((-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1))]
    arms = sum((~ring[i] & ring[(i + 1) % 8]).astype(np.uint8) for i in range(8))
    junction_points = skeleton & (arms >= 3)
    junctions = binary_dilation(junction_points, iterations=2)
    branches, count = label(skeleton & zone & ~junctions, structure=np.ones((3, 3), np.uint8))
    class_components = {kind: label(vessel_mask & zone & support, structure=np.ones((3, 3), np.uint8))[0]
                        for kind, support in ((1, artery_probability >= 0.5), (2, artery_probability < 0.5))}
    distance = distance_transform_edt(vessel_mask)
    green = rgb[:, :, 1].astype(np.float32) / 255
    junction_y, junction_x = np.nonzero(junction_points)
    junction_locations = np.column_stack((junction_y, junction_x)).astype(np.float32)
    junction_clearance_squared = np.maximum(2.0, distance[junction_y, junction_x]) ** 2
    candidates, rejected = [], []
    for index, bounds in enumerate(find_objects(branches), start=1):
        if bounds is None:
            continue
        ys, xs = np.nonzero(branches[bounds] == index)
        ys, xs = ys + bounds[0].start, xs + bounds[1].start
        if len(ys) < 8:
            continue
        probability = float(np.mean(artery_probability[ys, xs]))
        if 0.4 < probability < 0.6:
            rejected.append({"branch": index, "reason": "AMBIGUOUS_AV_CLASS"})
            continue
        points = np.column_stack((ys, xs)).astype(np.float32)
        # Short valid branches still need at least three distinct cross-sections.
        chosen = np.linspace(1, len(points) - 2, min(32, len(points) - 2)).astype(int)
        widths, cross_sections, excluded_junction_samples = [], [], 0
        for selected in chosen:
            center = points[selected]
            # Splitting a graph at two pixels does not exclude the physical width of a crossing.
            if len(junction_locations) and np.any(np.sum((junction_locations - center) ** 2, axis=1) <= junction_clearance_squared):
                excluded_junction_samples += 1
                continue
            delta = points - center
            tangent_radius = max(8.0, 2 * distance[int(center[0]), int(center[1])])
            neighbors = delta[np.sum(delta * delta, axis=1) <= tangent_radius ** 2]
            if len(neighbors) < 5:
                continue
            covariance = np.cov(neighbors.T)
            _, eigenvectors = np.linalg.eigh(covariance)
            tangent = eigenvectors[:, -1]
            normal = np.array([-tangent[1], tangent[0]])
            measured = native_edge_diameter(green, center[0], center[1], normal, max(1, distance[int(center[0]), int(center[1])]))
            if measured is not None:
                widths.append(measured)
                cross_sections.append({"center_y": float(center[0]), "center_x": float(center[1]),
                                       "normal": normal.tolist(), "diameter_px": measured})
        if len(widths) < 3 or np.std(widths) / np.mean(widths) > 0.25:
            rejected.append({"branch": index, "reason": "UNSTABLE_NATIVE_EDGE_WIDTH", "valid_samples": len(widths),
                             "width_cv": float(np.std(widths) / np.mean(widths)) if widths else None,
                             "diameters_px": widths, "cross_sections": cross_sections,
                             "excluded_junction_samples": excluded_junction_samples})
            continue
        record = {"branch_id": index, "diameter_px": float(np.median(widths)), "samples": len(widths),
                  "artery_probability_uncalibrated": probability, "sample_center": points[len(points) // 2].tolist(),
                  "cross_sections": cross_sections, "excluded_junction_samples": excluded_junction_samples}
        kind = 1 if probability >= 0.6 else 2
        component_ids = class_components[kind][ys, xs]
        component_ids = component_ids[component_ids > 0]
        if not len(component_ids):
            continue
        record.update(_points=points, _kind=kind, _component=(kind, int(np.bincount(component_ids).argmax())))
        candidates.append(record)
    junction_labels, junction_count = label(junctions, structure=np.ones((3, 3), np.uint8))
    centers = center_of_mass(junction_points, junction_labels, range(1, junction_count + 1)) if junction_count else []
    centers = [center for center in centers if 2 * radius <= np.hypot(center[1] - cx, center[0] - cy) <= 3 * radius]
    selected = distinct_zone_vessels(candidates, centers)
    arteries = [record for record in selected if record['artery_probability_uncalibrated'] >= 0.6]
    veins = [record for record in selected if record['artery_probability_uncalibrated'] <= 0.4]
    return {"arteries": sorted(arteries, key=lambda r: r["diameter_px"], reverse=True)[:6],
            "veins": sorted(veins, key=lambda r: r["diameter_px"], reverse=True)[:6], "rejected": rejected,
            "method": "DISTINCT_ZONE_B_VESSEL_NATIVE_GREEN_EDGE_DIAMETER_PX", "zone_inner_radii": 2, "zone_outer_radii": 3}


def distinct_zone_vessels(candidates, junction_centers):
    """Count a connected same-class tree once; reconnect opposite arms of A/V crossings."""
    parents = {record['_component']: record['_component'] for record in candidates}

    def root(key):
        while parents[key] != key:
            parents[key] = parents[parents[key]]
            key = parents[key]
        return key

    for center in junction_centers:
        near = []
        for record in candidates:
            delta = record['_points'] - center
            index = np.argmin(np.sum(delta * delta, axis=1))
            vector = delta[index]
            distance = np.linalg.norm(vector)
            if 0 < distance <= 6:
                near.append((record, vector / distance))
        for i, (left, direction) in enumerate(near):
            for right, other_direction in near[i + 1:]:
                if left['_kind'] == right['_kind'] and np.dot(direction, other_direction) <= -0.8:
                    parents[root(right['_component'])] = root(left['_component'])
    groups = {}
    for record in candidates:
        groups.setdefault(root(record['_component']), []).append(record)
    selected = []
    for records in groups.values():
        record = max(records, key=lambda r: r['diameter_px']).copy()
        record['source_branch_ids'] = sorted(item['branch_id'] for item in records)
        for key in ('_points', '_kind', '_component'):
            del record[key]
        selected.append(record)
    return selected
