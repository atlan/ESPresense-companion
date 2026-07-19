import { base } from '$app/paths';

/**
 * Build a base-path-aware URL for a backend endpoint (`/api/...`, `/ws/...`).
 *
 * SvelteKit's typed `resolve()` from `$app/paths` only accepts the app's own
 * page routes (checked against the generated route manifest) - it rejects
 * arbitrary paths like backend API/WebSocket endpoints at the type level,
 * even though calling it with such a path happens to work at runtime today.
 * `base` is the officially documented building block for exactly this case
 * (see the SvelteKit `paths.base` docs: "you can use `base` from `$app/paths`
 * for that"), so this is the same result without fighting the route types.
 */
export function apiPath(path: string): string {
	return `${base}${path}`;
}
