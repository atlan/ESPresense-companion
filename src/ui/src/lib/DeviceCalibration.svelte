<script lang="ts">
	import { apiPath } from '$lib/api';
	import { devices, nodes, config, wsManager } from '$lib/stores';
	import Map from '$lib/Map.svelte';
	import { getToastStore } from '$lib/toast/toastStore';
	import type { DeviceSetting, NodeSetting } from '$lib/types';
	import type { DeviceMessage } from '$lib/types';
	import { onMount, onDestroy } from 'svelte';

	const toastStore = getToastStore();

	// Changed from 'export let deviceId' to 'export let deviceSettings'
	export let deviceSettings: DeviceSetting;

	let nodeSettings: Record<string, NodeSetting | null> = {};

	// Device state - adjusted to fetch based on deviceId
	let selectedFloorId: string | null = null;
	let calibrationSpot: { x: number; y: number; z?: number } | null = null;
	let calibrationSpotHeight = 1.0; // Default height in meters
	let currentRefRssi: number | null = null; // Will set after fetch

	// Local storage for all device messages (keyed by nodeId)
	let deviceMessages: Record<string, DeviceMessage[]> = {};

	$: isAnchored = deviceSettings?.x != null && deviceSettings?.y != null && deviceSettings?.z != null;

	let showInstructions = false;

	function toggleInstructions() {
		showInstructions = !showInstructions;
		try {
			localStorage.setItem('deviceCalibrationInstructions', showInstructions ? 'shown' : 'hidden');
		} catch {
			// localStorage unavailable; toggle still works for this session
		}
	}

	// Function to fetch device settings based on deviceId
	async function fetchDeviceSettings() {
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings.id}`));
			if (response.ok) {
				deviceSettings = await response.json();
				currentRefRssi = deviceSettings?.['rssi@1m'] || null;
			} else {
				const errorData = await response.text();
				toastStore.trigger({ message: `Error fetching device settings: ${errorData || response.statusText}`, background: 'preset-filled-error-500' });
			}
		} catch (error) {
			console.error(`Error fetching settings for device ${deviceSettings.id}:`, error);
			toastStore.trigger({ message: 'Error fetching device settings.', background: 'preset-filled-error-500' });
		}
	}

	// Function to fetch node settings
	async function fetchNodeSettings(nodeId: string) {
		try {
			const response = await fetch(apiPath(`/api/node/${nodeId}`));
			if (response.ok) {
				const data = await response.json();
				nodeSettings[nodeId] = data.settings;
				// Force reactivity by creating a new object reference
				nodeSettings = { ...nodeSettings };
			}
		} catch (error) {
			console.error(`Error fetching settings for node ${nodeId}:`, error);
		}
	}

	// Update the message handler to store all messages in an array
	function handleDeviceMessage(eventData: { deviceId: string; nodeId: string; data: DeviceMessage }) {
		if (eventData.deviceId === deviceSettings.id) {
			// Initialize array if it doesn't exist
			if (!deviceMessages[eventData.nodeId]) {
				deviceMessages[eventData.nodeId] = [];
			}

			// Add new message to the array
			deviceMessages[eventData.nodeId].push(eventData.data);

			// Limit number of stored messages to prevent memory issues
			if (deviceMessages[eventData.nodeId].length > 20) {
				deviceMessages[eventData.nodeId] = deviceMessages[eventData.nodeId].slice(-20);
			}

			// Force reactivity by creating a new object reference
			deviceMessages = { ...deviceMessages };
		}
	}

	onMount(async () => {
		try {
			showInstructions = localStorage.getItem('deviceCalibrationInstructions') === 'shown';
		} catch {
			// localStorage unavailable; leave instructions hidden
		}
		// Initialize currentRefRssi from deviceSettings
		currentRefRssi = deviceSettings?.['rssi@1m'] || null;
		if (deviceSettings?.id) {
			wsManager.subscribeToEvent('deviceMessage', handleDeviceMessage);
			wsManager.subscribeDeviceMessage(deviceSettings.id);
			console.log('Subscribed to device messages for', deviceSettings.id);
		}
		refreshCapture();
		captureInterval = setInterval(refreshCapture, 1000);
		refFetchStatus();
		refTimer = setInterval(refFetchStatus, 2000);
	});

	onDestroy(() => {
		if (captureInterval) clearInterval(captureInterval);
		if (refTimer) clearInterval(refTimer);
		if (positionDebounce) clearTimeout(positionDebounce);
		if (deviceSettings?.id) {
			wsManager.unsubscribeFromEvent('deviceMessage', handleDeviceMessage);
			wsManager.sendMessage({
				command: 'unsubscribe',
				type: 'deviceMessage',
				deviceId: deviceSettings.id
			});
			deviceMessages = {};
		}
	});

	// Calibration metrics and related reactive state
	let nodeDistances: { id: string; name: string; distance: number; nodeZ?: number }[] = [];
	let rssiValues: { [key: string]: number | null } = {};
	let includedNodes: { [key: string]: boolean } = {};
	let calculatedRefRssi: number | null = null;

	// Error handling adjusted for fetched settings
	$: if (deviceSettings?.error) {
		toastStore.trigger({ message: deviceSettings.error, background: 'preset-filled-error-500' });
	}

	// Reactive device and floor lookup
	$: device = $devices?.find((d: any) => d.id === deviceSettings.id);
	$: floor = $config?.floors.find((f: any) => f.id === selectedFloorId);
	$: bounds = floor?.bounds;

	// Initialize from device data when available
	$: if ($devices && deviceSettings?.id && !calibrationSpot) {
		const device = $devices?.find((d: any) => d.id === deviceSettings.id);
		if (device) {
			if (device.floor !== null) {
				selectedFloorId = device.floor.id;
			}
			if (device.location?.x != null && device.location?.y != null) {
				calibrationSpot = { x: device.location.x, y: device.location.y };
			}
		}
	}

	// Reset data on floor change
	$: if (selectedFloorId && calibrationSpot) {
		rssiValues = {};
		calculatedRefRssi = null;
	}

	// Update Z coordinate when height changes
	$: if (calibrationSpot && calibrationSpotHeight) {
		const floorLowerZ = bounds ? bounds[0][2] : 0;
		calibrationSpot.z = floorLowerZ + calibrationSpotHeight;
	}

	// Calculate node distances whenever calibration spot or floor changes
	$: nodeDistances = calculateNodeDistances(calibrationSpot, selectedFloorId, $nodes, bounds, calibrationSpotHeight);

	// Set default inclusion for nodes
	$: {
		// Fetch settings for each node
		nodeDistances.forEach((node) => {
			// Set default inclusion
			if (includedNodes[node.id] === undefined) {
				includedNodes[node.id] = true;
			}

			// Fetch node settings if not already fetched
			if (!nodeSettings[node.id]) {
				fetchNodeSettings(node.id);
			}
		});
	}

	// Update RSSI values using all available messages for each node
	$: if (nodeDistances.length > 0) {
		const newRssiValues: { [key: string]: number | null } = {};
		nodeDistances.forEach((node) => {
			if (node.id in deviceMessages && deviceMessages[node.id].length > 0) {
				// Use all messages for this node to calculate average RSSI
				const messages = deviceMessages[node.id];
				const validRssiValues = messages.map((msg) => msg.rssi).filter((rssi) => rssi !== null && rssi !== undefined) as number[];

				if (validRssiValues.length > 0) {
					// Calculate the average RSSI from all messages
					const avgRssi = validRssiValues.reduce((sum, val) => sum + val, 0) / validRssiValues.length;
					newRssiValues[node.id] = avgRssi;
				}
			}
		});
		if (Object.keys(newRssiValues).length > 0) {
			rssiValues = newRssiValues;
		}
	}

	// Calculate final RSSI using all device messages when enough data is collected
	$: if (Object.values(deviceMessages).some((msgs) => msgs.length >= 5)) {
		calculatedRefRssi = calculateFinalRssi();
	}

	// --- Helper functions ---

	// Calculate standard deviation helper function
	function calculateStdDev(values: number[]): number {
		if (values.length <= 1) return 0;

		const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
		const squaredDiffs = values.map((val) => Math.pow(val - mean, 2));
		const variance = squaredDiffs.reduce((sum, val) => sum + val, 0) / values.length;

		return Math.sqrt(variance);
	}

	function calculateNodeDistances(calibrationSpot: { x: number; y: number; z?: number } | null, selectedFloorId: string | null, nodes: any[] | undefined, bounds: any, calibrationSpotHeight: number) {
		if (!nodes || !calibrationSpot || !selectedFloorId) {
			return [];
		}
		// z can still be unset right after a calibration spot is first placed
		// (see anchorDevice()'s identical fallback) - falling back to undefined
		// here would silently NaN out every distance below.
		const spotZ = calibrationSpot.z ?? (bounds ? bounds[0][2] + calibrationSpotHeight : calibrationSpotHeight);
		return nodes
			.filter((node: any) => {
				return node.floors.includes(selectedFloorId) && node.location.x != null && node.location.y != null;
			})
			.map((node: any) => {
				// Use the relative heights for the z-component of the distance calculation
				const distance = Math.sqrt(Math.pow(node.location.x - calibrationSpot.x, 2) + Math.pow(node.location.y - calibrationSpot.y, 2) + Math.pow(node.location.z - spotZ, 2));

				const floorLowerZ = bounds ? bounds[0][2] : 0;
				const nodeHeightFromFloor = node.location.z - floorLowerZ;

				return {
					id: node.id,
					name: node.name || node.id,
					distance,
					nodeZ: nodeHeightFromFloor
				};
			});
	}

	function buildSettingsPayload(overrides: Partial<DeviceSetting & { x: number | null; y: number | null; z: number | null }>) {
		const { error, ...baseSettings } = deviceSettings ?? {};
		return {
			...baseSettings,
			...overrides
		};
	}

	async function anchorDevice() {
		if (!deviceSettings?.id && !deviceSettings?.originalId) return;
		if (!calibrationSpot || calibrationSpot.x == null || calibrationSpot.y == null) {
			toastStore.trigger({ message: 'Select a position on the map before anchoring.', background: 'preset-filled-error-500' });
			return;
		}

		const zValue = calibrationSpot.z ?? (bounds ? bounds[0][2] + calibrationSpotHeight : calibrationSpotHeight);
		const payload = buildSettingsPayload({
			x: calibrationSpot.x,
			y: calibrationSpot.y,
			z: zValue
		});

		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings?.originalId || deviceSettings?.id}`), {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(payload)
			});

			if (!response.ok) {
				throw new Error('Failed to anchor device');
			}

			deviceSettings = { ...deviceSettings, x: calibrationSpot.x, y: calibrationSpot.y, z: zValue };
			toastStore.trigger({ message: 'Device anchored to the selected location.', background: 'preset-filled-success-500' });
		} catch (error) {
			console.error('Error anchoring device:', error);
			toastStore.trigger({ message: 'Error anchoring device.', background: 'preset-filled-error-500' });
		}
	}

	async function clearAnchor() {
		if (!deviceSettings?.id && !deviceSettings?.originalId) return;
		const payload = buildSettingsPayload({ x: null, y: null, z: null });
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings?.originalId || deviceSettings?.id}`), {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(payload)
			});
			if (!response.ok) {
				throw new Error('Failed to clear anchor');
			}

			deviceSettings = { ...deviceSettings, x: null, y: null, z: null };
			toastStore.trigger({ message: 'Anchor removed. Device will return to automatic positioning.', background: 'preset-filled-success-500' });
		} catch (error) {
			console.error('Error clearing anchor:', error);
			toastStore.trigger({ message: 'Error clearing anchor.', background: 'preset-filled-error-500' });
		}
	}

	// --- Recording (capture for offline accuracy analysis) ---

	type CaptureStatus = { deviceId: string; active: boolean; count: number; positions: number; started: string; ended?: string; truncated: boolean };

	let capture: CaptureStatus | null = null;
	let captureInterval: ReturnType<typeof setInterval> | null = null;
	let lastSentPosition: string | null = null;

	$: exportUrl = deviceSettings?.id ? apiPath(`/api/device/${deviceSettings.id}/capture/export`) : '';

	async function refreshCapture() {
		if (!deviceSettings?.id) return;
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings.id}/capture`));
			capture = response.ok ? await response.json() : null;
		} catch {
			capture = null;
		}
	}

	async function startCapture() {
		if (!deviceSettings?.id) return;
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings.id}/capture/start`), { method: 'POST' });
			if (!response.ok) throw new Error(response.statusText);
			capture = await response.json();
			lastSentPosition = null;
			await sendCapturePosition();
		} catch (error) {
			console.error('Error starting capture:', error);
			toastStore.trigger({ message: 'Error starting recording.', background: 'preset-filled-error-500' });
		}
	}

	async function stopCapture() {
		if (!deviceSettings?.id) return;
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings.id}/capture/stop`), { method: 'POST' });
			if (response.ok) capture = await response.json();
		} catch (error) {
			console.error('Error stopping capture:', error);
		}
	}

	async function discardCapture() {
		if (!deviceSettings?.id) return;
		try {
			await fetch(apiPath(`/api/device/${deviceSettings.id}/capture`), { method: 'DELETE' });
			capture = null;
		} catch (error) {
			console.error('Error discarding capture:', error);
		}
	}

	async function sendCapturePosition() {
		if (!capture?.active || !deviceSettings?.id || !calibrationSpot || calibrationSpot.x == null || calibrationSpot.y == null) return;
		const z = calibrationSpot.z ?? (bounds ? bounds[0][2] + calibrationSpotHeight : calibrationSpotHeight);
		const position = { x: calibrationSpot.x, y: calibrationSpot.y, z, floor: selectedFloorId };
		const key = JSON.stringify(position);
		if (key === lastSentPosition) return;
		lastSentPosition = key;
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings.id}/capture/position`), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(position)
			});
			if (response.ok) capture = await response.json();
		} catch (error) {
			console.error('Error sending capture position:', error);
		}
	}

	// Record a ground-truth marker whenever the marker moves during recording.
	// Depends only on the marker (not `capture`), so status polling can't trigger
	// posts — otherwise every open tab would push its own stale marker position.
	// Debounced so dragging posts a sparse trail instead of one point per mousemove.
	let positionDebounce: ReturnType<typeof setTimeout> | null = null;
	$: queueCapturePosition(calibrationSpot, calibrationSpotHeight, selectedFloorId);
	function queueCapturePosition(..._deps: unknown[]) {
		if (!capture?.active) return;
		if (positionDebounce) clearTimeout(positionDebounce);
		positionDebounce = setTimeout(() => sendCapturePosition(), 300);
	}

	// Update this function to use only parameters from device messages
	let skippedUncalibrated: string[] = [];

	function calculateFinalRssi() {
		const refRssiEstimates: Array<{ refRssi: number; weight: number }> = [];
		skippedUncalibrated = [];

		// Calculate using all device messages
		Object.entries(deviceMessages).forEach(([nodeId, messages]) => {
			// Skip if node isn't included or doesn't have enough messages
			if (!includedNodes[nodeId] || messages.length < 5) return;

			// Find the associated node in nodeDistances
			const node = nodeDistances.find((n) => n.id === nodeId);
			if (!node || node.distance < 0.1) return;

			// Extract valid RSSI values
			const validRssiValues = messages.map((msg) => msg.rssi).filter((rssi) => rssi !== null && rssi !== undefined) as number[];

			if (validRssiValues.length < 5) return;

			// Calculate average RSSI
			const avgRssi = validRssiValues.reduce((sum, val) => sum + val, 0) / validRssiValues.length;

			// A node without a calibrated absorption is skipped, not substituted. The old code fell
			// back to 2 silently; measured on a real installation the nodes sit around 4.28, and the
			// correction term below is 10*absorption*log10(distance) - so that substitution was worth
			// 10.9 dB at 3 m and 15.9 dB at 5 m, applied without a word. A freshly added node lands in
			// exactly this case.
			const absorption = nodeSettings[nodeId]?.calibration?.absorption;
			if (absorption == null || absorption <= 0) {
				skippedUncalibrated.push(nodeId);
				return;
			}

			// The formula is RSSI@1m = RSSI + 10*n*log10(distance) where n is the path loss exponent.
			// Note how much of the answer this term carries: ~20 dB at 3 m, ~30 dB at 5 m. Any
			// systematic error in absorption transfers into rssi@1m one for one, which is why the
			// guided 1 m measurement exists - at 1 m log10(d) is 0 and absorption drops out entirely.
			const refRssi = avgRssi + 10 * absorption * Math.log10(node.distance);

			// Weight by inverse distance (closer nodes get higher weight)
			const weight = 1 / Math.max(1, node.distance);

			refRssiEstimates.push({ refRssi, weight });
		});

		// If no valid estimates, return null
		if (refRssiEstimates.length === 0) return null;

		// Median rather than a weighted mean: one node whose absorption is off drags a mean by its
		// full error, and the estimates being combined here are exactly the ones that disagree.
		const sorted = refRssiEstimates.map((e) => e.refRssi).sort((a, b) => a - b);
		const mid = Math.floor(sorted.length / 2);
		const rawValue = sorted.length % 2 === 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;

		return Math.round(rawValue);
	}

	function toggleNodeInclusion(nodeId: string) {
		includedNodes[nodeId] = !includedNodes[nodeId];
		includedNodes = { ...includedNodes }; // Trigger reactivity
	}

	$: messageStats = calculateMessageStats(deviceMessages);

	// Get message statistics for display
	function calculateMessageStats(deviceMessages: Record<string, DeviceMessage[]>) {
		const stats: Record<string, { count: number; avgRssi: number | null; minRssi: number | null; maxRssi: number | null; stdDev: number | null }> = {};

		Object.entries(deviceMessages).forEach(([nodeId, messages]) => {
			const validRssiValues = messages.map((msg) => msg.rssi).filter((rssi) => rssi !== null && rssi !== undefined) as number[];

			if (validRssiValues.length > 0) {
				const sum = validRssiValues.reduce((acc, val) => acc + val, 0);
				stats[nodeId] = {
					count: messages.length,
					avgRssi: sum / validRssiValues.length,
					minRssi: Math.min(...validRssiValues),
					maxRssi: Math.max(...validRssiValues),
					stdDev: validRssiValues.length > 1 ? calculateStdDev(validRssiValues) : null
				};
			} else {
				stats[nodeId] = {
					count: messages.length,
					avgRssi: null,
					minRssi: null,
					maxRssi: null,
					stdDev: null
				};
			}
		});

		return stats;
	}

	// ---------------------------------------------------------------------------------------------
	// Guided 1 m reference measurement.
	//
	// The map-based flow below derives rssi@1m from a distance, which means the answer rests on the
	// nodes' absorption rather than on the measurement: the correction term is ~20 dB at 3 m and
	// ~30 dB at 5 m. At one metre log10(d) is exactly 0, the term vanishes, and what is left is the
	// level itself. That is the whole reason this mode exists, and why it is the recommended one.
	// ---------------------------------------------------------------------------------------------
	interface RefNodeReading {
		nodeId: string;
		nodeName?: string;
		samples: number;
		medianLevelDbm: number;
		isReference: boolean;
	}
	interface RefStatus {
		running: boolean;
		deviceId?: string;
		referenceNodeId?: string;
		distanceM?: number;
		runs: number;
		estimatedRefRssi?: number;
		runSpreadDb?: number;
		trusted: boolean;
		warning?: string;
		contextNote?: string;
		nodes: RefNodeReading[];
	}

	let refStatus: RefStatus | null = null;
	let refNode: string = '';
	let refDistance = 1.0;
	let refBusy = false;
	let refTimer: ReturnType<typeof setInterval> | null = null;

	async function refFetchStatus() {
		try {
			const res = await fetch(apiPath('/api/wizard/device-setup/reference/status'));
			if (res.ok) refStatus = await res.json();
		} catch {
			// transient; the poll will try again
		}
	}

	async function refStart() {
		refBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/device-setup/reference/start'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({
					deviceId: deviceSettings.originalId ?? deviceSettings.id,
					referenceNodeId: refNode,
					distanceM: refDistance
				})
			});
			if (res.ok) refStatus = await res.json();
		} finally {
			refBusy = false;
		}
	}

	async function refFinish() {
		refBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/device-setup/reference/finish'), { method: 'POST' });
			if (res.ok) refStatus = await res.json();
		} finally {
			refBusy = false;
		}
	}

	async function refApply() {
		if (refStatus?.estimatedRefRssi == null) return;
		refBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/device-setup/apply'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({
					deviceId: deviceSettings.originalId ?? deviceSettings.id,
					refRssi: refStatus.estimatedRefRssi
				})
			});
			if (!res.ok) throw new Error(await res.text());
			currentRefRssi = refStatus.estimatedRefRssi;
			await fetchDeviceSettings();
			toastStore.trigger({
				message: `rssi@1m set to ${refStatus.estimatedRefRssi} dBm`,
				background: 'preset-filled-success-500'
			});
		} catch (e) {
			toastStore.trigger({
				message: `Could not save: ${(e as Error).message}`,
				background: 'preset-filled-error-500'
			});
		} finally {
			refBusy = false;
		}
	}

	async function saveCalibration() {
		if (!calculatedRefRssi) return;
		try {
			const response = await fetch(apiPath(`/api/device/${deviceSettings?.originalId || deviceSettings?.id}`), {
				method: 'PUT',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ ...deviceSettings, 'rssi@1m': calculatedRefRssi })
			});
			if (response.ok) {
				currentRefRssi = calculatedRefRssi;
				toastStore.trigger({
					message: 'Calibration saved successfully!',
					background: 'preset-filled-success-500'
				});
			} else if (response.status === 400) {
				const errorData = await response.json();
				throw new Error(errorData.error || 'Bad request. Please check your input.');
			} else {
				throw new Error('Error saving calibration. Please try again.');
			}
		} catch (e: unknown) {
			const error = e as Error;
			toastStore.trigger({ message: error.message, background: 'preset-filled-error-500' });
		}
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Device Calibration</title>
</svelte:head>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-3 space-y-4">
		<header class="flex items-center justify-between">
			<h1 class="text-2xl font-bold text-surface-900-100">Device Calibration</h1>
		</header>

		<div class="card preset-tonal">
			<button type="button" class="w-full flex items-center justify-between px-4 py-2" onclick={toggleInstructions} aria-expanded={showInstructions}>
				<span class="font-semibold">Instructions</span>
				<span class="text-sm opacity-70">{showInstructions ? 'Hide ▲' : 'Show ▼'}</span>
			</button>
			{#if showInstructions}
				<div class="px-4 pb-4">
					<p class="mb-2">This tool helps calibrate the RSSI@1m value for your device to improve location accuracy.</p>
					<ol class="list-decimal pl-6 mb-2">
						<li>Select a floor from the dropdown</li>
						<li>Place the marker where your device is physically located (drag to position)</li>
						<li>Data is automatically collected as you keep the device stationary</li>
						<li>Compare Map Distance (actual) with Est. Distance (calculated from RSSI)</li>
						<li>When stability is good, review and save the calculated value</li>
					</ol>
					<p class="text-sm font-medium mt-2">The closer Map Distance matches Est. Distance for all nodes, the more accurate your positioning will be.</p>
				</div>
			{/if}
		</div>

		<div class="card p-4">
			<header class="flex items-center justify-between mb-2">
				<h2 class="text-xl font-semibold">Reference measurement at 1 m</h2>
				<span class="badge preset-filled-primary-500">Recommended</span>
			</header>
			<p class="text-sm text-surface-600-400 mb-3">
				<code>rssi@1m</code> is a property of the transmitter, so it cannot come out of the node
				calibration - the nodes calibrate each other, and a new tag is a stranger to all of them. The
				map method further down derives it from a distance, which means the answer leans on the nodes'
				absorption: that correction term is around 20 dB at 3 m and 30 dB at 5 m, so a systematic
				absorption error passes straight through. At one metre the term is exactly zero and the
				measurement stands on its own.
			</p>

			<div class="flex flex-wrap items-end gap-3 mb-2">
				<label class="label text-sm">
					<span>Place the device next to</span>
					<select class="select" bind:value={refNode}>
						<option value="">Pick a node</option>
						{#each $nodes ?? [] as n (n.id)}
							<option value={n.id}>{n.name ?? n.id}</option>
						{/each}
					</select>
				</label>
				<label class="label text-sm w-28">
					<span>Distance (m)</span>
					<input class="input" type="number" step="0.05" bind:value={refDistance} />
				</label>
				{#if refStatus?.running}
					<button class="btn preset-filled-primary-500" onclick={refFinish} disabled={refBusy}>Finish run</button>
				{:else}
					<button class="btn preset-filled-primary-500" onclick={refStart} disabled={refBusy || !refNode}>Start run</button>
				{/if}
			</div>
			<p class="text-xs text-surface-600-400 mb-3">
				Put the device down and step away rather than holding it: repeat runs scattered 4.4 dB when
				held and 1.6 dB when left lying. Two runs that agree are needed before the value is treated
				as trustworthy.
			</p>

			{#if refStatus}
				<div class="text-sm mb-2">
					{#if refStatus.estimatedRefRssi != null}
						<strong>{refStatus.estimatedRefRssi} dBm</strong>
						<span class="text-surface-600-400">
							after {refStatus.runs} run{refStatus.runs === 1 ? '' : 's'}{refStatus.runs > 1 && refStatus.runSpreadDb != null ? `, spread ${refStatus.runSpreadDb} dB` : ''}
						</span>
						<span class="badge {refStatus.trusted ? 'preset-filled-success-500' : 'preset-filled-warning-500'} ml-2">
							{refStatus.trusted ? 'trustworthy' : 'provisional'}
						</span>
						{#if currentRefRssi != null}
							<span class="text-surface-600-400 ml-2">(currently {currentRefRssi} dBm)</span>
						{/if}
						{#if refStatus.trusted}
							<button class="btn btn-sm preset-filled-primary-500 ml-3" onclick={refApply} disabled={refBusy}>Apply</button>
						{/if}
					{:else if refStatus.running}
						<span class="text-surface-600-400">Collecting readings...</span>
					{/if}
				</div>
				{#if refStatus.warning}<p class="text-sm text-warning-600-400 mb-2">{refStatus.warning}</p>{/if}
				{#if refStatus.contextNote}<p class="text-sm text-warning-600-400 mb-2">{refStatus.contextNote}</p>{/if}
				{#if refStatus.nodes.length > 0}
					<div class="overflow-x-auto overflow-y-auto max-h-48">
						<table class="table table-compact">
							<thead><tr><th>Node</th><th>Readings</th><th>Level</th></tr></thead>
							<tbody>
								{#each refStatus.nodes as n (n.nodeId)}
									<tr class={n.isReference ? 'font-semibold' : ''}>
										<td>{n.nodeName ?? n.nodeId}{n.isReference ? ' (reference)' : ''}</td>
										<td>{n.samples}</td>
										<td>{n.medianLevelDbm.toFixed(1)} dBm</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				{/if}
			{/if}
		</div>

		<header class="pt-2">
			<h2 class="text-xl font-semibold">Measure from a known spot on the map</h2>
			<p class="text-sm text-surface-600-400">
				Fallback for devices that cannot be picked up and put next to a node - a beacon screwed to a
				wall, for instance. Only nodes with a calibrated absorption are used; the rest are listed and
				skipped rather than filled in with a guess.
			</p>
		</header>

		{#if $config?.floors}
			<div class="grid grid-cols-1 gap-4">
				<div>
					<label class="label font-medium mb-1" for="floor-select">Select Floor</label>
					<select id="floor-select" bind:value={selectedFloorId} class="select w-full">
						{#each $config?.floors as { id, name }}
							<option value={id}>{name}</option>
						{/each}
					</select>
				</div>

				{#if calibrationSpot}
					<div>
						<label class="label font-medium mb-1" for="height-input">Height from Floor (m)</label>
						<div class="input-group input-group-divider grid-cols-[1fr_auto]">
							<input id="height-input" type="number" min="0" max="5" step="0.1" bind:value={calibrationSpotHeight} class="input rounded-r-none" />
							<button type="button" class="btn preset-filled-primary-500 rounded-l-none" onclick={() => (calibrationSpotHeight = calibrationSpotHeight)}>Set</button>
						</div>
					</div>
				{/if}
			</div>
		{/if}

		{#if selectedFloorId}
			<div class="card h-[400px] relative overflow-hidden">
				<Map floorId={selectedFloorId} deviceId={device?.id} exclusive={true} calibrate={true} bind:calibrationSpot />
			</div>
			<div class="flex flex-wrap items-center justify-between gap-3">
				<div class="text-sm text-surface-700-300">
					{#if isAnchored}
						<span class="text-success-500 font-medium">Anchored</span>
						<span>
							@ ({deviceSettings?.x?.toFixed(2) ?? '-'}, {deviceSettings?.y?.toFixed(2) ?? '-'}, {deviceSettings?.z?.toFixed(2) ?? '-'})
						</span>
					{:else}
						<span>Device is not anchored. Position updates will use auto-location.</span>
					{/if}
				</div>
				<div class="flex gap-2">
					<button class="btn preset-filled-primary-500" onclick={anchorDevice} disabled={!calibrationSpot}>Anchor here</button>
					{#if isAnchored}
						<button class="btn preset-filled-error-500" onclick={clearAnchor}>Remove anchor</button>
					{/if}
				</div>
			</div>

			<div class="card p-4 preset-tonal">
				<header class="font-semibold mb-2">Recording</header>
				<p class="text-sm mb-3">Records this device's raw node measurements along with the marker position as ground truth. Keep the marker on the device's actual location — move it whenever the device moves.</p>
				<div class="flex flex-wrap items-center gap-2">
					{#if capture?.active}
						<span class="text-sm font-medium text-success-500">Recording… {capture.count} messages, {capture.positions} positions</span>
						<button class="btn preset-filled-error-500" onclick={stopCapture}>Stop</button>
					{:else}
						<button class="btn preset-filled-primary-500" onclick={startCapture} disabled={!calibrationSpot}>Start recording</button>
						{#if capture && capture.count > 0}
							<span class="text-sm font-medium">{capture.count} messages, {capture.positions} positions captured</span>
						{/if}
					{/if}
					{#if capture && capture.count > 0}
						<a class="btn preset-filled-secondary-500" href={exportUrl} download>Export JSON</a>
						{#if !capture.active}
							<button class="btn preset-filled-surface-500" onclick={discardCapture}>Discard</button>
						{/if}
					{/if}
				</div>
				{#if capture?.truncated}
					<p class="text-sm text-warning-500 mt-2">Capture truncated (message limit reached)</p>
				{/if}
			</div>
		{/if}

		{#if calibrationSpot}
			<div class="grid grid-cols-1 gap-4 lg:grid-cols-3">
				<div class="card p-4 preset-tonal lg:col-span-2">
					<header class="text-xl font-semibold mb-2">Node Distances and RSSI Values</header>
					<p class="text-sm mb-3">
						<span class="font-semibold">Map Distance:</span> Calculated from node and calibration spot positions in 3D space (X, Y, and Z).<br />
						<span class="font-semibold">Est. Distance:</span> Estimated from RSSI using current calibration settings.<br />
						<span class="font-semibold">Height from Floor:</span> The Z-coordinate (height) of the node from the floor.
					</p>
					<div class="table-container">
						<table class="table table-compact">
							<thead>
								<tr>
									<th>Node</th>
									<th>Height from Floor (m)</th>
									<th>Map Distance (m)</th>
									<th>Est. Distance (m)</th>
									<th>RSSI (dBm)</th>
									<th>Est. RSSI@1m</th>
									<th>Include</th>
								</tr>
							</thead>
							<tbody>
								{#each nodeDistances as node}
									<tr>
										<td>{node.name}</td>
										<td>{node.nodeZ?.toFixed(1) || 'n/a'}</td>
										<td>{node.distance?.toFixed(1) || 'n/a'}</td>
										<td>
											{#if rssiValues[node.id] != null && currentRefRssi != null}
												{#if nodeSettings[node.id]?.calibration?.absorption != null}
													{Math.pow(10, (currentRefRssi - (rssiValues[node.id] || 0)) / (10 * (nodeSettings[node.id]?.calibration?.absorption || 2))).toFixed(1)}
												{:else}
													{Math.pow(10, (currentRefRssi - (rssiValues[node.id] || 0)) / 20).toFixed(1)}
												{/if}
											{:else}
												n/a
											{/if}
										</td>
										<td>{rssiValues[node.id] != null ? rssiValues[node.id]?.toFixed(1) : 'n/a'}</td>
										<td>
											{#if rssiValues[node.id] != null && node.distance != null && node.distance > 0.1}
												{((rssiValues[node.id] || 0) + 10 * (nodeSettings[node.id]?.calibration?.absorption || 2) * Math.log10(node.distance)).toFixed(1)}
											{:else}
												n/a
											{/if}
										</td>
										<td>
											<input type="checkbox" checked={includedNodes[node.id] || false} onchange={() => toggleNodeInclusion(node.id)} class="checkbox" />
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>

					<!-- Device Message Statistics Table -->
					<header class="text-xl font-semibold mb-2 mt-6">Device Message Statistics</header>
					<div class="table-container">
						<table class="table table-compact">
							<thead>
								<tr>
									<th>Node</th>
									<th>Messages Count</th>
									<th>Avg RSSI (dBm)</th>
									<th>Min RSSI (dBm)</th>
									<th>Max RSSI (dBm)</th>
									<th>Std Deviation</th>
								</tr>
							</thead>
							<tbody>
								{#each Object.entries(messageStats) as [nodeId, stats]}
									{@const node = nodeDistances.find((n) => n.id === nodeId)}
									<tr>
										<td>{node?.name || nodeId}</td>
										<td>{stats.count}</td>
										<td>{stats.avgRssi != null ? stats.avgRssi.toFixed(1) : 'n/a'}</td>
										<td>{stats.minRssi != null ? stats.minRssi.toFixed(1) : 'n/a'}</td>
										<td>{stats.maxRssi != null ? stats.maxRssi.toFixed(1) : 'n/a'}</td>
										<td>{stats.stdDev != null ? stats.stdDev.toFixed(2) : 'n/a'}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				</div>

				<div class="col-span-1 space-y-4">
					<div class="card p-4 preset-tonal">
						<header class="font-semibold mb-2">Data Collection Status</header>
						<div class="mt-4">
							<div class="flex justify-between mb-1">
								<span>Total Messages:</span>
								<span class="font-medium">{Object.values(deviceMessages).reduce((sum, msgs) => sum + msgs.length, 0)}</span>
							</div>
							<div class="progress h-2">
								<div class="progress-bar bg-primary-500" style="width: {Math.min(100, Object.values(deviceMessages).reduce((sum, msgs) => sum + msgs.length, 0) / 2)}%"></div>
							</div>
						</div>
						<p class="mt-4 text-sm">Keep the device stationary for best results.</p>
					</div>
					<div class="card p-4 preset-tonal">
						<header class="font-semibold mb-4">Calibration Results</header>
						{#if skippedUncalibrated.length > 0}
							<p class="text-sm text-warning-600-400 mb-3">
								Skipped {skippedUncalibrated.length} node{skippedUncalibrated.length === 1 ? '' : 's'} with no
								calibrated absorption ({skippedUncalibrated.join(', ')}). Their contribution would have been a
								guess: substituting a default of 2 where the fleet sits near 4.3 is worth about 11 dB at 3 m.
								Run the node calibration first, or use the 1 m measurement above, which does not need absorption
								at all.
							</p>
						{/if}
						<div class="grid grid-cols-1 gap-4 mb-4">
							<div class="card p-4 preset-tonal">
								<header class="font-semibold mb-2">Current Values</header>
								<p class="text-xl font-bold">
									RSSI@1m: {currentRefRssi != null ? Math.round(currentRefRssi) : 'n/a'} dBm
								</p>
							</div>
							<div class="card p-4 preset-filled-primary-500">
								<header class="font-semibold mb-2">New Values</header>
								<p class="text-xl font-bold">
									RSSI@1m: {calculatedRefRssi != null ? calculatedRefRssi : 'n/a'} dBm
								</p>
							</div>
						</div>
						{#if currentRefRssi != null && calculatedRefRssi != null}
							<div class="card p-4 preset-tonal-warning border border-warning-500 mb-4">
								<p>
									This is a <span class="font-semibold">{Math.abs(calculatedRefRssi - Math.round(currentRefRssi))} dBm</span>
									{calculatedRefRssi > currentRefRssi ? 'increase' : 'decrease'}.
								</p>
								<p>This change will affect how distances are calculated for this device.</p>
							</div>
						{/if}
						<button class="btn btn-lg preset-filled-primary-500 w-full" onclick={saveCalibration} disabled={calculatedRefRssi == null || currentRefRssi === calculatedRefRssi}> Accept New Calibration </button>
					</div>
				</div>
			</div>
		{/if}
	</div>
</div>
