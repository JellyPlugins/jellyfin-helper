(function () {
    'use strict';

    if (window.JellyfinHelperUi) return;

    var dialogs = [];
    var backgroundState = new Map();
    var originalOverflow = null;
    var keyboardInput = false;
    // Pointer activation does not always move focus, so dialogs retain the actual control.
    var pointerTrigger = null;
    var focusSelector = 'button, a[href], input, select, textarea, [tabindex]';
    var panelTransitions = new WeakMap();
    var panelDuration = 200;

    if (!document.getElementById('jfh-ui-styles')) {
        var style = document.createElement('style');
        style.id = 'jfh-ui-styles';
        style.textContent =
            '.jfh-ui-surface{--color-primary:#00a4dc;--color-primary-hover:#0088b8;--color-success:#2ecc71;--color-danger:#e74c3c;--color-button-primary:#00749c;--color-button-success:#167d44;--color-button-danger:#b92c1d;--color-surface-card:#1c1c1e;--color-text:#eeeeee;--color-text-muted:#b6b6bd;--color-border:rgba(255,255,255,.15);--color-primary-scrollbar:rgba(0,164,220,.3);--color-primary-scrollbar-hover:rgba(0,164,220,.5);--ease-out:cubic-bezier(.23,1,.32,1);--motion-press:120ms;--motion-feedback:180ms}' +
            '.jfh-ui-surface[hidden],.jfh-ui-surface [hidden]{display:none!important}' +
            '.jfh-ui-dialog{box-sizing:border-box;max-width:calc(100vw - 2rem);max-height:calc(100vh - 2rem);max-height:calc(100dvh - 2rem);overflow:auto;overscroll-behavior:contain}' +
            '.jfh-ui-surface .jfh-card-exiting{opacity:0;transform:scale(.98);transition:opacity var(--motion-feedback) var(--ease-out),transform var(--motion-feedback) var(--ease-out);pointer-events:none}' +
            '.jfh-keyboard-input .jfh-ui-surface .action-btn:active,.jfh-keyboard-input .jfh-ui-surface .health-clickable:active,.jfh-keyboard-input .jfh-ui-surface .codec-row:active,.jfh-keyboard-input .jfh-ui-surface .jfh-discovery-btn:active{transform:none;transition:none}' +
            '.jfh-ui-surface button:focus-visible,.jfh-ui-surface a:focus-visible{outline:2px solid var(--color-primary);outline-offset:2px}' +
            '.jfh-toast-stack{position:fixed;bottom:calc(1rem + env(safe-area-inset-bottom,0px));left:50%;transform:translateX(-50%);z-index:100001;display:flex;flex-direction:column;gap:.5rem;width:min(480px,calc(100vw - 2rem));max-height:50vh;max-height:50dvh;overflow:auto;pointer-events:none}' +
            '.jfh-toast{position:relative;box-sizing:border-box;width:100%;padding:.9em 2.8rem .9em 1.4em;background:var(--color-surface-card);color:var(--color-text);font-size:.88em;line-height:1.4;border-radius:8px;border-left:4px solid var(--color-danger);box-shadow:0 4px 24px rgba(0,0,0,.4);overflow-wrap:anywhere;opacity:0;pointer-events:none;transform:translateY(8px);transition:opacity var(--motion-feedback) var(--ease-out),transform var(--motion-feedback) var(--ease-out)}' +
            '.jfh-toast-visible{opacity:1;pointer-events:auto;transform:none}' +
            '.jfh-toast-hidden{opacity:0;pointer-events:none;transform:translateY(8px)}' +
            '.jfh-toast-close{position:absolute;right:.4rem;top:.4rem;background:transparent;border:none;color:inherit;cursor:pointer;width:32px;height:32px}.jfh-toast-close::before{content:"×";font-size:1.3rem}' +
            '@media(pointer:coarse){.jfh-ui-surface :where(input:not([type]),input[type="text"],input[type="number"],input[type="password"],input[type="search"],input[type="email"],input[type="url"],input[type="tel"],textarea){font-size:max(16px,1em)!important}.jfh-ui-surface .action-btn,.jfh-ui-surface .jfh-discovery-btn,.jfh-ui-surface .jfh-discovery-tab,.jfh-ui-surface .jfh-discovery-popup-item,.jfh-ui-surface .jfh-discovery-popup-cancel{touch-action:manipulation}}' +
            '@media(prefers-color-scheme:light){.jfh-ui-surface{--color-primary:#00749c;--color-surface-card:#ffffff;--color-text:#17171b;--color-text-muted:#565660;--color-border:rgba(0,0,0,.15);--color-primary-scrollbar:rgba(0,136,184,.55);--color-primary-scrollbar-hover:rgba(0,136,184,.75)}}' +
            '@media(prefers-reduced-motion:reduce){.jfh-ui-surface .jfh-card-exiting,.jfh-toast{transform:none;transition:none}}';
        document.head.appendChild(style);
    }

    function reducedMotion() {
        return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
    }

    function focusable(root) {
        return Array.from(root.querySelectorAll(focusSelector)).filter(function (element) {
            if (element.disabled || element.tabIndex < 0 || element.closest('[hidden], [inert], [aria-hidden="true"]')) return false;
            var style = getComputedStyle(element);
            return style.display !== 'none' && style.visibility !== 'hidden';
        });
    }

    function syncDialogs() {
        dialogs = dialogs.filter(function (entry) { return entry.overlay.isConnected; });
        backgroundState.forEach(function (wasInert, element) {
            element.toggleAttribute('inert', wasInert);
        });
        backgroundState.clear();
        var current = dialogs.at(-1);
        if (current) {
            for (var child of document.body.children) {
                if (child === current.overlay || child.contains(current.overlay) || child.matches('script, style')) continue;
                backgroundState.set(child, child.hasAttribute('inert'));
                child.setAttribute('inert', '');
            }
            if (originalOverflow === null) originalOverflow = document.body.style.overflow;
            if (document.body.style.overflow !== 'hidden') document.body.style.overflow = 'hidden';
        } else if (originalOverflow !== null) {
            document.body.style.overflow = originalOverflow;
            originalOverflow = null;
        }
        return current;
    }

    function openDialog(overlay, dialog, trigger, onClose) {
        var opener = trigger || overlay._jfhTrigger || getDialogTrigger();
        overlay.classList.add('jfh-ui-surface');
        dialog.classList.add('jfh-ui-dialog');
        dialog.setAttribute('role', 'dialog');
        dialog.setAttribute('aria-modal', 'true');
        dialog.tabIndex = -1;
        document.body.appendChild(overlay);
        var cancel = dialog.querySelector('[data-dialog-cancel]');
        dialogs.push({ overlay: overlay, dialog: dialog, trigger: opener, onClose: onClose || (cancel ? function () { cancel.click(); } : null) });
        syncDialogs();
        var initial = dialog.querySelector('[data-dialog-cancel]') || focusable(dialog)[0] || dialog;
        initial.focus({ preventScroll: true });
    }

    function closeDialog(overlay, restoreFocus) {
        if (!overlay) return;
        var entry = dialogs.find(function (item) { return item.overlay === overlay; });
        var ownedFocus = overlay.contains(document.activeElement);
        overlay.remove();
        var current = syncDialogs();
        if (restoreFocus !== false && ownedFocus) {
            var target = entry?.trigger;
            if (target?.isConnected && !target.disabled && !target.closest('[hidden], [inert]')) target.focus({ preventScroll: true });
            else if (current) (focusable(current.dialog)[0] || current.dialog).focus({ preventScroll: true });
            else if (target?.disabled) queueMicrotask(function () {
                if (document.activeElement === document.body && target.isConnected && !target.disabled && !target.closest('[hidden], [inert], .hide')) {
                    target.focus({ preventScroll: true });
                }
            });
        }
    }

    document.addEventListener('keydown', function (event) {
        if (!keyboardInput) {
            keyboardInput = true;
            document.documentElement.classList.add('jfh-keyboard-input');
        }
        var current = dialogs.length ? syncDialogs() : null;
        if (!current) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopImmediatePropagation();
            if (current.onClose) current.onClose();
            else closeDialog(current.overlay);
        } else if (event.key === 'Tab') {
            var items = focusable(current.dialog);
            var first = items[0] || current.dialog;
            var last = items.at(-1) || current.dialog;
            if (!current.dialog.contains(document.activeElement) || (event.shiftKey ? document.activeElement === first : document.activeElement === last)) {
                event.preventDefault();
                (event.shiftKey ? last : first).focus({ preventScroll: true });
            }
        }
    }, true);

    document.addEventListener('pointerdown', function (event) {
        pointerTrigger = event.target?.closest?.('button, a[href], input, select, textarea, [role="button"]') || null;
        if (keyboardInput) {
            keyboardInput = false;
            document.documentElement.classList.remove('jfh-keyboard-input');
        }
    }, true);

    function getDialogTrigger() {
        return !keyboardInput && pointerTrigger?.isConnected ? pointerTrigger : document.activeElement;
    }

    document.addEventListener('focusin', function (event) {
        var current = dialogs.length ? syncDialogs() : null;
        if (current && !current.dialog.contains(event.target)) {
            (focusable(current.dialog)[0] || current.dialog).focus({ preventScroll: true });
        }
    });

    function setExpanded(body, expanded, trigger, options) {
        if (!body) return;
        var before = options?.before || (options ? panelFrame(body) : null);
        cancelPanelTransition(body);
        if (!expanded && body.contains(document.activeElement)) trigger?.focus({ preventScroll: true });
        options?.update?.();
        body.hidden = false;
        body.toggleAttribute('inert', !expanded);
        body.setAttribute('aria-hidden', String(!expanded));
        if (trigger) trigger.setAttribute('aria-expanded', String(expanded));
        if (before && options.animate !== false && !reducedMotion() && !document.hidden && typeof body.animate === 'function') {
            startPanelTransition(body, expanded, before, options);
        } else {
            body.hidden = !expanded;
            if (!expanded) options?.onClosed?.();
        }
    }

    function panelFrame(body) {
        var height = body.getBoundingClientRect().height;
        var style = getComputedStyle(body);
        var frame = {
            height: height + 'px',
            opacity: height === 0 ? '0' : style.opacity,
            visibility: 'visible',
            boxSizing: 'border-box',
            overflow: 'hidden',
            minHeight: '0px',
            maxHeight: 'none'
        };
        for (var property of ['paddingTop', 'paddingBottom', 'paddingLeft', 'paddingRight', 'marginTop', 'marginBottom',
            'borderTopWidth', 'borderBottomWidth', 'borderLeftWidth', 'borderRightWidth', 'borderColor', 'borderStyle', 'backgroundColor']) {
            frame[property] = style[property];
        }
        return height === 0 ? collapsedPanelFrame(frame) : frame;
    }

    function collapsedPanelFrame(frame) {
        return { ...frame, height: '0px', opacity: '0', paddingTop: '0px', paddingBottom: '0px',
            marginTop: '0px', marginBottom: '0px', borderTopWidth: '0px', borderBottomWidth: '0px' };
    }

    function isExpanded(body) {
        if (!body) return false;
        var hidden = body.getAttribute('aria-hidden');
        return hidden === null ? !body.hidden && getComputedStyle(body).display !== 'none' : hidden === 'false';
    }

    function cancelPanelTransition(body) {
        var state = panelTransitions.get(body);
        if (!state) return;
        panelTransitions.delete(body);
        clearTimeout(state.timer);
        state.observer?.disconnect();
        window.removeEventListener('resize', state.retarget);
        state.motionQuery?.removeEventListener?.('change', state.motionChanged);
        cancelPanelEffect(state);
    }

    function cancelPanelEffect(state) {
        if (!state.animation) return;
        state.animation.onfinish = null;
        state.animation.oncancel = null;
        state.animation.cancel();
        state.animation = null;
    }

    function finishPanelTransition(body, state) {
        if (panelTransitions.get(body) !== state) return;
        body.hidden = !state.expanded;
        cancelPanelTransition(body);
        if (!state.expanded) state.onClosed?.();
    }

    // Presentation values preserve continuity on reversal without retaining inline styles.
    function animatePanelFrames(body, state, before) {
        cancelPanelEffect(state);
        var duration = Math.max(0, state.deadline - Date.now());
        if (duration < 16 || !body.isConnected) {
            finishPanelTransition(body, state);
            return;
        }
        var after = state.expanded ? panelFrame(body) : collapsedPanelFrame(before);
        var from = { ...before, paddingLeft: after.paddingLeft, paddingRight: after.paddingRight,
            borderLeftWidth: after.borderLeftWidth, borderRightWidth: after.borderRightWidth,
            borderColor: after.borderColor, borderStyle: after.borderStyle, backgroundColor: after.backgroundColor };
        if (from.height === after.height && from.opacity === after.opacity) {
            finishPanelTransition(body, state);
            return;
        }
        try {
            state.animation = body.animate([from, after], { duration: duration, easing: 'cubic-bezier(0.25, 0.1, 0.25, 1)', fill: 'both' });
            state.animation.onfinish = function () { finishPanelTransition(body, state); };
            state.animation.oncancel = function () { finishPanelTransition(body, state); };
        } catch {
            finishPanelTransition(body, state);
        }
    }

    function startPanelTransition(body, expanded, before, options) {
        var state = { expanded: expanded, onClosed: options.onClosed, deadline: Date.now() + (options.duration || panelDuration), animation: null };
        panelTransitions.set(body, state);
        state.retarget = function () {
            if (panelTransitions.get(body) === state) animatePanelFrames(body, state, panelFrame(body));
        };
        state.timer = setTimeout(function () { finishPanelTransition(body, state); }, (options.duration || panelDuration) + 50);
        if (expanded) {
            state.observer = new MutationObserver(state.retarget);
            state.observer.observe(body, { childList: true, subtree: true, characterData: true, attributes: true, attributeFilter: ['class', 'hidden', 'style'] });
            window.addEventListener('resize', state.retarget);
        }
        state.motionQuery = window.matchMedia?.('(prefers-reduced-motion: reduce)');
        state.motionChanged = function (event) {
            if (event.matches) finishPanelTransition(body, state);
        };
        state.motionQuery?.addEventListener?.('change', state.motionChanged);
        animatePanelFrames(body, state, before);
    }

    function syncTabs(list, selector, active, panelFor) {
        if (!list) return;
        list.setAttribute('role', 'tablist');
        for (var button of list.querySelectorAll(selector)) {
            var selected = button === active;
            var panel = panelFor(button);
            button.type = 'button';
            button.setAttribute('role', 'tab');
            button.setAttribute('aria-selected', String(selected));
            button.tabIndex = selected ? 0 : -1;
            if (panel) {
                if (!button.id) button.id = panel.id + '-tab';
                button.setAttribute('aria-controls', panel.id);
                panel.setAttribute('role', 'tabpanel');
                panel.setAttribute('aria-labelledby', button.id);
                panel.tabIndex = 0;
                panel.hidden = !selected;
            }
        }
        if (list.dataset.jfhTabsBound) return;
        list.dataset.jfhTabsBound = '1';
        list.addEventListener('keydown', function (event) {
            if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
            var button = event.target.closest(selector);
            if (!button) return;
            var buttons = Array.from(list.querySelectorAll(selector)).filter(function (item) {
                return !item.hidden && !item.disabled && getComputedStyle(item).display !== 'none';
            });
            var index = buttons.indexOf(button);
            if (index < 0) return;
            event.preventDefault();
            var rtl = getComputedStyle(list).direction === 'rtl';
            var next;
            if (event.key === 'Home') next = 0;
            else if (event.key === 'End') next = buttons.length - 1;
            else {
                var step = (event.key === 'ArrowRight') !== rtl ? 1 : -1;
                next = (index + step + buttons.length) % buttons.length;
            }
            buttons[next].focus({ preventScroll: true });
        });
    }

    function removeCard(card, done) {
        if (!card?.isConnected) return;
        var grid = card.parentElement;
        var cards = Array.from(grid.children);
        var index = cards.indexOf(card);
        if (card.contains(document.activeElement)) {
            var next = cards.slice(index + 1).concat(cards.slice(0, index).reverse()).find(function (item) {
                return !item.hasAttribute('inert') && focusable(item).length;
            });
            var target = next ? focusable(next)[0] : grid;
            if (target === grid) grid.tabIndex = -1;
            target.focus({ preventScroll: true });
        }
        card.setAttribute('inert', '');
        card.classList.add('jfh-card-exiting');
        var finished = false;
        function finish() {
            if (finished) return;
            finished = true;
            clearTimeout(timer);
            card.removeEventListener('transitionend', ended);
            card.remove();
            if (done) done();
        }
        function ended(event) {
            if (event.target === card && event.propertyName === 'opacity') finish();
        }
        var timer = setTimeout(finish, reducedMotion() ? 0 : 200);
        card.addEventListener('transitionend', ended);
    }

    function showToast(message, options) {
        if (!message) return null;
        var className = options?.className ?? 'jfh-toast';
        var closeLabel = options?.closeLabel ?? 'Close';
        var remaining = options?.duration ?? 5000;
        var stack = document.getElementById('jfh-toast-stack');
        if (!stack) {
            stack = document.createElement('div');
            stack.id = 'jfh-toast-stack';
            stack.className = 'jfh-toast-stack jfh-ui-surface';
            document.body.appendChild(stack);
        }
        var toast = document.createElement('div');
        toast.className = className;
        toast.setAttribute('role', 'alert');
        toast.textContent = message;
        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'jfh-toast-close';
        close.setAttribute('aria-label', closeLabel);
        toast.appendChild(close);
        stack.appendChild(toast);
        toast.getBoundingClientRect();
        toast.classList.add(className + '-visible');
        var started = 0;
        var timer = null;
        var hovered = false;
        var closed = false;
        function pause() {
            if (timer !== null) {
                clearTimeout(timer);
                remaining = Math.max(0, remaining - (Date.now() - started));
                timer = null;
            }
        }
        function resume() {
            pause();
            if (closed || document.hidden || hovered || toast.contains(document.activeElement)) return;
            started = Date.now();
            timer = setTimeout(function () { closeToast(toast); }, remaining);
        }
        function visibility() { if (document.hidden) pause(); else resume(); }
        toast._jfhDismiss = function (immediate) {
            if (closed) return;
            closed = true;
            pause();
            document.removeEventListener('visibilitychange', visibility);
            toast.classList.remove(className + '-visible');
            toast.classList.add(className + '-hidden');
            function remove() {
                toast.remove();
                if (!stack.children.length) stack.remove();
            }
            if (immediate || reducedMotion()) remove();
            else setTimeout(remove, 200);
        };
        close.addEventListener('click', function () { closeToast(toast); });
        toast.addEventListener('click', function () { closeToast(toast); });
        toast.addEventListener('mouseenter', function () { hovered = true; pause(); });
        toast.addEventListener('mouseleave', function () { hovered = false; resume(); });
        toast.addEventListener('focusin', pause);
        toast.addEventListener('focusout', function () { queueMicrotask(resume); });
        document.addEventListener('visibilitychange', visibility);
        resume();
        return toast;
    }

    function closeToast(toast, immediate) {
        toast?._jfhDismiss?.(immediate);
    }

    function ownsDialogTrigger(root) {
        return dialogs.some(function (entry) { return entry.overlay.isConnected && root.contains(entry.trigger); });
    }

    window.JellyfinHelperUi = {
        reducedMotion: reducedMotion,
        openDialog: openDialog,
        closeDialog: closeDialog,
        setExpanded: setExpanded,
        isExpanded: isExpanded,
        measurePanel: panelFrame,
        syncTabs: syncTabs,
        removeCard: removeCard,
        showToast: showToast,
        closeToast: closeToast,
        ownsDialogTrigger: ownsDialogTrigger,
        isKeyboardInput: function () { return keyboardInput; },
        getDialogTrigger: getDialogTrigger
    };
})();
