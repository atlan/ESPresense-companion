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

	let validation: { issues: ValidationIssue[]; hasErrors: boolean; hasWarnings: boolean } | null = null;
	let health: HealthResult | null = null;
	let suggestions: PairSuggestion[] = [];
	let currentlyExcluded: string[] = [];
	let loading = true;
	let calibrateBusy = false;
	let pairBusy: Record<string, boolean> = {};
	let refreshTimer: ReturnType<typeof setInterval> | null = null;

	async function fetchAll() {
		try {
			const [vRes, hRes, sRes] = await Promise.all([
				fetch(apiPath('/api/wizard/validation')),
				fetch(apiPath('/api/wizard/health')),
				fetch(apiPath('/api/wizard/excluded-pairs/suggestions'))
			]);
			if (vRes.ok) validation = await vRes.json();
			if (hRes.ok) health = await hRes.json();
			if (sRes.ok) {
				const data = await sRes.json();
				suggestions = data.suggestions ?? [];
				currentlyExcluded = data.currentlyExcluded ?? [];
			}
		} catch (error) {
			console.error('Error fetching wizard data:', error);
		} finally {
			loading = false;
		}
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
	});

	onDestroy(() => {
		if (refreshTimer) clearInterval(refreshTimer);
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
		{/if}
	</div>
</div>
