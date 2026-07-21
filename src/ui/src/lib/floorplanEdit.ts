import { writable } from 'svelte/store';

/**
 * Shared state between the floorplan editor's SVG layer (inside the LayerCake <Svg>) and its HTML
 * toolbar/panel (outside the SVG) - they live in different render contexts inside Map.svelte.
 */
export type EditMode = 'off' | 'nodes' | 'rooms';

export const editMode = writable<EditMode>('off');

// nodes mode
export const selectedNodeId = writable<string | null>(null);
/** Unsaved position edits per node id (map coordinates). */
export const nodeEdits = writable<Record<string, { x: number; y: number; z: number }>>({});
/** When set, the next map click places a new node at that position. */
export const placingNode = writable<boolean>(false);
/** A newly placed (not yet saved) node: position chosen by click, id/name entered in the panel. */
export const pendingNode = writable<{ x: number; y: number; z: number } | null>(null);

// rooms mode
export const selectedRoomId = writable<string | null>(null);
/** Unsaved polygon edits per room id. */
export const roomEdits = writable<Record<string, number[][]>>({});
/** In-progress new-room vertex list (click to append), null = not drafting. */
export const draftRoom = writable<number[][] | null>(null);

/**
 * Session-only tracing image rendered in MAP coordinates below the editor layers, so it pans and
 * zooms with the map - lets the user draw rooms over a scanned floor plan. widthM sets the scale
 * (image height follows the aspect ratio); movable=true routes drags to the image.
 */
export interface TraceImage {
	url: string;
	x: number;
	y: number;
	widthM: number;
	aspect: number; // height / width of the source image
	opacity: number;
	movable: boolean;
}
export const traceImage = writable<TraceImage | null>(null);

export function resetEditState() {
	selectedNodeId.set(null);
	nodeEdits.set({});
	placingNode.set(false);
	pendingNode.set(null);
	selectedRoomId.set(null);
	roomEdits.set({});
	draftRoom.set(null);
	// traceImage intentionally survives mode switches - losing the aligned image on an
	// accidental mode change would be infuriating; it's cleared explicitly via its Remove button.
}
