<script lang="ts">
	import { apiPath } from '$lib/api';
	import { config, nodes } from '$lib/stores';
	import { getToastStore } from '$lib/toast/toastStore';
	import { showConfirm } from '$lib/modal/modalStore';
	import { editMode, selectedNodeId, nodeEdits, placingNode, pendingNode, selectedRoomId, roomEdits, draftRoom, traceImage, imageTool, scalePoints, boundsPoints, resetEditState } from '$lib/floorplanEdit';

	const toastStore = getToastStore();

	export let floorId: string | null = null;

	$: floor = $config?.floors.find((f) => f.id === floorId);
	$: selNode = ($nodes ?? []).find((n) => n.id === $selectedNodeId);
	$: selNodePos = selNode ? ($nodeEdits[selNode.id] ?? selNode.location) : null;
	$: selRoom = floor?.rooms?.find((r) => r.id === $selectedRoomId);
	$: selRoomDirty = !!($selectedRoomId && $roomEdits[$selectedRoomId]);

	// Nodes already announcing themselves over MQTT but not yet placed in the config - the most
	// likely candidates when adding a node, offered as suggestions for the id field.
	$: unplacedNodes = ($nodes ?? []).filter((n) => n.sourceType === 'Discovered');

	let newNodeId = '';
	let newNodeName = '';
	let newRoomName = '';
	let roomName = '';
	let lastSelRoomId: string | null = null;
	let busy = false;

	// Seed the editable room name whenever the selection changes.
	$: if ($selectedRoomId !== lastSelRoomId) {
		lastSelRoomId = $selectedRoomId;
		roomName = selRoom?.name ?? '';
	}

	// Prefill the new-node name from a matching discovered node when an id suggestion is picked.
	$: {
		const match = unplacedNodes.find((n) => n.id === newNodeId);
		if (match?.name && !newNodeName) newNodeName = match.name;
	}

	let boundsEdit: number[][] | null = null;

	let newFloorName = '';
	let newFloorBounds: number[][] | null = null;
	let renameFloorName = '';
	let lastFloorId: string | null = null;
	let seededFloorName: string | null = null;
	let traceInput: HTMLInputElement;

	// Seed the rename field when the floor changes - AND when the floor's name first arrives.
	// Right after creating a floor we switch to its tab before the config reload delivers it
	// (~1s), so the first seed attempt finds no name; re-seed once it shows up (but never while
	// the user is editing - seededFloorName guards against overwriting typed input).
	$: {
		const name = floor?.name ?? null;
		if (floorId !== lastFloorId || (seededFloorName === null && name !== null)) {
			lastFloorId = floorId;
			seededFloorName = name;
			renameFloorName = name ?? '';
		}
	}

	async function renameFloor() {
		if (!floorId || !renameFloorName.trim() || busy) return;
		busy = true;
		try {
			await post('/api/floorplan/floor', { floorId, name: renameFloorName.trim() });
			ok(`Floor renamed to '${renameFloorName.trim()}'`);
		} catch (e) {
			fail(e, 'Failed to rename floor');
		} finally {
			busy = false;
		}
	}

	async function deleteFloor() {
		if (!floorId || busy) return;
		const confirmed = await showConfirm({
			title: 'Delete floor',
			body: `Delete floor '${floor?.name ?? floorId}' including all its rooms? Nodes referencing it block the deletion (move them first).`
		});
		if (!confirmed) return;
		busy = true;
		try {
			const deletedId = floorId;
			const res = await fetch(apiPath(`/api/floorplan/floor/${deletedId}`), { method: 'DELETE' });
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			ok('Floor deleted');
			// Don't leave the UI staring at the deleted floor: clear the tracing aid and
			// switch to the first remaining floor.
			$traceImage = null;
			$imageTool = 'none';
			$scalePoints = [];
			$boundsPoints = [];
			$selectedRoomId = null;
			floorId = ($config?.floors ?? []).find((f) => f.id !== deletedId)?.id ?? null;
		} catch (e) {
			fail(e, 'Failed to delete floor');
		} finally {
			busy = false;
		}
	}

	// Text field instead of type=number: number inputs reject "4,5" in non-German browser
	// locales (silently - the bound value just stays empty). Parsed manually, comma accepted.
	let scaleRealDistance = '';

	function parseDistance(v: string): number | null {
		const n = parseFloat(v.replace(',', '.'));
		return Number.isFinite(n) && n > 0 ? n : null;
	}

	function startScaleTool() {
		$scalePoints = [];
		scaleRealDistance = '';
		$imageTool = 'scale';
	}

	function cancelImageTool() {
		$imageTool = 'none';
		$scalePoints = [];
		$boundsPoints = [];
		scaleRealDistance = '';
	}

	function applyScale() {
		const realDist = parseDistance(scaleRealDistance);
		if (!$traceImage || $scalePoints.length !== 2 || !realDist) return;
		const [p1, p2] = $scalePoints;
		const mapDist = Math.hypot(p2[0] - p1[0], p2[1] - p1[1]);
		if (mapDist < 1e-6) return;
		const factor = realDist / mapDist;
		// Pivot: once an origin is set, rescale around MAP (0,0) so the aligned origin stays
		// exactly put (scaling around the click point silently shifted a previously set origin -
		// the red crosshair then appeared "somewhere else" on the image). Without an origin,
		// rescale around the first clicked point so the measured feature stays under the cursor.
		const pivot = $traceImage.originSet ? [0, 0] : p1;
		$traceImage = {
			...$traceImage,
			widthM: Math.round($traceImage.widthM * factor * 100) / 100,
			x: Math.round((pivot[0] - (pivot[0] - $traceImage.x) * factor) * 100) / 100,
			y: Math.round((pivot[1] - (pivot[1] - $traceImage.y) * factor) * 100) / 100
		};
		cancelImageTool();
		ok('Scale applied - the clicked distance now measures ' + realDist + 'm');
	}

	function rotateImage(delta: number) {
		if (!$traceImage || $traceImage.originSet) return;
		$traceImage = { ...$traceImage, rotation: (($traceImage.rotation + delta) % 360 + 360) % 360 };
	}

	function startBoundsDraw() {
		$boundsPoints = [];
		$imageTool = 'bounds';
	}

	async function saveDrawnBounds() {
		if ($boundsPoints.length !== 2 || !floorId || busy) return;
		const [p1, p2] = $boundsPoints;
		const z0 = floor?.bounds?.[0]?.[2] ?? 0;
		const z1 = floor?.bounds?.[1]?.[2] ?? z0 + 2.5;
		const bounds = [
			[Math.min(p1[0], p2[0]), Math.min(p1[1], p2[1]), z0],
			[Math.max(p1[0], p2[0]), Math.max(p1[1], p2[1]), z1]
		];
		busy = true;
		try {
			await post('/api/floorplan/floor-bounds', { floorId, bounds });
			ok('Floor bounds saved (z range kept)');
			cancelImageTool();
		} catch (e) {
			fail(e, 'Failed to set bounds');
		} finally {
			busy = false;
		}
	}

	function handleTraceUpload(event: Event) {
		const file = (event.target as HTMLInputElement).files?.[0];
		if (!file) return;
		const reader = new FileReader();
		reader.onload = (e) => {
			const url = e.target?.result as string;
			const img = new Image();
			img.onload = () => {
				const boundsW = floor?.bounds?.[1]?.[0] ?? 10;
				$traceImage = {
					url,
					x: 0,
					y: 0,
					widthM: boundsW,
					aspect: img.height / img.width,
					opacity: 0.4,
					movable: true,
					rotation: 0,
					originSet: false
				};
			};
			img.src = url;
		};
		reader.readAsDataURL(file);
	}

	function startNewFloor() {
		// Prefill x/y extent from the current floor, stack the z range on top of the highest floor.
		const b = floor?.bounds;
		const maxZ = Math.max(...($config?.floors ?? []).map((f) => f.bounds?.[1]?.[2] ?? 0), 0);
		newFloorBounds = [
			[0, 0, Math.round((maxZ + 0.3) * 100) / 100],
			[b?.[1]?.[0] ?? 10, b?.[1]?.[1] ?? 10, Math.round((maxZ + 2.8) * 100) / 100]
		];
		newFloorName = '';
	}

	async function saveNewFloor() {
		if (!newFloorBounds || !newFloorName.trim() || busy) return;
		busy = true;
		try {
			const res = await fetch(apiPath('/api/floorplan/floor'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ name: newFloorName.trim(), bounds: newFloorBounds })
			});
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			const data = await res.json();
			ok(`Floor '${newFloorName.trim()}' created`);
			newFloorBounds = null;
			newFloorName = '';
			// Jump straight to the new floor's tab (map falls back to the first floor for the
			// ~1s until the config reload delivers it, then switches over).
			if (data?.floorId) floorId = data.floorId;
		} catch (e) {
			fail(e, 'Failed to create floor');
		} finally {
			busy = false;
		}
	}

	function setMode(mode: 'off' | 'nodes' | 'rooms') {
		resetEditState();
		$editMode = mode;
	}

	function fail(error: unknown, fallback: string) {
		toastStore.trigger({
			message: error instanceof Error ? error.message : fallback,
			background: 'preset-filled-error-500'
		});
	}

	function ok(message: string) {
		toastStore.trigger({ message, background: 'preset-filled-success-500' });
	}

	async function post(path: string, body: unknown): Promise<void> {
		const res = await fetch(apiPath(path), {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify(body)
		});
		if (!res.ok) {
			const err = await res.json().catch(() => null);
			throw new Error(err?.error ?? `HTTP ${res.status}`);
		}
	}

	function updateSelNodeField(field: 'x' | 'y' | 'z', value: number) {
		if (!selNode || !selNodePos) return;
		$nodeEdits = { ...$nodeEdits, [selNode.id]: { ...selNodePos, [field]: value } };
	}

	async function saveSelectedNode() {
		if (!selNode || !selNodePos || busy) return;
		busy = true;
		try {
			await post('/api/floorplan/node', { id: selNode.id, x: selNodePos.x, y: selNodePos.y, z: selNodePos.z, floors: selNode.floors });
			ok(`Node '${selNode.name ?? selNode.id}' saved`);
			const edits = { ...$nodeEdits };
			delete edits[selNode.id];
			$nodeEdits = edits;
		} catch (e) {
			fail(e, 'Failed to save node');
		} finally {
			busy = false;
		}
	}

	async function deleteSelectedNode() {
		if (!selNode || busy) return;
		const confirmed = await showConfirm({
			title: 'Delete node from config',
			body: `Remove node '${selNode.name ?? selNode.id}' from the floorplan config? This does not touch the physical device or its MQTT settings.`
		});
		if (!confirmed) return;
		busy = true;
		try {
			const res = await fetch(apiPath(`/api/floorplan/node/${selNode.id}`), { method: 'DELETE' });
			if (!res.ok) throw new Error(`HTTP ${res.status}`);
			ok('Node removed from config');
			$selectedNodeId = null;
		} catch (e) {
			fail(e, 'Failed to delete node');
		} finally {
			busy = false;
		}
	}

	async function saveNewNode() {
		if (!$pendingNode || !newNodeId.trim() || !floorId || busy) return;
		busy = true;
		try {
			await post('/api/floorplan/node', {
				id: newNodeId.trim(),
				name: newNodeName.trim() || newNodeId.trim(),
				x: $pendingNode.x,
				y: $pendingNode.y,
				z: $pendingNode.z,
				floors: [floorId]
			});
			ok(`Node '${newNodeId.trim()}' created`);
			$pendingNode = null;
			newNodeId = '';
			newNodeName = '';
		} catch (e) {
			fail(e, 'Failed to create node');
		} finally {
			busy = false;
		}
	}

	async function saveSelectedRoom() {
		if (!selRoom || !floorId || busy) return;
		const points = $roomEdits[selRoom.id] ?? selRoom.points;
		busy = true;
		try {
			await post('/api/floorplan/room', { floorId, roomId: selRoom.id, points, name: roomName.trim() || selRoom.name });
			ok(`Room '${roomName.trim() || selRoom.name}' saved`);
			const edits = { ...$roomEdits };
			delete edits[selRoom.id];
			$roomEdits = edits;
		} catch (e) {
			fail(e, 'Failed to save room');
		} finally {
			busy = false;
		}
	}

	async function deleteSelectedRoom() {
		if (!selRoom || !floorId || busy) return;
		const confirmed = await showConfirm({
			title: 'Delete room',
			body: `Remove room '${selRoom.name}' from floor '${floor?.name ?? floorId}'?`
		});
		if (!confirmed) return;
		busy = true;
		try {
			const res = await fetch(apiPath(`/api/floorplan/room/${floorId}/${selRoom.id}`), { method: 'DELETE' });
			if (!res.ok) throw new Error(`HTTP ${res.status}`);
			ok('Room deleted');
			$selectedRoomId = null;
		} catch (e) {
			fail(e, 'Failed to delete room');
		} finally {
			busy = false;
		}
	}

	async function finishDraftRoom() {
		if (!$draftRoom || $draftRoom.length < 3 || !newRoomName.trim() || !floorId || busy) return;
		busy = true;
		try {
			await post('/api/floorplan/room', { floorId, name: newRoomName.trim(), points: $draftRoom });
			ok(`Room '${newRoomName.trim()}' created`);
			$draftRoom = null;
			newRoomName = '';
		} catch (e) {
			fail(e, 'Failed to create room');
		} finally {
			busy = false;
		}
	}

	function startBoundsEdit() {
		boundsEdit = (floor?.bounds ?? []).map((b) => [...b]);
	}

	async function saveBounds() {
		if (!boundsEdit || !floorId || busy) return;
		busy = true;
		try {
			await post('/api/floorplan/floor-bounds', { floorId, bounds: boundsEdit });
			ok('Floor bounds saved');
			boundsEdit = null;
		} catch (e) {
			fail(e, 'Failed to save bounds');
		} finally {
			busy = false;
		}
	}
</script>

<div class="absolute top-2 left-2 z-10 flex flex-col gap-2 w-80 max-w-[90vw]">
	<div class="flex gap-1 bg-surface-100-900/90 rounded-lg p-1 shadow">
		<button class="btn btn-sm {$editMode === 'off' ? 'preset-filled-primary-500' : 'preset-tonal'}" onclick={() => setMode('off')}>View</button>
		<button class="btn btn-sm {$editMode !== 'off' ? 'preset-filled-primary-500' : 'preset-tonal'}" onclick={() => setMode('rooms')}>Edit</button>
	</div>

	{#if $editMode === 'nodes'}
		<div class="bg-surface-100-900/90 rounded-lg p-3 shadow space-y-2 text-sm">
			<div class="flex items-center justify-between">
				<p class="font-semibold text-xs">Nodes</p>
				<button class="btn btn-sm preset-tonal" onclick={() => ($editMode = 'rooms')}>Back</button>
			</div>
			{#if $pendingNode}
				<p class="font-semibold">New node at ({$pendingNode.x}, {$pendingNode.y})</p>
				<input class="input" placeholder="node id (e.g. kitchen_2)" bind:value={newNodeId} list="floorplan-known-nodes" />
				<datalist id="floorplan-known-nodes">
					{#each unplacedNodes as n (n.id)}
						<option value={n.id}>{n.name ?? n.id}</option>
					{/each}
				</datalist>
				{#if unplacedNodes.length > 0}
					<p class="text-xs text-surface-600-400">{unplacedNodes.length} discovered but unplaced node{unplacedNodes.length === 1 ? '' : 's'} - the id field suggests them.</p>
				{/if}
				<input class="input" placeholder="name (optional)" bind:value={newNodeName} />
				<label class="label"><span>Z (height)</span><input class="input" type="number" step="0.1" bind:value={$pendingNode.z} /></label>
				<div class="flex gap-2">
					<button class="btn btn-sm preset-filled-success-500" onclick={saveNewNode} disabled={busy || !newNodeId.trim()}>Create</button>
					<button class="btn btn-sm preset-tonal" onclick={() => ($pendingNode = null)}>Cancel</button>
				</div>
			{:else if selNode && selNodePos}
				<p class="font-semibold">{selNode.name ?? selNode.id}</p>
				<div class="grid grid-cols-3 gap-2">
					<label class="label"><span>X</span><input class="input" type="number" step="0.05" value={selNodePos.x} onchange={(e) => updateSelNodeField('x', +e.currentTarget.value)} /></label>
					<label class="label"><span>Y</span><input class="input" type="number" step="0.05" value={selNodePos.y} onchange={(e) => updateSelNodeField('y', +e.currentTarget.value)} /></label>
					<label class="label"><span>Z</span><input class="input" type="number" step="0.05" value={selNodePos.z} onchange={(e) => updateSelNodeField('z', +e.currentTarget.value)} /></label>
				</div>
				<div class="flex gap-2">
					<button class="btn btn-sm preset-filled-success-500" onclick={saveSelectedNode} disabled={busy || !$nodeEdits[selNode.id]}>Save</button>
					<button class="btn btn-sm preset-filled-error-500" onclick={deleteSelectedNode} disabled={busy}>Delete</button>
				</div>
				<p class="text-xs text-surface-600-400">Drag the marker or edit the fields. Z is the absolute height incl. floor offset.</p>
			{:else}
				<p class="text-xs">Drag a node marker to move it, click one to select. After saving, the wizard's placement check re-evaluates automatically.</p>
				<button class="btn btn-sm {$placingNode ? 'preset-filled-warning-500' : 'preset-tonal'}" onclick={() => ($placingNode = !$placingNode)}>
					{$placingNode ? 'Click map to place...' : 'Add node'}
				</button>
			{/if}
		</div>
	{/if}

	{#if $editMode === 'rooms'}
		<div class="bg-surface-100-900/90 rounded-lg p-3 shadow space-y-2 text-sm">
			{#if $draftRoom !== null}
				<p class="font-semibold">New room: {$draftRoom.length} points</p>
				<input class="input" placeholder="room name" bind:value={newRoomName} />
				<div class="flex gap-2">
					<button class="btn btn-sm preset-filled-success-500" onclick={finishDraftRoom} disabled={busy || $draftRoom.length < 3 || !newRoomName.trim()}>Finish</button>
					<button class="btn btn-sm preset-tonal" onclick={() => ($draftRoom = null)}>Cancel</button>
				</div>
				<p class="text-xs text-surface-600-400">Click the map to add corner points (at least 3). Finish closes the outline back to the first point automatically - no need to click the start again.</p>
			{:else if selRoom}
				<input class="input font-semibold" bind:value={roomName} placeholder="room name" />
				<div class="flex gap-2">
					<button class="btn btn-sm preset-filled-success-500" onclick={saveSelectedRoom} disabled={busy || (!selRoomDirty && roomName.trim() === selRoom.name)}>Save</button>
					<button class="btn btn-sm preset-filled-error-500" onclick={deleteSelectedRoom} disabled={busy}>Delete</button>
					<button class="btn btn-sm preset-tonal" onclick={() => ($selectedRoomId = null)}>Deselect</button>
				</div>
				<p class="text-xs text-surface-600-400">Drag corners to move them. Click a small edge dot to insert a corner, double-click a corner to remove it. The name is editable above.</p>
			{:else}
				<p class="text-xs">Click a room to edit its outline.</p>
				<div class="flex gap-2 items-center">
					<input class="input" bind:value={renameFloorName} placeholder="floor name" />
					<button class="btn btn-sm preset-tonal" onclick={renameFloor} disabled={busy || !renameFloorName.trim() || renameFloorName.trim() === floor?.name}>Rename</button>
					<button class="btn btn-sm preset-filled-error-500" onclick={deleteFloor} disabled={busy}>Delete</button>
				</div>
				<div class="flex gap-2">
					<button class="btn btn-sm preset-tonal" onclick={() => ($editMode = 'nodes')}>Edit nodes</button>
					<button class="btn btn-sm preset-tonal" onclick={() => ($draftRoom = [])}>New room</button>
				</div>
				{#if newFloorBounds}
					<div class="space-y-1">
						<p class="font-semibold text-xs">New floor</p>
						<input class="input" placeholder="floor name (e.g. Anbau)" bind:value={newFloorName} />
						<p class="text-xs text-surface-600-400">Bounds [x, y, z] - z range prefilled on top of the highest floor:</p>
						{#each newFloorBounds as corner, ci (ci)}
							<div class="grid grid-cols-3 gap-1">
								{#each corner as v, vi (vi)}
									<input class="input" type="number" step="0.1" bind:value={newFloorBounds[ci][vi]} />
								{/each}
							</div>
						{/each}
						<div class="flex gap-2">
							<button class="btn btn-sm preset-filled-success-500" onclick={saveNewFloor} disabled={busy || !newFloorName.trim()}>Create floor</button>
							<button class="btn btn-sm preset-tonal" onclick={() => (newFloorBounds = null)}>Cancel</button>
						</div>
					</div>
				{:else}
					<button class="btn btn-sm preset-tonal" onclick={startNewFloor}>Add floor</button>
				{/if}
				{#if boundsEdit}
					<div class="space-y-1">
						<p class="font-semibold text-xs">Floor bounds [x, y, z]</p>
						{#each boundsEdit as corner, ci (ci)}
							<div class="grid grid-cols-3 gap-1">
								{#each corner as v, vi (vi)}
									<input class="input" type="number" step="0.1" bind:value={boundsEdit[ci][vi]} />
								{/each}
							</div>
						{/each}
						<div class="flex gap-2">
							<button class="btn btn-sm preset-filled-success-500" onclick={saveBounds} disabled={busy}>Save bounds</button>
							<button class="btn btn-sm preset-tonal" onclick={() => (boundsEdit = null)}>Cancel</button>
						</div>
					</div>
				{:else}
					{#if $imageTool === 'bounds'}
						<div class="space-y-1">
							<p class="text-xs">Click two opposite corners of the floor's extent on the map ({$boundsPoints.length}/2). Dimension chains outside the outline don't matter - just frame the actual living area.</p>
							<div class="flex gap-2">
								<button class="btn btn-sm preset-filled-success-500" onclick={saveDrawnBounds} disabled={busy || $boundsPoints.length !== 2}>Save bounds</button>
								<button class="btn btn-sm preset-tonal" onclick={cancelImageTool}>Cancel</button>
							</div>
						</div>
					{:else}
						<div class="flex gap-2">
							<button class="btn btn-sm preset-tonal" onclick={startBoundsDraw}>Draw bounds</button>
							<button class="btn btn-sm preset-tonal" onclick={startBoundsEdit}>Edit floor bounds</button>
						</div>
					{/if}
				{/if}

				<!-- Tracing image (scale template) -->
				<input bind:this={traceInput} type="file" accept=".svg,.png,.jpg,.jpeg,.webp" onchange={handleTraceUpload} style="display: none;" />
				{#if $traceImage}
					<div class="space-y-1 border-t border-surface-300-700 pt-2">
						<p class="font-semibold text-xs">Tracing image</p>
						{#if $imageTool === 'scale'}
							<p class="text-xs">Click the two ends of a feature with a KNOWN length on the image (e.g. a measured wall). {$scalePoints.length}/2 points set.</p>
							{#if $scalePoints.length === 2}
								<label class="label text-xs"><span>Real distance (m) - comma or dot both work</span>
									<input class="input" type="text" inputmode="decimal" placeholder="e.g. 4,50" bind:value={scaleRealDistance} />
								</label>
								<div class="flex gap-2">
									<button class="btn btn-sm preset-filled-success-500" onclick={applyScale} disabled={!parseDistance(scaleRealDistance)}>Apply scale</button>
									<button class="btn btn-sm preset-tonal" onclick={cancelImageTool}>Cancel</button>
								</div>
							{:else}
								<button class="btn btn-sm preset-tonal" onclick={cancelImageTool}>Cancel</button>
							{/if}
						{:else if $imageTool === 'origin'}
							<p class="text-xs">Click the point on the image that should become the map origin (0,0) - usually the building's top-left corner. The red crosshair marks the current origin.</p>
							<button class="btn btn-sm preset-tonal" onclick={cancelImageTool}>Cancel</button>
						{:else}
							<div class="flex flex-wrap gap-2">
								<button class="btn btn-sm preset-tonal" onclick={() => ($imageTool = 'origin')}>Set origin</button>
								<button class="btn btn-sm preset-tonal" onclick={startScaleTool}>Measure scale</button>
								<button
									class="btn btn-sm {$traceImage.movable ? 'preset-filled-warning-500' : 'preset-tonal'}"
									onclick={() => ($traceImage = $traceImage ? { ...$traceImage, movable: !$traceImage.movable } : null)}
									disabled={$traceImage.originSet}
									title={$traceImage.originSet ? 'Origin is set - the image is locked. Re-run Set origin to reposition.' : ''}
								>
									{$traceImage.originSet ? 'Locked (origin set)' : $traceImage.movable ? 'Moving (drag image)' : 'Move'}
								</button>
							</div>
							<div class="flex flex-wrap items-center gap-2">
								<button class="btn btn-sm preset-tonal" onclick={() => rotateImage(-90)} disabled={$traceImage.originSet} title="Rotate 90° counter-clockwise">⟲ 90°</button>
								<button class="btn btn-sm preset-tonal" onclick={() => rotateImage(90)} disabled={$traceImage.originSet} title="Rotate 90° clockwise">⟳ 90°</button>
								<label class="label text-xs flex items-center gap-1"><span>°</span>
									<input class="input w-20" type="number" step="0.5" bind:value={$traceImage.rotation} disabled={$traceImage.originSet} />
								</label>
								<button class="btn btn-sm preset-tonal" onclick={() => ($traceImage = null)}>Remove</button>
							</div>
							<label class="label text-xs"><span>Opacity: {Math.round($traceImage.opacity * 100)}%</span>
								<input class="input" type="range" min="0.1" max="1" step="0.05" bind:value={$traceImage.opacity} />
							</label>
							<p class="text-xs text-surface-600-400">Workflow: 1. Measure scale (two clicks on a known distance) → 2. Set origin (click the building corner) → 3. Draw bounds (frame the living area) → 4. draw rooms over it. Session-only - not saved.</p>
						{/if}
					</div>
				{:else}
					<button class="btn btn-sm preset-tonal" onclick={() => traceInput.click()}>Load tracing image</button>
				{/if}
			{/if}
		</div>
	{/if}
</div>
