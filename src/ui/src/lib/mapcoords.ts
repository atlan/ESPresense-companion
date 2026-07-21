import type { ZoomTransform } from 'd3-zoom';

/**
 * Convert a mouse/pointer event position to map coordinates via the SVG's screen CTM and the
 * current d3-zoom transform. NOTE: LayerCake applies its padding by CSS-positioning the <svg>
 * element itself (top/left = padding), so getScreenCTM() already accounts for it - subtracting
 * the padding AGAIN shifted every converted point ~16px towards the top-left (clicked markers
 * appeared offset, and the coordinate readout was slightly off for the same reason).
 */
export function screenToMap(
	svg: SVGSVGElement,
	clientX: number,
	clientY: number,
	transform: ZoomTransform,
	xScale: { invert: (v: number) => number },
	yScale: { invert: (v: number) => number }
): { x: number; y: number } | null {
	const point = svg.createSVGPoint();
	point.x = clientX;
	point.y = clientY;
	const ctm = svg.getScreenCTM();
	if (!ctm) return null;
	const p = point.matrixTransform(ctm.inverse());
	return {
		x: xScale.invert((p.x - transform.x) / transform.k),
		y: yScale.invert((p.y - transform.y) / transform.k)
	};
}
