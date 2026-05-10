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

    function waitForApi(callback) {
        if (typeof ApiClient === 'undefined' || !ApiClient.getCurrentUserId || !ApiClient.getCurrentUserId()) {
            setTimeout(function () { waitForApi(callback); }, 500);
            return;
        }
        callback();
    }

    // ===== I18N =====
    var _strings = null;

    function loadStrings(callback) {
        // No lang parameter — the server returns the language configured in plugin settings
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('/JellyfinHelper/Translations'),
            dataType: 'json'
        }).then(function (data) {
            _strings = data || {};
            callback();
        }).catch(function () {
            _strings = {};
            callback();
        });
    }

    function t(key, fallback) {
        if (_strings && _strings[key]) return _strings[key];
        return fallback || key;
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
            // Poster flip container
            '.jfh-discovery-card-poster { position: relative; perspective: 800px; cursor: pointer; overflow: hidden; }' +
            '.jfh-discovery-flip-inner { position: relative; width: 100%; aspect-ratio: 2/3; transition: transform 0.5s ease; transform-style: preserve-3d; }' +
            '.jfh-discovery-card-poster.flipped .jfh-discovery-flip-inner { transform: rotateY(180deg); }' +
            '.jfh-discovery-flip-front, .jfh-discovery-flip-back { position: absolute; top: 0; left: 0; width: 100%; height: 100%; backface-visibility: hidden; -webkit-backface-visibility: hidden; box-sizing: border-box; }' +
            '.jfh-discovery-flip-front img { width: 100%; height: 100%; object-fit: cover; display: block; }' +
            '.jfh-discovery-flip-back { transform: rotateY(180deg); background: rgba(20,20,30,0.95); padding: 1.2em; overflow-y: auto; box-sizing: border-box; }' +
            '.jfh-discovery-flip-back-text { font-size: 0.82em; line-height: 1.5; opacity: 0.9; color: #eee; word-break: break-word; overflow-wrap: break-word; }' +
            '.jfh-discovery-no-poster { width: 100%; aspect-ratio: 2/3; display: flex; align-items: center; justify-content: center; background: rgba(255,255,255,0.02); }' +
            // Card body
            '.jfh-discovery-card-body { padding: 0.8em; flex: 1; display: flex; flex-direction: column; gap: 0.4em; }' +
            '.jfh-discovery-card-title { font-weight: 600; font-size: 0.95em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '.jfh-discovery-card-meta { display: flex; flex-wrap: nowrap; gap: 0.3em; overflow: hidden; max-height: 1.6em; }' +
            '.jfh-discovery-tag { background: rgba(255,255,255,0.1); border-radius: 4px; padding: 0.15em 0.5em; font-size: 0.75em; white-space: nowrap; flex-shrink: 0; }' +
            '.jfh-discovery-score { height: 4px; background: rgba(255,255,255,0.1); border-radius: 2px; overflow: hidden; margin: 0.3em 0; }' +
            '.jfh-discovery-score-bar { height: 100%; border-radius: 2px; }' +
            '.jfh-discovery-score-high .jfh-discovery-score-bar { background: #2ecc71; }' +
            '.jfh-discovery-score-mid .jfh-discovery-score-bar { background: #f39c12; }' +
            '.jfh-discovery-score-low .jfh-discovery-score-bar { background: #e74c3c; }' +
            '.jfh-discovery-score-text { font-size: 0.7em; opacity: 0.6; }' +
            '.jfh-discovery-btn { margin-top: auto; padding: 0.5em; border: none; border-radius: 4px; background: #00a4dc; color: #fff; cursor: pointer; font-size: 0.85em; display: flex; align-items: center; justify-content: center; gap: 0.3em; transition: background 0.2s; }' +
            '.jfh-discovery-btn:hover { background: #0090c4; }' +
            '.jfh-discovery-btn:disabled { opacity: 0.6; cursor: not-allowed; }' +
            '.jfh-discovery-btn-done { background: #2ecc71 !important; }' +
            '.jfh-discovery-msg { text-align: center; padding: 2em; opacity: 0.6; }' +
            '.jfh-discovery-reason { font-size: 0.78em; opacity: 0.7; margin: 0.2em 0; font-style: italic; }';
        document.head.appendChild(style);
    }

    // ===== CUSTOM TAB =====
    var lastMountedContainer = null;

    function initCustomTab() {
        injectStyles();
        tryMountCustomTab();
        var pending = false;
        var observeTarget = document.querySelector('.mainAnimatedPages, .skinBody') || document.body;
        var observer = new MutationObserver(function () {
            if (!pending) {
                pending = true;
                requestAnimationFrame(function () {
                    pending = false;
                    tryMountCustomTab();
                });
            }
        });
        observer.observe(observeTarget, { childList: true, subtree: true });
    }

    function tryMountCustomTab() {
        var container = findActiveContainer();
        if (!container) { lastMountedContainer = null; return; }
        // Check if our rendered content is still present (not just any child node).
        // SPA navigation may clear the container's innerHTML while keeping the same DOM node.
        if (container === lastMountedContainer && container.querySelector('.jfh-discovery-container')) return;
        renderDiscovery(container);
        lastMountedContainer = container;
    }

    function findActiveContainer() {
        var all = document.querySelectorAll(CUSTOM_TAB_SELECTOR);
        for (var i = all.length - 1; i >= 0; i--) {
            var page = all[i].closest('.page, .tabContent');
            if (page && !page.classList.contains('hide')) return all[i];
        }
        return all.length > 0 ? all[all.length - 1] : null;
    }

    function renderDiscovery(container) {
        container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-spinner"></div></div>';
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' })
            .then(function (data) { renderCards(container, data); })
            .catch(function (err) {
                var msg = t('discoveryLoadError', 'Could not load discovery suggestions.');
                if (err && err.status === 403) {
                    msg = t('discoveryDisabled', 'Discovery is not enabled. Ask your server administrator to enable this feature in Jellyfin Helper settings.');
                }
                container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(msg) + '</p></div></div>';
            });
    }

    function renderCards(container, userDiscovery) {
        if (!userDiscovery || !userDiscovery.Recommendations || userDiscovery.Recommendations.length === 0) {
            container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(t('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.')) + '</p></div></div>';
            return;
        }
        var TMDB_IMG = 'https://image.tmdb.org/t/p/w300';
        var html = '<div class="jfh-discovery-container"><div class="jfh-discovery-grid">';
        var recs = userDiscovery.Recommendations;
        for (var i = 0; i < recs.length; i++) {
            var r = recs[i];
            var posterUrl = r.PosterPath ? TMDB_IMG + r.PosterPath : '';
            // Build poster with flip (front = image, back = overview)
            var overviewText = r.Overview || '';
            var poster;
            if (posterUrl) {
                poster = '<div class="jfh-discovery-card-poster">' +
                    '<div class="jfh-discovery-flip-inner">' +
                    '<div class="jfh-discovery-flip-front"><img src="' + esc(posterUrl) + '" alt="' + esc(r.Title || '') + '" loading="lazy"></div>' +
                    '<div class="jfh-discovery-flip-back"><div class="jfh-discovery-flip-back-text">' + esc(overviewText || t('discoveryNoResults', 'No description available.')) + '</div></div>' +
                    '</div></div>';
            } else {
                poster = '<div class="jfh-discovery-card-poster jfh-discovery-no-poster"><span style="opacity:0.3;font-size:2em;">\uD83C\uDFAC</span></div>';
            }
            var year = r.Year ? '<span class="jfh-discovery-tag">' + r.Year + '</span>' : '';
            var type = r.MediaType ? '<span class="jfh-discovery-tag">' + esc(r.MediaType === 'movie' ? t('movies', 'Movie') : t('tvShows', 'TV')) + '</span>' : '';
            var rating = r.TmdbRating ? '<span class="jfh-discovery-tag">\u2B50 ' + r.TmdbRating.toFixed(1) + '</span>' : '';
            var genres = (r.Genres && r.Genres.length > 0) ? r.Genres.slice(0, 2).map(function(g) { return '<span class="jfh-discovery-tag">' + esc(g) + '</span>'; }).join('') : '';
            var scorePercent = Math.max(0, Math.min(100, Math.round((Number(r.Score) || 0) * 100)));
            var scoreClass = scorePercent >= 80 ? 'jfh-discovery-score-high' : scorePercent >= 50 ? 'jfh-discovery-score-mid' : 'jfh-discovery-score-low';
            var scoreHtml = '<div class="jfh-discovery-score ' + scoreClass + '"><div class="jfh-discovery-score-bar" style="width:' + scorePercent + '%"></div></div><div class="jfh-discovery-score-text">' + scorePercent + '% ' + t('recsMatch', 'match') + '</div>';
            var reasonText = formatReason(r.ReasonKey, r.Reason, r.RelatedInfo);
            var reason = reasonText ? '<div class="jfh-discovery-reason">' + esc(reasonText) + '</div>' : '';
            var btnText = r.AlreadyRequested ? '\u2713 ' + t('discoveryRequested', 'Requested') : t('discoveryRequest', 'Request');
            var btnClass = r.AlreadyRequested ? 'jfh-discovery-btn jfh-discovery-btn-done' : 'jfh-discovery-btn';
            var btnDisabled = r.AlreadyRequested ? ' disabled' : '';
            html += '<div class="jfh-discovery-card">' + poster +
                '<div class="jfh-discovery-card-body">' +
                '<div class="jfh-discovery-card-title" title="' + esc(r.Title || '') + '">' + esc(r.Title || t('recsUnknownTitle', 'Unknown')) + '</div>' +
                '<div class="jfh-discovery-card-meta">' + year + type + rating + genres + '</div>' +
                scoreHtml + reason +
                '<button class="' + btnClass + '" data-tmdb="' + r.TmdbId + '" data-type="' + esc(r.MediaType || '') + '"' + btnDisabled + '>' + esc(btnText) + '</button>' +
                '</div></div>';
        }
        html += '</div></div>';
        container.innerHTML = html;
        var buttons = container.querySelectorAll('.jfh-discovery-btn:not([disabled])');
        for (var j = 0; j < buttons.length; j++) { buttons[j].addEventListener('click', handleRequest); }
        // Attach poster flip handlers
        var posters = container.querySelectorAll('.jfh-discovery-card-poster .jfh-discovery-flip-inner');
        for (var p = 0; p < posters.length; p++) {
            posters[p].parentElement.addEventListener('click', function () {
                this.classList.toggle('flipped');
            });
        }
    }

    function handleRequest(e) {
        var btn = e.currentTarget;
        btn.disabled = true;
        btn.textContent = t('discoveryRequesting', 'Requesting...');
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
                btn.textContent = '\u2713 ' + t('discoveryRequested', 'Requested');
                btn.classList.add('jfh-discovery-btn-done');
            } else {
                btn.textContent = result ? result.Message : t('discoveryRequestFailed', 'Failed');
                setTimeout(function () { btn.textContent = t('discoveryRequest', 'Request'); btn.disabled = false; }, 3000);
            }
        }).catch(function () {
            btn.textContent = t('discoveryRequestFailed', 'Failed');
            setTimeout(function () { btn.textContent = t('discoveryRequest', 'Request'); btn.disabled = false; }, 3000);
        });
    }

    // Translate reason key to localized human-readable text.
    // Backend DetermineReason produces: reasonPerson, reasonGenre, reasonTrending, reasonPopular
    function formatReason(reasonKey, reason, relatedInfo) {
        if (!reasonKey && !reason) return '';
        var key = reasonKey || '';
        // Try i18n lookup first (keys match en.json: reasonPopular, reasonGenre, reasonTrending, etc.)
        if (key === 'reasonPerson' && relatedInfo) {
            var personTpl = t('reasonPersonNamed', 'Featuring {0}');
            return personTpl.replace('{0}', relatedInfo);
        }
        if (key === 'reasonGenre' && relatedInfo) {
            var genreTpl = t('reasonGenre', 'Because you enjoy {0}');
            return genreTpl.replace('{0}', relatedInfo);
        }
        if (key === 'reasonTrending') return t('reasonTrending', 'Trending now');
        if (key === 'reasonPopular') return t('reasonPopular', 'Popular and highly rated');
        if (key === 'reasonHighlyRated') return t('reasonHighlyRated', 'Highly rated');
        // If we have a known i18n key, try it
        if (key && _strings && _strings[key]) {
            var val = _strings[key];
            return relatedInfo ? val.replace('{0}', relatedInfo) : val;
        }
        // Fallback: if reason looks like a raw key (starts with "reason"), hide it
        if (reason && reason.indexOf('reason') === 0) {
            var parts = reason.split(': ');
            if (parts.length === 2) {
                return formatReason(parts[0], null, parts[1]);
            }
            return '';
        }
        return reason || '';
    }

    function esc(str) {
        if (!str) return '';
        return str.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // ===== SIDEBAR =====
    function initSidebar() {
        injectNavigation();
        var drawer = document.querySelector('.mainDrawer');
        if (!drawer) return;
        var observer = new MutationObserver(function () {
            var sidebar = document.querySelector('.mainDrawer-scrollContainer');
            if (sidebar && !sidebar.querySelector('.' + NAV_ITEM_CLASS)) {
                injectNavigation();
            }
        });
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
            '<span class="sectionName navMenuOptionText">' + t('discoveryTitle', 'Seerr Discovery') + '</span>';
        navItem.addEventListener('click', function (e) {
            e.preventDefault();
            var tabs = document.querySelectorAll('.headerTabs button, [role="tab"]');
            for (var i = 0; i < tabs.length; i++) {
                if (tabs[i].textContent.trim().toLowerCase().indexOf('discover') !== -1) {
                    tabs[i].click();
                    return;
                }
            }
            window.location.href = DISCOVERY_PAGE_URL;
        });
        section.appendChild(navItem);
    }

    // ===== INIT =====
    waitForApi(function () {
        loadStrings(function () {
            initCustomTab();
            initSidebar();
            // Retry mount after delay in case DOM wasn't fully ready on hard reload
            setTimeout(tryMountCustomTab, 1000);
            setTimeout(tryMountCustomTab, 3000);
        });
    });
})();
