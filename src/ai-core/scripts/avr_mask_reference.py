"""Offline raster-mask reference; not independent clinical calipers or expert-reported AVR."""
import numpy as np
from scipy.ndimage import map_coordinates

from app.services.avr_service import compute_knudtson_crae, compute_knudtson_crve


def mask_cross_section(labels, section, vessel_class):
    normal = np.asarray(section['normal'], dtype=float)
    if normal.shape != (2,) or not np.isfinite(normal).all() or abs(np.linalg.norm(normal) - 1) > 1e-3:
        raise ValueError('Expected a finite unit cross-section normal.')
    y, x = section['center_y'], section['center_x']
    height, width = labels.shape
    if not 0 <= y <= height - 1 or not 0 <= x <= width - 1:
        return None, 'IMAGE_BOUNDARY'
    center_class = int(map_coordinates(labels, [[y], [x]], order=0)[0])
    if center_class == 255:
        return None, 'AMBIGUOUS_REFERENCE'
    if center_class != vessel_class:
        return None, 'REFERENCE_CLASS_OR_LOCATION_MISMATCH'
    reach = 40
    while reach <= 2 * max(40, height, width):
        positions = np.arange(-reach, reach + 0.125, 0.25)
        ys, xs = y + positions * normal[0], x + positions * normal[1]
        valid = (ys >= 0) & (ys <= height - 1) & (xs >= 0) & (xs <= width - 1)
        left_boundary, right_boundary = not valid[0], not valid[-1]
        positions, ys, xs = positions[valid], ys[valid], xs[valid]
        y0, x0 = int(np.floor(ys.min())), int(np.floor(xs.min()))
        y1, x1 = int(np.ceil(ys.max())) + 1, int(np.ceil(xs.max())) + 1
        # Interpolate a local class patch, not a full-image float copy per section.
        support = (labels[y0:y1, x0:x1] == vessel_class).astype(np.float32)
        profile = map_coordinates(support, [ys - y0, xs - x0], order=1)
        inside = profile >= 0.5
        middle = int(np.searchsorted(positions, 0))
        if not inside[middle]:
            return None, 'REFERENCE_CLASS_OR_LOCATION_MISMATCH'
        left = np.flatnonzero(~inside[:middle])
        right = np.flatnonzero(~inside[middle + 1:]) + middle + 1
        if len(left) and len(right):
            li, ri = left[-1], right[0]
            edges = np.array([li, li + 1, ri - 1, ri])
            unknown = (labels[y0:y1, x0:x1] == 255).astype(np.float32)
            if np.any(map_coordinates(unknown, [ys[edges] - y0, xs[edges] - x0], order=1) > 0):
                return None, 'AMBIGUOUS_REFERENCE'
            xleft = positions[li] + 0.25 * (0.5 - profile[li]) / (profile[li + 1] - profile[li])
            xright = positions[ri - 1] + 0.25 * (0.5 - profile[ri - 1]) / (profile[ri] - profile[ri - 1])
            diameter = float(xright - xleft)
            if not np.isfinite(diameter) or diameter <= 0:
                return None, 'NO_TWO_REFERENCE_EDGES'
            return diameter, 'MATCHED'
        if (not len(left) and left_boundary) or (not len(right) and right_boundary):
            return None, 'IMAGE_BOUNDARY'
        reach *= 2
    return None, 'NO_TWO_REFERENCE_EDGES'


def compare_mask_reference(labels, measurement):
    from collections import Counter
    reasons, branches, absolute_errors = Counter(), [], []
    equivalent_sets = {1: [], 2: []}
    per_class = {}
    for vessel_class, name in ((1, 'arteries'), (2, 'veins')):
        class_reasons = Counter()
        for branch in measurement.get(name, []):
            predicted, reference = [], []
            for section in branch.get('cross_sections', []):
                diameter, reason = mask_cross_section(labels, section, vessel_class)
                reasons[reason] += 1
                class_reasons[reason] += 1
                if diameter is not None:
                    predicted.append(section['diameter_px'])
                    reference.append(diameter)
                    absolute_errors.append(abs(section['diameter_px'] - diameter))
            if len(reference) >= 3:
                p, r = float(np.median(predicted)), float(np.median(reference))
                equivalent_sets[vessel_class].append((p, r))
                branches.append(dict(vessel_class=vessel_class, branch_id=branch['branch_id'],
                                     matched_sections=len(reference), predicted_diameter_px=p, reference_mask_diameter_px=r))
        pairs = equivalent_sets[vessel_class]
        signed = [p - r for p, r in pairs]
        per_class[name] = dict(selected_branches=len(measurement.get(name, [])), matched_branches=len(pairs),
                               unmatched_branches=len(measurement.get(name, [])) - len(pairs),
                               selected_sections=sum(class_reasons.values()), matched_sections=class_reasons['MATCHED'],
                               matching_counts=dict(class_reasons),
                               mean_signed_branch_error_px=float(np.mean(signed)) if pairs else None,
                               mean_absolute_branch_error_px=float(np.mean(np.abs(signed))) if pairs else None,
                               mean_relative_branch_error=float(np.mean([(p - r) / r for p, r in pairs])) if pairs else None)
    report = dict(reference_type='ANNOTATION_RASTER_AT_MATCHED_PREDICTED_CROSS_SECTIONS',
                  evaluation_protocol='RASTER_BILINEAR_ADAPTIVE_V2',
                  selection_scope='PREDICTED_SELECTED_VESSELS_NOT_EXPERT_TOP_SIX',
                  independent_expert_calipers=False, optic_disc_reference='SAME_AUTOMATIC_ROI_NOT_EXPERT_LANDMARK',
                  matching_counts=dict(reasons), matched_branches=branches,
                  matched_cross_section_diameter_mae_px=float(np.mean(absolute_errors)) if absolute_errors else None,
                  conditional_avr_absolute_error=None, conditional_reference_avr=None,
                  per_class=per_class, conditional_equivalents=None)
    if len(equivalent_sets[1]) == 6 and len(equivalent_sets[2]) == 6:
        pa = compute_knudtson_crae([p for p, _ in equivalent_sets[1]])
        pv = compute_knudtson_crve([p for p, _ in equivalent_sets[2]])
        ra = compute_knudtson_crae([r for _, r in equivalent_sets[1]])
        rv = compute_knudtson_crve([r for _, r in equivalent_sets[2]])
        pred, ref = pa / pv, ra / rv
        report.update(conditional_avr_absolute_error=abs(pred - ref), conditional_reference_avr=ref,
                      conditional_predicted_avr=pred,
                      conditional_equivalents=dict(predicted_crae_px=pa, predicted_crve_px=pv,
                                                   reference_mask_crae_px=ra, reference_mask_crve_px=rv,
                                                   arterial_effect=(pa - ra) / rv,
                                                   venous_effect=ra * (1 / pv - 1 / rv),
                                                   interaction_effect=(pa - ra) * (1 / pv - 1 / rv),
                                                   total_avr_difference=pred - ref))
    return report


def summarize_mask_bias(comparisons):
    """Equal weight per image; missing matches stay in the coverage denominator."""
    per_class = {}
    for name in ('arteries', 'veins'):
        rows = [r['per_class'][name] for r in comparisons]
        matched = [r for r in rows if r['matched_branches']]
        per_class[name] = dict(total_cases=len(rows), cases_with_matches=len(matched),
            selected_branches=sum(r['selected_branches'] for r in rows),
            matched_branches=sum(r['matched_branches'] for r in rows),
            selected_sections=sum(r['selected_sections'] for r in rows),
            matched_sections=sum(r['matched_sections'] for r in rows))
        for field in ('mean_signed_branch_error_px', 'mean_absolute_branch_error_px', 'mean_relative_branch_error'):
            per_class[name]['case_macro_' + field] = float(np.mean([r[field] for r in matched])) if matched else None
    complete = [r['conditional_equivalents'] for r in comparisons if r['conditional_equivalents'] is not None]
    effects = {field: float(np.mean([r[field] for r in complete])) if complete else None
               for field in ('arterial_effect', 'venous_effect', 'interaction_effect', 'total_avr_difference')}
    return dict(evaluation_protocol='RASTER_BILINEAR_ADAPTIVE_V2', independent_expert_calipers=False,
                selection_scope='PREDICTED_SELECTED_VESSELS_NOT_EXPERT_TOP_SIX', per_class=per_class,
                conditional_equivalents=dict(total_cases=len(comparisons), cases_with_six_matched_each=len(complete),
                                            case_macro_effects=effects))
