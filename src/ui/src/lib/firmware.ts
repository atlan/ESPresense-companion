import { writable, derived } from 'svelte/store';
import { apiPath } from '$lib/api';
import type { FirmwareManifest, Release, WorkflowRun } from '$lib/types';

export const updateMethod: SvelteStore<string> = writable('self');
// Empty by default: no GitHub source is queried until the user actively picks one
// (keeps the anonymous 60 req/h GitHub API budget untouched on page load).
export const firmwareSource: SvelteStore<string> = writable('');
export const flavor: SvelteStore<string> = writable();
export const version: SvelteStore<string> = writable();
export const artifact: SvelteStore<string> = writable();
// Asset filename within a selected release of the personal fork (see forkReleases
// below) - the fork doesn't have a board/flavor catalog like $firmwareTypes, so its
// asset list is used directly instead of the cpu/flavor-driven lookup.
export const forkAsset: SvelteStore<string> = writable('');

export const firmwareTypes = writable<FirmwareManifest | null>(null, function start(set) {
	fetch(apiPath('/api/firmware/types'))
		.then((r) => r.json())
		.then((r) => set(r));
});

export const cpuNames = derived(firmwareTypes, (a) =>
	a?.cpus?.reduce((acc, cur) => {
		acc.set(cur.value, cur.name);
		return acc;
	}, new Map<string, string>())
);

export const flavorNames = derived(firmwareTypes, (a) =>
	a?.flavors?.reduce((acc, cur) => {
		acc.set(cur.value, cur.name);
		return acc;
	}, new Map<string, string>())
);

// GitHub-backed firmware sources (releases / fork releases / CI artifacts).
//
// Lazy + source-scoped to respect GitHub's 60 req/h anonymous rate limit: nothing is
// fetched on page load. A source is polled only while the update UI is mounted AND it
// is the selected source (see setActiveSource / deactivateSources, wired to
// VersionPicker's lifecycle + firmwareSource). Refresh interval: 60 min.
const REFRESH_MS = 60 * 60000;

function lazySource<T>(fetchImpl: () => Promise<Map<string, T[]>>) {
	const { subscribe, set } = writable<Map<string, T[]>>(new Map());
	let interval: ReturnType<typeof setInterval> | null = null;
	let errors = 0;
	let outstanding = false;

	async function run() {
		if (outstanding) return;
		outstanding = true;
		try {
			set(await fetchImpl());
			errors = 0;
		} catch (ex) {
			if (++errors > 5) set(new Map<string, T[]>());
			console.log(ex);
		} finally {
			outstanding = false;
		}
	}

	return {
		subscribe,
		activate() {
			if (interval) return; // already polling this source
			run();
			interval = setInterval(run, REFRESH_MS);
		},
		deactivate() {
			if (interval) {
				clearInterval(interval);
				interval = null;
			}
		}
	};
}

function groupReleases(data: Release[], minAssets: number): Map<string, Release[]> {
	return data
		.filter((i) => i.assets.length > minAssets)
		.reduce((p: Map<string, Release[]>, c) => {
			const key = c.prerelease ? 'Beta' : 'Release';
			const arr = p.get(key);
			if (arr) arr.push(c);
			else p.set(key, [c]);
			return p;
		}, new Map<string, Release[]>());
}

async function fetchReleases(): Promise<Map<string, Release[]>> {
	const res = await fetch('https://api.github.com/repos/ESPresense/ESPresense/releases', { credentials: 'same-origin' });
	return groupReleases(await res.json(), 5);
}

// Fork releases (github.com/atlan/ESPresense): no >5-assets filter - unlike upstream's
// per-board build matrix, a fork release may legitimately ship just one board's firmware.
async function fetchForkReleases(): Promise<Map<string, Release[]>> {
	const res = await fetch('https://api.github.com/repos/atlan/ESPresense/releases', { credentials: 'same-origin' });
	return groupReleases(await res.json(), 0);
}

async function fetchArtifacts(): Promise<Map<string, WorkflowRun[]>> {
	const res = await fetch('https://api.github.com/repos/ESPresense/ESPresense/actions/workflows/build.yml/runs?status=success&per_page=100', { credentials: 'same-origin' });
	const data: { workflow_runs: WorkflowRun[] } = await res.json();
	const wf = data.workflow_runs.filter((i) => i.head_repository.full_name === 'ESPresense/ESPresense' && i.status == 'completed' && (i.pull_requests.length > 0 || (i.head_branch == 'main' && Date.now() - +new Date(i.created_at) < 1000 * 60 * 60 * 24 * 7)));
	return wf.reduce((p: Map<string, WorkflowRun[]>, c) => {
		const arr = p.get(c.head_branch);
		if (arr) arr.push(c);
		else p.set(c.head_branch, [c]);
		return p;
	}, new Map<string, WorkflowRun[]>());
}

export const releases = lazySource<Release>(fetchReleases);
export const forkReleases = lazySource<Release>(fetchForkReleases);
export const artifacts = lazySource<WorkflowRun>(fetchArtifacts);

/** Poll exactly the selected GitHub source and stop the others. */
export function setActiveSource(src: string): void {
	releases.deactivate();
	forkReleases.deactivate();
	artifacts.deactivate();
	if (src === 'release') releases.activate();
	else if (src === 'fork') forkReleases.activate();
	else if (src === 'artifact') artifacts.activate();
}

/** Stop all GitHub polling (call when the update UI unmounts). */
export function deactivateSources(): void {
	releases.deactivate();
	forkReleases.deactivate();
	artifacts.deactivate();
}

export function getFirmwareUrl(firmwareSource: string, version: string, artifact: string, firmware: string): string | null {
	if (firmware) {
		switch (firmwareSource) {
			case 'artifact':
				if (artifact) return `https://espresense.com/artifacts/download/runs/${artifact}/${firmware}`;
				break;
			case 'release':
				if (version) return `https://github.com/ESPresense/ESPresense/releases/download/${version}/${firmware}`;
				break;
			case 'fork':
				if (version) return `https://github.com/atlan/ESPresense/releases/download/${version}/${firmware}`;
				break;
		}
	}
	return null;
}

export function getLocalFirmwareUrl(firmwareSource: string, version: string, artifact: string, firmware: string): string | null {
	const url = getFirmwareUrl(firmwareSource, version, artifact, firmware);
	if (!url) return null;

	const loc = new URL(apiPath('/api/firmware/download'), window.location.href);

	const params = new URLSearchParams();
	params.append('url', url);
	loc.search = params.toString();

	return loc.toString();
}

type Callback = (percentComplete: number, message: string) => void;

export async function firmwareUpdate(id: string, url: string, callback: Callback): Promise<void> {
	var loc = new URL(apiPath(`/ws/firmware/update/${id}`), window.location.href);
	var wsUrl = (loc.protocol === 'https:' ? 'wss:' : 'ws:') + '//' + loc.host + loc.pathname + `?${new URLSearchParams({ url: url })}`;
	const ws = new WebSocket(wsUrl);

	ws.addEventListener('message', (event) => {
		const data = event.data;

		try {
			const json = JSON.parse(data);
			const { percentComplete, message } = json;
			callback(percentComplete, message);
		} catch (e) {
			console.error('Could not parse message:', data);
		}
	});

	ws.addEventListener('error', (event) => {
		console.error(`WebSocket Error: ${event}`);
	});

	ws.addEventListener('close', (event) => {
		if (event.wasClean) {
			console.log(`Connection closed cleanly, code=${event.code}, reason=${event.reason}`);
		} else {
			console.error(`Connection died`);
		}
	});

	return new Promise<void>((resolve, reject) => {
		ws.addEventListener('close', () => {
			resolve();
		});

		ws.addEventListener('error', () => {
			reject(new Error('WebSocket error'));
		});
	});
}
