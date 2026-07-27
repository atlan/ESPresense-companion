<script lang="ts">
	import Map from '$lib/Map.svelte';
	import FloorTabs from '$lib/FloorTabs.svelte';
	import BackgroundUpload from '$lib/BackgroundUpload.svelte';
	import { gotoDetail } from '$lib/urls';
	import { page } from '$app/stores';

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
	<Map onselected={(item) => gotoDetail(item)} bind:floorId editable calibrationSpot={spot} />
</div>
