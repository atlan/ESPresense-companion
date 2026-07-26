<script lang="ts">
	import type { DeviceSetting } from './types';
	import { gotoCalibration } from '$lib/urls';

	export let settings: DeviceSetting; // Parent handles loading
	export let anchorEnabled = false;
</script>

{#if settings}
	<div class="space-y-4">
		<label class="label">
			<span class="text-surface-800-200">ID (Original)</span>
			<input class="input" type="text" disabled bind:value={settings.originalId} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Alias</span>
			<input class="input" type="text" bind:value={settings.id} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">Name</span>
			<input class="input" type="text" bind:value={settings.name} />
		</label>
		<label class="label">
			<span class="text-surface-800-200">RSSI@1m</span>
			<input class="input" type="number" placeholder="e.g., -65" bind:value={settings['rssi@1m']} />
		</label>
		<p class="text-xs text-surface-600-400 -mt-2">
			The level this device is heard at from one metre away. It varies by tens of dB between
			transmitters and cannot be derived from the node calibration, so a typed guess costs accuracy
			everywhere this device is tracked.
			<button type="button" class="anchor" onclick={() => gotoCalibration(settings.originalId ?? settings.id)}>
				Measure it
			</button>
			instead - it takes about a minute.
		</p>
		<label class="label inline-flex items-center space-x-2">
			<input type="checkbox" class="checkbox" bind:checked={anchorEnabled} />
			<span class="text-surface-800-200">Anchored device</span>
		</label>
		{#if anchorEnabled}
			<div class="grid gap-3 sm:grid-cols-3">
				<label class="label">
					<span class="text-surface-800-200">X (meters)</span>
					<input class="input" type="number" step="0.01" placeholder="e.g., 1.25" bind:value={settings.x} />
				</label>
				<label class="label">
					<span class="text-surface-800-200">Y (meters)</span>
					<input class="input" type="number" step="0.01" placeholder="e.g., 2.50" bind:value={settings.y} />
				</label>
				<label class="label">
					<span class="text-surface-800-200">Z (meters)</span>
					<input class="input" type="number" step="0.01" placeholder="e.g., 0.75" bind:value={settings.z} />
				</label>
			</div>
		{/if}
	</div>
{:else}
	<div class="text-center text-surface-600-400">Settings not available.</div>
{/if}
