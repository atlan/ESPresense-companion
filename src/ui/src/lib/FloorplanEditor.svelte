<script lang="ts">
	import { getContext } from 'svelte';
	import { zoomIdentity } from 'd3-zoom';
	import { config, nodes } from '$lib/stores';
	import { screenToMap } from '$lib/mapcoords';
	import { editMode, selectedNodeId, nodeEdits, placingNode, pendingNode, selectedRoomId, roomEdits, draftRoom } from '$lib/floorplanEdit';
	import type { LayerCakeContext } from '$lib/types';

	export let transform = zoomIdentity;
	export let floorId: string | null = null;
	export let svgEl: SVGElement | undefined = undefined;

	const { xScale, yScale, padding } = getContext<LayerCakeContext>('LayerCake');

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
		return screenToMap(svgEl as SVGSVGElement, e.clientX, e.clientY, $padding, transform, $xScale, $yScale);
	}

	function round(v: number): number {
		return Math.round(v * 100) / 100;
	}

	// ─── node dragging ───────────────────────────────────────────────────────
	let dragNodeId: string | null = null;

	function nodeDown(e: PointerEvent, id: string) {
		e.stopPropagation();
		e.preventDefault();
		dragNodeId = id;
		$selectedNodeId = id;
		(e.target as Element).setPointerCapture(e.pointerId);
	}

	function nodeMove(e: PointerEvent, n: { id: string; location: { z: number } }) {
		if (dragNodeId !== n.id) return;
		const m = toMap(e);
		if (!m) return;
		const z = $nodeEdits[n.id]?.z ?? n.location.z;
		$nodeEdits = { ...$nodeEdits, [n.id]: { x: round(m.x), y: round(m.y), z } };
	}

	function nodeUp() {
		dragNodeId = null;
	}

	// ─── room vertex dragging ────────────────────────────────────────────────
	let dragVertex: { roomId: string; index: number } | null = null;

	function vertexDown(e: PointerEvent, roomId: string, index: number) {
		e.stopPropagation();
		e.preventDefault();
		dragVertex = { roomId, index };
		(e.target as Element).setPointerCapture(e.pointerId);
	}

	function vertexMove(e: PointerEvent, stored: number[][]) {
		if (!dragVertex) return;
		const m = toMap(e);
		if (!m) return;
		const pts = (roomPoints(dragVertex.roomId, stored) ?? []).map((p) => [...p]);
		pts[dragVertex.index] = [round(m.x), round(m.y)];
		$roomEdits = { ...$roomEdits, [dragVertex.roomId]: pts };
	}

	function vertexUp() {
		dragVertex = null;
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

	// ─── map background clicks (place node / draft room) ─────────────────────
	function mapClick(e: MouseEvent) {
		const m = toMap(e);
		if (!m) return;
		if ($editMode === 'nodes' && $placingNode) {
			const zBase = floor?.bounds?.[0]?.[2] ?? 0;
			$pendingNode = { x: round(m.x), y: round(m.y), z: round(zBase + 0.25) };
			$placingNode = false;
		} else if ($editMode === 'rooms' && $draftRoom !== null) {
			$draftRoom = [...$draftRoom, [round(m.x), round(m.y)]];
		}
	}

	function roomClick(e: MouseEvent, roomId: string) {
		if ($draftRoom !== null) return; // drafting: click-through appends vertices via mapClick
		e.stopPropagation();
		$selectedRoomId = roomId;
	}

	function polyPath(pts: number[][]): string {
		return `M${pts.map((p) => [$xScale(p[0]), $yScale(p[1])]).join('L')}Z`;
	}
</script>

{#if $editMode !== 'off'}
	<g transform={transform.toString()}>
		<!-- Transparent capture layer for placement/draft clicks (below handles) -->
		<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
		<rect
			x={-10000}
			y={-10000}
			width={20000}
			height={20000}
			fill="transparent"
			style="pointer-events: {($editMode === 'nodes' && $placingNode) || ($editMode === 'rooms' && $draftRoom !== null) ? 'all' : 'none'}; cursor: crosshair;"
			onclick={mapClick}
		/>

		{#if $editMode === 'rooms'}
			<!-- Room selection overlays + selected-room handles -->
			{#each floor?.rooms ?? [] as room (room.id)}
				{@const pts = roomPoints(room.id, room.points)}
				<!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
				<path
					d={polyPath(pts)}
					fill="transparent"
					stroke={$selectedRoomId === room.id ? '#f59e0b' : 'transparent'}
					stroke-width={2 / transform.k}
					style="pointer-events: {$draftRoom !== null ? 'none' : 'all'}; cursor: pointer;"
					onclick={(e) => roomClick(e, room.id)}
				/>
				{#if $selectedRoomId === room.id}
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
							style="cursor: copy;"
							onclick={(e) => insertVertex(e, room.id, room.points, i)}
							onpointerdown={(e) => e.stopPropagation()}
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
							style="cursor: grab;"
							onpointerdown={(e) => vertexDown(e, room.id, i)}
							onpointermove={(e) => vertexMove(e, room.points)}
							onpointerup={vertexUp}
							ondblclick={(e) => removeVertex(e, room.id, room.points, i)}
						/>
					{/each}
				{/if}
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
				{#each $draftRoom as p, i (i)}
					<circle cx={$xScale(p[0])} cy={$yScale(p[1])} r={hrSmall} fill="#22c55e" />
				{/each}
			{/if}
		{/if}

		{#if $editMode === 'nodes'}
			{#each floorNodes as n (n.id)}
				{@const pos = nodePos(n)}
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
					onpointerdown={(e) => nodeDown(e, n.id)}
					onpointermove={(e) => nodeMove(e, n)}
					onpointerup={nodeUp}
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
