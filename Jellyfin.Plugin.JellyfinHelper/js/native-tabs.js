// Jellyfin Helper - registry for the Home page native tab strip.
// Jellyfin's tab mechanism is generic: a button in .emby-tabs-slider with data-index N
// and a .tabContent.pageTabContent panel at the same position is enough, because
// emby-tabs selects by position. Owning the tab here removes the dependency on
// third party tab plugins and keeps the mechanism in one place.
(function () {
    'use strict';

    var OWN_ID_PREFIX = 'jfh-tab-';
    var RESERVED_ID_PREFIX = 'jfh-tab-reserved-';
    var LINK_ID_PREFIX = 'jfh-tab-link-';
    var GROUP_ID = 'jfh-native-tabs-group';
    var SEPARATOR_ID = 'jfh-native-tabs-separator';
    var DEEP_LINK_PARAM = 'jfhTab';

    var entries = [];
    var injectPending = false;
    var appliedDeepLink = null;
    var rewrittenHash = null;
    var userPickedHash = null;
    var selectingTab = false;
    var watchedTabs = null;
    var lastTabStripInput = 0;
    var pendingFocusId = null;
    var visibilityCache = {};

    function isOnHomePage() {
        var hash = window.location.hash;
        return hash === '' || hash === '#/home' || hash === '#/home.html'
            || hash.indexOf('#/home?') !== -1 || hash.indexOf('#/home.html?') !== -1;
    }

    function findEntry(id) {
        for (var i = 0; i < entries.length; i++) {
            if (entries[i].id === id) {
                return entries[i];
            }
        }
        return null;
    }

    function isActiveTabContainer(element) {
        if (!element || !element.isConnected) {
            return false;
        }
        var node = element.parentElement;
        while (node && node !== document.body) {
            if (node.hidden || node.classList.contains('hide')) {
                return false;
            }
            if (node.classList.contains('tabContent') && !node.classList.contains('is-active')) {
                return false;
            }
            node = node.parentElement;
        }
        return element.offsetParent !== null;
    }

    // The shared parent of the native tab panels. Cached Home pages may linger in
    // the document, so the visible one wins.
    function getTabsRoot() {
        var panels = document.querySelectorAll('.tabContent.pageTabContent[data-index="0"]');
        for (var i = panels.length - 1; i >= 0; i--) {
            var root = panels[i].parentElement;
            if (root && isActiveTabContainer(root)) {
                return root;
            }
        }
        return null;
    }

    function isOwnSlot(element) {
        return element.id.indexOf(OWN_ID_PREFIX) === 0;
    }

    function slotIndex(element) {
        return parseInt(element.getAttribute('data-index'), 10);
    }

    function highestForeignIndex(slider, root) {
        var max = 1;
        var consider = function (index) {
            if (!isNaN(index) && index > max) {
                max = index;
            }
        };
        Array.prototype.forEach.call(slider.querySelectorAll('.emby-tab-button'), function (el) {
            if (!isOwnSlot(el)) {
                consider(slotIndex(el));
            }
        });
        Array.prototype.forEach.call(root.querySelectorAll('.tabContent'), function (el) {
            if (!isOwnSlot(el)) {
                consider(slotIndex(el));
            }
        });
        // Other plugins on the modern layout only add Home deep links, so the
        // header links are their only trace in the DOM.
        document.querySelectorAll('a[href*="#/home?"], a[href*="#/home.html?"]').forEach(function (el) {
            if (el.closest('[id^="' + OWN_ID_PREFIX + '"]')) {
                return;
            }
            var match = /^#\/home(?:\.html)?\?(?:[^#]*&)?tab=(\d+)/.exec(el.getAttribute('href') || '');
            if (match) {
                consider(parseInt(match[1], 10));
            }
        });
        return max;
    }

    function relabel(entry, index) {
        var previous = entry.index;
        entry.index = index;
        var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
        var panel = document.getElementById(OWN_ID_PREFIX + 'panel-' + entry.id);
        if (btn) {
            btn.setAttribute('data-index', String(index));
        }
        if (panel) {
            panel.setAttribute('data-index', String(index));
        }
        if (previous == null) {
            return;
        }
        var tabsElem = document.querySelector('[is="emby-tabs"]');
        if (tabsElem && btn && btn.classList.contains('emby-tab-button-active') && tabsElem.selectedTabIndex === previous) {
            tabsElem.selectedTabIndex = index;
        }
    }

    function createReservedSlot(kind, index) {
        var el;
        if (kind === 'btn') {
            el = document.createElement('button');
            el.type = 'button';
            el.className = 'emby-tab-button hide';
            el.tabIndex = -1;
            el.setAttribute('aria-hidden', 'true');
        } else {
            el = document.createElement('div');
            el.className = 'tabContent pageTabContent';
        }
        el.id = RESERVED_ID_PREFIX + kind + '-' + index;
        el.setAttribute('data-index', String(index));
        return el;
    }

    // Positions must match indices because Jellyfin resolves tabs positionally.
    // Slots below our first index that nothing fills become hidden placeholders,
    // and every tab from index 2 up is kept in index order. Idempotent.
    function alignSlots(container, kind, lastIndex) {
        var selector = kind === 'btn' ? '.emby-tab-button' : '.tabContent';
        var items = function () {
            return Array.prototype.filter.call(container.querySelectorAll(selector),
                function (el) { return el.parentElement === container; });
        };
        var changed = false;
        var claimed = {};
        items().forEach(function (el) {
            if (!isOwnSlot(el)) {
                claimed[slotIndex(el)] = true;
            }
        });
        entries.forEach(function (entry) {
            if (entry.index != null) {
                claimed[entry.index] = true;
            }
        });

        items().forEach(function (el) {
            if (el.id.indexOf(RESERVED_ID_PREFIX) !== 0) {
                return;
            }
            var idx = slotIndex(el);
            if (claimed[idx] || idx >= lastIndex) {
                el.remove();
                changed = true;
            }
        });
        for (var i = 2; i < lastIndex; i++) {
            if (claimed[i]) {
                continue;
            }
            var slot = document.getElementById(RESERVED_ID_PREFIX + kind + '-' + i);
            if (slot && slot.parentElement === container) {
                continue;
            }
            container.appendChild(slot || createReservedSlot(kind, i));
            changed = true;
        }

        var all = items();
        if (all.some(function (el) { return isNaN(slotIndex(el)); })) {
            return changed;
        }
        var head = all.filter(function (el) { return slotIndex(el) < 2; });
        var tabs = all.filter(function (el) { return slotIndex(el) >= 2; });
        var sorted = tabs.slice().sort(function (x, y) {
            return slotIndex(x) - slotIndex(y) || tabs.indexOf(x) - tabs.indexOf(y);
        });
        var ref = head.length ? head[head.length - 1] : null;
        sorted.forEach(function (el) {
            var target = ref ? ref.nextElementSibling : container.firstElementChild;
            if (target !== el) {
                container.insertBefore(el, target);
                changed = true;
            }
            ref = el;
        });
        return changed;
    }

    function getHeaderButtonTray() {
        var legacy = document.querySelector('.headerRight:not([data-jfh-header-fallback])');
        if (legacy && legacy.offsetParent !== null) {
            return legacy;
        }

        var userMenuButton = document.querySelector('[aria-controls="app-user-menu"]');
        var toolbar = (userMenuButton && userMenuButton.closest('.MuiToolbar-root'))
            || document.querySelector('.MuiAppBar-root .MuiToolbar-root');
        if (!toolbar) {
            return null;
        }

        var userMenuBox = userMenuButton ? userMenuButton.closest('.MuiBox-root') : null;
        var tray = userMenuBox ? userMenuBox.previousElementSibling : null;
        if (tray && tray.hasAttribute('data-jfh-header-fallback')
            && tray.previousElementSibling && tray.previousElementSibling.matches('.MuiBox-root')) {
            tray = tray.previousElementSibling;
        }
        if (tray && !tray.matches('button, a, [role="button"]')) {
            return tray;
        }

        var container = toolbar.querySelector(':scope > .headerRight');
        if (!container) {
            container = document.createElement('div');
            container.className = 'headerRight';
            container.dataset.jfhHeaderFallback = '';
            if (userMenuBox) {
                toolbar.insertBefore(container, userMenuBox);
            } else {
                toolbar.appendChild(container);
            }
        }
        return container;
    }

    function getOrCreateGroup(headerRight) {
        var group = document.getElementById(GROUP_ID);
        if (group) {
            return group;
        }
        group = document.createElement('div');
        group.id = GROUP_ID;
        group.style.cssText = 'display:flex;align-items:center;order:-1;';

        var separator = document.createElement('span');
        separator.id = SEPARATOR_ID;
        separator.setAttribute('aria-hidden', 'true');
        separator.style.cssText = 'display:inline-block;width:1px;height:1.4em;margin:0 0.5em;background:rgba(255,255,255,0.3);';
        group.appendChild(separator);

        headerRight.appendChild(group);
        return group;
    }

    function removeGroupIfEmpty() {
        var group = document.getElementById(GROUP_ID);
        if (group && group.children.length <= 1) {
            group.remove();
        }
    }

    function isTabButtonVisible(btn, id) {
        var cached = visibilityCache[id];
        var now = Date.now();
        if (cached && cached.btn === btn && now - cached.ts < 1000) {
            return cached.visible;
        }
        var visible = btn.offsetParent !== null;
        visibilityCache[id] = { btn: btn, ts: now, visible: visible };
        return visible;
    }

    // On Jellyfin 12's experimental layout the tab strip lives in a hidden header,
    // so the button exists but cannot be clicked. A header tray link keeps the tab
    // reachable there without adding a second entry point on the stable layout.
    function ensureDiscoverable(entry) {
        var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
        var linkId = LINK_ID_PREFIX + entry.id;

        if (entry.index == null || (btn && isTabButtonVisible(btn, entry.id))) {
            var stale = document.getElementById(linkId);
            if (stale) {
                stale.remove();
            }
            removeGroupIfEmpty();
            return;
        }
        if (document.getElementById(linkId)) {
            return;
        }

        var headerRight = getHeaderButtonTray();
        if (!headerRight) {
            return;
        }
        if (document.getElementById(linkId)) {
            return;
        }

        var group = getOrCreateGroup(headerRight);
        var separator = document.getElementById(SEPARATOR_ID);

        var link = document.createElement('button');
        link.id = linkId;
        link.type = 'button';
        link.setAttribute('is', 'paper-icon-button-light');
        link.className = 'headerButton headerButtonRight paper-icon-button-light';
        link.title = entry.title;
        link.setAttribute('aria-label', entry.title);
        link.innerHTML = '<i class="material-icons" aria-hidden="true">' + (entry.icon || 'tab') + '</i>';
        link.addEventListener('click', function () {
            if (entry.index != null) {
                window.location.hash = deepLinkHash(entry);
            }
        });

        group.insertBefore(link, separator);
    }

    function hashParam(name) {
        var match = new RegExp('[?&]' + name + '=([^&]*)').exec(window.location.hash);
        if (!match) {
            return null;
        }
        try {
            return decodeURIComponent(match[1]);
        } catch (e) {
            return match[1];
        }
    }

    function homeRoute() {
        var hash = window.location.hash;
        return hash.indexOf('#/home') === 0 ? hash.split('?')[0] : '#/home';
    }

    function watchUserTabChanges(tabsElem) {
        if (!tabsElem || tabsElem === watchedTabs) {
            return;
        }
        watchedTabs = tabsElem;
        tabsElem.addEventListener('beforetabchange', function (e) {
            if (selectingTab) {
                return;
            }
            var urlIndex = parseInt(hashParam('tab'), 10);
            var picked = parseInt(e.detail && e.detail.selectedTabIndex, 10);
            if (picked !== (isNaN(urlIndex) ? 0 : urlIndex) || Date.now() - lastTabStripInput < 1000) {
                userPickedHash = window.location.hash;
            }
        });
        tabsElem.addEventListener('tabchange', function (e) {
            var picked = parseInt(e.detail && e.detail.selectedTabIndex, 10);
            setTimeout(function () {
                clearStraySelection(tabsElem, picked);
            }, 0);
        });
    }

    // Jellyfin only clears the previously highlighted tab, so a half applied
    // selection can leave a stray panel visible under later tabs.
    function clearStraySelection(tabsElem, index) {
        var root = getTabsRoot();
        if (!root || isNaN(index) || !tabsElem.selectedIndex || tabsElem.selectedIndex() !== index) {
            return;
        }
        var btn = tabsElem.querySelectorAll('.emby-tab-button')[index];
        var panel = root.querySelectorAll('.tabContent')[index];
        if (!btn || !panel || slotIndex(btn) !== index || slotIndex(panel) !== index) {
            return;
        }
        if (!btn.classList.contains('emby-tab-button-active') || !panel.classList.contains('is-active')) {
            return;
        }
        Array.prototype.forEach.call(root.querySelectorAll('.tabContent.is-active'), function (el) {
            if (el !== panel) {
                el.classList.remove('is-active');
            }
        });
        Array.prototype.forEach.call(tabsElem.querySelectorAll('.emby-tab-button-active'), function (el) {
            if (el !== btn) {
                el.classList.remove('emby-tab-button-active');
            }
        });
    }

    function noteTabStripInput(e) {
        if (e.isTrusted && e.target && e.target.closest && e.target.closest('[is="emby-tabs"]')) {
            lastTabStripInput = Date.now();
        }
    }

    // tab=N lets Jellyfin act on the link natively, while jfhTab=<id> keeps the
    // target stable when other tabs shift the index.
    function deepLinkHash(entry) {
        return homeRoute() + '?tab=' + entry.index + '&' + DEEP_LINK_PARAM + '=' + encodeURIComponent(entry.id);
    }

    function syncDeepLink(slider, root) {
        var wantedId = hashParam(DEEP_LINK_PARAM);
        var wantedIndex = parseInt(hashParam('tab'), 10);
        var entry = wantedId != null ? findEntry(wantedId) : null;
        if (entry && entry.index != null && entry.index !== wantedIndex) {
            var staleHash = window.location.hash;
            var freshHash = deepLinkHash(entry);
            rewrittenHash = freshHash;
            history.replaceState(history.state, '', window.location.pathname + window.location.search + freshHash);
            wantedIndex = entry.index;
            if (userPickedHash === staleHash) {
                userPickedHash = freshHash;
            }
            if (appliedDeepLink && appliedDeepLink.hash === staleHash
                && appliedDeepLink.panel === document.getElementById(OWN_ID_PREFIX + 'panel-' + entry.id)) {
                appliedDeepLink.hash = window.location.hash;
                appliedDeepLink.btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
            }
        }
        if (isNaN(wantedIndex) || wantedIndex < 2) {
            return;
        }
        if (userPickedHash === window.location.hash) {
            return;
        }

        var tabsElem = slider.closest('[is="emby-tabs"]') || document.querySelector('[is="emby-tabs"]');
        var btn = tabsElem && tabsElem.querySelectorAll('.emby-tab-button')[wantedIndex];
        var panel = root.querySelectorAll('.tabContent')[wantedIndex];
        if (!tabsElem || !tabsElem.selectedIndex || !btn || !panel
            || slotIndex(btn) !== wantedIndex || slotIndex(panel) !== wantedIndex) {
            return;
        }
        var hash = window.location.hash;
        if (appliedDeepLink && appliedDeepLink.hash === hash && appliedDeepLink.root === root
            && appliedDeepLink.panel === panel && appliedDeepLink.tabs === tabsElem) {
            var stillSelected = tabsElem.selectedIndex() === wantedIndex
                && panel.classList.contains('is-active')
                && !Array.prototype.some.call(tabsElem.querySelectorAll('.emby-tab-button-active'),
                    function (el) { return el !== btn; });
            if (appliedDeepLink.btn === btn || !stillSelected) {
                appliedDeepLink.btn = btn;
                return;
            }
        }
        var strays = function () {
            return Array.prototype.filter.call(root.querySelectorAll('.tabContent.is-active'),
                function (el) { return el !== panel; }).concat(Array.prototype.filter.call(
                tabsElem.querySelectorAll('.emby-tab-button-active'), function (el) { return el !== btn; }));
        };
        var isApplied = function () {
            return tabsElem.selectedIndex() === wantedIndex && panel.classList.contains('is-active')
                && btn.classList.contains('emby-tab-button-active') && strays().length === 0;
        };
        if (!isApplied()) {
            selectingTab = true;
            try {
                tabsElem.selectedIndex(wantedIndex);
            } finally {
                selectingTab = false;
            }
            strays().forEach(function (el) {
                el.classList.remove('is-active', 'emby-tab-button-active');
            });
        }
        if (isApplied()) {
            appliedDeepLink = { hash: hash, root: root, panel: panel, tabs: tabsElem, btn: btn };
        }
    }

    function applyPendingFocus() {
        if (!pendingFocusId) {
            return;
        }
        var entry = findEntry(pendingFocusId);
        if (!entry || entry.index == null) {
            return;
        }
        var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
        if (!btn) {
            return;
        }
        pendingFocusId = null;
        btn.click();
    }

    function ensureInjected() {
        if (entries.length === 0 || !isOnHomePage()) {
            return;
        }

        var slider = document.querySelector('.emby-tabs-slider');
        var root = getTabsRoot();
        if (!slider || !root) {
            return;
        }

        watchUserTabChanges(slider.closest('[is="emby-tabs"]'));

        var addedTabButton = false;
        var nextIndex = highestForeignIndex(slider, root) + 1;

        entries.forEach(function (entry) {
            var index = nextIndex++;
            if (entry.index !== index) {
                relabel(entry, index);
            }

            var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
            if (!btn) {
                btn = document.createElement('button');
                btn.type = 'button';
                btn.setAttribute('is', 'emby-button');
                btn.id = OWN_ID_PREFIX + 'btn-' + entry.id;
                btn.className = 'emby-tab-button';
                btn.setAttribute('data-index', String(entry.index));

                var label = document.createElement('div');
                label.className = 'emby-button-foreground';
                label.textContent = entry.title;
                btn.appendChild(label);

                slider.appendChild(btn);
                if (window.CustomElements && window.CustomElements.upgradeSubtree) {
                    window.CustomElements.upgradeSubtree(slider);
                }
                addedTabButton = true;
            }

            var panel = document.getElementById(OWN_ID_PREFIX + 'panel-' + entry.id);
            if (!panel) {
                panel = document.createElement('div');
                panel.id = OWN_ID_PREFIX + 'panel-' + entry.id;
                panel.className = 'tabContent pageTabContent';
                panel.setAttribute('data-index', String(entry.index));
                root.appendChild(panel);
                entry.onMount(panel);
            } else if (panel.parentElement !== root) {
                panel.classList.remove('is-active');
                root.appendChild(panel);
            }
        });

        var lastIndex = entries[entries.length - 1].index;
        if (alignSlots(slider, 'btn', lastIndex)) {
            addedTabButton = true;
        }
        alignSlots(root, 'panel', lastIndex);

        // Read visibility before acting on it so all reads share one layout.
        entries.forEach(function (entry) {
            var tabBtn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
            if (tabBtn) {
                isTabButtonVisible(tabBtn, entry.id);
            }
        });
        entries.forEach(ensureDiscoverable);

        // emby-tabs caches each tab's width at init and does not watch for new
        // children, so an appended button overlaps its neighbours until refreshed.
        if (addedTabButton) {
            var tabsElem = document.querySelector('[is="emby-tabs"]');
            if (tabsElem && tabsElem.refresh) {
                tabsElem.refresh();
            }
        }

        syncDeepLink(slider, root);
        applyPendingFocus();
    }

    function scheduleInject() {
        if (injectPending) {
            return;
        }
        injectPending = true;
        requestAnimationFrame(function () {
            injectPending = false;
            ensureInjected();
        });
    }

    function register(id, title, onMount, icon) {
        if (!id || findEntry(id)) {
            return;
        }
        entries.push({ id: id, title: title, onMount: onMount, icon: icon, index: null });
        scheduleInject();
    }

    function unregister(id) {
        entries = entries.filter(function (entry) { return entry.id !== id; });
        var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + id);
        if (btn) {
            btn.remove();
        }
        var panel = document.getElementById(OWN_ID_PREFIX + 'panel-' + id);
        if (panel) {
            panel.remove();
        }
        var link = document.getElementById(LINK_ID_PREFIX + id);
        if (link) {
            link.remove();
        }
        removeGroupIfEmpty();
        if (pendingFocusId === id) {
            pendingFocusId = null;
        }
        scheduleInject();
    }

    // Activates a registered tab. Returns false when the caller must first
    // navigate to the Home page, because the tab strip only exists there.
    function focus(id) {
        if (!findEntry(id)) {
            return false;
        }
        pendingFocusId = id;
        if (!isOnHomePage()) {
            return false;
        }
        ensureInjected();
        return true;
    }

    function init() {
        var observer = new MutationObserver(scheduleInject);
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class', 'hidden']
        });
        window.addEventListener('hashchange', function () {
            if (window.location.hash !== rewrittenHash) {
                appliedDeepLink = null;
                userPickedHash = null;
            }
            rewrittenHash = null;
            scheduleInject();
        });
        window.addEventListener('popstate', scheduleInject);
        window.addEventListener('pageshow', scheduleInject);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                scheduleInject();
            }
        });
        document.addEventListener('pointerdown', noteTabStripInput, { capture: true, passive: true });
        document.addEventListener('keydown', noteTabStripInput, { capture: true, passive: true });
    }

    window.JellyfinHelperTabs = {
        register: register,
        unregister: unregister,
        focus: focus
    };

    init();
})();
