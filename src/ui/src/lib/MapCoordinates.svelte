<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import { getToastStore } from '$lib/toast/toastStore';
	import { nodes } from '$lib/stores';
	import type { LayerCakeContext } from '$lib/types';
	const toastStore = getToastStore();
	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	$: cursorX = 0;
	$: cursorY = 0;
	let snappedNode: string | null = null;

	// Cursor within this many screen pixels of a node marker snaps the readout to the node's
	// exact configured coordinates - the raw cursor position inside a marker circle is otherwise
	// off by up to the marker radius (~0.2m at typical zoom), which reads like a config mismatch.
	const SNAP_PX = 14;

	let copiedCoords: string[] = [];
	let hasFocus = false;

	const { xScale, yScale, width, height, padding } = getContext<LayerCakeContext>('LayerCake');

	function updateCoordinates(event: MouseEvent) {
		if (!$xScale || !$yScale) return;

		const target = event.target as SVGElement;
		const svg = target.ownerSVGElement || (target as SVGSVGElement);

		if (!(svg instanceof SVGSVGElement)) return;

		try {
			const point = svg.createSVGPoint();
			point.x = event.clientX;
			point.y = event.clientY;

			const screenCTM = svg.getScreenCTM();
			if (!screenCTM) return;

			// Convert screen coordinates to SVG coordinates
			const svgPoint = point.matrixTransform(screenCTM.inverse());

			// Adjust for padding from context
			const adjustedX = svgPoint.x - ($padding?.left ?? 0);
			const adjustedY = svgPoint.y - ($padding?.top ?? 0);

			const transformedX = (adjustedX - transform.x) / transform.k;
			const transformedY = (adjustedY - transform.y) / transform.k;

			cursorX = $xScale.invert(transformedX);
			cursorY = $yScale.invert(transformedY);
			snappedNode = null;

			for (const n of $nodes ?? []) {
				if (!n.location) continue;
				if (floorId && n.floors && !n.floors.includes(floorId)) continue;
				const nx = $xScale(n.location.x) * transform.k + transform.x;
				const ny = $yScale(n.location.y) * transform.k + transform.y;
				if (Math.hypot(adjustedX - nx, adjustedY - ny) <= SNAP_PX) {
					cursorX = n.location.x;
					cursorY = n.location.y;
					snappedNode = n.name ?? n.id;
					break;
				}
			}
		} catch (error) {
			console.error('Error updating coordinates:', error);
		}
	}

	function handleKeydown(event: KeyboardEvent) {
		// Check for Ctrl/Cmd + C
		if ((event.ctrlKey || event.metaKey) && event.key === 'c') {
			event.preventDefault();
			hasFocus = true;
			const roundedX = Math.round(cursorX * 10) / 10;
			const roundedY = Math.round(cursorY * 10) / 10;
			const coords = `          - [${roundedX},${roundedY}]`;
			copiedCoords = [...copiedCoords, coords];
			navigator.clipboard
				.writeText(copiedCoords.join('\n'))
				.then(() => {
					toastStore.trigger({
						message: `Copied ${copiedCoords.length} coordinate${copiedCoords.length > 1 ? 's' : ''} to clipboard!`,
						background: 'preset-filled-success-500',
						timeout: 1000
					});
				})
				.catch((error) => {
					toastStore.trigger({
						message: `Failed to copy to clipboard: ${error}`,
						background: 'preset-filled-error-500'
					});
				});
		}
	}

	function handleFocusOut() {
		if (hasFocus) {
			hasFocus = false;
			copiedCoords = [];
			toastStore.trigger({
				message: 'Coordinate collection reset',
				background: 'preset-filled-primary-500',
				timeout: 1000
			});
		}
	}
</script>

<svelte:window onmousemove={updateCoordinates} onkeydown={handleKeydown} onblur={handleFocusOut} />

<g transform="translate({$width - (snappedNode ? 190 : 120)}, {$height - 40})">
	<rect width={snappedNode ? 180 : 110} height="30" rx="4" fill={snappedNode ? '#059669' : '#2563eb'} class="shadow-lg" />
	<text x={snappedNode ? 90 : 55} y="19" text-anchor="middle" fill="white" font-size="12">
		{#if snappedNode}
			{snappedNode}: {Math.round(cursorX * 10) / 10}, {Math.round(cursorY * 10) / 10}
		{:else}
			X: {Math.round(cursorX * 10) / 10}, Y: {Math.round(cursorY * 10) / 10}
		{/if}
	</text>
</g>
