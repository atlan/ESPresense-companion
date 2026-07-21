<script lang="ts">
	import { apiPath } from '$lib/api';
	import { config, nodes } from '$lib/stores';
	import { getToastStore } from '$lib/toast/toastStore';
	import { showConfirm } from '$lib/modal/modalStore';
	import { editMode, selectedNodeId, nodeEdits, placingNode, pendingNode, selectedRoomId, roomEdits, draftRoom, resetEditState } from '$lib/floorplanEdit';

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
			await post('/api/floorplan/floor', { name: newFloorName.trim(), bounds: newFloorBounds });
			ok(`Floor '${newFloorName.trim()}' created - select it in the floor tabs to draw rooms`);
			newFloorBounds = null;
			newFloorName = '';
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

<div class="absolute top-2 left-2 z-10 flex flex-col gap-2 max-w-xs">
	<div class="flex gap-1 bg-surface-100-900/90 rounded-lg p-1 shadow">
		<button class="btn btn-sm {$editMode === 'off' ? 'preset-filled-primary-500' : 'preset-tonal'}" onclick={() => setMode('off')}>View</button>
		<button class="btn btn-sm {$editMode === 'nodes' ? 'preset-filled-primary-500' : 'preset-tonal'}" onclick={() => setMode('nodes')}>Edit Nodes</button>
		<button class="btn btn-sm {$editMode === 'rooms' ? 'preset-filled-primary-500' : 'preset-tonal'}" onclick={() => setMode('rooms')}>Edit Rooms</button>
	</div>

	{#if $editMode === 'nodes'}
		<div class="bg-surface-100-900/90 rounded-lg p-3 shadow space-y-2 text-sm">
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
				<p class="text-xs text-surface-600-400">Click the map to add corner points (at least 3).</p>
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
				<button class="btn btn-sm preset-tonal" onclick={() => ($draftRoom = [])}>New room</button>
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
					<button class="btn btn-sm preset-tonal" onclick={startBoundsEdit}>Edit floor bounds</button>
				{/if}
			{/if}
		</div>
	{/if}
</div>
