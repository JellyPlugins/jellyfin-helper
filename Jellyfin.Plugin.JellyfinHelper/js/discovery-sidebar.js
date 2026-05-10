// Jellyfin Helper — Discovery Custom Tab + Sidebar Script
// Injected into index.html via File Transformation plugin.
// Custom Tab: Renders discovery into <div class="jellyfinhelper discovery"> (requires Custom Tabs plugin)
// Sidebar: Also adds a "Seerr Discovery" link as fallback navigation
(function () {
    'use strict';

    var CUSTOM_TAB_SELECTOR = '.jellyfinhelper.discovery';
    var SECTION_CLASS = 'jellyfinHelperSection';
    var NAV_ITEM_CLASS = 'jfhelper-nav-discovery';
    var DISCOVERY_PAGE_URL = '/JellyfinHelper/discoveryPage';
    var API_URL = '/JellyfinHelper/Discovery/My';

    // Wait for ApiClient to be available and user to be logged in
    function waitForApi(callback) {
        if (typeof ApiClient === 'undefined' || !ApiClient.getCurrentUserId || !ApiClient.getCurrentUserId()) {
            setTimeout(function () { waitForApi(callback); }, 500);
            return;
        }
        callback();
    }

    // ===== STYLES =====
    function injectStyles() {
        if (document.getElementById('jfhelper-discovery-styles')) return;
        var style = document.createElement('style');
        style.id = 'jfhelper-discovery-styles';
        style.textContent =
            '@keyframes dspin { to { transform: rotate(360deg); } }' +
            '.jfh-discovery-container { padding: 12px 3vw; }' +
            '.jfh-discovery-spinner { display:flex;justify-content:center;padding:2em; }' +
            '.jfh-discovery-spinner::after { content:"";width:24px;height:24px;border:3px solid rgba(255,255,255,0.2);border-top-color:#00a4dc;border-radius:50%;animation:dspin 0.8s linear infinite; }' +
            '.jfh-discovery-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 1em; }' +
            '.jfh-discovery-card { background: rgba(255,255,255,0.05); border-radius: 8px; overflow: hidden; display: flex; flex-direction: column; }' +
            '.jfh-discovery-card-poster img { width: 100%; height: 180px; object-fit: cover; display: block; }' +
            '.jfh-discovery-card-body { padding: 0.8em; flex: 1; display: flex; flex-direction: column; gap: 0.4em; }' +
            '.jfh-discovery-card-title { font-weight: 600; font-size: 0.95em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '.jfh-discovery-card-meta { display: flex; flex-wrap: wrap; gap: 0.3em; }' +
            '.jfh-discovery-tag { background: rgba(255,255,255,0.1); border-radius: 4px; padding: 0.15em 0.5em; font-size: 0.75em; }' +
            '.jfh-discovery-btn { margin-top: auto; padding: 0.5em; border: none; border-radius: 4px; background: #00a4dc; color: #fff; cursor: pointer; font-size: 0.85em; display: flex; align-items: center; justify-content: center; gap: 0.3em; transition: background 0.2s; }' +
            '.jfh-discovery-btn:hover { background: #0090c4; }' +
            '.jfh-discovery-btn:disabled { opacity: 0.6; cursor: not-allowed; }' +
            '.jfh-discovery-btn-done { background: #2ecc71 !important; }' +
            '.jfh-discovery-msg { text-align: center; padding: 2em; opacity: 0.6; }' +
            '.jfh-discovery-reason { font-size: 0.78em; opacity: 0.7; margin: 0.2em 0; font-style: italic; }' +
            '.jfh-discovery-no-poster { width: 100%; height: 180px; display: flex; align-items: center; justify-content: center; background: rgba(255,255,255,0.02); }';
        document.head.appendChild(style);
    }

    // ===== CUSTOM TAB MODE =====
    var lastMountedContainer = null;

    function initCustomTab() {
        injectStyles();
        tryMountCustomTab();

        // Persistent observer for DOM rebuilds
        var pending = false;
        var observer = new MutationObserver(function () {
            if (!pending) {
                pending = true;
                requestAnimationFrame(function () {
                    pending = false;
                    tryMountCustomTab();
                });
            }
        });
        observer.observe(document.body, { childList: true, subtree: true });
    }

    function tryMountCustomTab() {
        var container = findActiveContainer();
        if (!container) {
            lastMountedContainer = null;
            return;
        }
        if (container === lastMountedContainer && container.hasChildNodes()) return;

        renderDiscovery(container);
        lastMountedContainer = container;
    }

    function findActiveContainer() {
        var all = document.querySelectorAll(CUSTOM_TAB_SELECTOR);
        for (var i = all.length - 1; i >= 0; i--) {
            var page = all[i].closest('.page, .tabContent');
            if (page && !page.classList.contains('hide')) return all[i];
        }
        if (all.length > 0) return all[all.length - 1];
        return null;
    }

    function renderDiscovery(container) {
        container.className = container.className; // keep original classes
        container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-spinner"></div></div>';

        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl(API_URL),
            dataType: 'json'
        }).then(function (data) {
            renderCards(container, data);
        }).catch(function (err) {
            var msg = 'Could not load discovery suggestions.';
            if (err && err.status === 403) {
                msg = 'Discovery is not enabled. Ask your server administrator to enable this feature in Jellyfin Helper settings.';
            }
            container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + msg + '</p></div></div>';
        });
    }

    function renderCards(container, userDiscovery) {
        if (!userDiscovery || !userDiscovery.Recommendations || userDiscovery.Recommendations.length === 0) {
            container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>No suggestions available yet. Results will appear after the next scheduled task run.</p></div></div>';
            return;
        }

        var TMDB_IMG = 'https://image.tmdb.org/t/p/w300';
        var html = '<div class="jfh-discovery-container"><div class="jfh-discovery-grid">';
        var recs = userDiscovery.Recommendations;
        for (var i = 0; i < recs.length; i++) {
            var r = recs[i];
            // PosterPath is relative to TMDB CDN (e.g. "/abc123.jpg")
            var posterUrl = r.PosterPath ? TMDB_IMG + r.PosterPath : '';
            var poster = posterUrl
                ? '<div class="jfh-discovery-card-poster"><img src="' + esc(posterUrl) + '" alt="' + esc(r.Title || '') + '" loading="lazy"></div>'
                : '<div class="jfh-discovery-card-poster jfh-discovery-no-poster"><span style="opacity:0.3;font-size:2em;">🎬</span></div>';

            var year = r.Year ? '<span class="jfh-discovery-tag">' + r.Year + '</span>' : '';
            var type = r.MediaType ? '<span class="jfh-discovery-tag">' + esc(r.MediaType === 'movie' ? 'Movie' : 'TV') + '</span>' : '';
            var rating = r.TmdbRating ? '<span class="jfh-discovery-tag">⭐ ' + r.TmdbRating.toFixed(1) + '</span>' : '';
            var genres = (r.Genres && r.Genres.length > 0) ? r.Genres.slice(0, 2).map(function(g) { return '<span class="jfh-discovery-tag">' + esc(g) + '</span>'; }).join('') : '';
            var reason = r.Reason ? '<div class="jfh-discovery-reason">' + esc(r.Reason) + '</div>' : '';
            var btnText = r.AlreadyRequested ? '\u2713 Requested' : 'Request';
            var btnClass = r.AlreadyRequested ? 'jfh-discovery-btn jfh-discovery-btn-done' : 'jfh-discovery-btn';
            var btnDisabled = r.AlreadyRequested ? ' disabled' : '';

            html += '<div class="jfh-discovery-card">' +
                poster +
                '<div class="jfh-discovery-card-body">' +
                '<div class="jfh-discovery-card-title" title="' + esc(r.Title || '') + '">' + esc(r.Title || 'Unknown') + '</div>' +
                '<div class="jfh-discovery-card-meta">' + year + type + rating + genres + '</div>' +
                reason +
                '<button class="' + btnClass + '" data-tmdb="' + r.TmdbId + '" data-type="' + esc(r.MediaType || '') + '"' + btnDisabled + '>' + btnText + '</button>' +
                '</div></div>';
        }
        html += '</div></div>';
        container.innerHTML = html;

        // Attach handlers
        var buttons = container.querySelectorAll('.jfh-discovery-btn:not([disabled])');
        for (var j = 0; j < buttons.length; j++) {
            buttons[j].addEventListener('click', handleRequest);
        }
    }

    function handleRequest(e) {
        var btn = e.currentTarget;
        btn.disabled = true;
        btn.textContent = 'Requesting...';

        var tmdbId = parseInt(btn.getAttribute('data-tmdb'), 10);
        var mediaType = btn.getAttribute('data-type');

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(API_URL + '/Request'),
            data: JSON.stringify({ TmdbId: tmdbId, MediaType: mediaType }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            if (result && result.Success) {
                btn.textContent = '\u2713 Requested';
                btn.classList.add('jfh-discovery-btn-done');
            } else {
                btn.textContent = result ? result.Message : 'Failed';
                setTimeout(function () { btn.textContent = 'Request'; btn.disabled = false; }, 3000);
            }
        }).catch(function () {
            btn.textContent = 'Error';
            setTimeout(function () { btn.textContent = 'Request'; btn.disabled = false; }, 3000);
        });
    }

    function esc(str) {
        if (!str) return '';
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // ===== SIDEBAR MODE (optional fallback) =====
    function initSidebar() {
        injectNavigation();
        var observer = new MutationObserver(function () {
            var sidebar = document.querySelector('.mainDrawer-scrollContainer');
            if (sidebar && !sidebar.querySelector('.' + NAV_ITEM_CLASS)) {
                injectNavigation();
            }
        });
        var drawer = document.querySelector('.mainDrawer') || document.body;
        observer.observe(drawer, { childList: true, subtree: true });
    }

    function injectNavigation() {
        var sidebar = document.querySelector('.mainDrawer-scrollContainer');
        if (!sidebar || sidebar.querySelector('.' + NAV_ITEM_CLASS)) return;

        var section = sidebar.querySelector('.' + SECTION_CLASS);
        if (!section) {
            section = document.createElement('div');
            section.className = SECTION_CLASS;
            section.innerHTML = '<h3 class="sidebarHeader">Jellyfin Helper</h3>';
            var mediaSection = sidebar.querySelector('.libraryMenuOptions');
            if (mediaSection) {
                sidebar.insertBefore(section, mediaSection);
            } else {
                sidebar.appendChild(section);
            }
        }

        var navItem = document.createElement('a');
        navItem.setAttribute('is', 'emby-linkbutton');
        navItem.className = 'navMenuOption lnkMediaFolder emby-button ' + NAV_ITEM_CLASS;
        navItem.href = '#';
        navItem.innerHTML =
            '<span class="material-icons navMenuOptionIcon" aria-hidden="true">explore</span>' +
            '<span class="sectionName navMenuOptionText">Seerr Discovery</span>';
        navItem.addEventListener('click', function (e) {
            e.preventDefault();
            // Try to switch to the Discovery tab if it exists
            var tabs = document.querySelectorAll('.headerTabs button, [role="tab"]');
            for (var i = 0; i < tabs.length; i++) {
                if (tabs[i].textContent.trim().toLowerCase().indexOf('discover') !== -1) {
                    tabs[i].click();
                    return;
                }
            }
            // Fallback
            window.location.href = DISCOVERY_PAGE_URL;
        });
        section.appendChild(navItem);
    }

    // ===== INIT =====
    // Custom Tab: start immediately (no access check needed — the tab content will show the error)
    // Sidebar: wait for ApiClient to be ready
    waitForApi(function () {
        initCustomTab();
        initSidebar();
    });
})();