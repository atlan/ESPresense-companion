<script lang="ts">
	import { onMount, onDestroy } from 'svelte';
	import { getToastStore } from '$lib/toast/toastStore';
	import { calibration } from '$lib/stores';
	import { apiPath } from '$lib/api';
	import { showConfirm } from '$lib/modal/modalStore';

	const toastStore = getToastStore();

	interface ValidationIssue {
		severity: 'Info' | 'Warning' | 'Error';
		category: string;
		message: string;
		floorId?: string;
		roomId?: string;
		nodeId?: string;
	}

	interface HealthNode {
		id: string;
		name?: string;
		online: boolean;
		version?: string;
		telemetryAgeSecs?: number;
		stale: boolean;
	}

	interface HealthResult {
		passed: boolean;
		nodes: HealthNode[];
		offlineNodes: string[];
		staleNodes: string[];
		firmwareVersions: string[];
	}

	interface PairSuggestion {
		nodeA: string;
		nodeB: string;
		nodeAName?: string;
		nodeBName?: string;
		avgAbsPercentError: number;
		aboveThresholdFraction: number;
		samples: number;
		observedHours: number;
		pairId: string;
	}

	interface WalkTestNodeLive {
		nodeId: string;
		nodeName?: string;
		samples: number;
		medianDistance: number;
		mapDistance?: number;
		percentError?: number;
	}

	interface WalkTestActive {
		deviceId: string;
		deviceName?: string;
		x: number;
		y: number;
		z: number;
		floorId?: string;
		elapsedSecs: number;
		remainingSecs: number;
		totalSamples: number;
		nodes: WalkTestNodeLive[];
	}

	interface WalkTestPointNode {
		nodeId: string;
		nodeName?: string;
		samples: number;
		medianDistance: number;
		mapDistance: number;
	}

	interface WalkTestPoint {
		id: string;
		deviceId: string;
		deviceName?: string;
		x: number;
		y: number;
		z: number;
		floorId?: string;
		recordedAt: string;
		nodes: WalkTestPointNode[];
	}

	interface WalkTestStatus {
		active: WalkTestActive | null;
		devices: { id: string; name?: string }[];
		points: WalkTestPoint[];
		defaultDurationSecs: number;
	}

	interface WalkPointSuggestion {
		x: number;
		y: number;
		z: number;
		floorId?: string;
		floorName?: string;
		reason: string;
		nodeA: string;
		nodeB: string;
		pairErrorPercent: number;
	}

	let validation: { issues: ValidationIssue[]; hasErrors: boolean; hasWarnings: boolean } | null = null;
	let health: HealthResult | null = null;
	let suggestions: PairSuggestion[] = [];
	let currentlyExcluded: string[] = [];
	let loading = true;
	let calibrateBusy = false;
	let pairBusy: Record<string, boolean> = {};
	let refreshTimer: ReturnType<typeof setInterval> | null = null;
	let walkTimer: ReturnType<typeof setInterval> | null = null;

	let walkStatus: WalkTestStatus | null = null;
	let walkSuggestions: WalkPointSuggestion[] = [];
	let wtDevice = '';
	let wtX: number | null = null;
	let wtY: number | null = null;
	let wtZ: number | null = null;
	let wtDuration = 120;
	let wtBusy = false;

	async function fetchAll() {
		try {
			const [vRes, hRes, sRes, wRes, wsRes] = await Promise.all([
				fetch(apiPath('/api/wizard/validation')),
				fetch(apiPath('/api/wizard/health')),
				fetch(apiPath('/api/wizard/excluded-pairs/suggestions')),
				fetch(apiPath('/api/wizard/walktest/status')),
				fetch(apiPath('/api/wizard/walktest/suggest'))
			]);
			if (vRes.ok) validation = await vRes.json();
			if (hRes.ok) health = await hRes.json();
			if (sRes.ok) {
				const data = await sRes.json();
				suggestions = data.suggestions ?? [];
				currentlyExcluded = data.currentlyExcludedFriendly ?? data.currentlyExcluded ?? [];
			}
			if (wRes.ok) {
				walkStatus = await wRes.json();
				if (!wtDevice && walkStatus?.devices?.length) wtDevice = walkStatus.devices[0].id;
			}
			if (wsRes.ok) {
				const data = await wsRes.json();
				walkSuggestions = data.suggestions ?? [];
			}
		} catch (error) {
			console.error('Error fetching wizard data:', error);
		} finally {
			loading = false;
		}
	}

	async function fetchWalkStatus() {
		try {
			const res = await fetch(apiPath('/api/wizard/walktest/status'));
			if (res.ok) walkStatus = await res.json();
		} catch (error) {
			console.error('Error fetching walk test status:', error);
		}
	}

	async function startWalkTest() {
		if (wtBusy || !wtDevice || wtX == null || wtY == null || wtZ == null) return;
		wtBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/walktest/start'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ deviceId: wtDevice, x: wtX, y: wtY, z: wtZ, durationSecs: wtDuration })
			});
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({ message: 'Walk test started - keep the device in place', background: 'preset-filled-success-500' });
			await fetchWalkStatus();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to start walk test',
				background: 'preset-filled-error-500'
			});
		} finally {
			wtBusy = false;
		}
	}

	async function stopWalkTest(cancel: boolean) {
		if (wtBusy) return;
		wtBusy = true;
		try {
			const res = await fetch(apiPath(cancel ? '/api/wizard/walktest/cancel' : '/api/wizard/walktest/stop'), { method: 'POST' });
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({
				message: cancel ? 'Walk test cancelled' : 'Walk test point recorded',
				background: cancel ? 'preset-filled-surface-500' : 'preset-filled-success-500'
			});
			await fetchWalkStatus();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Walk test action failed',
				background: 'preset-filled-error-500'
			});
		} finally {
			wtBusy = false;
		}
	}

	async function deleteWalkPoint(id: string) {
		try {
			const res = await fetch(apiPath(`/api/wizard/walktest/points/${id}`), { method: 'DELETE' });
			if (!res.ok) throw new Error(`HTTP ${res.status}`);
			await fetchWalkStatus();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to delete point',
				background: 'preset-filled-error-500'
			});
		}
	}

	function useSuggestion(s: WalkPointSuggestion) {
		wtX = s.x;
		wtY = s.y;
		wtZ = s.z;
	}

	async function calibrateNow() {
		if (calibrateBusy) return;
		calibrateBusy = true;
		try {
			const response = await fetch(apiPath('/api/wizard/calibrate-now'), { method: 'POST' });
			if (!response.ok) {
				const err = await response.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${response.status}`);
			}
			toastStore.trigger({
				message: 'Calibration cycle triggered - watch Best R / Best RMSE update below',
				background: 'preset-filled-success-500'
			});
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to trigger calibration',
				background: 'preset-filled-error-500'
			});
		} finally {
			calibrateBusy = false;
		}
	}

	async function excludePair(s: PairSuggestion) {
		const confirmed = await showConfirm({
			title: 'Exclude pair from calibration',
			body: `Exclude "${s.nodeAName ?? s.nodeA}" ↔ "${s.nodeBName ?? s.nodeB}" from calibration fitting? Its persistent ${(s.avgAbsPercentError * 100).toFixed(0)}% distance error suggests an RF obstruction between them that would otherwise distort both nodes' calibration.`
		});
		if (!confirmed) return;

		pairBusy[s.pairId] = true;
		try {
			const response = await fetch(apiPath('/api/wizard/excluded-pairs'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify([s.pairId])
			});
			if (!response.ok) {
				const err = await response.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${response.status}`);
			}
			toastStore.trigger({
				message: `Pair ${s.pairId} excluded from calibration`,
				background: 'preset-filled-success-500'
			});
			await fetchAll();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to exclude pair',
				background: 'preset-filled-error-500'
			});
		} finally {
			pairBusy[s.pairId] = false;
		}
	}

	function severityClass(sev: string): string {
		switch (sev) {
			case 'Error':
				return 'preset-filled-error-500';
			case 'Warning':
				return 'preset-filled-warning-500';
			default:
				return 'preset-filled-surface-500';
		}
	}

	function ageLabel(secs?: number): string {
		if (secs == null) return 'never';
		if (secs < 90) return `${Math.round(secs)}s ago`;
		return `${Math.round(secs / 60)}min ago`;
	}

	onMount(() => {
		fetchAll();
		refreshTimer = setInterval(fetchAll, 15000);
		// Faster poll for the walk test progress while a session runs
		walkTimer = setInterval(() => {
			if (walkStatus?.active) fetchWalkStatus();
		}, 3000);
	});

	onDestroy(() => {
		if (refreshTimer) clearInterval(refreshTimer);
		if (walkTimer) clearInterval(walkTimer);
	});
</script>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-2 space-y-6">
		{#if loading}
			<p class="text-surface-600-400">Loading setup checks...</p>
		{:else}
			<!-- 1. Health gate -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Node Health</h2>
					{#if health}
						<span class="badge {health.passed ? 'preset-filled-success-500' : 'preset-filled-warning-500'}">
							{health.passed ? 'All good' : 'Attention needed'}
						</span>
					{/if}
				</header>
				{#if health}
					{#if health.offlineNodes.length > 0}
						<p class="text-error-500 text-sm mb-1">Offline: {health.offlineNodes.join(', ')}</p>
					{/if}
					{#if health.staleNodes.length > 0}
						<p class="text-warning-500 text-sm mb-1">Online but no recent telemetry (possibly stuck): {health.staleNodes.join(', ')}</p>
					{/if}
					{#if health.firmwareVersions.length > 1}
						<p class="text-warning-500 text-sm mb-1">Mixed firmware versions: {health.firmwareVersions.join(' / ')}</p>
					{/if}
					{#if health.passed}
						<p class="text-sm text-surface-600-400">{health.nodes.length} nodes online, telemetry fresh, single firmware version{health.firmwareVersions.length === 1 ? ` (${health.firmwareVersions[0]})` : ''}.</p>
					{:else}
						<div class="overflow-x-auto mt-2">
							<table class="table table-compact">
								<thead>
									<tr><th>Node</th><th>Online</th><th>Telemetry</th><th>Version</th></tr>
								</thead>
								<tbody>
									{#each health.nodes.filter((n) => !n.online || n.stale) as n (n.id)}
										<tr>
											<td>{n.name ?? n.id}</td>
											<td>{n.online ? 'yes' : 'NO'}</td>
											<td>{ageLabel(n.telemetryAgeSecs)}</td>
											<td>{n.version ?? '-'}</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					{/if}
				{/if}
			</div>

			<!-- 2. Validation issues -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Configuration Checks</h2>
					{#if validation}
						<span class="badge {validation.issues.length === 0 ? 'preset-filled-success-500' : validation.hasErrors ? 'preset-filled-error-500' : 'preset-filled-warning-500'}">
							{validation.issues.length === 0 ? 'No issues' : `${validation.issues.length} issue${validation.issues.length === 1 ? '' : 's'}`}
						</span>
					{/if}
				</header>
				{#if validation}
					{#if validation.issues.length === 0}
						<p class="text-sm text-surface-600-400">Floor bounds, room polygons and node placements all look consistent.</p>
					{:else}
						<ul class="space-y-2">
							{#each validation.issues as issue}
								<li class="flex items-start gap-2">
									<span class="badge {severityClass(issue.severity)} shrink-0 mt-0.5">{issue.severity}</span>
									<span class="text-sm">{issue.message}</span>
								</li>
							{/each}
						</ul>
					{/if}
				{/if}
			</div>

			<!-- 3. Calibrate now -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Calibration</h2>
					<button class="btn preset-filled-primary-500" onclick={calibrateNow} disabled={calibrateBusy}>
						{calibrateBusy ? 'Triggering...' : 'Calibrate now'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">Runs a fit cycle immediately instead of waiting for the next scheduled interval. Useful right after moving a node or changing its coordinates.</p>
				{#if $calibration?.optimizerState}
					<div class="grid grid-cols-2 md:grid-cols-4 gap-3">
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-primary-500">{$calibration?.r?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">Current R</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-primary-500">{$calibration?.rmse?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">Current RMSE</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-success-500">{$calibration?.optimizerState?.bestR?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">Best R</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-success-500">{$calibration?.optimizerState?.bestRMSE?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">Best RMSE</div>
						</div>
					</div>
				{/if}
			</div>

			<!-- 4. Excluded pair suggestions -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Problem Pair Suggestions</h2>
					<span class="badge {suggestions.length === 0 ? 'preset-filled-success-500' : 'preset-filled-warning-500'}">
						{suggestions.length === 0 ? 'None' : suggestions.length}
					</span>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Same-floor node pairs whose distance error stays persistently high - usually an RF obstruction (wall, appliance) between them. Pairs only appear after at least 2 hours of observation with the error above threshold most of that time, so post-move calibration transients don't trigger false suggestions; moving a node resets its pairs' statistics.
				</p>
				{#if suggestions.length === 0}
					<p class="text-sm text-surface-600-400">No persistently bad pairs detected (pairs need 2h+ of consistently high error to appear here).</p>
				{:else}
					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr><th>Pair</th><th>Avg error</th><th>Bad</th><th>Observed</th><th></th></tr>
							</thead>
							<tbody>
								{#each suggestions as s (s.pairId)}
									<tr>
										<td>{s.nodeAName ?? s.nodeA} ↔ {s.nodeBName ?? s.nodeB}</td>
										<td>{(s.avgAbsPercentError * 100).toFixed(0)}%</td>
										<td>{(s.aboveThresholdFraction * 100).toFixed(0)}% of time</td>
										<td>{s.observedHours < 48 ? `${s.observedHours.toFixed(1)}h` : `${(s.observedHours / 24).toFixed(1)}d`}</td>
										<td>
											<button class="btn btn-sm preset-filled-warning-500" onclick={() => excludePair(s)} disabled={pairBusy[s.pairId]}>
												Exclude
											</button>
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
				{/if}
				{#if currentlyExcluded.length > 0}
					<p class="text-xs text-surface-600-400 mt-3">Currently excluded: {currentlyExcluded.join(', ')}</p>
				{/if}
			</div>

			<!-- 5. Walk test -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Walk Test</h2>
					{#if walkStatus?.active}
						<span class="badge preset-filled-primary-500">Running</span>
					{/if}
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Place a tracked device at a known position and record its signal for a couple of minutes. Each recorded point acts as an extra reference transmitter with known coordinates for the calibration optimizer - node-to-node links alone give it only a handful of fixed distances to learn from.
				</p>

				{#if walkStatus?.active}
					{@const a = walkStatus.active}
					<div class="mb-3">
						<p class="text-sm mb-2">
							Recording <strong>{a.deviceName ?? a.deviceId}</strong> at ({a.x}, {a.y}, {a.z}) -
							{Math.round(a.remainingSecs)}s remaining, {a.totalSamples} samples
						</p>
						<div class="overflow-x-auto mb-3">
							<table class="table table-compact">
								<thead>
									<tr><th>Node</th><th>Samples</th><th>Measured</th><th>Map</th><th>Error</th></tr>
								</thead>
								<tbody>
									{#each a.nodes as n (n.nodeId)}
										<tr>
											<td>{n.nodeName ?? n.nodeId}</td>
											<td>{n.samples}</td>
											<td>{n.medianDistance.toFixed(2)}m</td>
											<td>{n.mapDistance != null ? n.mapDistance.toFixed(2) + 'm' : '-'}</td>
											<td>{n.percentError != null ? (n.percentError * 100).toFixed(0) + '%' : '-'}</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
						<div class="flex gap-2">
							<button class="btn preset-filled-success-500" onclick={() => stopWalkTest(false)} disabled={wtBusy}>Finish now</button>
							<button class="btn preset-filled-surface-500" onclick={() => stopWalkTest(true)} disabled={wtBusy}>Cancel</button>
						</div>
					</div>
				{:else}
					{#if walkSuggestions.length > 0}
						<div class="mb-3">
							<p class="text-sm font-semibold mb-1">Suggested placements (worst-calibrated pairs first):</p>
							<ul class="space-y-1">
								{#each walkSuggestions as s}
									<li class="flex items-center gap-2 text-sm">
										<button class="btn btn-sm preset-tonal" onclick={() => useSuggestion(s)}>({s.x}, {s.y}, {s.z})</button>
										<span class="text-surface-600-400">{s.reason}</span>
									</li>
								{/each}
							</ul>
						</div>
					{/if}
					<div class="flex flex-wrap items-end gap-3 mb-3">
						<label class="label text-sm">
							<span>Device</span>
							<select class="select" bind:value={wtDevice}>
								{#each walkStatus?.devices ?? [] as d (d.id)}
									<option value={d.id}>{d.name ?? d.id}</option>
								{/each}
							</select>
						</label>
						<label class="label text-sm w-24">
							<span>X</span>
							<input class="input" type="number" step="0.1" bind:value={wtX} />
						</label>
						<label class="label text-sm w-24">
							<span>Y</span>
							<input class="input" type="number" step="0.1" bind:value={wtY} />
						</label>
						<label class="label text-sm w-24">
							<span>Z</span>
							<input class="input" type="number" step="0.1" bind:value={wtZ} />
						</label>
						<label class="label text-sm w-28">
							<span>Duration (s)</span>
							<input class="input" type="number" min="30" max="900" bind:value={wtDuration} />
						</label>
						<button class="btn preset-filled-primary-500" onclick={startWalkTest} disabled={wtBusy || !wtDevice || wtX == null || wtY == null || wtZ == null}>
							Start
						</button>
					</div>
					<p class="text-xs text-surface-600-400 mb-3">Place the device FIRST, then press Start. Coordinates are in map meters (same as node positions); Z is the absolute height including the floor offset.</p>
				{/if}

				{#if (walkStatus?.points ?? []).length > 0}
					<p class="text-sm font-semibold mb-1">Recorded points (feeding the optimizer):</p>
					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr><th>Point</th><th>Device</th><th>Position</th><th>Nodes</th><th>Recorded</th><th></th></tr>
							</thead>
							<tbody>
								{#each walkStatus?.points ?? [] as p (p.id)}
									<tr>
										<td>{p.id}</td>
										<td>{p.deviceName ?? p.deviceId}</td>
										<td>({p.x}, {p.y}, {p.z})</td>
										<td>{p.nodes.length}</td>
										<td>{new Date(p.recordedAt).toLocaleTimeString()}</td>
										<td>
											<button class="btn btn-sm preset-filled-surface-500" onclick={() => deleteWalkPoint(p.id)}>Delete</button>
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">Points live in memory - an app restart clears them. Points whose receiving node was moved afterwards are ignored automatically.</p>
				{/if}
			</div>
		{/if}
	</div>
</div>
