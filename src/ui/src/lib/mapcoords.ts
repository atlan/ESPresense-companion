import type { ZoomTransform } from 'd3-zoom';

/**
 * Convert a mouse/pointer event position to map coordinates, accounting for the SVG's screen CTM,
 * the LayerCake padding and the current d3-zoom transform. Same math the MapCoordinates readout
 * uses - factored out for the floorplan editor's drag/placement interactions.
 */
export function screenToMap(
	svg: SVGSVGElement,
	clientX: number,
	clientY: number,
	padding: { left?: number; top?: number } | undefined,
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
	const ax = p.x - (padding?.left ?? 0);
	const ay = p.y - (padding?.top ?? 0);
	return {
		x: xScale.invert((ax - transform.x) / transform.k),
		y: yScale.invert((ay - transform.y) / transform.k)
	};
}
