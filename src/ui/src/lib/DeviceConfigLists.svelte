<script lang="ts">
	import { onMount } from 'svelte';
	import { apiPath } from '$lib/api';
	import { getToastStore } from '$lib/toast/toastStore';

	const toastStore = getToastStore();

	interface Entry {
		name?: string | null;
		id?: string | null;
	}

	let devices: Entry[] = [];
	let excludeDevices: Entry[] = [];
	let open = false;
	let loaded = false;
	let busy = false;

	async function load() {
		try {
			const res = await fetch(apiPath('/api/config/devices'));
			if (!res.ok) throw new Error(`HTTP ${res.status}`);
			const data = await res.json();
			devices = data.devices ?? [];
			excludeDevices = data.excludeDevices ?? [];
			loaded = true;
		} catch (error) {
			console.error('Error loading device lists:', error);
		}
	}

	async function save() {
		if (busy) return;
		busy = true;
		try {
			const res = await fetch(apiPath('/api/config/devices'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ devices, excludeDevices })
			});
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({ message: 'Device lists saved to config', background: 'preset-filled-success-500' });
			await load();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to save device lists',
				background: 'preset-filled-error-500'
			});
		} finally {
			busy = false;
		}
	}

	onMount(load);
</script>

<div class="card p-4">
	<header class="flex items-center justify-between">
		<h2 class="text-lg font-semibold">Config: Tracked & Excluded Devices</h2>
		<button class="btn btn-sm preset-tonal" onclick={() => (open = !open)}>{open ? 'Hide' : 'Show'}</button>
	</header>
	{#if open && loaded}
		<p class="text-sm text-surface-600-400 mt-2 mb-3">
			These are the config.yaml <code>devices:</code> (tracked) and <code>exclude_devices:</code> lists. Ids support wildcards (e.g. <code>phone:*</code>). Deleting a device in the table above also removes its exact-id entry here; excluding is managed in this panel.
		</p>
		<div class="grid grid-cols-1 md:grid-cols-2 gap-6">
			<div>
				<h3 class="font-semibold text-sm mb-2">Tracked (devices)</h3>
				{#each devices as d, i (i)}
					<div class="flex gap-2 mb-1">
						<input class="input" placeholder="name" bind:value={d.name} />
						<input class="input" placeholder="id (e.g. iBeacon:... or phone:*)" bind:value={d.id} />
						<button class="btn btn-sm preset-filled-error-500" onclick={() => (devices = devices.filter((_, j) => j !== i))}>✕</button>
					</div>
				{/each}
				<button class="btn btn-sm preset-tonal mt-1" onclick={() => (devices = [...devices, { name: '', id: '' }])}>Add entry</button>
			</div>
			<div>
				<h3 class="font-semibold text-sm mb-2">Excluded (exclude_devices)</h3>
				{#each excludeDevices as d, i (i)}
					<div class="flex gap-2 mb-1">
						<input class="input" placeholder="name" bind:value={d.name} />
						<input class="input" placeholder="id" bind:value={d.id} />
						<button class="btn btn-sm preset-filled-error-500" onclick={() => (excludeDevices = excludeDevices.filter((_, j) => j !== i))}>✕</button>
					</div>
				{/each}
				<button class="btn btn-sm preset-tonal mt-1" onclick={() => (excludeDevices = [...excludeDevices, { name: '', id: '' }])}>Add entry</button>
			</div>
		</div>
		<button class="btn preset-filled-primary-500 mt-4" onclick={save} disabled={busy}>Save</button>
	{/if}
</div>
