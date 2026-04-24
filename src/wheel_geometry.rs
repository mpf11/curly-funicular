//! Pure math for the wheel layout — no Win32 dependencies, making it unit-testable.

pub const SLOT_COUNT: usize = 8;
pub const SLICE_SPAN_DEG: f64 = 360.0 / SLOT_COUNT as f64;

/// Angle in degrees for the leading edge of slot i (the boundary BEFORE it, clockwise from top).
pub fn slice_boundary_angle_deg(i: usize) -> f64 {
    -90.0 - SLICE_SPAN_DEG / 2.0 + i as f64 * SLICE_SPAN_DEG
}

/// Center angle (degrees) of slot i; slot 0 is at 12 o'clock.
pub fn slice_center_angle_deg(i: usize) -> f64 {
    -90.0 + i as f64 * SLICE_SPAN_DEG
}

/// Map a point (dx, dy relative to wheel centre) to a slot index.
/// Returns None if inside the hub (dist < inner_r).
/// If require_in_bounds is true, also returns None outside outer_r.
/// Otherwise the nearest slice is returned for any outward distance.
pub fn point_to_slot(
    dx: f64,
    dy: f64,
    inner_r: f64,
    outer_r: f64,
    require_in_bounds: bool,
) -> Option<usize> {
    let dist = (dx * dx + dy * dy).sqrt();
    if dist < inner_r {
        return None;
    }
    if require_in_bounds && dist > outer_r {
        return None;
    }

    let ang_deg = dy.atan2(dx).to_degrees();
    let from_top = (ang_deg + 90.0 + 360.0) % 360.0;
    let shifted = (from_top + SLICE_SPAN_DEG / 2.0) % 360.0;
    Some((shifted / SLICE_SPAN_DEG) as usize % SLOT_COUNT)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slot_0_at_top() {
        // Straight up from center should hit slot 0.
        let slot = point_to_slot(0.0, -200.0, 50.0, 300.0, true);
        assert_eq!(slot, Some(0));
    }

    #[test]
    fn slot_2_at_right() {
        // Straight right should be slot 2.
        let slot = point_to_slot(200.0, 0.0, 50.0, 300.0, true);
        assert_eq!(slot, Some(2));
    }

    #[test]
    fn hub_returns_none() {
        let slot = point_to_slot(5.0, 5.0, 50.0, 300.0, true);
        assert!(slot.is_none());
    }

    #[test]
    fn outside_outer_returns_none_when_required() {
        let slot = point_to_slot(0.0, -400.0, 50.0, 300.0, true);
        assert!(slot.is_none());
    }

    #[test]
    fn outside_outer_returns_slot_when_not_required() {
        let slot = point_to_slot(0.0, -400.0, 50.0, 300.0, false);
        assert_eq!(slot, Some(0));
    }
}
