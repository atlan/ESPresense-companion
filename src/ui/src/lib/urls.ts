import { resolve } from '$app/paths';
import { goto } from '$app/navigation';
import { isNode, type Device, type Node } from '$lib/types';

/**
 * Navigate to the detail page for a Node or Device.
 *
 * If `d` is a Node, navigates to `/nodes/{id}`; otherwise navigates to `/devices/{id}`.
 *
 * @param d - The target Device or Node. If `d` is `null` or has no `id`, the generated URL will include `undefined`.
 */
export function gotoDetail(d: Device | Node | null) {
	if (isNode(d)) goto(resolve(`/nodes/${d?.id ?? ''}`));
	else goto(resolve(`/devices/${d?.id ?? ''}`));
}

/**
 * Navigate to the 3D view for the given device.
 *
 * @param deviceId - The device identifier to include in the path; if `null` the literal `"null"` will be placed in the URL (`.../3d/null`).
 */
export function gotoDetail3d(deviceId: string | null) {
	goto(resolve(`/3d/${deviceId ?? ''}`));
}

/**
 * Navigate to a device's calibration view.
 *
 * If `d` is null or `d.id` is undefined, the generated URL will include `undefined` in place of the device id.
 *
 * @param d - The device to calibrate (may be `null`); navigation occurs as a side effect.
 */
export function gotoMap() {
	goto(resolve('/'));
}

/**
 * Navigate to the map and mark one spot on it.
 *
 * Muss ueber resolve()+goto laufen, nicht ueber ein rohes href="/?...": im HA-Ingress laeuft die App
 * unter einem Praefix, und ein absoluter Pfad verlaesst die App - der Rahmen laedt dann Home
 * Assistant neu und landet auf dessen Startseite statt auf der Karte. Genau das ist beim ersten
 * Versuch passiert.
 */
// z ist OPTIONAL, aber wichtig: die Karte selbst ist zweidimensional, doch wer den
// Punkt von dort in das Walk-Test-Formular uebernimmt, braucht die Hoehe. z ist die
// ABSOLUTE Gebaeudehoehe und streut INNERHALB einer Etage um ~1,5 m - ein Vorgabewert
// waere geraten, und man liefe den Punkt in der falschen Hoehe nach.
export function gotoMapSpot(floorId: string | null | undefined, x: number, y: number, z?: number) {
	const q = new URLSearchParams();
	if (floorId) q.set('floor', floorId);
	q.set('x', String(x));
	q.set('y', String(y));
	if (z !== undefined && z !== null) q.set('z', String(z));
	// Kein rohes href: unter HA-Ingress liegt die App hinter einem Praefix, ein
	// absoluter Pfad wuerde HA neu laden statt in der App zu bleiben.
	goto(`${resolve('/')}?${q}`);
}

export function gotoDevices() {
	goto(resolve('/devices'));
}

export function gotoNodes() {
	goto(resolve('/nodes'));
}

export function gotoCalibration(target?: Device | string | null) {
	const id = typeof target === 'string' ? target : target?.id;
	goto(resolve(id ? `/calibration/devices/${id}` : '/calibration'));
}

/**
 * Open the calibration page on the Setup tab with the walk-test coordinates prefilled
 * (used by the map's walk-point picker).
 */
export function gotoWalkSetup(x: number, y: number, z: number) {
	goto(`${resolve('/calibration')}?tab=setup&walk_x=${x}&walk_y=${y}&walk_z=${z}`);
}
