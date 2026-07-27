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
	$: spotFloor = sp.get('floor');
	$: spotX = Number(sp.get('x'));
	$: spotY = Number(sp.get('y'));
	$: spot = Number.isFinite(spotX) && Number.isFinite(spotY) && sp.has('x') && sp.has('y')
		? { x: spotX, y: spotY }
		: null;
	$: if (spotFloor && floorId !== spotFloor) floorId = spotFloor;
</script>

<svelte:head>
	<title>ESPresense Companion: Map</title>
</svelte:head>

<div class="w-full h-full bg-surface-50-950">
	<FloorTabs bind:floorId />
	<BackgroundUpload />
	<Map onselected={(item) => gotoDetail(item)} bind:floorId editable calibrationSpot={spot} />
</div>
