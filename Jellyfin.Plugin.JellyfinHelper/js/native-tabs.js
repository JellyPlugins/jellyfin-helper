// Jellyfin Helper - registry for the Home page native tab strip.
// Jellyfin's tab mechanism is generic: a button in .emby-tabs-slider with data-index N
// and a .tabContent.pageTabContent panel at the same position is enough, because
// emby-tabs selects by position. Owning the tab here removes the dependency on
// third party tab plugins and keeps the mechanism in one place.
(function () {
    'use strict';

    // The script can be served twice (for example after a stale response is replayed),
    // and a second registry would replace the first one while its tab stays registered there.
    if (window.JellyfinHelperTabs) {
        return;
    }

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
    var lastTabStripInput = null;
    var routeSelectionPending = false;
    var pendingFocusId = null;
    var activeEntry = null;
    var activePanel = null;

    function isOnHomePage() {
        var hash = window.location.hash;
        return hash === '' || hash === '#/home' || hash === '#/home.html'
            || hash.includes('#/home?') || hash.includes('#/home.html?');
    }

    function findEntry(id) {
        for (var entry of entries) {
            if (entry.id === id) {
                return entry;
            }
        }
        return null;
    }

    function isActiveTabContainer(element) {
        if (!element?.isConnected) {
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
        return element.id.startsWith(OWN_ID_PREFIX);
    }

    function slotIndex(element) {
        return Number.parseInt(element.dataset.index, 10);
    }

    function highestForeignIndex(slider, root) {
        var max = 1;
        var consider = function (index) {
            if (!Number.isNaN(index) && index > max) {
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
                consider(Number.parseInt(match[1], 10));
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
            btn.dataset.index = String(index);
        }
        if (panel) {
            panel.dataset.index = String(index);
        }
        if (previous == null) {
            return;
        }
        var tabsElem = document.querySelector('[is="emby-tabs"]');
        if (tabsElem && btn?.classList.contains('emby-tab-button-active') && tabsElem.selectedTabIndex === previous) {
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
        el.dataset.index = String(index);
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
            if (!el.id.startsWith(RESERVED_ID_PREFIX)) {
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
            if (slot?.parentElement === container) {
                continue;
            }
            container.appendChild(slot || createReservedSlot(kind, i));
            changed = true;
        }

        var all = items();
        if (all.some(function (el) { return Number.isNaN(slotIndex(el)); })) {
            return changed;
        }
        var head = all.filter(function (el) { return slotIndex(el) < 2; });
        var tabs = all.filter(function (el) { return slotIndex(el) >= 2; });
        var sorted = tabs.slice().sort(function (x, y) {
            return slotIndex(x) - slotIndex(y) || tabs.indexOf(x) - tabs.indexOf(y);
        });
        var ref = head.length ? head.at(-1) : null;
        sorted.forEach(function (el) {
            var target = ref ? ref.nextElementSibling : container.firstElementChild;
            if (target !== el) {
                if (target) {
                    target.before(el);
                } else {
                    container.appendChild(el);
                }
                changed = true;
            }
            ref = el;
        });
        return changed;
    }

    function getHeaderButtonTray() {
        var legacy = Array.from(document.querySelectorAll('.headerRight:not([data-jfh-header-fallback])'))
            .find(function (element) { return element.offsetParent !== null; });
        if (legacy) return legacy;

        var userMenuButton = Array.from(document.querySelectorAll('[aria-controls="app-user-menu"]'))
            .find(function (element) { return element.closest('.MuiToolbar-root')?.offsetParent != null; });
        var toolbar = userMenuButton?.closest('.MuiToolbar-root')
            || Array.from(document.querySelectorAll('.MuiAppBar-root .MuiToolbar-root'))
                .find(function (element) { return element.offsetParent !== null; });
        if (!toolbar) {
            return null;
        }

        var userMenuBox = userMenuButton?.closest('.MuiBox-root') ?? null;
        var tray = userMenuBox?.previousElementSibling ?? null;
        if (tray && 'jfhHeaderFallback' in tray.dataset && tray.previousElementSibling?.matches('.MuiBox-root')) {
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
                userMenuBox.before(container);
            } else {
                toolbar.appendChild(container);
            }
        }
        return container;
    }

    function getOrCreateGroup(headerRight) {
        var group = document.getElementById(GROUP_ID);
        if (group) {
            if (group.parentElement !== headerRight) headerRight.appendChild(group);
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
        document.querySelectorAll('[id="' + GROUP_ID + '"]').forEach(function (group) {
            if (group.children.length <= 1) group.remove();
        });
    }

    function isTabButtonVisible(btn) {
        return btn.offsetParent !== null;
    }

    function removeHeaderLink(linkId) {
        document.querySelectorAll('[id^="' + LINK_ID_PREFIX + '"]').forEach(function (link) {
            if (link.id === linkId) link.remove();
        });
        removeGroupIfEmpty();
    }

    function createHeaderLink(entry, linkId) {
        var link = document.createElement('button');
        link.id = linkId;
        link.type = 'button';
        link.setAttribute('is', 'paper-icon-button-light');
        link.className = 'headerButton headerButtonRight paper-icon-button-light';
        link.title = entry.title;
        link.setAttribute('aria-label', entry.title);
        var icon = document.createElement('i');
        icon.className = 'material-icons';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = entry.icon || 'tab';
        link.appendChild(icon);
        link.addEventListener('click', function () {
            if (link.isConnected && findEntry(entry.id) === entry && entry.index != null) {
                window.location.hash = deepLinkHash(entry);
            }
        });
        return link;
    }

    // On Jellyfin 12's experimental layout the tab strip lives in a hidden header,
    // so the button exists but cannot be clicked. A header tray link keeps the tab
    // reachable there without adding a second entry point on the stable layout.
    function ensureDiscoverable(entry) {
        var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
        var linkId = LINK_ID_PREFIX + entry.id;

        if (entry.index == null || (btn && isTabButtonVisible(btn))) {
            removeHeaderLink(linkId);
            return;
        }
        var headerRight = getHeaderButtonTray();
        if (!headerRight) {
            return;
        }

        var group = getOrCreateGroup(headerRight);
        var separator = document.getElementById(SEPARATOR_ID);
        var link = entry.headerLink || createHeaderLink(entry, linkId);
        entry.headerLink = link;
        document.querySelectorAll('[id^="' + LINK_ID_PREFIX + '"]').forEach(function (element) {
            if (element.id === linkId && element !== link) element.remove();
        });
        if (link.parentElement === group) {
            removeGroupIfEmpty();
            return;
        }
        if (separator) {
            separator.before(link);
        } else {
            group.appendChild(link);
        }
        removeGroupIfEmpty();
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
        return hash.startsWith('#/home') ? hash.split('?')[0] : '#/home';
    }

    function watchUserTabChanges(tabsElem) {
        if (!tabsElem || tabsElem === watchedTabs) {
            return;
        }
        watchedTabs = tabsElem;
        tabsElem.addEventListener('beforetabchange', function (e) {
            var input = lastTabStripInput;
            lastTabStripInput = null;
            if (selectingTab) {
                return;
            }
            var urlIndex = Number.parseInt(hashParam('tab'), 10);
            var picked = Number.parseInt(e.detail?.selectedTabIndex, 10);
            if (input?.tabs !== tabsElem || input.index !== picked || Date.now() - input.time >= 1000) return;
            // Record the requested tab before an older delayed completion can replace its URL.
            routeSelectionPending = false;
            userPickedHash = picked === (Number.isNaN(urlIndex) ? 0 : urlIndex) ? null : window.location.hash;
            syncSelectedTabHash(tabsElem, picked);
        });
        tabsElem.addEventListener('tabchange', function (e) {
            var picked = Number.parseInt(e.detail?.selectedTabIndex, 10);
            setTimeout(function () {
                picked = repairRemovedTabSelection(tabsElem, picked);
                clearStraySelection(tabsElem, picked);
                scheduleInject();
            }, 0);
        });
    }

    function repairRemovedTabSelection(tabsElem, index) {
        if (isOnHomePage() && tabsElem.isConnected && index >= 2 && tabsElem.selectedIndex?.() === index
            && !tabsElem.querySelectorAll('.emby-tab-button')[index]) {
            // A removed tab can still emit its delayed selection event, so keep the native Home panel selected.
            tabsElem.selectedIndex(0);
            return 0;
        }
        return index;
    }

    function syncSelectedTabHash(tabsElem, index) {
        var button = tabsElem.querySelectorAll('.emby-tab-button')[index];
        if (!isOnHomePage() || !tabsElem.isConnected || Number.isNaN(index)
            || !tabsElem.selectedIndex || !button?.classList.contains('emby-tab-button-active')
            || slotIndex(button) !== index || (hashParam(DEEP_LINK_PARAM) == null && hashParam('tab') == null)) {
            return;
        }
        var entry = entries.find(function (item) { return item.index === index; });
        if (entry) {
            return;
        }
        var params = new URLSearchParams(window.location.hash.split('?')[1] || '');
        params.delete(DEEP_LINK_PARAM);
        if (index === 0) {
            params.delete('tab');
        } else {
            params.set('tab', String(index));
        }
        var query = params.toString();
        var hash = homeRoute() + (query ? '?' + query : '');
        if (hash === window.location.hash) return;
        history.pushState(history.state, '', window.location.pathname + window.location.search + hash);
        userPickedHash = hash;
        appliedDeepLink = null;
    }

    function syncActiveEntry(root) {
        var entry = entries.find(function (item) {
            var panel = document.getElementById(OWN_ID_PREFIX + 'panel-' + item.id);
            return panel?.parentElement === root && panel.classList.contains('is-active');
        }) || null;
        var panel = entry ? document.getElementById(OWN_ID_PREFIX + 'panel-' + entry.id) : null;
        if (entry === activeEntry && panel === activePanel) {
            return;
        }
        activeEntry = entry || null;
        activePanel = panel;
        if (entry && panel?.parentElement === root) {
            entry.onActivate?.(panel);
        }
    }

    // Jellyfin only clears the previously highlighted tab, so a half applied
    // selection can leave a stray panel visible under later tabs.
    function clearStraySelection(tabsElem, index) {
        var root = getTabsRoot();
        if (!root || Number.isNaN(index) || !tabsElem.selectedIndex || tabsElem.selectedIndex() !== index) {
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
        if (!e.isTrusted && e.type !== 'click') return;
        var button = e.target?.closest?.('.emby-tab-button');
        var tabsElem = button?.closest('[is="emby-tabs"]');
        if (!tabsElem) return;
        lastTabStripInput = { tabs: tabsElem, index: slotIndex(button), time: Date.now() };
    }

    // tab=N lets Jellyfin act on the link natively, while jfhTab=<id> keeps the
    // target stable when other tabs shift the index.
    function deepLinkHash(entry) {
        return homeRoute() + '?tab=' + entry.index + '&' + DEEP_LINK_PARAM + '=' + encodeURIComponent(entry.id);
    }

    // Rewrites a stale ?tab=N to the entry's current index and keeps the applied marker in sync.
    function retargetDeepLink(entry, wantedIndex) {
        if (entry?.index == null || entry.index === wantedIndex) {
            return wantedIndex;
        }
        var staleHash = window.location.hash;
        var freshHash = deepLinkHash(entry);
        rewrittenHash = freshHash;
        history.replaceState(history.state, '', window.location.pathname + window.location.search + freshHash);
        if (userPickedHash === staleHash) {
            userPickedHash = freshHash;
        }
        if (appliedDeepLink?.hash === staleHash
            && appliedDeepLink?.panel === document.getElementById(OWN_ID_PREFIX + 'panel-' + entry.id)) {
            appliedDeepLink.hash = window.location.hash;
            appliedDeepLink.btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + entry.id);
        }
        return entry.index;
    }

    // Resolves the button/panel pair for an index, or null while the strip is not mounted yet.
    function deepLinkTarget(slider, root, index) {
        var tabsElem = slider.closest('[is="emby-tabs"]') || document.querySelector('[is="emby-tabs"]');
        var btn = tabsElem?.querySelectorAll('.emby-tab-button')[index];
        var panel = root.querySelectorAll('.tabContent')[index];
        if (!tabsElem?.selectedIndex || !btn || !panel
            || slotIndex(btn) !== index || slotIndex(panel) !== index) {
            return null;
        }
        return { tabsElem: tabsElem, btn: btn, panel: panel };
    }

    function isDeepLinkMarked(target, root, hash) {
        return appliedDeepLink?.hash === hash && appliedDeepLink.root === root
            && appliedDeepLink.panel === target.panel && appliedDeepLink.tabs === target.tabsElem;
    }

    // Selects the tab and drops any stray active marker. Returns whether the strip now matches.
    function selectDeepLinkTab(root, target, index) {
        var strays = function () {
            return Array.prototype.filter.call(root.querySelectorAll('.tabContent.is-active'),
                function (el) { return el !== target.panel; }).concat(Array.prototype.filter.call(
                target.tabsElem.querySelectorAll('.emby-tab-button-active'),
                function (el) { return el !== target.btn; }));
        };
        var isApplied = function () {
            return target.tabsElem.selectedIndex() === index
                && target.panel.classList.contains('is-active')
                && target.btn.classList.contains('emby-tab-button-active') && strays().length === 0;
        };
        if (!isApplied()) {
            selectingTab = true;
            try {
                target.tabsElem.selectedIndex(index);
            } finally {
                selectingTab = false;
            }
            strays().forEach(function (el) {
                el.classList.remove('is-active', 'emby-tab-button-active');
            });
        }
        return isApplied();
    }

    function syncDeepLink(slider, root) {
        var wantedId = hashParam(DEEP_LINK_PARAM);
        var entry = wantedId != null ? findEntry(wantedId) : null;
        var tabParam = hashParam('tab');
        var wantedIndex = retargetDeepLink(entry, Number.parseInt(tabParam, 10));
        var markedHash = appliedDeepLink?.hash === window.location.hash;
        if ((routeSelectionPending || markedHash) && tabParam == null && wantedId == null) wantedIndex = 0;
        if (Number.isNaN(wantedIndex) || wantedIndex < 0 || (wantedIndex < 2 && !routeSelectionPending && !markedHash)
            || (userPickedHash === window.location.hash && !routeSelectionPending && !markedHash)) {
            return;
        }
        var target = deepLinkTarget(slider, root, wantedIndex);
        if (!target) {
            return;
        }
        var hash = window.location.hash;
        if (!routeSelectionPending && isDeepLinkMarked(target, root, hash)) {
            var stillSelected = target.tabsElem.selectedIndex() === wantedIndex
                && target.panel.classList.contains('is-active')
                && target.btn.classList.contains('emby-tab-button-active')
                && !Array.prototype.some.call(root.querySelectorAll('.tabContent.is-active'),
                    function (el) { return el !== target.panel; })
                && !Array.prototype.some.call(target.tabsElem.querySelectorAll('.emby-tab-button-active'),
                    function (el) { return el !== target.btn; });
            if (stillSelected) {
                appliedDeepLink.btn = target.btn;
                return;
            }
        }
        if (!selectDeepLinkTab(root, target, wantedIndex)) {
            return;
        }
        routeSelectionPending = false;
        appliedDeepLink = {
            hash: hash, root: root, panel: target.panel, tabs: target.tabsElem, btn: target.btn
        };
    }

    function applyPendingFocus() {
        if (!pendingFocusId) {
            return;
        }
        var entry = findEntry(pendingFocusId);
        if (entry?.index == null) {
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
            activeEntry = null;
            activePanel = null;
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
                btn.dataset.index = String(entry.index);

                var label = document.createElement('div');
                label.className = 'emby-button-foreground';
                label.textContent = entry.title;
                btn.appendChild(label);

                btn.addEventListener('click', function () {
                    if (entry.index == null) {
                        return;
                    }
                    var fresh = deepLinkHash(entry);
                    if (window.location.hash !== fresh) {
                        rewrittenHash = fresh;
                        history.pushState(history.state, '', window.location.pathname + window.location.search + fresh);
                    }
                });

                slider.appendChild(btn);
                window.CustomElements?.upgradeSubtree?.(slider);
                addedTabButton = true;
            }

            var panel = document.getElementById(OWN_ID_PREFIX + 'panel-' + entry.id);
            if (!panel) {
                panel = document.createElement('div');
                panel.id = OWN_ID_PREFIX + 'panel-' + entry.id;
                panel.className = 'tabContent pageTabContent';
                panel.dataset.index = String(entry.index);
                root.appendChild(panel);
                entry.onMount(panel);
            } else if (panel.parentElement !== root) {
                panel.classList.remove('is-active');
                root.appendChild(panel);
            }
        });

        var lastIndex = entries.at(-1).index;
        if (alignSlots(slider, 'btn', lastIndex)) {
            addedTabButton = true;
        }
        alignSlots(root, 'panel', lastIndex);

        entries.forEach(ensureDiscoverable);

        // emby-tabs caches each tab's width at init and does not watch for new
        // children, so an appended button overlaps its neighbours until refreshed.
        if (addedTabButton) {
            var tabsElem = document.querySelector('[is="emby-tabs"]');
            tabsElem?.refresh?.();
        }

        syncDeepLink(slider, root);
        applyPendingFocus();
        syncActiveEntry(root);
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

    function register(id, title, onMount, icon, onActivate) {
        if (!id || findEntry(id)) {
            return;
        }
        entries.push({ id: id, title: title, onMount: onMount, onActivate: onActivate, icon: icon, index: null });
        scheduleInject();
    }

    function unregister(id) {
        if (activeEntry?.id === id) {
            activeEntry = null;
            activePanel = null;
        }
        entries = entries.filter(function (entry) { return entry.id !== id; });
        var btn = document.getElementById(OWN_ID_PREFIX + 'btn-' + id);
        var tabsElem = btn?.closest('[is="emby-tabs"]');
        var panel = document.getElementById(OWN_ID_PREFIX + 'panel-' + id);
        if (panel) {
            if (isOnHomePage() && panel.classList.contains('is-active') && tabsElem?.selectedIndex) {
                tabsElem.selectedIndex(0);
            }
        }
        document.querySelectorAll('[id^="' + OWN_ID_PREFIX + 'btn-"], [id^="' + OWN_ID_PREFIX + 'panel-"]').forEach(function (element) {
            if (element.id === OWN_ID_PREFIX + 'btn-' + id || element.id === OWN_ID_PREFIX + 'panel-' + id) element.remove();
        });
        removeHeaderLink(LINK_ID_PREFIX + id);
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

    function handleRouteNavigation() {
        // Browser history owns the destination selection even when Jellyfin keeps the Home page mounted.
        appliedDeepLink = null;
        userPickedHash = null;
        rewrittenHash = null;
        lastTabStripInput = null;
        routeSelectionPending = true;
        scheduleInject();
    }

    function init() {
        var observer = new MutationObserver(scheduleInject);
        observer.observe(document.documentElement, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class', 'hidden', 'style']
        });
        document.addEventListener('jfh:navigation', function () {
            if (window.location.hash !== rewrittenHash) {
                handleRouteNavigation();
            }
            rewrittenHash = null;
            scheduleInject();
        });
        document.addEventListener('viewshow', scheduleInject, true);
        document.addEventListener('load', scheduleInject, true);
        window.addEventListener('pageshow', scheduleInject);
        window.addEventListener('resize', scheduleInject);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                scheduleInject();
            }
        });
        document.addEventListener('pointerdown', noteTabStripInput, { capture: true, passive: true });
        document.addEventListener('keydown', noteTabStripInput, { capture: true, passive: true });
        document.addEventListener('click', noteTabStripInput, { capture: true, passive: true });
    }

    window.JellyfinHelperTabs = {
        register: register,
        unregister: unregister,
        focus: focus,
        refresh: scheduleInject
    };

    init();
})();
