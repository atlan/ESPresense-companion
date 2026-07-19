<script lang="ts">
	import { modalStore } from './modalStore.js';
	import ConfirmModal from './ConfirmModal.svelte';
	import AlertModal from './AlertModal.svelte';
	import ComponentModal from './ComponentModal.svelte';

	function isInsideInteractive(element: HTMLElement | null): boolean {
		if (!element) return false;
		
		// Check if the element itself is interactive
		const interactiveTags = ['INPUT', 'TEXTAREA', 'BUTTON', 'SELECT', 'A'];
		if (interactiveTags.includes(element.tagName)) return true;
		
		// Check if the element is contentEditable
		if (element.isContentEditable) return true;
		
		// Check if the element has interactive attributes
		if (element.hasAttribute('tabindex') && element.getAttribute('tabindex') !== '-1') return true;
		if (element.getAttribute('role') === 'button' || element.getAttribute('role') === 'link') return true;
		
		// Check parent elements up the tree
		return isInsideInteractive(element.parentElement);
	}

	async function handleKeydown(event: KeyboardEvent) {
		if ($modalStore.length === 0) return;

		const modal = $modalStore[$modalStore.length - 1];

		if (event.key === 'Escape') {
			event.preventDefault();
			event.stopPropagation();
			if (modal.onCancel) {
				await modal.onCancel();
			} else if (modal.onConfirm) {
				await modal.onConfirm();
			} else {
				modalStore.close();
			}
		} else if (event.key === 'Enter') {
			const target = event.target as HTMLElement;
			if (isInsideInteractive(target)) {
				return;
			}
			event.preventDefault();
			if (modal.onConfirm) {
				await modal.onConfirm();
			}
		}
	}
</script>

<svelte:window onkeydown={handleKeydown} />

{#each $modalStore as modal (modal.id)}
	<!-- svelte-ignore a11y_click_events_have_key_events -->
	<div
		class="fixed inset-0 z-50 flex items-center justify-center p-4"
		onclick={async () => {
			if (modal.onCancel) {
				await modal.onCancel();
			} else if (modal.onConfirm) {
				await modal.onConfirm();
			} else {
				modalStore.close();
			}
		}}
		role="dialog"
		tabindex="-1"
		aria-modal="true"
		aria-labelledby="modal-title-{modal.id}"
	>
		<!-- Click-outside-to-close backdrop. Keyboard users get the same
		     "close the modal" action via Escape (handled by the window-level
		     handleKeydown above), so no separate onkeydown is needed here. -->
		<!-- Backdrop -->
		<div class="absolute inset-0 bg-black/50 backdrop-blur-sm"></div>

		<!-- Modal Content -->
		<!-- svelte-ignore a11y_click_events_have_key_events -->
		<!-- svelte-ignore a11y_no_static_element_interactions -->
		<!-- Pure event-propagation guard (stops the backdrop's close-on-click from
		     firing when the click originated inside the modal content) - not a
		     user-facing action in its own right, so there's no keyboard
		     equivalent to add. -->
		<div class="relative z-10 max-w-lg w-full" onclick={(e) => e.stopPropagation()} aria-hidden="false">
			{#if modal.type === 'alert'}
				<AlertModal {modal} />
			{:else if modal.type === 'confirm'}
				<ConfirmModal {modal} />
			{:else if modal.type === 'component' && modal.component}
				<ComponentModal {modal} />
			{/if}
		</div>
	</div>
{/each}
