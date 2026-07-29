<script lang="ts">
	import { gotoMapSpot } from '$lib/urls';
	import { onMount, onDestroy } from 'svelte';
	import { getToastStore } from '$lib/toast/toastStore';
	import { calibration } from '$lib/stores';
	import { apiPath } from '$lib/api';
	import { showConfirm } from '$lib/modal/modalStore';

	const toastStore = getToastStore();

	interface ConflictingWalkPair {
		idA: string; idB: string;
		recordedAtA: string; recordedAtB: string;
		floorId?: string | null; floorName?: string | null; roomName?: string | null;
		x: number; y: number; z: number;
		distanceM: number; sharedNodes: number;
		medianDeltaDb: number; medianGeometryDb: number; medianResidualDb: number;
		maxDeltaDb: number; maxDeltaNode?: string | null;
		irreconcilable: boolean; explainableDb: number;
	}

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
		rawTicks?: number;
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
		roomId?: string;
		roomName?: string;
		nearestNodeM: number;
		medianNearestNodeM: number;
		existingPoints: number;
		floorHitRate?: number;
		score: number;
		reason: string;
	}

	interface RoomCoverage {
		floorId: string;
		roomName?: string;
		medianNearestNodeM: number;
		worstNearestNodeM: number;
		wellCoveredFraction: number;
	}

	interface Diagnostics {
		issues: ValidationIssue[];
		near: { pairs: number; medianAbsRssiErrorDb?: number };
		far: { pairs: number; medianAbsRssiErrorDb?: number };
		nearFarSplitM: number;
		roomCoverage: RoomCoverage[];
		signalOutliers: { rxName?: string; rxId: string; txName?: string; txId: string; mapDistanceM: number; deltaDb: number }[];
		clampedParameters: { nodeName?: string; nodeId: string; parameter: string; value: number; bound: string; limit: number }[];
		staleWalkPoints?: {
			id: string;
			floorId?: string;
			floorName?: string;
			roomName?: string;
			x: number;
			y: number;
			z: number;
			ticks: number;
			recordedAt: string;
		}[];
		conflictingWalkPairs?: ConflictingWalkPair[];
	}

	interface LocatorRun {
		label: string;
		locators: string[];
		isCurrentConfiguration: boolean;
		error?: string;
		ticks: number;
		pointsUsed: number;
		medianErrorM?: number;
		p90ErrorM?: number;
		roomHitRate?: number;
		floorHitRate?: number;
		roomHitStandardErrorPoints?: number;
	}
	interface LocatorSweep {
		ranAt: string;
		error?: string;
		floorContrastWeightUsed: number;
		recommendation?: {
			locators: string[];
			label: string;
			reason: string;
			roomHitRate?: number;
			medianErrorM?: number;
			floorHitRate?: number;
			alreadyConfigured: boolean;
		};
		runs: LocatorRun[];
	}

	interface SweepRun {
		label: string;
		objective?: string;
		absorptionPenalty?: number;
		absorptionMin?: number;
		absorptionMax?: number;
		error?: string;
		medianErrorM?: number;
		respondingMedianErrorM?: number;
		p90ErrorM?: number;
		roomHitRate?: number;
		floorHitRate?: number;
		targetAbsorption?: number;
		absorptionMinFitted?: number;
		absorptionMedianFitted?: number;
		absorptionMaxFitted?: number;
	}
	interface SweepResult {
		ranAt: string;
		error?: string;
		verdict?: string;
		respondingPoints: number;
		totalPoints: number;
		respondingPointIds: string[];
		baseline?: SweepRun;
		runs: SweepRun[];
	}

	interface BenchmarkRun {
		ranAt: string;
		label?: string;
		error?: string;
		medianErrorM?: number;
		p90ErrorM?: number;
		roomHitRate?: number;
		floorHitRate?: number;
		floorConfusion: { pair: string; ticks: number }[];
		pointsUsed: number;
		pointsWithLevels: number;
		pointsSkipped: number;
		skipped: {
			id: string;
			floorId?: string;
			roomName?: string;
			ownFloorNodesHeard: number;
			ownFloorNodeCount: number;
			otherFloorNodesHeard: number;
			reason: string;
		}[];
		verdict?: string;
		floors: { floorId: string; medianErrorM?: number; ticks: number; floorHitRate?: number }[];
	}



	let diagnostics: Diagnostics | null = null;
	let benchmark: { last?: BenchmarkRun; history: BenchmarkRun[] } | null = null;
	let benchBusy = false;

	let validation: { issues: ValidationIssue[]; hasErrors: boolean; hasWarnings: boolean } | null = null;
	let health: HealthResult | null = null;
	let suggestions: PairSuggestion[] = [];
	let currentlyExcluded: string[] = [];
	let loading = true;
	let calibrateBusy = false;
	let pairBusy: Record<string, boolean> = {};
	let refreshTimer: ReturnType<typeof setInterval> | null = null;
	let walkTimer: ReturnType<typeof setInterval> | null = null;

	interface TuneCandidate {
		key: string;
		optimizer: string;
		absorptionPenalty?: number;
		label: string;
	}

	interface TuneResult {
		candidate: TuneCandidate;
		meanHoldoutComposite: number;
		meanTrainComposite: number;
		meanHoldoutR: number;
		meanHoldoutRmse: number;
		folds: number;
		isCurrent: boolean;
	}

	interface TuneState {
		running: boolean;
		phase?: string;
		candidatesDone: number;
		candidatesTotal: number;
		error?: string;
		results: TuneResult[];
		baseline?: TuneResult;
		recommendation?: string;
		measureCount: number;
		pairCount: number;
		finishedAt?: string;
	}

	let tuneState: TuneState | null = null;
	let tuneBusy = false;
	let tuneTimer: ReturnType<typeof setInterval> | null = null;

	interface LocatorTuneResult {
		candidate: { key: string; bandwidth: number; kernel: string; label: string };
		meanErrorM: number;
		meanJitterM: number;
		score: number;
		ticks: number;
		points: number;
		isCurrent: boolean;
	}

	interface LocatorTuneState {
		error?: string;
		results: LocatorTuneResult[];
		recommendation?: string;
		pointsUsed: number;
		ticksUsed: number;
		ranAt?: string;
	}

	let locatorTune: LocatorTuneState | null = null;
	let locatorTuneBusy = false;

	interface WizardSettings {
		intervalSecs: number;
		keepSnapshotMins: number;
		limits: Record<string, number>;
		weights: Record<string, number>;
		nadarayaWatsonEnabled: boolean;
		nadarayaWatsonBandwidth: number;
		nadarayaWatsonKernel: string;
		nelderMeadEnabled: boolean;
		mleEnabled: boolean;
		multiFloorEnabled: boolean;
		nearestNodeEnabled: boolean;
		nearestNodeMaxDistance?: number;
		timeout: number;
		awayTimeout: number;
		deviceRetention: string;
		optimizer: string;
		bfgsEnabled: boolean;
		nelderMeadSigma: number;
		mleSigma: number;
		filteringProcessNoise: number;
		filteringMeasurementNoise: number;
		filteringMaxVelocity: number;
		filteringSmoothingWeight: number;
		filteringMotionSigma: number;
		historyEnabled: boolean;
		historyDb: string;
		historyExpireAfter: string;
		mapFlipX: boolean;
		mapFlipY: boolean;
		mapWallThickness: number;
		mapWallColor?: string;
		mapWallOpacity?: number;
		gpsLatitude?: number;
		gpsLongitude?: number;
		gpsElevation?: number;
		gpsRotation?: number;
		gpsReport: boolean;
		mqttHost?: string;
		mqttPort?: number;
		mqttSsl?: boolean;
		mqttUsername?: string;
		mqttPassword?: string;
	}

	let settings: WizardSettings | null = null;
	let settingsBusy = false;
	let settingsOpen = false;

	let walkStatus: WalkTestStatus | null = null;
	let walkSuggestions: WalkPointSuggestion[] = [];
	let wtDevice = '';
	let wtX: number | null = null;
	let wtY: number | null = null;
	let wtZ: number | null = null;
	let wtDuration = 120;
	let wtBusy = false;

	// Walk-point deep link (?walk_x=..&walk_y=..&walk_z=.. from the map's picker): prefill the
	// start form and scroll to the walk-test card once it has rendered.
	let walkCardEl: HTMLElement | null = null;
	let pendingWalkScroll = false;

	$: if (pendingWalkScroll && !loading && walkCardEl) {
		pendingWalkScroll = false;
		const el = walkCardEl;
		setTimeout(() => el.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
	}

	function applyWalkDeepLink() {
		const params = new URLSearchParams(window.location.search);
		const x = parseFloat(params.get('walk_x') ?? '');
		const y = parseFloat(params.get('walk_y') ?? '');
		const z = parseFloat(params.get('walk_z') ?? '');
		if (Number.isFinite(x) && Number.isFinite(y)) {
			wtX = x;
			wtY = y;
			wtZ = Number.isFinite(z) ? z : 0;
			pendingWalkScroll = true;
		}
	}

	async function fetchAll() {
		try {
			const [vRes, hRes, sRes, wRes, wsRes, dRes, bRes] = await Promise.all([
				fetch(apiPath('/api/wizard/validation')),
				fetch(apiPath('/api/wizard/health')),
				fetch(apiPath('/api/wizard/excluded-pairs/suggestions')),
				fetch(apiPath('/api/wizard/walktest/status')),
				fetch(apiPath('/api/wizard/walktest/suggest')),
				fetch(apiPath('/api/wizard/diagnostics')),
				fetch(apiPath('/api/wizard/benchmark')),
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
				checkNewRawPoints();
			}
			if (wsRes.ok) {
				const data = await wsRes.json();
				walkSuggestions = data.suggestions ?? [];
			}
			if (dRes.ok) diagnostics = await dRes.json();
			if (bRes.ok) benchmark = await bRes.json();
		} catch (error) {
			console.error('Error fetching wizard data:', error);
		} finally {
			loading = false;
		}
	}

	let lastRawPointCount = -1;

	// Auto-rerun the locator replay whenever a new point with raw data appears (manual stop OR
	// server-side auto-finish) - the replay result is a snapshot of its last run and would
	// otherwise keep showing a stale "no raw data" error after the first recording.
	function checkNewRawPoints() {
		const count = walkStatus?.points?.filter((p) => (p.rawTicks ?? 0) > 0).length ?? 0;
		if (lastRawPointCount >= 0 && count > lastRawPointCount && !locatorTuneBusy) {
			runLocatorTune();
		}
		lastRawPointCount = count;
	}

	async function fetchWalkStatus() {
		try {
			const res = await fetch(apiPath('/api/wizard/walktest/status'));
			if (res.ok) {
				walkStatus = await res.json();
				checkNewRawPoints();
			}
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

	// Loeschen ist nicht rueckgaengig zu machen - deshalb immer mit Rueckfrage,
	// und danach die Diagnose neu holen: sowohl die Nachhol-Liste als auch die
	// Widerspruchs-Paare aendern sich dadurch, und eine veraltete Tabelle waere
	// schlimmer als gar keine.
	async function deleteWalkPointConfirmed(id: string, was: string) {
		const ok = await showConfirm({
			title: 'Walk-Punkt löschen',
			body: `${id} (${was}) endgültig entfernen? Das lässt sich nicht rückgängig machen.`
		});
		if (!ok) return;
		await deleteWalkPoint(id);
		await fetchAll();
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

	async function fetchLocatorTune() {
		try {
			const res = await fetch(apiPath('/api/wizard/locatortune/status'));
			if (res.ok) locatorTune = await res.json();
		} catch (error) {
			console.error('Error fetching locator tune status:', error);
		}
	}

	async function runLocatorTune() {
		if (locatorTuneBusy) return;
		locatorTuneBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/locatortune/run'), { method: 'POST' });
			if (!res.ok) throw new Error(`HTTP ${res.status}`);
			locatorTune = await res.json();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Locator tune failed',
				background: 'preset-filled-error-500'
			});
		} finally {
			locatorTuneBusy = false;
		}
	}

	async function applyLocatorCandidate(r: LocatorTuneResult) {
		const confirmed = await showConfirm({
			title: 'Apply locator configuration',
			body: `Set nadaraya_watson to "${r.candidate.label}" in the live config? Positioning behavior changes immediately.`
		});
		if (!confirmed) return;
		try {
			const res = await fetch(apiPath('/api/wizard/locatortune/apply'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ candidateKey: r.candidate.key })
			});
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({ message: `Applied: ${r.candidate.label}`, background: 'preset-filled-success-500' });
			await fetchSettings();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to apply locator candidate',
				background: 'preset-filled-error-500'
			});
		}
	}

	async function fetchSettings() {
		try {
			const res = await fetch(apiPath('/api/wizard/settings'));
			if (res.ok) settings = await res.json();
		} catch (error) {
			console.error('Error fetching settings:', error);
		}
	}

	async function saveSettings() {
		if (settingsBusy || !settings) return;
		settingsBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/settings'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify(settings)
			});
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({ message: 'Settings saved to config', background: 'preset-filled-success-500' });
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to save settings',
				background: 'preset-filled-error-500'
			});
		} finally {
			settingsBusy = false;
		}
	}

	async function fetchTuneStatus() {
		try {
			const res = await fetch(apiPath('/api/wizard/autotune/status'));
			if (res.ok) tuneState = await res.json();
		} catch (error) {
			console.error('Error fetching autotune status:', error);
		}
	}

	async function startAutoTune() {
		if (tuneBusy) return;
		tuneBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/autotune/start'), { method: 'POST' });
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({ message: 'Auto-tune started - this can take a minute or two', background: 'preset-filled-success-500' });
			await fetchTuneStatus();
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to start auto-tune',
				background: 'preset-filled-error-500'
			});
		} finally {
			tuneBusy = false;
		}
	}

	async function applyTuneCandidate(r: TuneResult) {
		const confirmed = await showConfirm({
			title: 'Apply optimizer configuration',
			body: `Set optimizer to "${r.candidate.label}" in the live config? The next calibration cycles will use it.`
		});
		if (!confirmed) return;
		try {
			const res = await fetch(apiPath('/api/wizard/autotune/apply'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ candidateKey: r.candidate.key })
			});
			if (!res.ok) {
				const err = await res.json().catch(() => null);
				throw new Error(err?.error ?? `HTTP ${res.status}`);
			}
			toastStore.trigger({ message: `Applied: ${r.candidate.label}`, background: 'preset-filled-success-500' });
		} catch (error) {
			toastStore.trigger({
				message: error instanceof Error ? error.message : 'Failed to apply candidate',
				background: 'preset-filled-error-500'
			});
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

	let sweep: SweepResult | null = null;
	let sweepBusy = false;

	let locatorSweep: LocatorSweep | null = null;
	let locatorBusy = false;
	let locatorApplied = false;

	async function runLocatorSweep() {
		locatorBusy = true;
		locatorApplied = false;
		try {
			const res = await fetch(apiPath('/api/wizard/locator-sweep'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({})
			});
			if (res.ok) locatorSweep = await res.json();
		} catch (error) {
			console.error('Error running locator sweep:', error);
		} finally {
			locatorBusy = false;
		}
	}

	async function applyLocatorChoice(locators: string[]) {
		locatorBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/locator-sweep/apply'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ locators })
			});
			locatorApplied = res.ok;
			if (res.ok) await runLocatorSweep();
		} finally {
			locatorBusy = false;
		}
	}

	async function runCalibrationSweep() {
		sweepBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/calibration-sweep'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({})
			});
			if (res.ok) sweep = await res.json();
		} catch (error) {
			console.error('Error running calibration sweep:', error);
		} finally {
			sweepBusy = false;
		}
	}

	async function runBenchmark() {
		benchBusy = true;
		try {
			const res = await fetch(apiPath('/api/wizard/benchmark/run'), {
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ label: null })
			});
			if (res.ok) {
				const run: BenchmarkRun = await res.json();
				benchmark = { last: run, history: [...(benchmark?.history ?? []), run] };
				toastStore.trigger({ message: run.error ?? run.verdict ?? 'Benchmark finished' });
			}
		} catch (e) {
			console.error(e);
		} finally {
			benchBusy = false;
		}
	}




	/// Colour by how far the room is from the 1.5 m that measured good accuracy here.
	function coverageClass(median: number): string {
		if (median <= 1.5) return 'preset-filled-success-500';
		return median > 3 ? 'preset-filled-error-500' : 'preset-filled-warning-500';
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
		applyWalkDeepLink();
		fetchAll();
		fetchTuneStatus();
		fetchLocatorTune();
		fetchSettings();
		refreshTimer = setInterval(fetchAll, 15000);
		// Faster poll for the walk test progress while a session runs
		walkTimer = setInterval(() => {
			if (walkStatus?.active) fetchWalkStatus();
		}, 3000);
		tuneTimer = setInterval(() => {
			if (tuneState?.running) fetchTuneStatus();
		}, 3000);
	});

	onDestroy(() => {
		if (refreshTimer) clearInterval(refreshTimer);
		if (walkTimer) clearInterval(walkTimer);
		if (tuneTimer) clearInterval(tuneTimer);
	});
</script>

<div class="h-full overflow-y-auto">
	<div class="w-full px-4 py-2 space-y-6">
		{#if loading}
			<p class="text-surface-600-400">Loading setup checks...</p>
		{:else}
			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-filled-primary-500 shrink-0">Schritt 1</span>
					<h2 class="text-xl font-bold">Pruefen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Stimmen Knoten und Konfiguration? Alles Weitere misst sonst nur den Defekt.</p>
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
			</section>

			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-filled-primary-500 shrink-0">Schritt 2</span>
					<h2 class="text-xl font-bold">Messen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Der Walk-Test liefert die Grundwahrheit, gegen die alle folgenden Schritte rechnen. Ohne ihn koennen sie nichts sagen.</p>
			<!-- 5. Walk test -->
			<div class="card p-4" bind:this={walkCardEl}>
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
							<p class="text-sm font-semibold mb-1">Where to measure next</p>
							<p class="text-xs text-surface-600-400 mb-2">
								Rooms nobody has measured come first - accuracy where no one has stood is not
								probably-fine, it is unknown. Then floors whose storey detection is weakest, then rooms
								whose nearest node is furthest away. The height is what the device was carried at on that
								floor, not where the nodes hang.
							</p>
							<ul class="space-y-1">
								{#each walkSuggestions as s}
									<li class="flex items-start gap-2 text-sm">
										<button class="btn btn-sm preset-tonal shrink-0" onclick={() => useSuggestion(s)}>({s.x}, {s.y}, {s.z})</button>
										<span>
											<span class="font-medium">{s.roomName ?? s.roomId}</span>
											<span class="text-surface-600-400"> — {s.reason}</span>
										</span>
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
					<div class="overflow-x-auto overflow-y-auto max-h-64">
						<table class="table table-compact">
							<thead class="sticky top-0 bg-surface-100-900">
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
					<p class="text-xs text-surface-600-400 mt-2">Points persist across restarts. Points whose receiving node was moved afterwards are ignored automatically.</p>
				{/if}
			</div>
			</section>

			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-filled-primary-500 shrink-0">Schritt 3</span>
					<h2 class="text-xl font-bold">Kalibrieren</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Aus den Messungen die Funkparameter je Knoten bestimmen - Absorption, Empfindlichkeit, Ausreisser-Paare.</p>
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
					<!-- Same metric order as the Nodes calibration page: RMSE, R, Best RMSE, Best R -->
					<div class="grid grid-cols-2 md:grid-cols-4 gap-3">
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-primary-500">{$calibration?.rmse?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">RMSE</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-primary-500">{$calibration?.r?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">R</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-success-500">{$calibration?.optimizerState?.bestRMSE?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">Best RMSE</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-success-500">{$calibration?.optimizerState?.bestR?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">Best R</div>
						</div>
					</div>
				{/if}
			</div>
			<!-- 2e. Calibration sweep -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Calibration Sweep</h2>
					<button class="btn preset-filled-primary-500" onclick={runCalibrationSweep} disabled={sweepBusy}>
						{sweepBusy ? 'Fitting...' : 'Run'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Fits the calibration several different ways on the current measurements and scores each one
					against your recorded walk points. The Locator Tune further down sweeps how positions are
					computed; this sweeps what the nodes believe about the radio. Nothing is written - it only
					reports which settings would have done better.
				</p>

				{#if sweep?.error}
					<p class="text-sm text-warning-600-400">{sweep.error}</p>
				{:else if sweep}
					<div class="mb-3 p-3 rounded {sweep.respondingPoints === 0 || sweep.respondingPoints / Math.max(sweep.totalPoints, 1) < 0.5 ? 'preset-tonal-warning' : 'preset-tonal'}">
						<p class="text-sm font-semibold">
							{sweep.respondingPoints} of {sweep.totalPoints} walk points can respond to a calibration change
						</p>
						<p class="text-xs text-surface-600-400 mt-1">
							Only points recorded with per-tick signal levels can react at all - the rest replay a
							distance their node derived at the time, which no setting here can alter. If that share is
							small, a flat column below means "mostly ballast", not "makes no difference". Record fresh
							walk points to raise it.
						</p>
					</div>

					{#if sweep.verdict}<p class="text-sm mb-3">{sweep.verdict}</p>{/if}

					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr>
									<th>Candidate</th>
									<th>Responding</th>
									<th>All points</th>
									<th>90th pct</th>
									<th>Room</th>
									<th>Floor</th>
									<th>Absorption fitted</th>
									<th>Pulled to</th>
								</tr>
							</thead>
							<tbody>
								{#if sweep.baseline}
									<tr class="opacity-70">
										<td>{sweep.baseline.label}</td>
										<td>-</td>
										<td>{sweep.baseline.medianErrorM?.toFixed(2)} m</td>
										<td>{sweep.baseline.p90ErrorM?.toFixed(2)} m</td>
										<td>{Math.round((sweep.baseline.roomHitRate ?? 0) * 100)}%</td>
										<td>{Math.round((sweep.baseline.floorHitRate ?? 0) * 100)}%</td>
										<td colspan="2">no fit performed</td>
									</tr>
								{/if}
								{#each sweep.runs as r, i (r.label)}
									<tr class={i === 0 && !r.error ? 'font-semibold' : ''}>
										<td>{r.label}</td>
										<td>{r.error ? '-' : `${r.respondingMedianErrorM?.toFixed(2)} m`}</td>
										<td>{r.error ? '-' : `${r.medianErrorM?.toFixed(2)} m`}</td>
										<td>{r.error ? '-' : `${r.p90ErrorM?.toFixed(2)} m`}</td>
										<td>{r.error ? '-' : `${Math.round((r.roomHitRate ?? 0) * 100)}%`}</td>
										<td>{r.error ? '-' : `${Math.round((r.floorHitRate ?? 0) * 100)}%`}</td>
										<td>
											{#if r.error}
												<span class="text-warning-600-400">{r.error}</span>
											{:else}
												{r.absorptionMinFitted?.toFixed(2)} – {r.absorptionMaxFitted?.toFixed(2)}
												<span class="text-surface-600-400">(median {r.absorptionMedianFitted?.toFixed(2)})</span>
											{/if}
										</td>
										<td>{r.targetAbsorption?.toFixed(2) ?? '-'}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">
						"Pulled to" is where the regularization shrinks absorption. It used to be the midpoint of the
						configured limits unconditionally, which quietly turned the limits into a target - widening
						them moved the goal instead of freeing the fit. It now follows the fleet's own median unless
						<code>weights.absorption_target</code> says otherwise.
					</p>
				{:else}
					<p class="text-sm text-surface-600-400">Not run yet. Needs recorded walk points and nodes that currently hear each other.</p>
				{/if}
			</div>
			<!-- 6. Optimizer auto-tune -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Optimizer Tuning</h2>
					{#if tuneState?.running}
						<span class="badge preset-filled-primary-500">Running</span>
					{:else}
						<button class="btn preset-filled-primary-500" onclick={startAutoTune} disabled={tuneBusy}>Run auto-tune</button>
					{/if}
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Fits each optimizer/penalty candidate on the collected measurements and scores it on held-out node pairs the fit never saw (3-fold cross-validation) - guards against configurations that only look good on their own training data. Walk test points count as extra data. Note: locator settings like nadaraya_watson bandwidth affect live positioning, not the fit, and cannot be tuned this way.
				</p>

				{#if tuneState?.running}
					<p class="text-sm">
						{tuneState.phase ?? 'Working'} ({tuneState.candidatesDone}/{tuneState.candidatesTotal} candidates done, {tuneState.pairCount} pairs / {tuneState.measureCount} measures)
					</p>
				{:else if tuneState?.error}
					<p class="text-sm text-error-500">{tuneState.error}</p>
				{/if}

				{#if tuneState && !tuneState.running && tuneState.results.length > 0}
					{#if tuneState.recommendation}
						<p class="text-sm font-semibold mb-2">{tuneState.recommendation}</p>
					{/if}
					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr><th>Candidate</th><th>Holdout</th><th>Train</th><th>R</th><th>RMSE</th><th></th></tr>
							</thead>
							<tbody>
								{#if tuneState.baseline}
									<tr class="opacity-75">
										<td>{tuneState.baseline.candidate.label}</td>
										<td>{tuneState.baseline.meanHoldoutComposite.toFixed(3)}</td>
										<td>-</td>
										<td>{tuneState.baseline.meanHoldoutR.toFixed(3)}</td>
										<td>{tuneState.baseline.meanHoldoutRmse.toFixed(3)}</td>
										<td><span class="badge preset-filled-surface-500">current</span></td>
									</tr>
								{/if}
								{#each tuneState.results as r, i (r.candidate.key)}
									<tr>
										<td>{r.candidate.label}{r.isCurrent ? ' (current)' : ''}</td>
										<td class={i === 0 ? 'font-bold text-success-500' : ''}>{r.meanHoldoutComposite.toFixed(3)}</td>
										<td>{Number.isNaN(r.meanTrainComposite) ? '-' : r.meanTrainComposite.toFixed(3)}</td>
										<td>{r.meanHoldoutR.toFixed(3)}</td>
										<td>{r.meanHoldoutRmse.toFixed(3)}</td>
										<td>
											{#if !r.isCurrent}
												<button class="btn btn-sm preset-filled-warning-500" onclick={() => applyTuneCandidate(r)}>Apply</button>
											{/if}
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">Holdout = mean composite score on pairs excluded from fitting (higher is better). A big train-vs-holdout gap indicates overfitting.</p>
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
			</section>

			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-filled-primary-500 shrink-0">Schritt 4</span>
					<h2 class="text-xl font-bold">Verorten</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Welche Locators sollen laufen und mit welchen Parametern? Gemessen am Szenarien-Wettbewerb, so wie er live entscheidet.</p>
			<!-- 2d2. Which locators should be enabled -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Locator Selection</h2>
					<button class="btn preset-filled-primary-500" onclick={runLocatorSweep} disabled={locatorBusy}>
						{locatorBusy ? 'Measuring...' : 'Measure'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Several position estimators can run at once, each producing a candidate per floor, and the most
					confident one wins. Which of them earn their place is a question about your building, not a
					matter of taste - so it is measured here by replaying your walk points through the real
					estimators, not a stand-in.
				</p>

				{#if locatorSweep?.error}
					<p class="text-sm text-warning-600-400">{locatorSweep.error}</p>
				{:else if locatorSweep}
					{#if locatorSweep.recommendation}
						{@const rec = locatorSweep.recommendation}
						<div class="p-3 rounded preset-tonal-primary mb-3">
							<div class="flex items-start justify-between gap-3">
								<div>
									<p class="text-sm font-semibold">Recommended: {rec.label}</p>
									<p class="text-xs text-surface-600-400 mt-1">{rec.reason}</p>
								</div>
								{#if rec.alreadyConfigured}
									<span class="badge preset-filled-success-500 shrink-0">already set</span>
								{:else}
									<button class="btn btn-sm preset-filled-primary-500 shrink-0"
										onclick={() => applyLocatorChoice(rec.locators)} disabled={locatorBusy}>
										Apply
									</button>
								{/if}
							</div>
							{#if locatorApplied}
								<p class="text-xs text-success-600-400 mt-2">
									Written to the configuration. Tracking picks it up on the next locator cycle.
								</p>
							{/if}
						</div>
					{/if}

					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr><th>Combination</th><th>Right room</th><th>Right floor</th><th>Median</th><th>Points</th></tr>
							</thead>
							<tbody>
								{#each locatorSweep.runs as r (r.label)}
									<tr class={r.isCurrentConfiguration ? 'font-semibold' : ''}>
										<td>{r.label}{r.isCurrentConfiguration ? ' (current)' : ''}</td>
										<td>
											{r.error ? '-' : `${Math.round((r.roomHitRate ?? 0) * 100)}%`}
											{#if r.roomHitStandardErrorPoints}
												<span class="text-surface-600-400">±{Math.round(r.roomHitStandardErrorPoints * 100)}</span>
											{/if}
										</td>
										<td>{r.error ? '-' : `${Math.round((r.floorHitRate ?? 0) * 100)}%`}</td>
										<td>{r.error ? '-' : `${r.medianErrorM?.toFixed(2)} m`}</td>
										<td>{r.pointsUsed}</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">
						Ranked by right-room rate, because that is what presence automations consume - and it already
						contains the rest: a scenario on the wrong floor cannot name the right room, and neither can
						one that is half a room off. The ± is measured across walk points rather than across ticks,
						since the ticks within one point are the same device standing in the same place.
					</p>
				{:else}
					<p class="text-sm text-surface-600-400">Not measured yet. Needs recorded walk points.</p>
				{/if}
			</div>
			<!-- 7. Locator tuning via walk-test replay -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Locator Tuning</h2>
					<button class="btn preset-filled-primary-500" onclick={runLocatorTune} disabled={locatorTuneBusy}>
						{locatorTuneBusy ? 'Running...' : 'Run replay'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Replays the raw per-tick readings of your recorded walk test points (real live noise, known true position) through nadaraya_watson bandwidth/kernel candidates. Scored on position accuracy AND jitter - how much the estimate wanders while the beacon sits still, i.e. the room-flapping symptom. The more walk points on different floors, the more representative the result.
				</p>
				{#if locatorTune?.error}
					{#if (walkStatus?.points?.filter((p) => (p.rawTicks ?? 0) > 0).length ?? 0) > 0}
						<p class="text-sm text-surface-600-400">Walk points with raw data are available - press "Run replay".</p>
					{:else}
						<p class="text-sm text-error-500">{locatorTune.error}</p>
					{/if}
				{/if}
				{#if locatorTune && !locatorTune.error && locatorTune.results.length > 0}
					{#if locatorTune.recommendation}
						<p class="text-sm font-semibold mb-2">{locatorTune.recommendation}</p>
					{/if}
					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr><th>Candidate</th><th>Mean error</th><th>Jitter</th><th>Score</th><th>Ticks</th><th></th></tr>
							</thead>
							<tbody>
								{#each locatorTune.results as r, i (r.candidate.key)}
									<tr>
										<td>{r.candidate.label}{r.isCurrent ? ' (current)' : ''}</td>
										<td class={i === 0 ? 'font-bold text-success-500' : ''}>{r.meanErrorM.toFixed(2)}m</td>
										<td>{r.meanJitterM.toFixed(2)}m</td>
										<td>{r.score.toFixed(2)}</td>
										<td>{r.ticks}</td>
										<td>
											{#if !r.isCurrent}
												<button class="btn btn-sm preset-filled-warning-500" onclick={() => applyLocatorCandidate(r)}>Apply</button>
											{/if}
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">Based on {locatorTune.pointsUsed} walk point{locatorTune.pointsUsed === 1 ? '' : 's'}. Caveats: stationary noise only (no walking-motion dynamics), and the scenario/Kalman smoothing above the locators is not replayed.</p>
				{/if}
			</div>
			</section>

			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-filled-primary-500 shrink-0">Schritt 5</span>
					<h2 class="text-xl font-bold">Nachweisen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Was kommt am Ende heraus? Dieselbe Messweise wie in Schritt 4, damit die Zahlen vergleichbar sind.</p>
			<!-- 2d. Accuracy benchmark -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Accuracy Benchmark</h2>
					<button class="btn preset-filled-primary-500" onclick={runBenchmark} disabled={benchBusy}>
						{benchBusy ? 'Running...' : 'Run'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Replays the recorded walk points through the locator with the current settings, so two runs can be
					compared. Without one figure computed the same way every time, "that made it better" is an opinion.
				</p>
				{#if benchmark?.last}
					{@const b = benchmark.last}
					{#if b.error}
						<p class="text-sm text-warning-600-400">{b.error}</p>
					{:else}
						<p class="text-sm mb-2">{b.verdict}</p>
						<div class="flex flex-wrap gap-6 text-sm mb-3">
							<span>Median <strong>{b.medianErrorM?.toFixed(2)} m</strong></span>
							<span>90th pct <strong>{b.p90ErrorM?.toFixed(2)} m</strong></span>
							<span>Right room <strong>{Math.round((b.roomHitRate ?? 0) * 100)}%</strong></span>
							{#if b.floorHitRate != null}
								<span>Right floor <strong class={b.floorHitRate < 0.95 ? 'text-warning-600-400' : ''}>{Math.round(b.floorHitRate * 100)}%</strong></span>
							{/if}
							<span class="text-surface-600-400">{b.pointsUsed} points, {b.pointsWithLevels} with signal levels</span>
							{#if b.pointsSkipped > 0}
								<span class="text-warning-600-400">{b.pointsSkipped} not scored</span>
							{/if}
						</div>
						{#if b.floors.length > 0}
							<div class="overflow-x-auto">
								<table class="table table-compact">
									<thead><tr><th>Floor</th><th>Median</th><th>Found</th><th>Ticks</th></tr></thead>
									<tbody>
										{#each b.floors as f}
											<tr>
												<td>{f.floorId}</td>
												<td>{f.medianErrorM?.toFixed(2)} m</td>
												<td class={f.floorHitRate != null && f.floorHitRate < 0.95 ? 'text-warning-600-400' : ''}>
													{f.floorHitRate != null ? `${Math.round(f.floorHitRate * 100)}%` : '-'}
												</td>
												<td>{f.ticks}</td>
											</tr>
										{/each}
									</tbody>
								</table>
							</div>
						{/if}
						{#if (b.skipped ?? []).length > 0}
							<div class="mt-3 p-3 rounded preset-tonal-warning">
								<p class="text-sm font-semibold mb-1">Walk points not scored</p>
								<p class="text-xs text-surface-600-400 mb-2">
									These were recorded but could not be measured against. Worth reading rather than
									skipping: a point that no node on its own floor can hear is not missing data, it is
									data about a gap.
								</p>
								<ul class="space-y-1">
									{#each b.skipped as sk (sk.id)}
										<li class="text-sm">
											<span class="font-medium">{sk.roomName ?? sk.id}</span>
											<span class="text-surface-600-400">({sk.floorId}) — {sk.reason}</span>
										</li>
									{/each}
								</ul>
							</div>
						{/if}
						{#if (b.floorConfusion ?? []).length > 0}
							<p class="text-xs text-surface-600-400 mt-2">
								Wrong floor most often: {b.floorConfusion.map((c) => `${c.pair} (${c.ticks}x)`).join(', ')}
							</p>
						{/if}
						<p class="text-xs text-surface-600-400 mt-2">
							"Found" is the only figure here scored without knowing the answer - median, 90th percentile
							and room are all measured on the correct floor, so a run can improve by centimetres while
							sending the device upstairs.
						</p>
					{/if}
				{:else}
					<p class="text-sm text-surface-600-400">Not run yet. Needs at least one walk test point.</p>
				{/if}
			</div>
			<!-- 2b. Measurement diagnostics: does the radio data agree with the map? -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Measurement Diagnostics</h2>
					{#if diagnostics}
						<span class="badge {diagnostics.issues.length === 0 ? 'preset-filled-success-500' : 'preset-filled-warning-500'}">
							{diagnostics.issues.length === 0 ? 'Nothing flagged' : `${diagnostics.issues.length} finding${diagnostics.issues.length === 1 ? '' : 's'}`}
						</span>
					{/if}
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Configuration Checks above validates the map. This checks the radio data against it - readings no
					path-loss setting can explain, parameters pinned to their limit, and how much of each room actually
					has a node close enough to work with.
				</p>

				{#if diagnostics}
					{#if diagnostics.near.pairs > 0 || diagnostics.far.pairs > 0}
						<div class="flex gap-6 text-sm mb-3">
							<span>Under {diagnostics.nearFarSplitM} m: <strong>{diagnostics.near.medianAbsRssiErrorDb ?? '-'} dB</strong> ({diagnostics.near.pairs} pairs)</span>
							<span>Beyond: <strong>{diagnostics.far.medianAbsRssiErrorDb ?? '-'} dB</strong> ({diagnostics.far.pairs} pairs)</span>
						</div>
					{/if}

					{#if diagnostics.issues.length > 0}
						<ul class="space-y-2 mb-3 max-h-80 overflow-y-auto pr-2">
							{#each diagnostics.issues as issue}
								<li class="flex items-start gap-2">
									<span class="badge {severityClass(issue.severity)} shrink-0 mt-0.5">{issue.category}</span>
									<span class="text-sm">{issue.message}</span>
								</li>
							{/each}
						</ul>
					{/if}

					{#if (diagnostics.conflictingWalkPairs?.length ?? 0) > 0 && diagnostics.conflictingWalkPairs}
						{@const unvereinbar = diagnostics.conflictingWalkPairs.filter((p) => p.irreconcilable)}
						{#if unvereinbar.length > 0}
							<h3 class="font-semibold text-sm mb-2">
								Widersprüchliche Doppel-Aufnahmen ({unvereinbar.length})
							</h3>
							<p class="text-xs text-surface-600-400 mb-2">
								Diese Punkte liegen praktisch am selben Ort, widersprechen sich aber stärker,
								als die Messstreuung erklären kann. Sie gehen als zusätzliche Referenzsender in
								dieselbe Zielfunktion ein wie die Knoten-Messungen — <strong>keine Kalibrierung
								kann beide erfüllen</strong>. Jede Änderung, die einer Seite hilft, verschlechtert
								die andere und wird verworfen. Das sieht aus wie „nichts zu verbessern", ist aber
								„unmögliche Vorgabe". Die unglaubwürdigere Aufnahme löschen, bevor weiter
								optimiert wird.
							</p>
							<div class="overflow-x-auto mb-3">
								<table class="table table-compact w-full text-sm">
									<thead>
										<tr>
											<th>Etage / Raum</th><th>Position</th><th class="text-right">Abstand</th>
											<th class="text-right">Knoten</th><th class="text-right">Δ gemessen</th>
											<th class="text-right">− Geometrie</th><th class="text-right">= Rest</th>
											<th class="text-right">erklärbar</th><th class="text-right">Rest max</th>
											<th>A</th><th>B</th>
										</tr>
									</thead>
									<tbody>
										{#each unvereinbar as c}
											<tr>
												<td>{c.floorName ?? c.floorId ?? '—'} / {c.roomName ?? '—'}</td>
												<td>
													<button type="button" class="anchor font-mono"
														onclick={() => gotoMapSpot(c.floorId, c.x, c.y, c.z)}
														title="Auf der Karte zeigen">{c.x} / {c.y} / {c.z}</button>
												</td>
												<td class="text-right">{c.distanceM} m</td>
												<td class="text-right">{c.sharedNodes}</td>
												<td class="text-right">{c.medianDeltaDb} dB</td>
												<td class="text-right text-surface-600-400"
													title="Was der blosse Ortsunterschied schon erklärt — bei einem nahen Knoten viel, bei einem fernen fast nichts">
													{c.medianGeometryDb} dB</td>
												<td class="text-right font-semibold">{c.medianResidualDb} dB</td>
												<td class="text-right text-surface-600-400">{c.explainableDb} dB</td>
												<td class="text-right" title={c.maxDeltaNode ?? ''}>{c.maxDeltaDb} dB</td>
												<td>
													<button type="button" class="btn btn-sm preset-tonal-error"
														onclick={() => deleteWalkPointConfirmed(c.idA, new Date(c.recordedAtA).toLocaleDateString())}
														title="Aufnahme A löschen">{c.idA}</button>
												</td>
												<td>
													<button type="button" class="btn btn-sm preset-tonal-error"
														onclick={() => deleteWalkPointConfirmed(c.idB, new Date(c.recordedAtB).toLocaleDateString())}
														title="Aufnahme B löschen">{c.idB}</button>
												</td>
											</tr>
										{/each}
									</tbody>
								</table>
							</div>
							<p class="text-xs text-surface-600-400 mb-3">
								„erklärbar" ist keine feste Grenze, sondern aus der aufgezeichneten
								Entfernungs-Streuung beider Aufnahmen gerechnet und über das Pfadverlustmodell in
								Dezibel umgesetzt — dieselbe Meter-Streuung bedeutet nah am Knoten deutlich mehr
								Dezibel als weit weg. Der Knoten mit der größten Einzeldifferenz steht als
								Hinweistext an „Δ max".
							</p>
						{/if}
					{/if}

					{#if (diagnostics.staleWalkPoints?.length ?? 0) > 0 && diagnostics.staleWalkPoints}
						<h3 class="font-semibold text-sm mb-2">Walk-Punkte ohne Pegel — nachzuholen ({diagnostics.staleWalkPoints.length})</h3>
						<p class="text-xs text-surface-600-400 mb-2">
							Ein neuer Spaziergang legt einen <em>zusätzlichen</em> Punkt an — der alte bleibt
							stehen. Die Liste wird also nur kürzer, wenn du den alten hier löschst.
						</p>
						<div class="overflow-x-auto mb-3">
							<table class="table table-compact w-full text-sm">
								<thead>
									<tr><th>Punkt</th><th>Etage</th><th>Raum</th><th>Position</th><th class="text-right">Ticks</th><th>aufgenommen</th><th></th></tr>
								</thead>
								<tbody>
									{#each diagnostics.staleWalkPoints as p}
										<tr>
											<td class="font-mono">{p.id}</td>
											<td>{p.floorName ?? p.floorId ?? '—'}</td>
											<td>{p.roomName ?? '—'}</td>
											<td>
												<!-- Kein rohes href: im HA-Ingress liegt die App unter einem Praefix, ein
												     absoluter Pfad wuerde HA neu laden. goto()+resolve() bleibt in der App. -->
												<button type="button" class="anchor font-mono"
													onclick={() => gotoMapSpot(p.floorId, p.x, p.y, p.z)}
													title="Auf der Karte zeigen">{p.x} / {p.y} / {p.z}</button>
											</td>
											<td class="text-right">{p.ticks}</td>
											<td class="text-surface-600-400">{new Date(p.recordedAt).toLocaleDateString()}</td>
											<td class="text-right">
												<button type="button" class="btn btn-sm preset-tonal-error"
													onclick={() => deleteWalkPointConfirmed(p.id, 'ohne Pegel')}
													title="Diesen Punkt entfernen">Löschen</button>
											</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					{/if}

					{#if diagnostics.roomCoverage.length > 0}
						<h3 class="font-semibold text-sm mb-2">Node coverage per room</h3>
						<p class="text-xs text-surface-600-400 mb-2">
							Measured here: spots with a node within 1.5 m averaged 1.1 m position error, spots beyond it
							2.5 m. Distance to the third-nearest node made no difference - one node close enough is what counts.
						</p>
						<div class="overflow-x-auto overflow-y-auto max-h-80">
							<table class="table table-compact">
								<thead><tr><th>Room</th><th>Floor</th><th>Nearest node</th><th>Worst corner</th><th>In range</th></tr></thead>
								<tbody>
									{#each diagnostics.roomCoverage as r}
										<tr>
											<td>{r.roomName ?? '-'}</td>
											<td class="text-surface-600-400">{r.floorId}</td>
											<td><span class="badge {coverageClass(r.medianNearestNodeM)}">{r.medianNearestNodeM.toFixed(1)} m</span></td>
											<td>{r.worstNearestNodeM.toFixed(1)} m</td>
											<td>{Math.round(r.wellCoveredFraction * 100)}%</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
					{/if}
				{/if}
			</div>
			</section>

			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-tonal-surface shrink-0">Experten</span>
					<h2 class="text-xl font-bold">Einstellungen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Schreibt direkt in die config.yaml. Wer hier dreht, sollte wissen warum - die Schritte oben messen und empfehlen dieselben Werte.</p>
			<!-- 8. Settings -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Settings</h2>
					<div class="flex gap-2">
						<button class="btn preset-tonal" onclick={() => (settingsOpen = !settingsOpen)}>{settingsOpen ? 'Hide' : 'Show'}</button>
						{#if settingsOpen}
							<button class="btn preset-filled-primary-500" onclick={saveSettings} disabled={settingsBusy || !settings}>Save</button>
						{/if}
					</div>
				</header>
				{#if settingsOpen && settings}
					<div class="grid grid-cols-1 md:grid-cols-2 gap-6">
						<div>
							<h3 class="font-semibold text-sm mb-2">Optimization</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm">
									<span>Interval (s)</span>
									<input class="input" type="number" min="15" bind:value={settings.intervalSecs} />
								</label>
								<label class="label text-sm">
									<span>Snapshot window (min)</span>
									<input class="input" type="number" min="1" max="120" bind:value={settings.keepSnapshotMins} />
								</label>
								<label class="label text-sm">
									<span>Absorption min</span>
									<input class="input" type="number" step="0.1" bind:value={settings.limits.absorption_min} />
								</label>
								<label class="label text-sm">
									<span>Absorption max</span>
									<input class="input" type="number" step="0.1" bind:value={settings.limits.absorption_max} />
								</label>
								<label class="label text-sm">
									<span>Absorption penalty</span>
									<input class="input" type="number" step="0.5" bind:value={settings.weights.absorption_penalty} />
								</label>
								<label class="label text-sm">
									<span>Optimizer</span>
									<select class="select" bind:value={settings.optimizer}>
										<option value="per_node_absorption">per_node_absorption</option>
										<option value="global_absorption">global_absorption</option>
										<option value="legacy">legacy</option>
									</select>
								</label>
								<label class="label text-sm"><span>Tx ref min</span><input class="input" type="number" step="1" bind:value={settings.limits.tx_ref_rssi_min} /></label>
								<label class="label text-sm"><span>Tx ref max</span><input class="input" type="number" step="1" bind:value={settings.limits.tx_ref_rssi_max} /></label>
								<label class="label text-sm"><span>Rx adj min</span><input class="input" type="number" step="1" bind:value={settings.limits.rx_adj_rssi_min} /></label>
								<label class="label text-sm"><span>Rx adj max</span><input class="input" type="number" step="1" bind:value={settings.limits.rx_adj_rssi_max} /></label>
							</div>
						</div>
						<div>
							<h3 class="font-semibold text-sm mb-2">Locators</h3>
							<div class="space-y-2">
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.nadarayaWatsonEnabled} id="nw-en" />
									<label for="nw-en" class="text-sm">nadaraya_watson</label>
									<input class="input w-20" type="number" step="0.1" bind:value={settings.nadarayaWatsonBandwidth} title="bandwidth" />
									<select class="select w-32" bind:value={settings.nadarayaWatsonKernel}>
										<option value="gaussian">gaussian</option>
										<option value="inverse_square">inverse</option>
									</select>
								</div>
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.nelderMeadEnabled} id="nm-en" />
									<label for="nm-en" class="text-sm">nelder_mead</label>
									<input class="input w-20" type="number" step="0.05" bind:value={settings.nelderMeadSigma} title="weighting sigma (rank-based)" />
								</div>
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.mleEnabled} id="mle-en" />
									<label for="mle-en" class="text-sm">mle</label>
									<input class="input w-20" type="number" step="0.05" bind:value={settings.mleSigma} title="weighting sigma (rank-based)" />
								</div>
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.bfgsEnabled} id="bfgs-en" />
									<label for="bfgs-en" class="text-sm">bfgs</label>
								</div>
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.multiFloorEnabled} id="mf-en" />
									<label for="mf-en" class="text-sm">multi_floor</label>
								</div>
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.nearestNodeEnabled} id="nn-en" />
									<label for="nn-en" class="text-sm">nearest_node</label>
									<input class="input w-20" type="number" step="0.5" bind:value={settings.nearestNodeMaxDistance} title="max distance" />
								</div>
							</div>
						</div>
					</div>
					<div class="grid grid-cols-1 md:grid-cols-2 gap-6 mt-4">
						<div>
							<h3 class="font-semibold text-sm mb-2">General</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Timeout (s)</span><input class="input" type="number" min="5" bind:value={settings.timeout} /></label>
								<label class="label text-sm"><span>Away timeout (s)</span><input class="input" type="number" min="10" bind:value={settings.awayTimeout} /></label>
								<label class="label text-sm"><span>Device retention</span><input class="input" placeholder="30d" bind:value={settings.deviceRetention} /></label>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">Filtering (Kalman)</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Process noise</span><input class="input" type="number" step="0.001" bind:value={settings.filteringProcessNoise} /></label>
								<label class="label text-sm"><span>Measurement noise</span><input class="input" type="number" step="0.01" bind:value={settings.filteringMeasurementNoise} /></label>
								<label class="label text-sm"><span>Max velocity (m/s)</span><input class="input" type="number" step="0.1" bind:value={settings.filteringMaxVelocity} /></label>
								<label class="label text-sm"><span>Smoothing weight</span><input class="input" type="number" step="0.05" min="0" max="1" bind:value={settings.filteringSmoothingWeight} /></label>
								<label class="label text-sm"><span>Motion sigma</span><input class="input" type="number" step="0.1" bind:value={settings.filteringMotionSigma} /></label>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">History</h3>
							<div class="space-y-2">
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.historyEnabled} id="hist-en" />
									<label for="hist-en" class="text-sm">enabled</label>
								</div>
								<label class="label text-sm"><span>DB</span><input class="input" bind:value={settings.historyDb} /></label>
								<label class="label text-sm"><span>Expire after</span><input class="input" placeholder="24h" bind:value={settings.historyExpireAfter} /></label>
							</div>
						</div>
						<div>
							<h3 class="font-semibold text-sm mb-2">Map</h3>
							<div class="space-y-2">
								<div class="flex items-center gap-4">
									<span class="flex items-center gap-2"><input type="checkbox" class="checkbox" bind:checked={settings.mapFlipX} id="map-fx" /><label for="map-fx" class="text-sm">flip_x</label></span>
									<span class="flex items-center gap-2"><input type="checkbox" class="checkbox" bind:checked={settings.mapFlipY} id="map-fy" /><label for="map-fy" class="text-sm">flip_y</label></span>
								</div>
								<div class="grid grid-cols-3 gap-3">
									<label class="label text-sm"><span>Wall thickness</span><input class="input" type="number" step="0.05" bind:value={settings.mapWallThickness} /></label>
									<label class="label text-sm"><span>Wall color</span><input class="input" placeholder="#888888" bind:value={settings.mapWallColor} /></label>
									<label class="label text-sm"><span>Wall opacity</span><input class="input" type="number" step="0.05" min="0" max="1" bind:value={settings.mapWallOpacity} /></label>
								</div>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">GPS</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Latitude</span><input class="input" type="number" step="0.000001" bind:value={settings.gpsLatitude} /></label>
								<label class="label text-sm"><span>Longitude</span><input class="input" type="number" step="0.000001" bind:value={settings.gpsLongitude} /></label>
								<label class="label text-sm"><span>Elevation (m)</span><input class="input" type="number" step="0.1" bind:value={settings.gpsElevation} /></label>
								<label class="label text-sm"><span>Rotation (°)</span><input class="input" type="number" step="0.1" bind:value={settings.gpsRotation} /></label>
							</div>
							<div class="flex items-center gap-3 mt-2">
								<input type="checkbox" class="checkbox" bind:checked={settings.gpsReport} id="gps-rep" />
								<label for="gps-rep" class="text-sm">report GPS position</label>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">MQTT</h3>
							<p class="text-xs text-surface-600-400 mb-1">Leave host empty to use the Supervisor-provided broker (HA add-on default).</p>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Host</span><input class="input" bind:value={settings.mqttHost} /></label>
								<label class="label text-sm"><span>Port</span><input class="input" type="number" bind:value={settings.mqttPort} /></label>
								<label class="label text-sm"><span>Username</span><input class="input" bind:value={settings.mqttUsername} /></label>
								<label class="label text-sm"><span>Password</span><input class="input" type="password" bind:value={settings.mqttPassword} /></label>
							</div>
							<div class="flex items-center gap-3 mt-2">
								<input type="checkbox" class="checkbox" bind:checked={settings.mqttSsl} id="mqtt-ssl" />
								<label for="mqtt-ssl" class="text-sm">SSL</label>
							</div>
						</div>
					</div>
					<p class="text-xs text-surface-600-400 mt-3">Writes these config.yaml sections directly - changes apply within a few seconds (config is polled), no restart needed. Changing MQTT to a wrong broker disconnects the companion from the fleet - the Supervisor default is usually right. correlation/rmse weights and floor assignments are deliberately not exposed here.</p>
				{/if}
			</div>
			</section>
		{/if}
	</div>
</div>
