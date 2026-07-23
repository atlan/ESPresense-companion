<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import { config, nodes } from '$lib/stores';
	import { screenToMap } from '$lib/mapcoords';
	import { editMode, selectedNodeId, nodeEdits, placingNode, pendingNode, pickingWalkPoint, selectedRoomId, roomEdits, draftRoom, traceImage, imageTool, scalePoints, boundsPoints } from '$lib/floorplanEdit';
	import { gotoWalkSetup } from '$lib/urls';
	import type { LayerCakeContext } from '$lib/types';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let svgEl: SVGElement | undefined = undefined;

	const { xScale, yScale } = getContext<LayerCakeContext>('LayerCake');

	$: floor = $config?.floors.find((f) => f.id === floorId);
	$: floorNodes = ($nodes ?? []).filter((n) => n.location && (!floorId || n.floors?.includes(floorId)));

	// Effective (edited or stored) geometry
	function nodePos(n: { id: string; location: { x: number; y: number; z: number } }) {
		const e = $nodeEdits[n.id];
		return e ?? n.location;
	}

	function roomPoints(roomId: string, stored: number[][]): number[][] {
		return $roomEdits[roomId] ?? stored;
	}

	// Handle sizes stay constant on screen regardless of zoom
	$: hr = 7 / transform.k;
	$: hrSmall = 4.5 / transform.k;

	function toMap(e: PointerEvent | MouseEvent): { x: number; y: number } | null {
		if (!svgEl) return null;
		return screenToMap(svgEl as SVGSVGElement, e.clientX, e.clientY, transform, $xScale, $yScale);
	}

	// d3-zoom starts its pan gesture from mousedown/touchstart on the SVG - stopping only
	// pointerdown is NOT enough (they are separate events that bubble independently), which made
	// dragging a handle pan the whole map instead of moving the handle.
	function blockZoom(e: Event) {
		e.stopPropagation();
	}

	function round(v: number): number {
		return Math.round(v * 100) / 100;
	}

	// ─── dragging (window-level) ─────────────────────────────────────────────
	// Drags are tracked via window pointermove/pointerup instead of per-circle handlers with
	// pointer capture - independent of event retargeting quirks and of the circle re-rendering
	// mid-drag when the edit stores update.
	let dragNodeId: string | null = null;
	let dragNodeZ = 0;
	let dragVertex: { roomId: string; index: number; stored: number[][] } | null = null;
	let dragImage: { grabDx: number; grabDy: number } | null = null;

	function imageDown(e: PointerEvent) {
		if (!$traceImage?.movable) return;
		e.stopPropagation();
		e.preventDefault();
		const m = toMap(e);
		if (!m) return;
		dragImage = { grabDx: m.x - $traceImage.x, grabDy: m.y - $traceImage.y };
	}

	function nodeDown(e: PointerEvent, n: { id: string; location: { z: number } }) {
		e.stopPropagation();
		e.preventDefault();
		dragNodeId = n.id;
		dragNodeZ = $nodeEdits[n.id]?.z ?? n.location.z;
		$selectedNodeId = n.id;
	}

	function vertexDown(e: PointerEvent, roomId: string, index: number, stored: number[][]) {
		e.stopPropagation();
		e.preventDefault();
		dragVertex = { roomId, index, stored };
	}

	function globalMove(e: PointerEvent) {
		if (dragNodeId) {
			const m = toMap(e);
			if (!m) return;
			$nodeEdits = { ...$nodeEdits, [dragNodeId]: { x: round(m.x), y: round(m.y), z: dragNodeZ } };
		} else if (dragVertex) {
			const m = toMap(e);
			if (!m) return;
			const pts = ($roomEdits[dragVertex.roomId] ?? dragVertex.stored).map((p) => [...p]);
			pts[dragVertex.index] = [round(m.x), round(m.y)];
			$roomEdits = { ...$roomEdits, [dragVertex.roomId]: pts };
		} else if (dragImage && $traceImage) {
			const m = toMap(e);
			if (!m) return;
			$traceImage = { ...$traceImage, x: round(m.x - dragImage.grabDx), y: round(m.y - dragImage.grabDy) };
		}
	}

	function endDrag() {
		dragNodeId = null;
		dragVertex = null;
		dragImage = null;
	}

	function insertVertex(e: MouseEvent, roomId: string, stored: number[][], index: number) {
		e.stopPropagation();
		const pts = roomPoints(roomId, stored).map((p) => [...p]);
		const a = pts[index];
		const b = pts[(index + 1) % pts.length];
		pts.splice(index + 1, 0, [round((a[0] + b[0]) / 2), round((a[1] + b[1]) / 2)]);
		$roomEdits = { ...$roomEdits, [roomId]: pts };
	}

	function removeVertex(e: MouseEvent, roomId: string, stored: number[][], index: number) {
		e.stopPropagation();
		e.preventDefault();
		const pts = roomPoints(roomId, stored).map((p) => [...p]);
		if (pts.length <= 3) return;
		pts.splice(index, 1);
		$roomEdits = { ...$roomEdits, [roomId]: pts };
	}

	// ─── map background clicks (place node / draft room / image calibration) ──
	function mapClick(e: MouseEvent) {
		const m = toMap(e);
		if (!m) return;
		if ($imageTool === 'origin' && $traceImage) {
			// Shift the image so the clicked spot becomes map (0,0) and LOCK it - an aligned
			// origin must not be nudged accidentally; re-setting the origin is the only way to move it.
			$traceImage = { ...$traceImage, x: round($traceImage.x - m.x), y: round($traceImage.y - m.y), movable: false, originSet: true };
			$imageTool = 'none';
		} else if ($imageTool === 'scale') {
			if ($scalePoints.length < 2) $scalePoints = [...$scalePoints, [m.x, m.y]];
		} else if ($imageTool === 'bounds') {
			if ($boundsPoints.length < 2) $boundsPoints = [...$boundsPoints, [round(m.x), round(m.y)]];
		} else if ($editMode === 'nodes' && $placingNode) {
			const zBase = floor?.bounds?.[0]?.[2] ?? 0;
			$pendingNode = { x: round(m.x), y: round(m.y), z: round(zBase + 0.25) };
			$placingNode = false;
		} else if ($editMode === 'rooms' && $draftRoom !== null) {
			$draftRoom = [...$draftRoom, [round(m.x), round(m.y)]];
		}
	}

	// Walk-test point picker: jump to the calibration setup page with the clicked spot prefilled.
	// Z is the clicked floor's base height (its local "0") - the wizard field is editable anyway.
	function walkPickClick(e: MouseEvent) {
		const m = toMap(e);
		if (!m) return;
		const zBase = floor?.bounds?.[0]?.[2] ?? 0;
		$pickingWalkPoint = false;
		gotoWalkSetup(round(m.x), round(m.y), round(zBase));
	}

	function roomClick(e: MouseEvent, roomId: string) {
		if ($draftRoom !== null) return; // drafting: click-through appends vertices via mapClick
		e.stopPropagation();
		$selectedRoomId = roomId;
	}

	// Config polygons are usually explicitly closed (first point repeated as last) - drop the
	// duplicate for editing so the user doesn't see/drag a phantom extra corner sitting exactly
	// on the first one (dragging it produced degenerate shapes).
	function openPolygon(pts: number[][]): number[][] {
		if (pts.length >= 2) {
			const f = pts[0];
			const l = pts[pts.length - 1];
			if (Math.abs(f[0] - l[0]) < 1e-9 && Math.abs(f[1] - l[1]) < 1e-9) return pts.slice(0, -1);
		}
		return pts;
	}

	// Scales are passed IN so the template expression references $xScale/$yScale directly -
	// reading them only inside the function body hides the dependency from Svelte's reactivity,
	// and the path then doesn't re-render when the scales change on browser resize (the handles
	// reference the scales inline and moved correctly, the outline didn't).
	function polyPath(pts: number[][], xs: (v: number) => number, ys: (v: number) => number): string {
		return `M${pts.map((p) => [xs(p[0]), ys(p[1])]).join('L')}Z`;
	}
</script>

<svelte:window onpointermove={globalMove} onpointerup={endDrag} />

{#if $pickingWalkPoint}
	<!-- Walk-picker capture layer: independent of edit mode (usable straight from view mode) -->
	<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
	<rect x={-10000} y={-10000} width={20000} height={20000} fill="transparent" style="pointer-events: all; cursor: crosshair;" onclick={walkPickClick} />
{/if}

{#if $editMode !== 'off'}
	<g transform={transform.toString()}>
		{#if $traceImage}
			{@const ix0 = $xScale($traceImage.x)}
			{@const ix1 = $xScale($traceImage.x + $traceImage.widthM)}
			{@const iy0 = $yScale($traceImage.y)}
			{@const iy1 = $yScale($traceImage.y + $traceImage.widthM * $traceImage.aspect)}
			{@const icx = (ix0 + ix1) / 2}
			{@const icy = (iy0 + iy1) / 2}
			<!-- Tracing image in map coordinates (pans/zooms with the map). Both corners are
			     scaled and min/abs'd so flipped axes (map.flip_x/flip_y) render correctly.
			     pointer-events only while 'movable' so drawing clicks pass through otherwise. -->
			<!-- svelte-ignore a11y_no_static_element_interactions -->
			<image
				href={$traceImage.url}
				x={Math.min(ix0, ix1)}
				y={Math.min(iy0, iy1)}
				width={Math.abs(ix1 - ix0)}
				height={Math.abs(iy1 - iy0)}
				opacity={$traceImage.opacity}
				preserveAspectRatio="none"
				transform={$traceImage.rotation ? `rotate(${$traceImage.rotation}, ${icx}, ${icy})` : undefined}
				style="pointer-events: {$traceImage.movable ? 'all' : 'none'}; cursor: {$traceImage.movable ? 'move' : 'default'};"
				onpointerdown={imageDown}
				onmousedown={blockZoom}
				ontouchstart={blockZoom}
			/>
		{/if}
		<!-- Transparent capture layer for placement/draft clicks (below handles) -->
		<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
		<rect
			x={-10000}
			y={-10000}
			width={20000}
			height={20000}
			fill="transparent"
			style="pointer-events: {($editMode === 'nodes' && $placingNode) || ($editMode === 'rooms' && $draftRoom !== null) || $imageTool !== 'none' ? 'all' : 'none'}; cursor: crosshair;"
			onclick={mapClick}
		/>

		<!-- Image calibration markers: origin crosshair at (0,0) while aligning, scale points/line -->
		{#if $traceImage && ($imageTool !== 'none' || $scalePoints.length > 0)}
			<line x1={$xScale(0) - 12 / transform.k} y1={$yScale(0)} x2={$xScale(0) + 12 / transform.k} y2={$yScale(0)} stroke="#ef4444" stroke-width={1.5 / transform.k} />
			<line x1={$xScale(0)} y1={$yScale(0) - 12 / transform.k} x2={$xScale(0)} y2={$yScale(0) + 12 / transform.k} stroke="#ef4444" stroke-width={1.5 / transform.k} />
		{/if}
		{#if $boundsPoints.length > 0}
			{#each $boundsPoints as p, i (i)}
				<circle cx={$xScale(p[0])} cy={$yScale(p[1])} r={5 / transform.k} fill="#22c55e" stroke="white" stroke-width={1 / transform.k} />
			{/each}
			{#if $boundsPoints.length === 2}
				{@const bx0 = $xScale(Math.min($boundsPoints[0][0], $boundsPoints[1][0]))}
				{@const bx1 = $xScale(Math.max($boundsPoints[0][0], $boundsPoints[1][0]))}
				{@const by0 = $yScale(Math.min($boundsPoints[0][1], $boundsPoints[1][1]))}
				{@const by1 = $yScale(Math.max($boundsPoints[0][1], $boundsPoints[1][1]))}
				<rect
					x={Math.min(bx0, bx1)}
					y={Math.min(by0, by1)}
					width={Math.abs(bx1 - bx0)}
					height={Math.abs(by1 - by0)}
					fill="#22c55e"
					fill-opacity="0.08"
					stroke="#22c55e"
					stroke-width={2 / transform.k}
					stroke-dasharray={`${6 / transform.k} ${4 / transform.k}`}
					style="pointer-events: none;"
				/>
			{/if}
		{/if}
		{#if $scalePoints.length > 0}
			{#each $scalePoints as p, i (i)}
				<circle cx={$xScale(p[0])} cy={$yScale(p[1])} r={5 / transform.k} fill="#ef4444" stroke="white" stroke-width={1 / transform.k} />
			{/each}
			{#if $scalePoints.length === 2}
				<line x1={$xScale($scalePoints[0][0])} y1={$yScale($scalePoints[0][1])} x2={$xScale($scalePoints[1][0])} y2={$yScale($scalePoints[1][1])} stroke="#ef4444" stroke-width={1.5 / transform.k} stroke-dasharray={`${5 / transform.k} ${3 / transform.k}`} />
			{/if}
		{/if}

		{#if $editMode === 'rooms'}
			<!-- Room selection overlays first ... -->
			{#each floor?.rooms ?? [] as room (room.id)}
				{@const pts = $roomEdits[room.id] ?? room.points}
				<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
				<path
					d={polyPath(pts, $xScale, $yScale)}
					fill="transparent"
					stroke={$selectedRoomId === room.id ? '#f59e0b' : 'transparent'}
					stroke-width={2 / transform.k}
					style="pointer-events: {$draftRoom !== null ? 'none' : 'all'}; cursor: pointer;"
					onclick={(e) => roomClick(e, room.id)}
				/>
			{/each}

			<!-- ... handles LAST so no later room's transparent click area paints above them
			     (SVG paint order = document order; corner handles sit exactly on shared edges,
			     where a neighboring room's interior would otherwise swallow the pointer events) -->
			{#each (floor?.rooms ?? []).filter((r) => r.id === $selectedRoomId) as room (room.id)}
				{@const pts = openPolygon($roomEdits[room.id] ?? room.points)}
				{#each pts as p, i (i)}
					{@const next = pts[(i + 1) % pts.length]}
					<!-- Edge midpoint: click to insert a vertex -->
					<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
					<circle
						cx={$xScale((p[0] + next[0]) / 2)}
						cy={$yScale((p[1] + next[1]) / 2)}
						r={hrSmall}
						fill="#94a3b8"
						opacity="0.7"
						style="cursor: copy; pointer-events: all;"
						onclick={(e) => insertVertex(e, room.id, pts, i)}
						onpointerdown={blockZoom}
						onmousedown={blockZoom}
						ontouchstart={blockZoom}
					/>
				{/each}
				{#each pts as p, i (i)}
					<!-- Vertex handle: drag to move, double-click to delete -->
					<!-- svelte-ignore a11y_no_static_element_interactions -->
					<circle
						cx={$xScale(p[0])}
						cy={$yScale(p[1])}
						r={hr}
						fill="#f59e0b"
						stroke="white"
						stroke-width={1.5 / transform.k}
						style="cursor: grab; pointer-events: all;"
						onpointerdown={(e) => vertexDown(e, room.id, i, pts)}
						onmousedown={blockZoom}
						ontouchstart={blockZoom}
						ondblclick={(e) => removeVertex(e, room.id, pts, i)}
					/>
				{/each}
			{/each}

			<!-- Draft room in progress -->
			{#if $draftRoom !== null && $draftRoom.length > 0}
				<path
					d={`M${$draftRoom.map((p) => [$xScale(p[0]), $yScale(p[1])]).join('L')}`}
					fill="none"
					stroke="#22c55e"
					stroke-width={2 / transform.k}
					stroke-dasharray={`${6 / transform.k} ${4 / transform.k}`}
				/>
				{#if $draftRoom.length >= 3}
					<!-- Implicit closing edge: Finish will close the outline here - shown fainter so
					     it's obvious the polygon closes itself without re-clicking the start point. -->
					<line
						x1={$xScale($draftRoom[$draftRoom.length - 1][0])}
						y1={$yScale($draftRoom[$draftRoom.length - 1][1])}
						x2={$xScale($draftRoom[0][0])}
						y2={$yScale($draftRoom[0][1])}
						stroke="#22c55e"
						stroke-opacity="0.35"
						stroke-width={2 / transform.k}
						stroke-dasharray={`${3 / transform.k} ${5 / transform.k}`}
					/>
				{/if}
				{#each $draftRoom as p, i (i)}
					<circle cx={$xScale(p[0])} cy={$yScale(p[1])} r={hrSmall} fill="#22c55e" />
				{/each}
			{/if}
		{/if}

		{#if $editMode === 'nodes'}
			{#each floorNodes as n (n.id)}
				{@const pos = $nodeEdits[n.id] ?? n.location}
				{@const dirty = !!$nodeEdits[n.id]}
				<!-- svelte-ignore a11y_no_static_element_interactions -->
				<circle
					cx={$xScale(pos.x)}
					cy={$yScale(pos.y)}
					r={hr}
					fill={$selectedNodeId === n.id ? '#f59e0b' : dirty ? '#22c55e' : '#3b82f6'}
					stroke="white"
					stroke-width={1.5 / transform.k}
					style="cursor: grab;"
					onpointerdown={(e) => nodeDown(e, n)}
					onmousedown={blockZoom}
					ontouchstart={blockZoom}
				/>
				<text
					x={$xScale(pos.x)}
					y={$yScale(pos.y) - 10 / transform.k}
					text-anchor="middle"
					fill="white"
					font-size={10 / transform.k}
					style="pointer-events: none;">{n.name ?? n.id}</text
				>
			{/each}

			{#if $pendingNode}
				<circle cx={$xScale($pendingNode.x)} cy={$yScale($pendingNode.y)} r={hr} fill="#22c55e" stroke="white" stroke-width={1.5 / transform.k} />
				<text x={$xScale($pendingNode.x)} y={$yScale($pendingNode.y) - 10 / transform.k} text-anchor="middle" fill="#22c55e" font-size={10 / transform.k} style="pointer-events: none;">new</text>
			{/if}
		{/if}
	</g>
{/if}
