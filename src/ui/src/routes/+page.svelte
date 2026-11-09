<script lang="ts">
	import Map from '#lib/Map.svelte';
	import FloorTabs from '#lib/FloorTabs.svelte';
	import BackgroundUpload from '#lib/BackgroundUpload.svelte';
	import NodeLinkControls from '#lib/NodeLinkControls.svelte';
	import type { LinkDirection } from '#lib/nodeLinks.js';
	import { gotoDetail, gotoWalkSetup } from '#lib/urls.js';
	import { showConfirm } from '#lib/modal/modalStore.js';
	import { page } from '$app/state';

	// Local state, not a prop: SvelteKit route components only receive `data` and `form`.
	let floorId: string | null = $state(null);

	let showLinks = $state(false);
	let nearest: number | null = $state(4);
	let direction: LinkDirection = $state('all');

	// Eine Position aus der Adresszeile anspringen: ?floor=basement&x=6&y=2 setzt die Etage und
	// markiert den Punkt auf der Karte. Gebraucht wird das von der Nachhol-Liste im Setup-Wizard -
	// Koordinaten in einer Tabelle sagen einem nicht, wo man hinlaufen muss, ein Punkt auf dem
	// Grundriss schon.
	let sp = $derived(page.url.searchParams);
	let spot = $derived(sp.has('x') && sp.has('y') && Number.isFinite(Number(sp.get('x'))) && Number.isFinite(Number(sp.get('y'))) ? { x: Number(sp.get('x')), y: Number(sp.get('y')) } : null);
	// z wird NUR durchgereicht, nicht gezeichnet - die Karte ist zweidimensional.
	// Gebraucht wird es fuer die Uebernahme ins Walk-Test-Formular: z ist die
	// absolute Gebaeudehoehe und streut INNERHALB einer Etage um ~1,5 m. Ein
	// Vorgabewert waere geraten, und man liefe den Punkt in der falschen Hoehe nach.
	let spotZ = $derived(sp.has('z') && Number.isFinite(Number(sp.get('z'))) ? Number(sp.get('z')) : null);

	// Klick auf die Markierung: Position in den Walk-Test uebernehmen und dorthin
	// springen. Mit Rueckfrage, weil es die Eingaben im Formular ueberschreibt.
	async function markerUebernehmen() {
		if (!spot) return;
		const hoehe = spotZ !== null ? `${spotZ}` : 'unbekannt';
		const ok = await showConfirm({
			title: 'Position in den Walk-Test übernehmen',
			body: `Diese Stelle (${spot.x} / ${spot.y} / ${hoehe}) in das Walk-Test-Formular übernehmen ` + `und dorthin wechseln?` + (spotZ === null ? ' ⚠ Es wurde keine Höhe mitgegeben — sie müsste im Formular von Hand gesetzt werden.' : '')
		});
		if (!ok) return;
		gotoWalkSetup(spot.x, spot.y, spotZ ?? 0);
	}

	// Die Etage NUR beim Wechsel der Adresse setzen, nicht reaktiv auf floorId: sonst haengt die
	// Zuweisung an ihrer eigenen Ausgabe und schnappt bei jedem manuellen Etagenwechsel zurueck.
	let appliedSearch: string | null = null;
	$effect.pre(() => {
		const search = page.url.search;
		if (search === appliedSearch) return;
		appliedSearch = search;
		const f = new URLSearchParams(search).get('floor');
		if (f) floorId = f;
	});
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full bg-surface-50-950">
	<FloorTabs bind:floorId />
	<BackgroundUpload />
	<NodeLinkControls bind:enabled={showLinks} bind:nearest bind:direction />
	<Map onselected={(item) => gotoDetail(item)} bind:floorId links={showLinks ? { nearest, direction } : null} editable calibrationSpot={spot} calibrationSpotInteractive={false} onCalibrationSpotClick={markerUebernehmen} />
</div>
