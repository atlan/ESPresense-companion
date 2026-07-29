<script lang="ts">
	import Map from '$lib/Map.svelte';
	import FloorTabs from '$lib/FloorTabs.svelte';
	import BackgroundUpload from '$lib/BackgroundUpload.svelte';
	import { gotoDetail, gotoWalkSetup } from '$lib/urls';
	import { page } from '$app/stores';
	import { showConfirm } from '$lib/modal/modalStore';

	export let floorId: string | null = null;

	// Eine Position aus der Adresszeile anspringen: ?floor=basement&x=6&y=2 setzt die Etage und
	// markiert den Punkt auf der Karte. Gebraucht wird das von der Nachhol-Liste im Setup-Wizard -
	// Koordinaten in einer Tabelle sagen einem nicht, wo man hinlaufen muss, ein Punkt auf dem
	// Grundriss schon.
	$: sp = $page.url.searchParams;
	$: spot =
		sp.has('x') && sp.has('y') && Number.isFinite(Number(sp.get('x'))) && Number.isFinite(Number(sp.get('y')))
			? { x: Number(sp.get('x')), y: Number(sp.get('y')) }
			: null;
	// z wird NUR durchgereicht, nicht gezeichnet - die Karte ist zweidimensional.
	// Gebraucht wird es fuer die Uebernahme ins Walk-Test-Formular: z ist die
	// absolute Gebaeudehoehe und streut INNERHALB einer Etage um ~1,5 m. Ein
	// Vorgabewert waere geraten, und man liefe den Punkt in der falschen Hoehe nach.
	$: spotZ = sp.has('z') && Number.isFinite(Number(sp.get('z'))) ? Number(sp.get('z')) : null;

	// Klick auf die Markierung: Position in den Walk-Test uebernehmen und dorthin
	// springen. Mit Rueckfrage, weil es die Eingaben im Formular ueberschreibt.
	async function markerUebernehmen() {
		if (!spot) return;
		const hoehe = spotZ !== null ? `${spotZ}` : 'unbekannt';
		const ok = await showConfirm({
			title: 'Position in den Walk-Test übernehmen',
			body:
				`Diese Stelle (${spot.x} / ${spot.y} / ${hoehe}) in das Walk-Test-Formular übernehmen ` +
				`und dorthin wechseln?` +
				(spotZ === null
					? ' ⚠ Es wurde keine Höhe mitgegeben — sie müsste im Formular von Hand gesetzt werden.'
					: '')
		});
		if (!ok) return;
		gotoWalkSetup(spot.x, spot.y, spotZ ?? 0);
	}

	// Die Etage NUR beim Wechsel der Adresse setzen, nicht reaktiv auf floorId: sonst haengt die
	// Zuweisung an ihrer eigenen Ausgabe und schnappt bei jedem manuellen Etagenwechsel zurueck.
	let appliedSearch: string | null = null;
	$: if ($page.url.search !== appliedSearch) {
		appliedSearch = $page.url.search;
		const f = new URLSearchParams($page.url.search).get('floor');
		if (f) floorId = f;
	}
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full bg-surface-50-950">
	<FloorTabs bind:floorId />
	<BackgroundUpload />
	<!-- calibrate schaltet in Map.svelte ausschliesslich die Sichtbarkeit des Markers frei (Zeile 175)
	     und sonst nichts - ohne das Flag bleibt calibrationSpot wirkungslos. Genau daran ist der erste
	     Versuch gescheitert. -->
	<Map
		onselected={(item) => gotoDetail(item)}
		bind:floorId
		editable
		calibrate={spot != null}
		calibrationSpot={spot}
		calibrationSpotInteractive={false}
		onCalibrationSpotClick={markerUebernehmen}
	/>
</div>
