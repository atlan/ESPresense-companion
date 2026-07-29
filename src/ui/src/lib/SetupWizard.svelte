<script lang="ts">
	import { gotoMapSpot } from '$lib/urls';
	import { onMount, onDestroy } from 'svelte';
	import { getToastStore } from '$lib/toast/toastStore';
	import { calibration } from '$lib/stores';
	import { apiPath } from '$lib/api';
	import { showConfirm } from '$lib/modal/modalStore';

	const toastStore = getToastStore();

	interface NextAction {
		id: string; rank: number; kind: 'Walk' | 'Hardware' | 'Cleanup' | number;
		title: string; why: string; gain?: string | null;
		nodeSpans?: { nodeId: string; nodeName?: string | null; points: number; minM: number; maxM: number; suggestedM: number; onlyInFolds: boolean }[] | null;
		rooms?: string[] | null;
		suggestions?: { x: number; y: number; z: number; floorId?: string | null; roomName?: string | null; nearestNodeM: number }[] | null;
	}
	interface SystemStatus {
		medianErrorM?: number | null; p90ErrorM?: number | null;
		roomHitRate?: number | null; floorHitRate?: number | null;
		measuredAt?: string | null; points: number;
		pointsWithLevels: number;
		responsiveMedianErrorM?: number | null; responsiveRoomHitRate?: number | null;
	}
	let todo: { status?: SystemStatus | null; actions: NextAction[] } | null = null;

	interface AppliedChange {
		id: string; at: string; undoneAt?: string | null;
		title: string; reason: string; before: string[]; after: string[];
		roomHitRate?: number | null; medianErrorM?: number | null;
	}
	let selbstGetan: AppliedChange[] = [];

	async function umstellungZuruecknehmen(id: string) {
		const ok = await showConfirm({
			title: 'Umstellung zurücknehmen',
			body: 'Die vorherige Einstellung wieder herstellen? Das Ortungsverhalten ändert sich sofort.'
		});
		if (!ok) return;
		const res = await fetch(apiPath(`/api/wizard/auto-applied/${encodeURIComponent(id)}/undo`), { method: 'POST' });
		toastStore.trigger({
			message: res.ok ? 'Zurückgenommen' : 'Fehlgeschlagen',
			background: res.ok ? 'preset-filled-success-500' : 'preset-filled-error-500'
		});
		if (res.ok) await fetchAll();
	}

	// Der Rest der Seite ist eingeklappt. Wer die Werkzeuge sucht, findet sie — aber die Seite
	// beantwortet zuerst die Frage, mit der man kommt: was muss ich tun.
	let zeigeDetails = false;
	let zeigeExperte = false;

	const KIND_TEXT: Record<string, string> = {
		Walk: 'Hingehen und messen',
		Hardware: 'Etwas anfassen'
	};
	const KIND_CLASS: Record<string, string> = {
		Walk: 'preset-filled-primary-500',
		Hardware: 'preset-filled-warning-500'
	};

	interface DisabledMeasurement {
		pointId: string; nodeId: string; nodeName?: string | null;
		floorId?: string | null; x: number; y: number; z: number;
		samples: number; medianRssi: number; refRssi: number; mapDistance: number;
		rule?: string | null; reason?: string | null; at?: string | null;
	}
	let stillgelegt: DisabledMeasurement[] = [];

	const REGEL_TEXT: Record<string, string> = {
		'few-samples': 'zu wenige Messwerte',
		'impossible-level': 'physikalisch unmöglicher Pegel',
		manual: 'von Hand'
	};

	async function messungSchalten(pointId: string, nodeId: string, disabled: boolean, reason?: string) {
		// Beim Zurücknehmen darf der Benutzer festhalten, WARUM — das ist die eine Information,
		// die er hat und das System nicht („ich hatte das Handy in der Tasche"). Freiwillig.
		const res = await fetch(
			apiPath(`/api/wizard/walktest/points/${encodeURIComponent(pointId)}/nodes/${encodeURIComponent(nodeId)}/disabled`),
			{
				method: 'POST',
				headers: { 'Content-Type': 'application/json' },
				body: JSON.stringify({ disabled, reason })
			}
		);
		if (!res.ok) {
			toastStore.trigger({ message: `Fehlgeschlagen: ${await res.text()}`, background: 'preset-filled-error-500' });
			return;
		}
		toastStore.trigger({
			message: disabled ? `${pointId}/${nodeId} stillgelegt` : `${pointId}/${nodeId} wieder aufgenommen`,
			background: 'preset-filled-success-500'
		});
		await fetchAll();
	}

	interface ConflictingWalkPair {
		idA: string; idB: string;
		recordedAtA: string; recordedAtB: string;
		floorId?: string | null; floorName?: string | null; roomName?: string | null;
		x: number; y: number; z: number;
		distanceM: number; sharedNodes: number;
		medianDeltaDb: number; medianGeometryDb: number; medianResidualDb: number;
		maxDeltaDb: number; maxDeltaNode?: string | null;
		irreconcilable: boolean; explainableDb: number; singleNodeOnly: boolean;
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
			decidedBy: string;
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
		beatsCurrentMeasurably?: boolean;
		scoreStandardError?: number | null;
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
			const [vRes, hRes, sRes, wRes, wsRes, dRes, bRes, offRes, naRes, aaRes] = await Promise.all([
				fetch(apiPath('/api/wizard/validation')),
				fetch(apiPath('/api/wizard/health')),
				fetch(apiPath('/api/wizard/excluded-pairs/suggestions')),
				fetch(apiPath('/api/wizard/walktest/status')),
				fetch(apiPath('/api/wizard/walktest/suggest')),
				fetch(apiPath('/api/wizard/diagnostics')),
				fetch(apiPath('/api/wizard/benchmark')),
				fetch(apiPath('/api/wizard/walktest/disabled')),
				fetch(apiPath('/api/wizard/next-actions')),
				fetch(apiPath('/api/wizard/auto-applied')),
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
			if (offRes.ok) stillgelegt = await offRes.json();
			if (naRes.ok) todo = await naRes.json();
			if (aaRes.ok) selbstGetan = await aaRes.json();
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
			toastStore.trigger({ message: 'Walk-Test läuft — das Gerät jetzt nicht bewegen', background: 'preset-filled-success-500' });
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

	/// Aus der Handlungsliste heraus: Koordinaten uebernehmen, Details AUFKLAPPEN und
	/// hinscrollen. Ohne das Aufklappen fuellt der Klick ein Formular, das der Benutzer
	/// nicht sieht - er drueckt und nichts passiert.
	function vorschlagUebernehmen(s: WalkPointSuggestion) {
		useSuggestion(s);
		zeigeDetails = true;
		pendingWalkScroll = true;
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

	// Der Assistent stellt das inzwischen selbst um (AutoApply). Der Knopf bleibt fuer
	// Ungeduldige, verschwindet aber, wo die Messung die Kandidaten nicht trennt.
	async function applyLocatorCandidate(r: LocatorTuneResult) {
		const confirmed = await showConfirm({
			title: 'Ortungs-Feineinstellung übernehmen',
			body: `Nadaraya-Watson auf „${r.candidate.label}“ umstellen? Das Ortungsverhalten ändert sich sofort. Der Assistent macht das beim nächsten Optimierungsdurchgang ohnehin selbst — dies nimmt es nur vorweg.`
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
			toastStore.trigger({ message: `Übernommen: ${r.candidate.label}`, background: 'preset-filled-success-500' });
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
			toastStore.trigger({ message: 'Einstellungen in die Konfiguration geschrieben', background: 'preset-filled-success-500' });
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
			toastStore.trigger({ message: 'Abgleich läuft — das dauert ein bis zwei Minuten', background: 'preset-filled-success-500' });
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
			title: 'Optimierer-Einstellung übernehmen',
			body: `Optimierer auf „${r.candidate.label}“ umstellen? Die nächsten Kalibrierdurchgänge benutzen ihn dann. ⚠ Engere Absorptionsgrenzen ziehen bestehende Kalibrierungen NICHT nachträglich in den erlaubten Bereich — Werte außerhalb bleiben stehen, während jeder Kandidat abgelehnt wird.`
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
			toastStore.trigger({ message: `Übernommen: ${r.candidate.label}`, background: 'preset-filled-success-500' });
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
				message: 'Kalibrierdurchgang angestoßen — unten ändern sich gleich bestes R und bestes RMSE',
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
			title: 'Knotenpaar von der Kalibrierung ausnehmen',
			body: `„${s.nodeAName ?? s.nodeA}" ↔ „${s.nodeBName ?? s.nodeB}" vom Fit ausnehmen? Der dauerhafte Entfernungsfehler von ${(s.avgAbsPercentError * 100).toFixed(0)} % deutet auf ein Hindernis zwischen beiden hin, das sonst die Kalibrierung BEIDER Knoten verzieht.`
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
				message: `Paar ${s.pairId} von der Kalibrierung ausgenommen`,
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
			<p class="text-surface-600-400">Prüfungen werden geladen …</p>
		{:else}
			<!-- ══ Statuszeile: EINE Zahl, ohne dass jemand einen Knopf druecken muss ══ -->
			{#if todo?.status?.medianErrorM != null}
				<div class="card p-4 preset-tonal-primary">
					<div class="flex flex-wrap items-baseline gap-x-6 gap-y-1">
						<span class="text-sm text-surface-600-400">Deine Anlage</span>
						<span><strong class="text-2xl">{todo.status.medianErrorM.toFixed(2)} m</strong>
							<span class="text-sm text-surface-600-400">typischer Fehler</span></span>
						{#if todo.status.roomHitRate != null}
							<span><strong class="text-xl">{(todo.status.roomHitRate * 100).toFixed(0)} %</strong>
								<span class="text-sm text-surface-600-400">richtiger Raum</span></span>
						{/if}
						{#if todo.status.floorHitRate != null}
							<span><strong class="text-xl">{(todo.status.floorHitRate * 100).toFixed(0)} %</strong>
								<span class="text-sm text-surface-600-400">richtige Etage</span></span>
						{/if}
					</div>
					<p class="text-xs text-surface-600-400 mt-2">
						Gemessen an {todo.status.points} abgelaufenen Punkten mit der Kalibrierung, die
						<em>heute</em> gilt{#if todo.status.measuredAt} — zuletzt {new Date(todo.status.measuredAt).toLocaleString()}{/if}.
						Läuft nach jedem Optimierungsdurchgang von selbst.
					</p>
					{#if todo.status.responsiveMedianErrorM != null && todo.status.pointsWithLevels < todo.status.points}
						<p class="text-xs text-surface-600-400 mt-1">
							Davon tragen {todo.status.pointsWithLevels} Punkte aufgezeichnete Pegel und können
							deshalb überhaupt auf eine Kalibrierung reagieren — auf diesen allein sind es
							<strong>{todo.status.responsiveMedianErrorM.toFixed(2)} m</strong>
							{#if todo.status.responsiveRoomHitRate != null}und {(todo.status.responsiveRoomHitRate * 100).toFixed(0)} % richtiger Raum{/if}.
							Die Zahl oben ist die Ortungsgüte deiner Anlage, diese hier sagt, was die Kalibrierung
							bewirkt. Beide sind richtig, sie beantworten verschiedene Fragen.
						</p>
					{/if}
				</div>
			{/if}

			<!-- ══ Was du tun kannst ══ -->
			<div class="card p-4">
				<h2 class="text-lg font-semibold mb-1">Was du tun kannst</h2>
				<p class="text-xs text-surface-600-400 mb-3">
					Nur Dinge, die ein Mensch tun muss, weil sie in der echten Welt passieren. Alles, was
					eine Rechnung ist, erledigt der Assistent selbst — dafür gibt es hier keine Knöpfe.
					Nach Nutzen sortiert.
				</p>

				{#if !todo?.actions?.length}
					<p class="text-sm">Nichts zu tun — an der Anlage selbst ist gerade nichts zu verbessern.
						Der Assistent kalibriert im Hintergrund weiter.</p>
				{:else}
					<ol class="space-y-4">
						{#each todo.actions as a}
							<li class="border-l-4 pl-3 {a.rank === 1 ? 'border-primary-500' : 'border-surface-300-700'}">
								<div class="flex items-start gap-2 flex-wrap">
									<span class="badge {KIND_CLASS[a.kind as string] ?? 'preset-tonal'} shrink-0">
										{KIND_TEXT[a.kind as string] ?? a.kind}
									</span>
									<strong class="text-base">{a.title}</strong>
									{#if a.rank === 1}<span class="badge preset-tonal-primary">größter Hebel</span>{/if}
								</div>
								<p class="text-sm mt-1">{a.why}</p>
								{#if a.gain}<p class="text-sm text-surface-600-400 mt-1">{a.gain}</p>{/if}

								{#if a.nodeSpans?.length}
									<div class="overflow-x-auto mt-2">
										<table class="table table-compact text-xs">
											<thead><tr><th>Knoten</th><th class="text-right">Aufnahmen</th>
												<th class="text-right">bisher</th><th class="text-right">fehlt: ein Punkt bei</th></tr></thead>
											<tbody>
												{#each a.nodeSpans as n}
													<tr>
														<td>{n.nodeName ?? n.nodeId}</td>
														<td class="text-right">{n.points}</td>
														<td class="text-right">{n.minM}–{n.maxM} m</td>
														<td class="text-right font-semibold">
															≈{n.suggestedM} m
															{#if n.onlyInFolds}<span class="text-surface-600-400 font-normal" title="Die Spanne reicht insgesamt, aber nicht mehr, sobald zum Prüfen ein Teil zurückgehalten wird — es fehlt eine zweite Stütze am nahen Rand">(zweite Stütze)</span>{/if}
														</td>
													</tr>
												{/each}
											</tbody>
										</table>
									</div>
								{/if}

								{#if a.rooms?.length}
									<ul class="text-xs text-surface-600-400 mt-2 list-disc list-inside">
										{#each a.rooms as r}<li>{r}</li>{/each}
									</ul>
								{/if}

								{#if a.suggestions?.length}
									<p class="text-xs text-surface-600-400 mt-2">Vorgeschlagene Stellen:</p>
									<div class="flex flex-wrap gap-2 mt-1">
										{#each a.suggestions as sug}
											<button type="button" class="btn btn-sm preset-tonal-primary"
												onclick={() => vorschlagUebernehmen(sug)}
												title="Koordinaten ins Formular übernehmen">
												{sug.roomName ?? '—'} ({sug.x}, {sug.y})
											</button>
										{/each}
									</div>
								{/if}
							</li>
						{/each}
					</ol>
				{/if}
			</div>

			{#if selbstGetan.filter((c) => !c.undoneAt).length > 0}
				<div class="card p-4">
					<h2 class="text-lg font-semibold mb-1">Was der Assistent selbst umgestellt hat</h2>
					<p class="text-xs text-surface-600-400 mb-3">
						Diese Entscheidungen sind reine Messfragen — du könntest nur die oberste Zeile einer
						Rangliste anklicken. Umgestellt wird nur, wenn der Vorsprung größer ist als die
						Streuung der Messung selbst; wo nichts messbar trennt, bleibt alles wie es ist.
						Jede Umstellung lässt sich zurücknehmen.
					</p>
					<ul class="space-y-3">
						{#each selbstGetan.filter((c) => !c.undoneAt) as c}
							<li class="border-l-4 border-success-500 pl-3">
								<div class="flex items-start justify-between gap-3 flex-wrap">
									<div>
										<strong>{c.title}</strong>
										<span class="text-xs text-surface-600-400">— {new Date(c.at).toLocaleString()}</span>
									</div>
									<button type="button" class="btn btn-sm preset-tonal"
										onclick={() => umstellungZuruecknehmen(c.id)}>zurücknehmen</button>
								</div>
								<p class="text-sm mt-1">{c.reason}</p>
								<p class="text-xs text-surface-600-400 mt-1">
									vorher: {c.before.join(' + ') || '—'} → jetzt: {c.after.join(' + ')}
								</p>
							</li>
						{/each}
					</ul>
				</div>
			{/if}

			<!-- ══ Alles Weitere: eingeklappt ══ -->
			<button type="button" class="btn preset-tonal w-full justify-between"
				onclick={() => (zeigeDetails = !zeigeDetails)}>
				<span>Details — Prüfungen, Messungen, Diagnosen</span>
				<span>{zeigeDetails ? '▲' : '▼'}</span>
			</button>

			{#if zeigeDetails}
			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<h2 class="text-xl font-bold">Prüfen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Stimmen Knoten und Konfiguration? Alles Weitere misst sonst nur den Defekt.</p>
			<!-- 1. Health gate -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Zustand der Knoten</h2>
					{#if health}
						<span class="badge {health.passed ? 'preset-filled-success-500' : 'preset-filled-warning-500'}">
							{health.passed ? 'alles in Ordnung' : 'da stimmt etwas nicht'}
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
									<tr><th>Knoten</th><th>erreichbar</th><th>Telemetrie</th><th>Version</th></tr>
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
					<h2 class="text-lg font-semibold">Prüfung der Konfiguration</h2>
					{#if validation}
						<span class="badge {validation.issues.length === 0 ? 'preset-filled-success-500' : validation.hasErrors ? 'preset-filled-error-500' : 'preset-filled-warning-500'}">
							{validation.issues.length === 0 ? 'No issues' : `${validation.issues.length} issue${validation.issues.length === 1 ? '' : 's'}`}
						</span>
					{/if}
				</header>
				{#if validation}
					{#if validation.issues.length === 0}
						<p class="text-sm text-surface-600-400">Etagen-Grenzen, Raum-Umrisse und Knoten-Positionen sind in sich stimmig.</p>
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
					<h2 class="text-xl font-bold">Messen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Der Walk-Test liefert die Grundwahrheit, gegen die alle folgenden Schritte rechnen. Ohne ihn koennen sie nichts sagen.</p>
			<!-- 5. Walk test -->
			<div class="card p-4" bind:this={walkCardEl}>
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Walk-Test</h2>
					{#if walkStatus?.active}
						<span class="badge preset-filled-primary-500">läuft</span>
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
									<tr><th>Knoten</th><th>Messwerte</th><th>gemessen</th><th>Map</th><th>Fehler</th></tr>
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
							<button class="btn preset-filled-success-500" onclick={() => stopWalkTest(false)} disabled={wtBusy}>jetzt beenden</button>
							<button class="btn preset-filled-surface-500" onclick={() => stopWalkTest(true)} disabled={wtBusy}>abbrechen</button>
						</div>
					</div>
				{:else}
					{#if walkSuggestions.length > 0}
						<div class="mb-3">
							<p class="text-sm font-semibold mb-1">Wo als Nächstes messen</p>
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
							<span>Gerät</span>
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
							<span>Dauer (s)</span>
							<input class="input" type="number" min="30" max="900" bind:value={wtDuration} />
						</label>
						<button class="btn preset-filled-primary-500" onclick={startWalkTest} disabled={wtBusy || !wtDevice || wtX == null || wtY == null || wtZ == null}>
							Start
						</button>
					</div>
					<p class="text-xs text-surface-600-400 mb-3">Erst das Gerät ablegen, dann Start drücken. Die Koordinaten sind Kartenmeter wie bei den Knoten; Z ist die absolute Höhe einschließlich des Etagen-Versatzes, nicht die Höhe über dieser Etage.</p>
				{/if}

				{#if (walkStatus?.points ?? []).length > 0}
					<p class="text-sm font-semibold mb-1">Aufgezeichnete Punkte (gehen in den Optimierer):</p>
					<div class="overflow-x-auto overflow-y-auto max-h-64">
						<table class="table table-compact">
							<thead class="sticky top-0 bg-surface-100-900">
								<tr><th>Punkt</th><th>Gerät</th><th>Position</th><th>Knoten</th><th>aufgezeichnet</th><th></th></tr>
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
											<button class="btn btn-sm preset-filled-surface-500" onclick={() => deleteWalkPoint(p.id)}>löschen</button>
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">Die Punkte überleben einen Neustart. Messungen, deren empfangender Knoten seither versetzt wurde, werden von selbst übergangen.</p>
				{/if}
			</div>
			</section>

			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<h2 class="text-xl font-bold">Kalibrieren</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Aus den Messungen die Funkparameter je Knoten bestimmen - Absorption, Empfindlichkeit, Ausreisser-Paare.</p>
			<!-- 3. Calibrate now -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Kalibrierung</h2>
					<button class="btn preset-filled-primary-500" onclick={calibrateNow} disabled={calibrateBusy}>
						{calibrateBusy ? 'wird angestoßen …' : 'jetzt kalibrieren'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">Stößt einen Kalibrierdurchgang sofort an, statt auf das nächste Intervall zu warten. Sinnvoll direkt nach dem Versetzen eines Knotens oder einer Änderung seiner Koordinaten.</p>
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
							<div class="text-xs text-surface-600-400">bestes RMSE</div>
						</div>
						<div class="card p-3 preset-tonal">
							<div class="text-xl font-bold text-success-500">{$calibration?.optimizerState?.bestR?.toFixed(3) ?? 'n/a'}</div>
							<div class="text-xs text-surface-600-400">bestes R</div>
						</div>
					</div>
				{/if}
			</div>
			<!-- 2e. Calibration sweep -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Kalibrier-Durchlauf</h2>
					<button class="btn preset-filled-primary-500" onclick={runCalibrationSweep} disabled={sweepBusy}>
						{sweepBusy ? 'rechnet …' : 'starten'}
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
									<th>Kandidat</th>
									<th>reagierend</th>
									<th>alle Punkte</th>
									<th>90. Perzentil</th>
									<th>Raum</th>
									<th>Etage</th>
									<th>Absorption gefittet</th>
									<th>gezogen auf</th>
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
										<td colspan="2">kein Fit durchgeführt</td>
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
					<p class="text-sm text-surface-600-400">Noch nicht gelaufen — dafür braucht es aufgezeichnete Walk-Punkte und Knoten, die sich gerade gegenseitig hören.</p>
				{/if}
			</div>
			<!-- 6. Optimizer auto-tune -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Optimierer-Abgleich</h2>
					{#if tuneState?.running}
						<span class="badge preset-filled-primary-500">läuft</span>
					{:else}
						<button class="btn preset-filled-primary-500" onclick={startAutoTune} disabled={tuneBusy}>Abgleich starten</button>
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
								<tr><th>Kandidat</th><th>Rückhalt</th><th>Training</th><th>R</th><th>RMSE</th><th></th></tr>
							</thead>
							<tbody>
								{#if tuneState.baseline}
									<tr class="opacity-75">
										<td>{tuneState.baseline.candidate.label}</td>
										<td>{tuneState.baseline.meanHoldoutComposite.toFixed(3)}</td>
										<td>-</td>
										<td>{tuneState.baseline.meanHoldoutR.toFixed(3)}</td>
										<td>{tuneState.baseline.meanHoldoutRmse.toFixed(3)}</td>
										<td><span class="badge preset-filled-surface-500">aktuell</span></td>
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
												<button class="btn btn-sm preset-filled-warning-500" onclick={() => applyTuneCandidate(r)}>übernehmen</button>
											{/if}
										</td>
									</tr>
								{/each}
							</tbody>
						</table>
					</div>
					<p class="text-xs text-surface-600-400 mt-2">Rückhalt = mittlere Gesamtpunktzahl auf den Paaren, die vom Fit ausgenommen waren (höher ist besser). Klafft Training und Rückhalt weit auseinander, passt sich der Fit an Zufälligkeiten an statt an die Anlage.</p>
				{/if}
			</div>
			<!-- 4. Excluded pair suggestions -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Auffällige Knotenpaare</h2>
					<span class="badge {suggestions.length === 0 ? 'preset-filled-success-500' : 'preset-filled-warning-500'}">
						{suggestions.length === 0 ? 'None' : suggestions.length}
					</span>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Same-floor node pairs whose distance error stays persistently high - usually an RF obstruction (wall, appliance) between them. Pairs only appear after at least 2 hours of observation with the error above threshold most of that time, so post-move calibration transients don't trigger false suggestions; moving a node resets its pairs' statistics.
				</p>
				{#if suggestions.length === 0}
					<p class="text-sm text-surface-600-400">Keine dauerhaft auffälligen Paare. Ein Paar erscheint hier erst, wenn es über mehr als zwei Stunden gleichbleibend stark danebenliegt — ein einzelner Ausrutscher genügt nicht.</p>
				{:else}
					<div class="overflow-x-auto">
						<table class="table table-compact">
							<thead>
								<tr><th>Paar</th><th>mittl. Fehler</th><th>Bad</th><th>beobachtet</th><th></th></tr>
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
					<h2 class="text-xl font-bold">Verorten</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Welche Locators sollen laufen und mit welchen Parametern? Gemessen am Szenarien-Wettbewerb, so wie er live entscheidet.</p>
			<!-- 2d2. Which locators should be enabled -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Wahl der Ortungsverfahren</h2>
					<button class="btn preset-filled-primary-500" onclick={runLocatorSweep} disabled={locatorBusy}>
						{locatorBusy ? 'misst …' : 'messen'}
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
							<p class="text-xs text-surface-600-400 mb-2">
								Diese Wahl trifft der Assistent selbst — nach jedem Optimierungsdurchgang, und nur
								wenn der Vorsprung größer ist als die Streuung der Messung. Was er umgestellt hat,
								steht oben auf der Seite und lässt sich dort zurücknehmen.
							</p>
							<div class="flex items-start justify-between gap-3">
								<div>
									<p class="text-sm font-semibold">Recommended: {rec.label}</p>
									<p class="text-xs text-surface-600-400 mt-1">{rec.reason}</p>
								</div>
								{#if rec.alreadyConfigured}
									<span class="badge preset-filled-success-500 shrink-0">bereits eingestellt</span>
								{:else if rec.decidedBy === 'simplicity'}
									<span class="badge preset-tonal shrink-0" title="Der Vorsprung ist kleiner als die Streuung der Messung selbst — umstellen wäre Unruhe ohne Gewinn">
										nicht messbar besser
									</span>
								{:else}
									<button class="btn btn-sm preset-tonal shrink-0"
										onclick={() => applyLocatorChoice(rec.locators)} disabled={locatorBusy}
										title="Der Assistent stellt das ohnehin beim nächsten Optimierungsdurchgang selbst um — dieser Knopf nimmt es nur vorweg">
										jetzt schon umstellen
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
								<tr><th>Kombination</th><th>richtiger Raum</th><th>richtige Etage</th><th>Median</th><th>Punkte</th></tr>
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
					<p class="text-sm text-surface-600-400">Noch nicht gemessen — dafür braucht es aufgezeichnete Walk-Punkte.</p>
				{/if}
			</div>
			<!-- 7. Locator tuning via walk-test replay -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Feinabgleich der Ortung</h2>
					<button class="btn preset-filled-primary-500" onclick={runLocatorTune} disabled={locatorTuneBusy}>
						{locatorTuneBusy ? 'läuft …' : 'Wiedergabe starten'}
					</button>
				</header>
				<p class="text-sm text-surface-600-400 mb-3">
					Replays the raw per-tick readings of your recorded walk test points (real live noise, known true position) through nadaraya_watson bandwidth/kernel candidates. Scored on position accuracy AND jitter - how much the estimate wanders while the beacon sits still, i.e. the room-flapping symptom. The more walk points on different floors, the more representative the result.
				</p>
				{#if locatorTune?.error}
					{#if (walkStatus?.points?.filter((p) => (p.rawTicks ?? 0) > 0).length ?? 0) > 0}
						<p class="text-sm text-surface-600-400">Es liegen Walk-Punkte mit Rohdaten vor — auf „Wiedergabe starten“ drücken.</p>
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
								<tr><th>Kandidat</th><th>mittlerer Fehler</th><th>Streuung</th><th>Punktzahl</th><th>Messungen</th><th></th></tr>
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
											{#if !r.isCurrent && i === 0 && locatorTune?.beatsCurrentMeasurably}
												<button class="btn btn-sm preset-tonal" onclick={() => applyLocatorCandidate(r)}
													title="Der Assistent stellt das beim nächsten Optimierungsdurchgang ohnehin selbst um">
													jetzt schon umstellen</button>
											{:else if !r.isCurrent && i === 0}
												<span class="text-xs text-surface-600-400"
													title="Der Vorsprung ist kleiner als die Streuung der Messung über die Walk-Punkte">
													nicht messbar besser</span>
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
					<h2 class="text-xl font-bold">Nachweisen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Was kommt am Ende heraus? Dieselbe Messweise wie in Schritt 4, damit die Zahlen vergleichbar sind.</p>
			<!-- 2d. Accuracy benchmark -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Genauigkeits-Prüfstand</h2>
					<button class="btn btn-sm preset-tonal" onclick={runBenchmark} disabled={benchBusy}
						title="Läuft nach jedem Optimierungsdurchgang ohnehin von selbst — dieser Knopf misst nur sofort">
						{benchBusy ? 'misst …' : 'jetzt neu messen'}
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
							<span>90. Perzentil <strong>{b.p90ErrorM?.toFixed(2)} m</strong></span>
							<span>richtiger Raum <strong>{Math.round((b.roomHitRate ?? 0) * 100)}%</strong></span>
							{#if b.floorHitRate != null}
								<span>richtige Etage <strong class={b.floorHitRate < 0.95 ? 'text-warning-600-400' : ''}>{Math.round(b.floorHitRate * 100)}%</strong></span>
							{/if}
							<span class="text-surface-600-400">{b.pointsUsed} points, {b.pointsWithLevels} with signal levels</span>
							{#if b.pointsSkipped > 0}
								<span class="text-warning-600-400">{b.pointsSkipped} not scored</span>
							{/if}
						</div>
						{#if b.floors.length > 0}
							<div class="overflow-x-auto">
								<table class="table table-compact">
									<thead><tr><th>Etage</th><th>Median</th><th>gefunden</th><th>Messungen</th></tr></thead>
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
								<p class="text-sm font-semibold mb-1">Walk-Punkte nicht bewertet</p>
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
					<p class="text-sm text-surface-600-400">Noch nicht gelaufen — dafür braucht es mindestens einen Walk-Punkt.</p>
				{/if}
			</div>
			<!-- 2b. Measurement diagnostics: does the radio data agree with the map? -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Diagnose der Messungen</h2>
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
							<span>darüber hinaus: <strong>{diagnostics.far.medianAbsRssiErrorDb ?? '-'} dB</strong> ({diagnostics.far.pairs} pairs)</span>
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
											<th>Was geschieht</th>
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
												<td class="text-xs">
													{#if c.singleNodeOnly}
														<span class="text-surface-600-400">
															„{c.maxDeltaNode}" stillgelegt — übrige Messungen unberührt
														</span>
													{:else}
														<span class="text-warning-600-400">
															nicht entscheidbar → {c.idA} oder {c.idB} neu aufnehmen
														</span>
													{/if}
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
								Dezibel als weit weg. „Rest max" nennt den auffälligsten Knoten als Hinweistext.
							</p>
							<p class="text-xs text-surface-600-400 mb-3">
								Fällt <strong>nur ein einzelner Knoten</strong> aus der Reihe, legt der Assistent
								dessen Messung in beiden Aufnahmen von selbst still — welche der beiden falsch
								liegt, ist nicht bestimmbar, und der Widerspruch verschwindet nur, wenn beide
								Seiten schweigen. Alles Übrige bleibt erhalten und steht rücknehmbar in der
								Tabelle darunter.
							</p>
							<p class="text-xs text-surface-600-400 mb-3">
								Weicht dagegen der <strong>Median</strong> ab, ist eine der beiden Aufnahmen als
								Ganzes fragwürdig — und aus den Daten <em>nicht</em> zu erkennen, welche. Hier wird
								dir bewusst nichts zur Auswahl gestellt: eine Entscheidung ohne Grundlage wäre ein
								Münzwurf mit deiner Unterschrift. Was hilft, ist die Stelle noch einmal
								abzugehen — die dritte Aufnahme entscheidet die Sache von allein.
							</p>
						{/if}
					{/if}

					{#if stillgelegt.length > 0}
						<h3 class="font-semibold text-sm mb-2">Stillgelegte Messungen ({stillgelegt.length})</h3>
						<p class="text-xs text-surface-600-400 mb-2">
							Diese <em>einzelnen</em> Knotenmessungen gehen nicht mehr in Kalibrierung, Gate und
							Prüfstand ein. Die zugehörigen Walk-Punkte sind vollständig erhalten — nur diese
							Werte schweigen. Nichts davon ist gelöscht, jede Zeile lässt sich zurücknehmen.
						</p>
						<div class="overflow-auto mb-2 max-h-80">
							<table class="table table-compact w-full text-sm">
								<thead class="sticky top-0 bg-surface-100-900">
									<tr>
										<th>Punkt</th><th>Knoten</th><th class="text-right">Messwerte</th>
										<th class="text-right">Pegel</th><th class="text-right">Abstand</th>
										<th>Grund</th><th></th>
									</tr>
								</thead>
								<tbody>
									{#each stillgelegt as m}
										<tr>
											<td class="font-mono">{m.pointId}</td>
											<td>{m.nodeName ?? m.nodeId}</td>
											<td class="text-right">{m.samples}</td>
											<td class="text-right">{m.medianRssi?.toFixed(1)} dBm</td>
											<td class="text-right">{m.mapDistance?.toFixed(1)} m</td>
											<td class="text-xs" title={m.reason ?? ''}>
												{REGEL_TEXT[m.rule ?? ''] ?? m.rule ?? '—'}
											</td>
											<td>
												<button type="button" class="btn btn-sm preset-tonal-success"
													onclick={() => messungSchalten(m.pointId, m.nodeId, false)}
													title="Diese Messung wieder in die Kalibrierung aufnehmen">
													zurücknehmen
												</button>
											</td>
										</tr>
									{/each}
								</tbody>
							</table>
						</div>
						<p class="text-xs text-surface-600-400 mb-3">
							Automatisch laufen nur Regeln, die <strong>ohne das Modell</strong> auskommen, das mit
							denselben Daten kalibriert wird: zu wenige Messwerte, oder ein Pegel, der stärker ist
							als auf dieser Entfernung physikalisch möglich. Urteile, die die Kalibrierung selbst
							zum Maßstab nehmen, bleiben Vorschläge — sonst würden so lange Abweichungen entfernt,
							bis der Fit schön aussieht, und die Ortung würde schlechter, ohne dass es auffällt.
						</p>
					{/if}

					{#if (diagnostics.staleWalkPoints?.length ?? 0) > 0 && diagnostics.staleWalkPoints}
						<h3 class="font-semibold text-sm mb-2">Walk-Punkte ohne aufgezeichnete Pegel ({diagnostics.staleWalkPoints.length})</h3>
						<p class="text-xs text-surface-600-400 mb-2">
							<strong>Diese Punkte sind in Ordnung</strong> — sie stammen nur aus einer Zeit, in der
							die Pegel noch nicht mitgeschrieben wurden. Für den Locator zählen sie voll mit; nur
							eine Kalibrierung können sie nicht bewerten, weil sie auf eine geänderte Kalibrierung
							gar nicht reagieren. Die Statuszeile oben nennt deshalb beide Zahlen getrennt.
							Es gibt hier <em>nichts nachzuholen</em>.
						</p>
						<p class="text-xs text-surface-600-400 mb-2">
							Wo du dennoch messen solltest, steht oben unter „Was du tun kannst" — dort zählt die
							Entfernung zu bestimmten Knoten, nicht diese Liste. Und beachte: ein neuer Spaziergang
							legt einen <em>zusätzlichen</em> Punkt an, der alte bleibt stehen. Die Liste wird also
							nur kürzer, wenn du hier löschst — was du nur tun solltest, wenn ein Punkt wirklich
							falsch ist.
						</p>
						<div class="overflow-x-auto mb-3">
							<table class="table table-compact w-full text-sm">
								<thead>
									<tr><th>Punkt</th><th>Etage</th><th>Raum</th><th>Position</th><th class="text-right">Messungen</th><th>aufgenommen</th><th></th></tr>
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
						<h3 class="font-semibold text-sm mb-2">Knoten-Abdeckung je Raum</h3>
						<p class="text-xs text-surface-600-400 mb-2">
							Measured here: spots with a node within 1.5 m averaged 1.1 m position error, spots beyond it
							2.5 m. Distance to the third-nearest node made no difference - one node close enough is what counts.
						</p>
						<div class="overflow-x-auto overflow-y-auto max-h-80">
							<table class="table table-compact">
								<thead><tr><th>Raum</th><th>Etage</th><th>nächster Knoten</th><th>schlechteste Ecke</th><th>in Reichweite</th></tr></thead>
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
			{/if}

			<button type="button" class="btn preset-tonal w-full justify-between"
				onclick={() => (zeigeExperte = !zeigeExperte)}>
				<span>Experte — Einstellungen, Grenzen, Konfigurationsdateien</span>
				<span>{zeigeExperte ? '▲' : '▼'}</span>
			</button>

			{#if zeigeExperte}
			<section class="space-y-6">
				<header class="flex items-baseline gap-3 pt-2">
					<span class="badge preset-tonal-surface shrink-0">Experten</span>
					<h2 class="text-xl font-bold">Einstellungen</h2>
				</header>
				<p class="text-sm text-surface-600-400 -mt-4">Schreibt direkt in die config.yaml. Wer hier dreht, sollte wissen warum - die Schritte oben messen und empfehlen dieselben Werte.</p>
			<!-- 8. Settings -->
			<div class="card p-4">
				<header class="flex items-center justify-between mb-3">
					<h2 class="text-lg font-semibold">Einstellungen</h2>
					<div class="flex gap-2">
						<button class="btn preset-tonal" onclick={() => (settingsOpen = !settingsOpen)}>{settingsOpen ? 'ausblenden' : 'anzeigen'}</button>
						{#if settingsOpen}
							<button class="btn preset-filled-primary-500" onclick={saveSettings} disabled={settingsBusy || !settings}>speichern</button>
						{/if}
					</div>
				</header>
				{#if settingsOpen && settings}
					<div class="grid grid-cols-1 md:grid-cols-2 gap-6">
						<div>
							<h3 class="font-semibold text-sm mb-2">Optimierung</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm">
									<span>Intervall (s)</span>
									<input class="input" type="number" min="15" bind:value={settings.intervalSecs} />
								</label>
								<label class="label text-sm">
									<span>Aufnahmefenster (min)</span>
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
									<span>Absorptions-Strafterm</span>
									<input class="input" type="number" step="0.5" bind:value={settings.weights.absorption_penalty} />
								</label>
								<label class="label text-sm">
									<span>Optimierer</span>
									<select class="select" bind:value={settings.optimizer}>
										<option value="per_node_absorption">per_node_absorption</option>
										<option value="global_absorption">global_absorption</option>
										<option value="legacy">legacy</option>
									</select>
								</label>
								<label class="label text-sm"><span>Tx-Referenz min</span><input class="input" type="number" step="1" bind:value={settings.limits.tx_ref_rssi_min} /></label>
								<label class="label text-sm"><span>Tx-Referenz max</span><input class="input" type="number" step="1" bind:value={settings.limits.tx_ref_rssi_max} /></label>
								<label class="label text-sm"><span>Rx-Korrektur min</span><input class="input" type="number" step="1" bind:value={settings.limits.rx_adj_rssi_min} /></label>
								<label class="label text-sm"><span>Rx-Korrektur max</span><input class="input" type="number" step="1" bind:value={settings.limits.rx_adj_rssi_max} /></label>
							</div>
						</div>
						<div>
							<h3 class="font-semibold text-sm mb-2">Ortungsverfahren</h3>
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
							<h3 class="font-semibold text-sm mb-2">Allgemein</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Zeitgrenze (s)</span><input class="input" type="number" min="5" bind:value={settings.timeout} /></label>
								<label class="label text-sm"><span>Abwesenheits-Zeitgrenze (s)</span><input class="input" type="number" min="10" bind:value={settings.awayTimeout} /></label>
								<label class="label text-sm"><span>Geräte-Aufbewahrung</span><input class="input" placeholder="30d" bind:value={settings.deviceRetention} /></label>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">Filterung (Kalman)</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Prozessrauschen</span><input class="input" type="number" step="0.001" bind:value={settings.filteringProcessNoise} /></label>
								<label class="label text-sm"><span>Messrauschen</span><input class="input" type="number" step="0.01" bind:value={settings.filteringMeasurementNoise} /></label>
								<label class="label text-sm"><span>Höchstgeschwindigkeit (m/s)</span><input class="input" type="number" step="0.1" bind:value={settings.filteringMaxVelocity} /></label>
								<label class="label text-sm"><span>Glättungsgewicht</span><input class="input" type="number" step="0.05" min="0" max="1" bind:value={settings.filteringSmoothingWeight} /></label>
								<label class="label text-sm"><span>Bewegungs-Sigma</span><input class="input" type="number" step="0.1" bind:value={settings.filteringMotionSigma} /></label>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">Verlauf</h3>
							<div class="space-y-2">
								<div class="flex items-center gap-3">
									<input type="checkbox" class="checkbox" bind:checked={settings.historyEnabled} id="hist-en" />
									<label for="hist-en" class="text-sm">eingeschaltet</label>
								</div>
								<label class="label text-sm"><span>DB</span><input class="input" bind:value={settings.historyDb} /></label>
								<label class="label text-sm"><span>verfällt nach</span><input class="input" placeholder="24h" bind:value={settings.historyExpireAfter} /></label>
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
									<label class="label text-sm"><span>Wandstärke</span><input class="input" type="number" step="0.05" bind:value={settings.mapWallThickness} /></label>
									<label class="label text-sm"><span>Wandfarbe</span><input class="input" placeholder="#888888" bind:value={settings.mapWallColor} /></label>
									<label class="label text-sm"><span>Wand-Deckkraft</span><input class="input" type="number" step="0.05" min="0" max="1" bind:value={settings.mapWallOpacity} /></label>
								</div>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">GPS</h3>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Breitengrad</span><input class="input" type="number" step="0.000001" bind:value={settings.gpsLatitude} /></label>
								<label class="label text-sm"><span>Längengrad</span><input class="input" type="number" step="0.000001" bind:value={settings.gpsLongitude} /></label>
								<label class="label text-sm"><span>Höhe (m)</span><input class="input" type="number" step="0.1" bind:value={settings.gpsElevation} /></label>
								<label class="label text-sm"><span>Drehung (°)</span><input class="input" type="number" step="0.1" bind:value={settings.gpsRotation} /></label>
							</div>
							<div class="flex items-center gap-3 mt-2">
								<input type="checkbox" class="checkbox" bind:checked={settings.gpsReport} id="gps-rep" />
								<label for="gps-rep" class="text-sm">GPS-Position melden</label>
							</div>
							<h3 class="font-semibold text-sm mb-2 mt-4">MQTT</h3>
							<p class="text-xs text-surface-600-400 mb-1">Host leer lassen, um den vom Supervisor bereitgestellten Broker zu benutzen — die Vorgabe für HA-Apps.</p>
							<div class="grid grid-cols-2 gap-3">
								<label class="label text-sm"><span>Host</span><input class="input" bind:value={settings.mqttHost} /></label>
								<label class="label text-sm"><span>Port</span><input class="input" type="number" bind:value={settings.mqttPort} /></label>
								<label class="label text-sm"><span>Benutzer</span><input class="input" bind:value={settings.mqttUsername} /></label>
								<label class="label text-sm"><span>Passwort</span><input class="input" type="password" bind:value={settings.mqttPassword} /></label>
							</div>
							<div class="flex items-center gap-3 mt-2">
								<input type="checkbox" class="checkbox" bind:checked={settings.mqttSsl} id="mqtt-ssl" />
								<label for="mqtt-ssl" class="text-sm">SSL</label>
							</div>
						</div>
					</div>
					<p class="text-xs text-surface-600-400 mt-3">Schreibt unmittelbar in die genannten Abschnitte der config.yaml. Änderungen greifen nach wenigen Sekunden, ein Neustart ist nicht nötig. ⚠ Ein falscher MQTT-Broker trennt den Companion von der gesamten Flotte — die Supervisor-Vorgabe stimmt fast immer. Die Gewichte für Korrelation und RMSE sowie die Etagen-Zuordnung stehen hier bewusst nicht: dort richtet ein Zahlendreher mehr Schaden an, als die Bequemlichkeit wert wäre.</p>
				{/if}
			</div>
			</section>
			{/if}
		{/if}
	</div>
</div>
