// Jellyfin Helper - Discovery Custom Tab + Sidebar Script Injected into index.html via File Transformation plugin.
(function () {
    'use strict';

    // Guard against double initialization (e.g., duplicate script tag injection
    // from stale fallback writes or concurrent transformation registrations).
    if (window.__jfhDiscoverySidebarInitialized) {
        return;
    }
    window.__jfhDiscoverySidebarInitialized = true;

    var CUSTOM_TAB_SELECTOR = '.jellyfinhelper.discovery';
    var SECTION_CLASS = 'jellyfinHelperSection';
    var NAV_ITEM_CLASS = 'jfhelper-nav-discovery';
    // No standalone page exists - discovery is rendered via Custom Tabs plugin. The sidebar click handler searches for the tab first; if not found, it shows an inline message instead of navigating to a 404 page.
    var API_URL = '/JellyfinHelper/Discovery/My';

    var TOAST_DURATION_MS = 5000;

    var _seerrBaseUrl = '';
    var EXTERNAL_LINKS_URL = '/JellyfinHelper/Discovery/My/ExternalLinks';

    // Remounts render instantly from the last good payload for the same user; mutations
    // and 403s clear it. A silent background refetch keeps it fresh, so there is no TTL.
    var _discoveryResultCache = null;
    var _backgroundRefreshInFlight = false;
    // Bumped on every mutation and on user change; a fetch captures it at start
    // and its result is discarded if the generation moved on while it was away.
    var _discoveryGeneration = 0;

    function currentDiscoveryUserId() {
        return (typeof ApiClient !== 'undefined' && ApiClient.getCurrentUserId?.()) || '';
    }


    /** * Returns the URL only if it uses a safe http(s) scheme, otherwise ''. */
    function safeHttpUrl(url) {
        if (typeof url !== 'string') return '';
        var trimmed = url.trim();
        // Leading-scheme check is case-insensitive; reject anything that is not http(s)://.
        return /^https?:\/\//i.test(trimmed) ? trimmed : '';
    }

    var MAX_FAST_RETRIES = 60;  // 30 seconds at 500ms intervals (fast polling)
    var MAX_SLOW_RETRIES = 40;  // 2 minutes at 3s intervals (slow polling); total cap ~150s
    var SLOW_POLL_INTERVAL = 3000;

    // Retry budget is local to each call so concurrent/sequential waits never share a counter:
    // a previous attempt's accrued retries must not shorten a later one's wait.
    function waitForApi(callback, onTimeout) {
        var retries = 0;
        function attempt() {
            if (typeof ApiClient === 'undefined' || !ApiClient.getCurrentUserId || !ApiClient.getCurrentUserId()) {
                retries++;
                if (retries > MAX_FAST_RETRIES + MAX_SLOW_RETRIES) {
                    // ApiClient did not become available within ~150 seconds - bail out to
                    // prevent an indefinite timer leak on unauthenticated/guest sessions.
                    if (typeof onTimeout === 'function') onTimeout();
                    return;
                }
                var delay = retries <= MAX_FAST_RETRIES ? 500 : SLOW_POLL_INTERVAL;
                setTimeout(attempt, delay);
                return;
            }
            callback();
        }
        attempt();
    }

    var _strings = null;

    function loadStrings(callback) {
        // No lang parameter - the server returns the language configured in plugin settings
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

    function getDiscoveryScoreClass(p) {
        if (p >= 80) return 'jfh-discovery-score-high';
        if (p >= 50) return 'jfh-discovery-score-mid';
        return 'jfh-discovery-score-low';
    }

    /**
     * Fetches the external links configuration (Seerr base URL) from the backend.
     * Best-effort: if the fetch fails, external link icons will still render but
     * the Seerr option in the popup will be hidden when _seerrBaseUrl is empty.
     */
    function loadExternalLinksConfig() {
        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl(EXTERNAL_LINKS_URL),
            dataType: 'json'
        }).then(function (data) {
            if (data && data.SeerrUrl) {
                // Only accept http(s) URLs , reject javascript:/data:/etc. at the source so a
                // misconfigured or hostile admin value can never reach the window.open() sink.
                _seerrBaseUrl = safeHttpUrl(data.SeerrUrl).replace(/\/+$/, '');
            }
        }).catch(function () {
            // Non-critical - Seerr link option will be hidden in the popup
        });
    }


    /** * Shows a temporary toast notification at the bottom-center of the viewport. * Used to surface error details from non-200 API responses without blocking the UI. */
    function showToast(message, duration) {
        if (!message) return;
        duration = duration || TOAST_DURATION_MS;

        var toast = document.createElement('div');
        toast.className = 'jfh-discovery-toast';
        toast.textContent = message;
        toast.setAttribute('role', 'alert');
        toast.setAttribute('aria-live', 'assertive');

        document.body.appendChild(toast);

        // Trigger reflow before adding the visible class to ensure CSS transition fires
        toast.getBoundingClientRect();
        toast.classList.add('jfh-discovery-toast-visible');

        var dismissTimeout = setTimeout(function () { dismissToast(toast); }, duration);

        // Allow manual dismissal via click
        toast.addEventListener('click', function () {
            clearTimeout(dismissTimeout);
            dismissToast(toast);
        });
    }

    /**
     * Gracefully dismisses and removes a toast element with a fade-out transition.
     * @param {HTMLElement} toast - The toast DOM element to remove.
     */
    function dismissToast(toast) {
        if (!toast || !toast.parentNode) return;
        toast.classList.remove('jfh-discovery-toast-visible');
        toast.classList.add('jfh-discovery-toast-hidden');
        // Remove from DOM after the CSS transition completes
        setTimeout(function () {
            toast.remove();
        }, 300);
    }

    /** * Extracts a human-readable error message from an API error response. * Handles both XHR-style objects (responseText/responseJSON) and plain error objects. */
    function extractErrorMessage(err) {
        if (!err) return '';
        try {
            // ApiClient.ajax may expose responseJSON directly
            if (err.responseJSON && err.responseJSON.Message) {
                return err.responseJSON.Message;
            }
            // Fall back to parsing responseText
            if (err.responseText) {
                var parsed = JSON.parse(err.responseText);
                if (parsed && parsed.Message) return parsed.Message;
            }
        } catch (e) {
            // JSON parse failure - fall through to empty string
        }
        return '';
    }

    /** * Maps a raw server error message to a short, user-friendly i18n toast message. * Inspects the message text for HTTP status codes and returns an appropriate translation. */
    function getUserFriendlyErrorMessage(serverMessage) {
        var msg = (serverMessage || '').toLowerCase();
        if (msg.includes('http 403') || msg.includes('not linked') || msg.includes('no permission')) {
            return t('discoveryErrNoPermission', 'No permission. Contact your admin.');
        }
        if (msg.includes('http 5') || msg.includes('unreachable')) {
            return t('discoveryErrServerUnavailable', 'Server unreachable. Try again later.');
        }
        if (msg.includes('timed out') || msg.includes('timeout')) {
            return t('discoveryErrTimeout', 'Timed out. Try again later.');
        }
        return t('discoveryErrGeneric', 'Request failed. Try again later.');
    }

    function injectStyles() {
        if (document.getElementById('jfhelper-discovery-styles')) return;
        var style = document.createElement('style');
        style.id = 'jfhelper-discovery-styles';
        style.textContent =
            '@keyframes dspin { to { transform: rotate(360deg); } }' +
            '.jfh-discovery-container { max-width: 1920px; margin: 0 auto; padding: 1em clamp(0.5em, 3vw, 2em); container-type: inline-size; }' +
            // Tab content renders its own container inside the shell container: the inner one must not
            // add padding again or the grid/headers indent against the tab bar.
            '.jfh-discovery-container .jfh-discovery-container { max-width: none; margin: 0; padding: 0; }' +
            '.jfh-discovery-spinner { display:flex;justify-content:center;padding:2em; }' +
            '.jfh-discovery-spinner::after { content:"";width:24px;height:24px;border:3px solid rgba(255,255,255,0.2);border-top-color:#00a4dc;border-radius:50%;animation:dspin 0.8s linear infinite; }' +
            '.jfh-discovery-grid { display: grid; grid-template-columns: repeat(2, 1fr); gap: clamp(0.5em, 1.2vw, 1.2em); }' +
            '@media (min-width: 480px) { .jfh-discovery-grid { grid-template-columns: repeat(3, 1fr); } }' +
            '@media (min-width: 768px) { .jfh-discovery-grid { grid-template-columns: repeat(4, 1fr); } }' +
            '@media (min-width: 1024px) { .jfh-discovery-grid { grid-template-columns: repeat(5, 1fr); } }' +
            '@media (min-width: 1400px) { .jfh-discovery-grid { grid-template-columns: repeat(6, 1fr); } }' +
            '@media (min-width: 1920px) { .jfh-discovery-grid { grid-template-columns: repeat(7, 1fr); } }' +
            '@media (min-width: 2560px) { .jfh-discovery-grid { grid-template-columns: repeat(8, 1fr); } }' +
            // Container queries mirror the breakpoints above but key off the panel width instead of the
            // viewport, so a capped panel on an ultrawide screen gets columns it can actually fit. They come
            // last so they win where supported; older clients without @container keep the media rules above.
            '@container (min-width: 480px) { .jfh-discovery-grid { grid-template-columns: repeat(3, 1fr); } }' +
            '@container (min-width: 768px) { .jfh-discovery-grid { grid-template-columns: repeat(4, 1fr); } }' +
            '@container (min-width: 1024px) { .jfh-discovery-grid { grid-template-columns: repeat(5, 1fr); } }' +
            '@container (min-width: 1400px) { .jfh-discovery-grid { grid-template-columns: repeat(6, 1fr); } }' +
            '@container (min-width: 1920px) { .jfh-discovery-grid { grid-template-columns: repeat(7, 1fr); } }' +
            '@container (min-width: 2560px) { .jfh-discovery-grid { grid-template-columns: repeat(8, 1fr); } }' +
            '.jfh-discovery-card { background: rgba(255,255,255,0.05); border-radius: 8px; overflow: hidden; display: flex; flex-direction: column; }' +
            // Poster flip container
            '.jfh-discovery-card-poster { position: relative; perspective: 800px; cursor: pointer; overflow: hidden; }' +
            '.jfh-discovery-flip-inner { position: relative; width: 100%; aspect-ratio: 2/3; transition: transform 0.5s ease; transform-style: preserve-3d; }' +
            '.jfh-discovery-card-poster.flipped .jfh-discovery-flip-inner { transform: rotateY(180deg); }' +
            '.jfh-discovery-flip-front, .jfh-discovery-flip-back { position: absolute; top: 0; left: 0; width: 100%; height: 100%; backface-visibility: hidden; -webkit-backface-visibility: hidden; box-sizing: border-box; }' +
            '.jfh-discovery-flip-front img { width: 100%; height: 100%; object-fit: cover; display: block; }' +
            '.jfh-discovery-flip-back { transform: rotateY(180deg); background: rgba(20,20,30,0.95); padding: 1.2em; overflow-y: auto; box-sizing: border-box; }' +
            '.jfh-discovery-flip-back-text { font-size: 0.92em; line-height: 1.5; opacity: 0.9; color: #eee; word-break: break-word; overflow-wrap: break-word; }' +
            '.jfh-discovery-no-poster { width: 100%; aspect-ratio: 2/3; display: flex; align-items: center; justify-content: center; background: rgba(255,255,255,0.02); }' +
            // Card body
            '.jfh-discovery-card-body { padding: 0.8em; flex: 1; display: flex; flex-direction: column; gap: 0.4em; }' +
            '.jfh-discovery-card-title { font-weight: 600; font-size: 0.95em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            '.jfh-discovery-card-meta { display: flex; flex-wrap: nowrap; gap: 0.3em; overflow: hidden; }' +
            '.jfh-discovery-card-genres { display: flex; flex-wrap: nowrap; gap: 0.3em; overflow-x: auto; overflow-y: hidden; padding-bottom: 2px; scrollbar-width: thin; scrollbar-color: var(--color-primary-scrollbar, rgba(0,164,220,0.4)) transparent; }' +
            '.jfh-discovery-card-genres::-webkit-scrollbar { height: 4px; }' +
            '.jfh-discovery-card-genres::-webkit-scrollbar-track { background: transparent; }' +
            '.jfh-discovery-card-genres::-webkit-scrollbar-thumb { background: var(--color-primary-scrollbar, rgba(0,164,220,0.4)); border-radius: 3px; }' +
            '.jfh-discovery-tag { background: rgba(255,255,255,0.1); border-radius: 4px; padding: 0.15em 0.5em; font-size: 0.75em; white-space: nowrap; flex-shrink: 0; }' +
            '.jfh-discovery-flip-links { display: flex; gap: 0.6em; margin-bottom: 0.8em; padding-bottom: 0.6em; border-bottom: 1px solid rgba(255,255,255,0.1); flex-wrap: wrap; }' +
            '.jfh-discovery-flip-link { display: inline-flex; align-items: center; gap: 0.3em; color: #00a4dc; text-decoration: none; font-size: 0.85em; font-weight: 500; padding: 0.3em 0.5em; border-radius: 4px; transition: background 0.2s, opacity 0.2s; opacity: 0.9; }' +
            '.jfh-discovery-flip-link:hover { background: rgba(0,164,220,0.15); opacity: 1; text-decoration: none; }' +
            '.jfh-discovery-score { height: 4px; background: rgba(255,255,255,0.1); border-radius: 2px; overflow: hidden; margin: 0.3em 0; }' +
            '.jfh-discovery-score-bar { height: 100%; border-radius: 2px; }' +
            '.jfh-discovery-score-high .jfh-discovery-score-bar { background: #2ecc71; }' +
            '.jfh-discovery-score-mid .jfh-discovery-score-bar { background: #f39c12; }' +
            '.jfh-discovery-score-low .jfh-discovery-score-bar { background: #e74c3c; }' +
            '.jfh-discovery-score-text { font-size: 0.7em; opacity: 0.6; }' +
            '.jfh-discovery-local-score { display: inline-block; margin-left: 0.4em; padding: 0 0.4em; border-radius: 3px; background: rgba(255,255,255,0.1); font-size: 0.9em; opacity: 0.85; white-space: nowrap; vertical-align: baseline; }' +
            '.jfh-discovery-btn-row { margin-top: auto; display: flex; flex-wrap: wrap; gap: 0.4em; align-items: stretch; }' +
            '.jfh-discovery-btn { flex: 1; padding: 0.5em; border: none; border-radius: 4px; background: #00a4dc; color: #fff; cursor: pointer; font-size: 0.85em; display: flex; align-items: center; justify-content: center; gap: 0.3em; transition: background 0.2s; white-space: normal; text-align: center; min-width: 0; }' +
            '.jfh-discovery-btn:hover { background: #0090c4; }' +
            '.jfh-discovery-btn:disabled { opacity: 0.6; cursor: not-allowed; }' +
            '.jfh-discovery-btn-done { background: #2ecc71 !important; }' +
            '.jfh-discovery-btn-failed { background: #e74c3c !important; }' +
            '.jfh-discovery-btn-dismiss { background: rgba(255,255,255,0.08); color: #ccc; }' +
            '.jfh-discovery-btn-dismiss:hover { background: rgba(231,76,60,0.2); color: #e74c3c; }' +
            '.jfh-discovery-msg { text-align: center; padding: 2em; opacity: 0.6; }' +
            '.jfh-discovery-reason { font-size: 0.78em; opacity: 0.7; margin: 0.2em 0; font-style: italic; }' +
            // Toast notification
            '.jfh-discovery-toast { position: fixed; bottom: 2em; left: 50%; transform: translateX(-50%) translateY(20px); z-index: 999999; max-width: 480px; width: calc(100% - 2em); padding: 0.9em 1.4em; background: rgba(30,30,40,0.95); color: #fff; font-size: 0.88em; line-height: 1.4; border-radius: 8px; border-left: 4px solid #e74c3c; box-shadow: 0 4px 24px rgba(0,0,0,0.4); opacity: 0; pointer-events: none; transition: opacity 0.3s ease, transform 0.3s ease; cursor: pointer; }' +
            '.jfh-discovery-toast-visible { opacity: 1; pointer-events: auto; transform: translateX(-50%) translateY(0); }' +
            '.jfh-discovery-toast-hidden { opacity: 0; pointer-events: none; transform: translateX(-50%) translateY(20px); }' +
            // Sub-tab bar: one full-width segmented tray with equal-width tabs, so the row is
            // always symmetric (centered by construction) on desktop and mobile. Long labels
            // ellipsize instead of breaking the layout on very narrow viewports.
            '.jfh-discovery-tabs { display: flex; align-items: stretch; gap: 4px; max-width: 100%; margin: 0 0 1em 0; padding: 4px; overflow-x: auto; overflow-y: hidden; -webkit-overflow-scrolling: touch; scrollbar-width: thin; scrollbar-color: var(--color-primary-scrollbar, rgba(0,164,220,0.4)) transparent; background: rgba(255,255,255,0.04); border: 1px solid rgba(255,255,255,0.07); border-radius: 10px; }' +
            '.jfh-discovery-tabs::-webkit-scrollbar { height: 4px; }' +
            '.jfh-discovery-tabs::-webkit-scrollbar-track { background: transparent; }' +
            '.jfh-discovery-tabs::-webkit-scrollbar-thumb { background: var(--color-primary-scrollbar, rgba(0,164,220,0.4)); border-radius: 3px; }' +
            '.jfh-discovery-tab { flex: 1 1 0; min-width: 0; display: inline-flex; align-items: center; justify-content: center; text-align: center; padding: 0.5em 1.1em; border: none; border-radius: 7px; background: transparent; color: #bbb; cursor: pointer; font-size: 0.9em; font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; transition: background 0.2s, color 0.2s, box-shadow 0.2s; }' +
            '.jfh-discovery-tab:hover { background: rgba(255,255,255,0.07); color: #fff; }' +
            '.jfh-discovery-tab:focus-visible { outline: 2px solid #00a4dc; outline-offset: 1px; }' +
            '.jfh-discovery-tab-active { background: #00a4dc; color: #fff; box-shadow: 0 2px 8px rgba(0,164,220,0.35); }' +
            // Narrow panels: slightly smaller tab labels so all three fit without truncation.
            '@container (max-width: 479px) { .jfh-discovery-tab { font-size: 0.8em; padding: 0.5em 0.4em; } }';
        document.head.appendChild(style);
    }

    function getCachedDiscoveryResult() {
        // Never serve one user's recommendations to another after an in-tab
        // account switch.
        if (_discoveryResultCache && _discoveryResultCache.userId === currentDiscoveryUserId()) {
            return _discoveryResultCache.data;
        }
        return null;
    }

    function setCachedDiscoveryResult(data) {
        _discoveryResultCache = { data: data, userId: currentDiscoveryUserId() };
    }

    function invalidateDiscoveryResult() {
        // A request/dismiss mutates the shared candidate pool, so every tab's
        // cache is stale - not just the own-tab one. Nulling the Trakt caches
        // keeps a pre-mutation card from reappearing if the user switches tabs
        // before the delayed refresh runs. (The caches are hoisted vars declared
        // further down.) The generation bump below invalidates in-flight Trakt
        // GETs via their startedGeneration guard.
        _discoveryResultCache = null;
        _traktPersonalCache = null;
        _traktTrendingCache = null;
        // Any GET already in flight predates this invalidation and must not
        // commit its (now stale) result.
        _discoveryGeneration++;
    }

    // Normalized timestamp used to decide whether a background fetch returned
    // newer data than what is already rendered.
    function discoveryGeneratedAt(data) {
        var raw = data?.GeneratedAt;
        if (!raw) { return 0; }
        var ms = Date.parse(raw);
        return Number.isNaN(ms) ? 0 : ms;
    }

    // Fingerprint of the visible set: the pool timestamp alone cannot catch a change
    // from another client, so the visible ids are compared instead.
    function discoveryVisibleKey(data) {
        var recs = data?.Recommendations;
        if (!Array.isArray(recs)) { return ''; }
        return recs.map(function (r) {
            return (r.TmdbId || '') + ':' + (r.MediaType || '');
        }).join('|');
    }

    // After a cache-first render, silently refetch once to pick up a scheduler
    // run or an out-of-band Seerr reconcile. Only re-renders when the payload is
    // genuinely newer or its visible set changed, and never shows a spinner, so
    // the visible grid updates in place without a loading flash. A 403 (feature
    // toggled off) clears the cache and leaves the current view untouched.
    function maybeRefreshInBackground(container) {
        if (_backgroundRefreshInFlight) { return; }
        _backgroundRefreshInFlight = true;
        var startedGeneration = _discoveryGeneration;
        var startedUserId = currentDiscoveryUserId();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' })
            .then(function (data) {
                // Discard if a mutation happened, or the user switched, while the
                // request was in flight: committing it would clobber newer data
                // or leak another user's recommendations.
                if (_discoveryGeneration !== startedGeneration || startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                var previous = _discoveryResultCache?.data;
                setCachedDiscoveryResult(data);
                var changed = !previous
                    || discoveryGeneratedAt(data) > discoveryGeneratedAt(previous)
                    || discoveryVisibleKey(data) !== discoveryVisibleKey(previous);
                // Render into the panel that is live now, not the one captured at
                // call time: Custom Tabs may have rebuilt it while we were away.
                // With Trakt tabs on, the shell owns lastMountedContainer: only refresh
                // the own-tab host, and only while it is still the active tab. Rendering
                // into the outer marker would replace the whole shell (tab bar included).
                var target = null;
                if (_traktEnabled) {
                    var liveHost = lastMountedContainer?.querySelector('.jfh-discovery-tab-host');
                    if (_activeTab === TAB_OWN && liveHost && document.contains(liveHost)) {
                        target = liveHost;
                    }
                } else if (lastMountedContainer && document.contains(lastMountedContainer)) {
                    target = lastMountedContainer;
                } else if (document.contains(container)) {
                    target = container;
                }
                if (changed && target) {
                    renderCards(target, data);
                }
            })
            .catch(function (err) {
                if (err?.status === 403) {
                    invalidateDiscoveryResult();
                }
            })
            .finally(function () {
                _backgroundRefreshInFlight = false;
            });
    }

    var lastMountedContainer = null;
    var customTabWatcherStarted = false;
    var mountPending = false;
    // Mounting is gated until strings are loaded so the first render never flashes the
    // English fallbacks before the configured language arrives. The observer/listeners,
    // however, start as early as possible (see startCustomTabWatcher) so a hard reload
    // that restores an already-built Discovery panel is still caught: without an early
    // watcher no mutation fires after our script loads, and the tab stays blank until a
    // manual nav. The watcher queues mounts; this gate releases them once strings land.
    var _mountEnabled = false;

    // Starts the DOM watcher and navigation listeners that drive mounts. Idempotent and
    // independent of the async availability probe, so it can run the instant the script
    // loads on a home context - the fix for the F5 blank-tab race.
    function startCustomTabWatcher() {
        if (customTabWatcherStarted) {
            return;
        }
        customTabWatcherStarted = true;
        // Watch the body: on Modern layout .mainAnimatedPages is an empty decoy sibling,
        // and reactivated tabs can toggle classes with no childList mutation. rAF keeps it cheap.
        var observer = new MutationObserver(function () {
            scheduleTryMount();
        });
        observer.observe(document.body, {
            childList: true,
            subtree: true,
            attributes: true,
            attributeFilter: ['class', 'hidden']
        });
        // Navigation does not always produce an observed mutation in time
        // (cached views, class-only activation), so re-check explicitly too.
        window.addEventListener('hashchange', scheduleTryMount);
        window.addEventListener('popstate', scheduleTryMount);
        window.addEventListener('pageshow', scheduleTryMount);
        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) {
                scheduleTryMount();
            }
        });
    }

    function initCustomTab() {
        injectStyles();
        // Strings are loaded by the time this runs; release the mount gate and watcher.
        _mountEnabled = true;
        startCustomTabWatcher();
        tryMountCustomTab();
    }

    function scheduleTryMount() {
        if (mountPending) {
            return;
        }
        mountPending = true;
        requestAnimationFrame(function () {
            mountPending = false;
            tryMountCustomTab();
        });
    }

    // On a hard reload (F5) Custom Tabs restores the active Discovery panel from the
    // ?tab=N deep link and finishes building it before our MutationObserver exists, so
    // no further mutation ever fires to trigger a mount - the four fixed timeouts this
    // replaces could all miss while the async probe/strings were still in flight, leaving
    // a blank tab until a manual nav produced a fresh mutation. Poll a short bounded
    // schedule instead, stopping as soon as the active container is filled (or we leave
    // the home context), so a slow server self-heals instead of racing a fixed deadline.
    var INITIAL_MOUNT_DELAYS_MS = [250, 500, 1000, 2000, 3500, 5000, 8000];

    function scheduleInitialMountRetries() {
        var step = 0;
        function tick() {
            tryMountCustomTab();
            if (step >= INITIAL_MOUNT_DELAYS_MS.length) {
                return;
            }
            // A filled active marker means the mount succeeded; stop early. Off the home
            // context there is nothing to mount, so stop too and let the observer/nav
            // listeners pick up the next home visit.
            if (!isOnHomePage()) {
                return;
            }
            var active = findActiveContainer();
            if (active?.querySelector('.jfh-discovery-container')) {
                return;
            }
            setTimeout(tick, INITIAL_MOUNT_DELAYS_MS[step++]);
        }
        tick();
    }

    function isOnHomePage() {
        var hash = window.location.hash;
        return hash === '' || hash === '#/home' || hash === '#/home.html'
            || hash.includes('#/home?') || hash.includes('#/home.html?');
    }

    function tryMountCustomTab() {
        // The watcher may fire before strings have loaded (early start for the F5 race).
        // Rendering now would flash English fallbacks, so defer until initCustomTab opens
        // the gate; the retry schedule and queued mutations re-drive the mount right after.
        if (!_mountEnabled) {
            return;
        }
        if (!isOnHomePage()) {
            lastMountedContainer = null;
            return;
        }
        // Custom Tabs destroys + rebuilds its panel div on every tab (re)activation,
        // so a cached host goes stale; drop it when detached and remount into the live one.
        if (lastMountedContainer && !document.contains(lastMountedContainer)) {
            lastMountedContainer = null;
        }
        // Custom Tabs owns the panel and re-injects our marker on each rebuild. Fill the
        // live marker only, never create one. A node-identity change (rebuild) or a marker
        // emptied by the rebuild both re-trigger a render below.
        var container = findActiveContainer();
        if (!container) {
            lastMountedContainer = null;
            // F5 recovery: on a hard reload Custom Tabs builds the panel via its legacy
            // path into the Modern layout's display:none .skinBody subtree and never moves
            // it into <main>, so no VISIBLE container exists even though a marker does. A
            // hashchange makes Custom Tabs re-render the active tab into <main> (verified:
            // the panel then lands visible under <main>, same as a real nav). Only nudge
            // when a stranded marker is actually present and we are on its deep link, and
            // rate-limit so a tab that legitimately has no visible panel never loops.
            maybeNudgeStrandedPanel();
            return;
        }
        var needsRender = container !== lastMountedContainer
            || !container.querySelector('.jfh-discovery-container');
        if (!needsRender) {
            return;
        }
        renderDiscovery(container);
        lastMountedContainer = container;
        // A visible container was found and rendered: the stranded-panel recovery (if any)
        // succeeded, so refresh the nudge budget for a possible later reload in this session.
        _nudgeCount = 0;
    }

    // F5-recovery nudge (see call site). A hard reload can leave the Custom Tabs panel
    // stranded in the Modern layout's display:none .skinBody subtree; dispatching a
    // hashchange makes Custom Tabs re-render the active tab into the visible <main>.
    // Guards: only fire when a marker exists but none is visible, only on a tab deep
    // link, and at most a few times with a cooldown so a tab that genuinely has no
    // visible panel (e.g. feature off) can never spin.
    var _lastNudgeAt = 0;
    var _nudgeCount = 0;
    var NUDGE_COOLDOWN_MS = 1500;
    var MAX_NUDGES = 4;

    function maybeNudgeStrandedPanel() {
        if (_nudgeCount >= MAX_NUDGES) {
            return;
        }
        // Only act while a tab deep link is active, AND only for the Discovery panel's own index. A hidden
        // Discovery marker is normal whenever ANOTHER tab is active (e.g. Favorites #/home?tab=1), so matching
        // any ?tab=N would make us tear down other tabs' panels and fire a synthetic hashchange on a tab that is
        // working fine. Custom Tabs stamps each panel's data-index with the same N its ?tab=N deep link carries,
        // so compare the two.
        var tabMatch = /[?&]tab=(\d+)/.exec(window.location.hash);
        if (!tabMatch) {
            return;
        }
        var activeIndex = tabMatch[1];
        // A marker must exist (Custom Tabs built the panel) yet be invisible (parked in a
        // display:none subtree). If there is no marker at all, Custom Tabs has not built
        // anything to recover and the normal observer path will handle a later build.
        var markers = document.querySelectorAll(CUSTOM_TAB_SELECTOR);
        if (markers.length === 0) {
            return;
        }
        var anyVisible = false;
        var strandedPanels = [];
        for (var marker of markers) {
            if (marker.offsetParent !== null) {
                anyVisible = true;
                break;
            }
            // Only the stranded panel whose index matches the active deep link is ours to recover.
            var panel = marker.closest('[id^="customTab_"]');
            if (panel && String(panel.dataset.index) === activeIndex) {
                strandedPanels.push(panel);
            }
        }
        // Nudge only when the active deep link's own Discovery panel is the stranded one.
        if (anyVisible || strandedPanels.length === 0) {
            return;
        }
        var now = Date.now();
        if (now - _lastNudgeAt < NUDGE_COOLDOWN_MS) {
            return;
        }
        _lastNudgeAt = now;
        _nudgeCount++;
        // CRITICAL: remove the stranded, invisible panel BEFORE nudging. Custom Tabs'
        // renderModernContent() bails early when it finds an existing customTab_ node via
        // main.querySelector() - and the legacy-inserted panel lives inside <main> (under
        // the display:none .skinBody), so it satisfies that check and the panel is never
        // rebuilt into the visible part of <main>. Worse, nudging without removing it first
        // can leave the stale hidden panel in place while a second visible one is built,
        // which is the "content duplicated after nav" symptom. Removing only the active
        // tab's own stranded panel makes the subsequent hashchange rebuild exactly one, visible.
        for (var strandedPanel of strandedPanels) {
            strandedPanel.remove();
        }
        // Custom Tabs listens for hashchange and re-renders the active tab into <main>.
        window.dispatchEvent(new HashChangeEvent('hashchange'));
    }

    // Determines if the marker resides in the currently active, VISIBLE tab panel.
    // Walks ancestors to reject a hidden/inactive legacy tab wrapper, then requires
    // the element to actually be laid out. The visibility check is authoritative: on a
    // hard reload Custom Tabs builds the panel via its legacy ensureContentDiv() path,
    // which inserts it after #favoritesTab inside the Modern layout's .skinBody subtree
    // (display:none). That subtree carries a .page ancestor (#indexPage) but is invisible,
    // so a .page-sawTabWrapper shortcut would wrongly accept it and we would render into a
    // panel the user never sees ("content gone after F5"). offsetParent === null reliably
    // means the element or an ancestor is display:none (our marker is never position:fixed),
    // so it catches exactly that case while still accepting the wrapper-less visible panel.
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
        // Actual layout is the final gate: a panel parked in a display:none subtree
        // (the F5 legacy-insert case) has a null offsetParent and is correctly rejected.
        return element.offsetParent !== null;
    }

    function findActiveContainer() {
        // Custom Tabs re-injects our marker on each panel (re)build, so re-query the
        // live DOM every pass and never fall back to a cached node. The deepest/last
        // active marker wins (a reactivated tab appends after stale siblings).
        var all = document.querySelectorAll(CUSTOM_TAB_SELECTOR);
        for (var i = all.length - 1; i >= 0; i--) {
            if (isActiveTabContainer(all[i])) {
                return all[i];
            }
        }
        return null;
    }

    // The active tab persists across remounts; each tab keeps its own cache
    // so switching tabs is instant and never shows another tab's data.
    var _traktEnabled = null;
    var _traktProbeUserId = null;
    var _activeTab = 'own';
    var _traktPersonalCache = null;
    var _traktTrendingCache = null;

    var TAB_OWN = 'own';
    var TAB_TRAKT = 'trakt';
    var TAB_TRENDING = 'trending';

    // Clears every Trakt client-side state so the next mount re-probes from scratch.
    // Used when the feature is toggled off mid-session (403) or the account changes.
    function resetTraktState() {
        _traktEnabled = false;
        _traktProbeUserId = null;
        _traktPersonalCache = null;
        _traktTrendingCache = null;
    }

    function renderDiscovery(container, forceRefresh) {
        // Probe Trakt once per account so one user's result never gates another user's tabs.
        // With Trakt off the panel stays the single ensemble grid, without layout shift.
        var probeUserId = currentDiscoveryUserId();
        if (_traktEnabled === null || _traktProbeUserId !== probeUserId) {
            _traktProbeUserId = probeUserId;
            _traktEnabled = null;
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt'), dataType: 'json' })
                .then(function () { _traktEnabled = true; })
                .catch(function (err) {
                    if (err?.status === 403) {
                        resetTraktState();
                    } else {
                        // Fail closed on a transient/non-403 error: show the single ensemble grid rather
                        // than a sticky Trakt tab bar whose tabs would each fail. Clearing the probe-user
                        // marker lets the next mount re-probe, so a brief network blip self-heals instead
                        // of latching the broken tabs for the rest of the session.
                        _traktEnabled = false;
                        _traktProbeUserId = null;
                    }
                })
                .finally(function () {
                    // The probe result belongs to probeUserId and to this mounted panel:
                    // drop it if the account switched or the panel detached meanwhile.
                    if (probeUserId !== currentDiscoveryUserId()) { _traktEnabled = null; return; }
                    if (!document.contains(container)) { return; }
                    renderShell(container, forceRefresh);
                });
            return;
        }

        renderShell(container, forceRefresh);
    }

    // Builds the tab bar (when Trakt is on) plus a content host, then renders the active tab into the host.
    // With Trakt off there is no tab bar and the host fills with the own-grid directly.
    function renderShell(container, forceRefresh) {
        if (!_traktEnabled) {
            renderOwnTab(container, forceRefresh);
            return;
        }

        var tabs =
            '<div class="jfh-discovery-container"><div class="jfh-discovery-tabs" role="tablist">' +
            tabButton(TAB_OWN, t('discoveryTabForYou', 'For you')) +
            tabButton(TAB_TRAKT, t('discoveryTabTraktForYou', 'Trakt for you')) +
            tabButton(TAB_TRENDING, t('discoveryTabTraktTrending', 'Trakt trending')) +
            '</div><div class="jfh-discovery-tab-host"></div></div>';
        container.innerHTML = tabs;

        var buttons = container.querySelectorAll('.jfh-discovery-tab');
        for (const button of buttons) {
            button.addEventListener('click', function () {
                var tab = this.dataset.tab;
                if (tab === _activeTab) { return; }
                _activeTab = tab;
                renderShell(container, false);
            });
        }

        var host = container.querySelector('.jfh-discovery-tab-host');
        if (_activeTab === TAB_TRAKT) {
            renderTraktPersonal(host, forceRefresh);
        } else if (_activeTab === TAB_TRENDING) {
            renderTraktTrending(host, forceRefresh);
        } else {
            renderOwnTab(host, forceRefresh);
        }
    }

    function tabButton(tab, label) {
        var cls = 'jfh-discovery-tab' + (tab === _activeTab ? ' jfh-discovery-tab-active' : '');
        return '<button class="' + cls + '" role="tab" data-tab="' + tab + '">' + esc(label) + '</button>';
    }

    function renderOwnTab(container, forceRefresh) {
        // Remounts render instantly from the last good payload, then a silent
        // background refetch swaps in newer data if the scheduler has run.
        // Only the first mount (or an explicit refresh after a mutation) shows
        // the spinner and blocks on the network.
        if (!forceRefresh) {
            var cached = getCachedDiscoveryResult();
            if (cached) {
                renderCards(container, cached);
                maybeRefreshInBackground(container);
                return;
            }
        }
        container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-spinner" role="status" aria-live="polite" aria-busy="true"><span style="position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0;">' + esc(t('loadingRecommendations', 'Loading recommendations\u2026')) + '</span></div></div>';
        var startedGeneration = _discoveryGeneration;
        var startedUserId = currentDiscoveryUserId();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' })
            .then(function (data) {
                // A mutation or account switch during the fetch makes this result
                // stale; drop it rather than cache/render outdated or cross-user data.
                if (_discoveryGeneration !== startedGeneration || startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                setCachedDiscoveryResult(data);
                renderCards(container, data);
            })
            .catch(function (err) {
                // An account switch during the fetch makes this failure another user's problem;
                // drop it so we never touch the new user's cache or render stale cards.
                if (startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                var fallback = getCachedDiscoveryResult();
                if (err?.status === 403) {
                    invalidateDiscoveryResult();
                } else if (fallback) {
                    // Transient failure: rather show the last known cards than a blank page.
                    renderCards(container, fallback);
                    return;
                }
                var msg = t('discoveryLoadError', 'Could not load discovery suggestions.');
                if (err?.status === 403) {
                    msg = t('discoveryDisabled', 'Discovery is not enabled. Ask your server administrator to enable this feature in Jellyfin Helper settings.');
                }
                container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(msg) + '</p></div></div>';
                // Surface a user-friendly toast for non-200 responses (never raw backend details)
                var serverMessage = extractErrorMessage(err);
                if (serverMessage) {
                    showToast(getUserFriendlyErrorMessage(serverMessage));
                }
            });
    }

    // Unlike the ensemble "own" tab, the Trakt tabs serve purely from the instant cache and do not
    // self-refresh in the background: the scheduled task warms both server-side pools, so a tab load
    // reflects the latest warmed data without a client-side refetch loop. A card mutation still bumps
    // _discoveryGeneration and nulls these caches, so the next render re-fetches fresh data.
    function renderTraktPersonal(host, forceRefresh) {
        if (!forceRefresh && _traktPersonalCache && _traktPersonalCache.userId === currentDiscoveryUserId()) {
            renderCards(host, _traktPersonalCache.data);
            return;
        }

        host.innerHTML = spinnerHtml();
        var startedGeneration = _discoveryGeneration;
        var startedUserId = currentDiscoveryUserId();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt'), dataType: 'json' })
            .then(function (resp) {
                // Drop results that raced an account switch or a mutation:
                // never cache or render cross-user data.
                if (_discoveryGeneration !== startedGeneration || startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                if (resp?.Linked !== true) {
                    renderTraktNotLinked(host);
                    return;
                }
                _traktPersonalCache = { data: resp.Result, userId: startedUserId };
                renderCards(host, resp.Result);
            })
            .catch(function (err) {
                // Same stale-response guard as the success path: a 403 from the
                // previous account must not reset the new user's Trakt state.
                if (_discoveryGeneration !== startedGeneration || startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                renderTraktError(host, err);
            });
    }

    // Global trending tab: no linking required, just fetch and render the per-user-scored pool.
    function renderTraktTrending(host, forceRefresh) {
        if (!forceRefresh && _traktTrendingCache && _traktTrendingCache.userId === currentDiscoveryUserId()) {
            renderCards(host, _traktTrendingCache.data);
            return;
        }

        host.innerHTML = spinnerHtml();
        var startedGeneration = _discoveryGeneration;
        var startedUserId = currentDiscoveryUserId();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt/Trending'), dataType: 'json' })
            .then(function (data) {
                // Same account-switch/mutation guard as the other tabs.
                if (_discoveryGeneration !== startedGeneration || startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                _traktTrendingCache = { data: data, userId: startedUserId };
                renderCards(host, data);
            })
            .catch(function (err) {
                // Same stale-response guard as the success path (see personal tab).
                if (_discoveryGeneration !== startedGeneration || startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                renderTraktError(host, err);
            });
    }

    function renderTraktError(host, err) {
        if (err?.status === 403) {
            // Feature toggled off mid-session: hide the tabs on the next mount.
            resetTraktState();
        }
        var msg = (err?.status === 403)
            ? t('discoveryTraktDisabled', 'Trakt is not enabled. Ask your server administrator to enable it in Jellyfin Helper settings.')
            : t('discoveryLoadError', 'Could not load discovery suggestions.');
        host.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(msg) + '</p></div></div>';
    }

    function spinnerHtml() {
        return '<div class="jfh-discovery-container"><div class="jfh-discovery-spinner" role="status" aria-live="polite" aria-busy="true"><span style="position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0;">' + esc(t('loadingRecommendations', 'Loading recommendations…')) + '</span></div></div>';
    }

    // Trakt is sourced only through the official Trakt plugin, so there is no in-app connect flow; a not-linked
    // user is told to link in that plugin, after which recommendations appear here automatically.
    function renderTraktNotLinked(host) {
        var msg = t('discoveryTraktNotLinkedOfficial', 'Trakt is not linked for your account. Connect your Trakt account in the official Trakt plugin; recommendations will appear here automatically.');
        host.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(msg) + '</p></div></div>';
    }

    function renderCards(container, userDiscovery) {
        if (!userDiscovery || !userDiscovery.Recommendations || userDiscovery.Recommendations.length === 0) {
            container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(t('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.')) + '</p></div></div>';
            return;
        }
        var TMDB_IMG = 'https://image.tmdb.org/t/p/w500';
        var html = '<div class="jfh-discovery-container"><div class="jfh-discovery-grid">';
        var recs = userDiscovery.Recommendations;
        for (var i = 0; i < recs.length; i++) {
            var r = recs[i];
            var posterUrl = r.PosterPath ? TMDB_IMG + r.PosterPath : '';
            // Build poster with flip (front = image, back = overview)
            var overviewText = r.Overview || '';
            var mediaType = (r.MediaType || '').trim().toLowerCase();
            var poster;
            // Build external links row for the flip back side.
            // Uses <span> with data-href + JS click handler instead of <a> to prevent
            // the poster flip from triggering and for consistent cross-platform behavior.
            var tmdbPath = mediaType === 'tv' ? 'tv' : 'movie';
            var tmdbExtUrl = 'https://www.themoviedb.org/' + tmdbPath + '/' + (Number.parseInt(r.TmdbId, 10) || 0);
            var extLinksHtml = '<div class="jfh-discovery-flip-links">' +
                '<span class="jfh-discovery-flip-link" data-href="' + esc(tmdbExtUrl) + '">' +
                '<span class="material-icons" style="font-size:0.95em;">open_in_new</span> TMDB</span>';
            if (_seerrBaseUrl) {
                var seerrExtUrl = _seerrBaseUrl + '/' + tmdbPath + '/' + (Number.parseInt(r.TmdbId, 10) || 0);
                extLinksHtml += '<span class="jfh-discovery-flip-link" data-href="' + esc(seerrExtUrl) + '">' +
                    '<span class="material-icons" style="font-size:0.95em;">open_in_new</span> Seerr</span>';
            }
            // Trakt deep link to the canonical trakt.tv page via the slug the API returns
            // (ids.slug). The old trakt.tv/search/tmdb/:id route 404s, and slugs must never be
            // guessed from titles, so the link renders only with a sane slug while Trakt is on.
            if (_traktEnabled && typeof r.TraktSlug === 'string' && /^[a-z0-9-]{1,100}$/.test(r.TraktSlug)) {
                var traktPath = mediaType === 'tv' ? 'shows' : 'movies';
                var traktExtUrl = 'https://trakt.tv/' + traktPath + '/' + r.TraktSlug;
                extLinksHtml += '<span class="jfh-discovery-flip-link" data-href="' + esc(traktExtUrl) + '">' +
                    '<span class="material-icons" style="font-size:0.95em;">open_in_new</span> Trakt</span>';
            }
            extLinksHtml += '</div>';

            if (posterUrl) {
                poster = '<div class="jfh-discovery-card-poster">' +
                    '<div class="jfh-discovery-flip-inner">' +
                    '<div class="jfh-discovery-flip-front"><img src="' + esc(posterUrl) + '" alt="' + esc(r.Title || '') + '" loading="lazy"></div>' +
                    '<div class="jfh-discovery-flip-back">' + extLinksHtml + '<div class="jfh-discovery-flip-back-text">' + esc(overviewText || t('discoveryNoDescription', 'No description available.')) + '</div></div>' +
                    '</div></div>';
            } else {
                poster = '<div class="jfh-discovery-card-poster jfh-discovery-no-poster"><span style="opacity:0.3;font-size:2em;">\uD83C\uDFAC</span></div>';
            }
            var year = r.Year ? '<span class="jfh-discovery-tag">' + esc(String(r.Year)) + '</span>' : '';
            var mediaLabel = mediaType === 'movie' ? t('movies', 'Movie') : t('tvShows', 'TV');
            var type = r.MediaType ? '<span class="jfh-discovery-tag">' + esc(mediaLabel) + '</span>' : '';
            var ratingNum = Number(r.TmdbRating);
            var rating = (!Number.isNaN(ratingNum) && ratingNum > 0) ? '<span class="jfh-discovery-tag">\u2B50 ' + ratingNum.toFixed(1) + '</span>' : '';
            var genres = (r.Genres && r.Genres.length > 0) ? r.Genres.slice(0, 2).map(function(g) { return '<span class="jfh-discovery-tag">' + esc(g) + '</span>'; }).join('') : '';
            var scorePercent = Math.max(0, Math.min(100, Math.round((Number(r.Score) || 0) * 100)));
            var scoreClass = getDiscoveryScoreClass(scorePercent);
            // On the Trakt tabs the cards keep Trakt's own ranking, so the percentage is our local signal, not
            // the sort order. Flag it with a small "Local score" tag so a lower % sitting above a higher % reads
            // as intentional. The normal Seerr tab, which IS sorted by this score, shows no tag.
            var isTraktTab = _activeTab === TAB_TRAKT || _activeTab === TAB_TRENDING;
            var localScoreTag = isTraktTab ? ' <span class="jfh-discovery-local-score">' + esc(t('discoveryLocalScore', 'Local score')) + '</span>' : '';
            var scoreHtml = '<div class="jfh-discovery-score ' + scoreClass + '"><div class="jfh-discovery-score-bar" style="width:' + scorePercent + '%"></div></div><div class="jfh-discovery-score-text">' + scorePercent + '% ' + t('recsMatch', 'match') + localScoreTag + '</div>';
            var reasonText = formatReason(r.ReasonKey, r.Reason, r.RelatedInfo);
            var reason = reasonText ? '<div class="jfh-discovery-reason">' + esc(reasonText) + '</div>' : '';
            var btnText = r.AlreadyRequested ? '\u2713 ' + t('discoveryRequested', 'Requested') : t('discoveryRequest', 'Request');
            var btnClass = r.AlreadyRequested ? 'jfh-discovery-btn jfh-discovery-btn-done' : 'jfh-discovery-btn';
            var btnDisabled = r.AlreadyRequested ? ' disabled' : '';
            var dismissBtnHtml = r.AlreadyRequested ? '' : '<button class="jfh-discovery-btn jfh-discovery-btn-dismiss" data-tmdb="' + (Number.parseInt(r.TmdbId, 10) || 0) + '" data-type="' + esc(mediaType) + '" data-title="' + esc(r.Title || '') + '">' + esc(t('discoveryDismiss', 'Not interested')) + '</button>';
            var genresHtml = genres ? '<div class="jfh-discovery-card-genres">' + genres + '</div>' : '';
            html += '<div class="jfh-discovery-card">' + poster +
                '<div class="jfh-discovery-card-body">' +
                '<div class="jfh-discovery-card-title" title="' + esc(r.Title || '') + '">' + esc(r.Title || t('recsUnknownTitle', 'Unknown')) + '</div>' +
                '<div class="jfh-discovery-card-meta">' + year + type + rating + '</div>' +
                genresHtml +
                scoreHtml + reason +
                '<div class="jfh-discovery-btn-row">' +
                '<button class="' + btnClass + '" data-tmdb="' + (Number.parseInt(r.TmdbId, 10) || 0) + '" data-type="' + esc(mediaType) + '"' + btnDisabled + '>' + esc(btnText) + '</button>' +
                dismissBtnHtml +
                '</div>' +
                '</div></div>';
        }
        html += '</div></div>';
        container.innerHTML = html;
        var buttons = container.querySelectorAll('.jfh-discovery-btn:not([disabled]):not(.jfh-discovery-btn-dismiss)');
        for (var j = 0; j < buttons.length; j++) { buttons[j].addEventListener('click', handleRequest); }
        // Attach dismiss button handlers
        var dismissBtns = container.querySelectorAll('.jfh-discovery-btn-dismiss');
        for (var d = 0; d < dismissBtns.length; d++) { dismissBtns[d].addEventListener('click', handleDismissClick); }
        // Attach poster flip handlers
        var posters = container.querySelectorAll('.jfh-discovery-card-poster .jfh-discovery-flip-inner');
        for (var p = 0; p < posters.length; p++) {
            posters[p].parentElement.addEventListener('click', function () {
                this.classList.toggle('flipped');
            });
        }
        // Attach external link handlers on the flip back side.
        // Opens URLs in a new tab. stopPropagation prevents the poster flip from triggering.
        var flipLinks = container.querySelectorAll('.jfh-discovery-flip-link[data-href]');
        for (var fl = 0; fl < flipLinks.length; fl++) {
            flipLinks[fl].addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var url = this.dataset.href;
                // Re-validate the scheme at the sink: even though data-href is HTML-escaped and
                // the source is filtered, never hand a non-http(s) URL to window.open().
                var safeUrl = safeHttpUrl(url);
                if (safeUrl) window.open(safeUrl, '_blank', 'noopener,noreferrer');
            });
        }
    }

    var _permCache = {};
    var PERM_CACHE_TTL_MS = 300000; // 5 minutes

    function handleRequest(e) {
        var btn = e.currentTarget;
        if (btn.disabled) return;
        var tmdbId = Number.parseInt(btn.dataset.tmdb, 10);
        var mediaType = btn.dataset.type;
        if (!tmdbId || !mediaType) return;
        fetchPermissionsAndRequest(tmdbId, mediaType, btn);
    }

    function fetchPermissionsAndRequest(tmdbId, mediaType, btn) {
        // Clear any pending reset timer from a previous denial/error to prevent
        // stale callbacks from overwriting the new request's button state.
        if (btn._resetTimer) {
            clearTimeout(btn._resetTimer);
            btn._resetTimer = null;
        }
        btn.classList.remove('jfh-discovery-btn-failed');
        var serviceType = (mediaType === 'tv') ? 'sonarr' : 'radarr';
        var userId = (ApiClient.getCurrentUserId && ApiClient.getCurrentUserId()) || '';
        var cacheKey = serviceType + ':' + mediaType + ':' + userId;
        var cached = _permCache[cacheKey];
        if (cached && (Date.now() - cached._ts) < PERM_CACHE_TTL_MS) {
            decideAndSubmit(tmdbId, mediaType, btn, cached);
            return;
        }
        btn.disabled = true;
        btn.textContent = t('discoveryRequesting', 'Requesting...');
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl(API_URL + '/RequestPermissions/' + serviceType + '?mediaType=' + mediaType),
            dataType: 'json'
        }).then(function (permResult) {
            var result = permResult || { CanRequest: false };
            // Only cache definitive responses - transient upstream failures (IsTransient)
            // should allow immediate retry on the next click instead of being sticky for 5 min.
            if (!result.IsTransient) {
                result._ts = Date.now();
                _permCache[cacheKey] = result;
            }
            btn.disabled = false;
            btn.textContent = t('discoveryRequest', 'Request');
            decideAndSubmit(tmdbId, mediaType, btn, result);
        }).catch(function () {
            // On network error, try submitting with defaults (server will validate).
            // Do NOT cache the fallback - a transient failure should allow retry on next click.
            btn.disabled = false;
            btn.textContent = t('discoveryRequest', 'Request');
            submitRequest(tmdbId, mediaType, null, null, null, btn);
        });
    }

    function decideAndSubmit(tmdbId, mediaType, btn, permResult) {
        if (!permResult.CanRequest) {
            btn.textContent = t('discoveryRequestFailed', 'Failed');
            btn.classList.add('jfh-discovery-btn-failed');
            showToast(permResult.DeniedReason || permResult.Message || t('discoveryNoPermission', 'You do not have permission to submit requests. Please contact your server administrator.'));
            btn._resetTimer = setTimeout(function () {
                btn._resetTimer = null;
                btn.textContent = t('discoveryRequest', 'Request');
                btn.classList.remove('jfh-discovery-btn-failed');
                btn.disabled = false;
            }, 3000);
            return;
        }
        var profiles = permResult.Profiles || [];
        if (profiles.length === 0) {
            submitRequest(tmdbId, mediaType, null, null, null, btn);
        } else if (profiles.length === 1) {
            var p = profiles[0];
            submitRequest(tmdbId, mediaType, p.ServerId, p.ProfileId, p.RootFolder, btn);
        } else {
            showProfilePopup(tmdbId, mediaType, btn, profiles);
        }
    }

    function showProfilePopup(tmdbId, mediaType, btn, profiles) {
        var existing = document.getElementById('jfhDiscoveryPopup');
        if (existing) {
            if (existing._onEsc) {
                document.removeEventListener('keydown', existing._onEsc);
            }
            existing.remove();
        }
        injectPopupStyles();

        var serverIds = {};
        for (let i = 0; i < profiles.length; i++) { serverIds[profiles[i].ServerId] = true; }
        var multiServer = Object.keys(serverIds).length > 1;

        var overlay = document.createElement('div');
        overlay.id = 'jfhDiscoveryPopup';
        overlay.className = 'jfh-discovery-popup-overlay';
        var popup = document.createElement('div');
        popup.className = 'jfh-discovery-popup';
        popup.setAttribute('role', 'dialog');
        popup.setAttribute('aria-modal', 'true');
        popup.setAttribute('aria-labelledby', 'jfhPopupTitle');

        var title = document.createElement('div');
        title.className = 'jfh-discovery-popup-title';
        title.id = 'jfhPopupTitle';
        title.textContent = t('discoverySelectQualityProfile', 'Select Quality Profile');
        popup.appendChild(title);

        var subtitle = document.createElement('div');
        subtitle.className = 'jfh-discovery-popup-subtitle';
        subtitle.textContent = t('discoverySelectQualityProfileDesc', 'Choose which quality profile to use for the download:');
        popup.appendChild(subtitle);

        var list = document.createElement('div');
        list.className = 'jfh-discovery-popup-list';
        for (let i = 0; i < profiles.length; i++) {
            var prof = profiles[i];
            var item = document.createElement('button');
            item.className = 'jfh-discovery-popup-item' + (prof.IsDefault ? ' jfh-discovery-popup-item-default' : '');
            // Build the button label using safe DOM operations instead of innerHTML to prevent XSS if ProfileName/ServerName originates from a compromised Arr/Seerr instance.
            item.appendChild(document.createTextNode(prof.ProfileName));
            if (multiServer) {
                item.appendChild(document.createTextNode(' '));
                var serverSpan = document.createElement('span');
                serverSpan.style.opacity = '0.6';
                serverSpan.textContent = '(' + prof.ServerName + ')';
                item.appendChild(serverSpan);
            }
            if (prof.IsDefault) {
                item.appendChild(document.createTextNode(' '));
                var defaultSpan = document.createElement('span');
                defaultSpan.style.opacity = '0.5';
                defaultSpan.style.fontSize = '0.8em';
                defaultSpan.textContent = '\u2605 ' + t('discoveryProfileDefault', 'default');
                item.appendChild(defaultSpan);
            }
            item.addEventListener('click', (function (sid, pid, rf) {
                return function () { closeDiscoveryPopup(btn); submitRequest(tmdbId, mediaType, sid, pid, rf, btn); };
            })(prof.ServerId, prof.ProfileId, prof.RootFolder));
            list.appendChild(item);
        }
        finalizeDiscoveryPopup(overlay, popup, list, btn);
    }

    function injectPopupStyles() {
        if (document.getElementById('jfhelper-popup-styles')) return;
        var s = document.createElement('style');
        s.id = 'jfhelper-popup-styles';
        s.textContent =
            '.jfh-discovery-popup-overlay{position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,.7);z-index:99999;display:flex;align-items:center;justify-content:center}' +
            '.jfh-discovery-popup{background:#1c1c2e;border-radius:12px;padding:1.5em;max-width:400px;width:90%;max-height:80vh;overflow-y:auto;box-shadow:0 8px 32px rgba(0,0,0,.5)}' +
            '.jfh-discovery-popup-title{font-size:1.1em;font-weight:600;margin-bottom:.3em;color:#fff}' +
            '.jfh-discovery-popup-subtitle{font-size:.85em;opacity:.7;margin-bottom:1em;color:#ccc}' +
            '.jfh-discovery-popup-list{display:flex;flex-direction:column;gap:.5em}' +
            '.jfh-discovery-popup-item{display:flex;align-items:center;gap:.6em;padding:.7em 1em;border:1px solid rgba(255,255,255,.1);border-radius:8px;background:rgba(255,255,255,.03);cursor:pointer;color:#fff;font-size:.9em;transition:background .2s,border-color .2s;text-align:left;width:100%}' +
            '.jfh-discovery-popup-item:hover{background:rgba(0,164,220,.15);border-color:#00a4dc}' +
            '.jfh-discovery-popup-item-default{border-color:rgba(0,164,220,.4);background:rgba(0,164,220,.08)}' +
            '.jfh-discovery-popup-cancel{display:block;width:100%;margin-top:1em;padding:.6em;border:none;border-radius:6px;background:rgba(255,255,255,.1);color:#fff;cursor:pointer;font-size:.85em;text-align:center;transition:background .2s}' +
            '.jfh-discovery-popup-cancel:hover{background:rgba(255,255,255,.2)}';
        document.head.appendChild(s);
    }

    function closeDiscoveryPopup(triggerBtn) {
        var el = document.getElementById('jfhDiscoveryPopup');
        if (el?._onEsc) {
            document.removeEventListener('keydown', el._onEsc);
        }
        if (el) el.remove();
        if (triggerBtn?.focus) triggerBtn.focus();
    }

    function finalizeDiscoveryPopup(overlay, popup, list, btn) {
        popup.appendChild(list);
        var cancelBtn = document.createElement('button');
        cancelBtn.className = 'jfh-discovery-popup-cancel';
        cancelBtn.textContent = t('discoveryCancel', 'Cancel');
        cancelBtn.addEventListener('click', function () { closeDiscoveryPopup(btn); });
        popup.appendChild(cancelBtn);
        overlay.appendChild(popup);
        document.body.appendChild(overlay);
        cancelBtn.focus();
        overlay.addEventListener('click', function (ev) { if (ev.target === overlay) closeDiscoveryPopup(btn); });
        function onEsc(ev) { if (ev.key === 'Escape') closeDiscoveryPopup(btn); }
        document.addEventListener('keydown', onEsc);
        overlay._onEsc = onEsc;
    }

    function submitRequest(tmdbId, mediaType, serverId, profileId, rootFolder, btn) {
        btn.disabled = true;
        btn.textContent = t('discoveryRequesting', 'Requesting...');
        var payload = { TmdbId: tmdbId, MediaType: mediaType };
        if (serverId != null) payload.ServerId = serverId;
        if (profileId != null) payload.ProfileId = profileId;
        if (rootFolder) payload.RootFolder = rootFolder;
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(API_URL + '/Request'),
            data: JSON.stringify(payload),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            if (result && result.Success) {
                // Invalidate immediately on success so a stale pool is never
                // served again, even if the user leaves the tab before the
                // card-removal animation below runs.
                invalidateDiscoveryResult();
                btn.textContent = '\u2713 ' + t('discoveryRequested', 'Requested');
                btn.classList.add('jfh-discovery-btn-done');
                // Hide dismiss button in the same row
                var row = btn.closest('.jfh-discovery-btn-row');
                var dismissBtn = row ? row.querySelector('.jfh-discovery-btn-dismiss') : null;
                if (dismissBtn) dismissBtn.style.display = 'none';
                // After 5 seconds: fade out and remove the card (consumed from pool)
                var card = btn.closest('.jfh-discovery-card');
                if (card) {
                    var scopeEl = card.closest('.jfh-discovery-container');
                    setTimeout(function () {
                        card.style.transition = 'opacity 0.4s ease, transform 0.4s ease';
                        card.style.opacity = '0';
                        card.style.transform = 'scale(0.95)';
                        setTimeout(function () {
                            card.remove();
                            if (lastMountedContainer) {
                                renderDiscovery(lastMountedContainer, true);
                            } else {
                                checkEmptyDiscoveryState(scopeEl);
                            }
                        }, 400);
                    }, 5000);
                }
            } else {
                btn.textContent = t('discoveryRequestFailed', 'Failed');
                btn.classList.add('jfh-discovery-btn-failed');
                showToast(getUserFriendlyErrorMessage(result && result.Message));
                setTimeout(function () { btn.textContent = t('discoveryRequest', 'Request'); btn.classList.remove('jfh-discovery-btn-failed'); btn.disabled = false; }, 3000);
            }
        }).catch(function (err) {
            btn.textContent = t('discoveryRequestFailed', 'Failed');
            btn.classList.add('jfh-discovery-btn-failed');
            var serverMessage = extractErrorMessage(err);
            showToast(getUserFriendlyErrorMessage(serverMessage));
            setTimeout(function () { btn.textContent = t('discoveryRequest', 'Request'); btn.classList.remove('jfh-discovery-btn-failed'); btn.disabled = false; }, 3000);
        });
    }

    /** * Checks if all discovery cards have been removed (requested/dismissed) and shows * the empty-state message so the user doesn't see a blank page. */
    function checkEmptyDiscoveryState(scopeContainer) {
        var grid = scopeContainer ? scopeContainer.querySelector('.jfh-discovery-grid') : null;
        if (!grid) return;
        if (grid.querySelectorAll('.jfh-discovery-card').length === 0) {
            var host = grid.closest('.jfh-discovery-container');
            if (host) {
                host.innerHTML = '<div class="jfh-discovery-msg"><p>' + esc(t('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.')) + '</p></div>';
            }
        }
    }


    /**
     * Handles the dismiss button click. Shows a confirmation popup before dismissing.
     */
    function handleDismissClick(e) {
        var btn = e.currentTarget;
        if (btn.disabled) return;
        var tmdbId = Number.parseInt(btn.dataset.tmdb, 10);
        var mediaType = btn.dataset.type;
        var title = btn.dataset.title || '';
        if (!tmdbId || !mediaType) return;
        showDismissConfirmation(tmdbId, mediaType, title, btn);
    }

    /**
     * Shows a confirmation popup before dismissing a discovery item.
     * Reuses the same popup overlay pattern as the profile selector.
     */
    function showDismissConfirmation(tmdbId, mediaType, title, btn) {
        var existing = document.getElementById('jfhDiscoveryPopup');
        if (existing) {
            if (existing._onEsc) document.removeEventListener('keydown', existing._onEsc);
            existing.remove();
        }
        injectPopupStyles();

        var overlay = document.createElement('div');
        overlay.id = 'jfhDiscoveryPopup';
        overlay.className = 'jfh-discovery-popup-overlay';
        var popup = document.createElement('div');
        popup.className = 'jfh-discovery-popup';
        popup.setAttribute('role', 'dialog');
        popup.setAttribute('aria-modal', 'true');
        popup.setAttribute('aria-labelledby', 'jfhPopupTitle');

        var titleEl = document.createElement('div');
        titleEl.className = 'jfh-discovery-popup-title';
        titleEl.id = 'jfhPopupTitle';
        titleEl.textContent = t('discoveryDismissConfirmTitle', 'Dismiss suggestion?');
        popup.appendChild(titleEl);

        var subtitle = document.createElement('div');
        subtitle.className = 'jfh-discovery-popup-subtitle';
        subtitle.textContent = title
            ? t('discoveryDismissConfirmText', 'This will hide "{0}" from future suggestions. It won\'t be shown again.').replace('{0}', title)
            : t('discoveryDismissConfirmGeneric', 'This item will be hidden from future suggestions.');
        popup.appendChild(subtitle);

        var list = document.createElement('div');
        list.className = 'jfh-discovery-popup-list';

        var confirmBtn = document.createElement('button');
        confirmBtn.className = 'jfh-discovery-popup-item';
        confirmBtn.style.justifyContent = 'center';
        confirmBtn.style.borderColor = 'rgba(231,76,60,0.4)';
        confirmBtn.style.color = '#e74c3c';
        confirmBtn.textContent = t('discoveryDismissConfirm', 'Yes, dismiss');
        confirmBtn.addEventListener('click', function () {
            closeDiscoveryPopup(btn);
            executeDismiss(tmdbId, mediaType, btn);
        });
        list.appendChild(confirmBtn);
        finalizeDiscoveryPopup(overlay, popup, list, btn);
    }

    /**
     * Submits the dismiss API call and removes the card from the grid on success.
     */
    function executeDismiss(tmdbId, mediaType, btn) {
        btn.disabled = true;
        btn.textContent = '...';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(API_URL + '/Dismiss'),
            data: JSON.stringify({ TmdbId: tmdbId, MediaType: mediaType }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function () {
            // Invalidate immediately on success so the dismissed item is never
            // served from cache again, even if the user navigates away before
            // the fade-out animation below completes.
            invalidateDiscoveryResult();
            // Remove the card with a fade-out animation
            var card = btn.closest('.jfh-discovery-card');
            if (card) {
                var scopeEl = card.closest('.jfh-discovery-container');
                card.style.transition = 'opacity 0.3s ease, transform 0.3s ease';
                card.style.opacity = '0';
                card.style.transform = 'scale(0.95)';
                setTimeout(function () {
                    card.remove();
                    if (lastMountedContainer) {
                        renderDiscovery(lastMountedContainer, true);
                    } else {
                        checkEmptyDiscoveryState(scopeEl);
                    }
                }, 300);
            }
        }).catch(function () {
            btn.disabled = false;
            btn.textContent = t('discoveryDismiss', 'Not interested');
            showToast(t('discoveryDismissError', 'Could not dismiss this item. Please try again.'));
        });
    }

    // Translate reason key to localized human-readable text.
    // Backend DetermineReason produces: reasonPersonNamed, reasonGenre, reasonTrending, reasonPopular
    function formatReason(reasonKey, reason, relatedInfo) {
        if (!reasonKey && !reason) return '';
        var key = reasonKey || '';
        // Try i18n lookup first (keys match en.json: reasonPopular, reasonGenre, reasonTrending, etc.)
        if (key === 'reasonPersonNamed' && relatedInfo) {
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
        if (reason && reason.startsWith('reason')) {
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
        // Coerce before escaping: a caller may pass a number (e.g. a year or rating), and replaceAll
        // only exists on strings - without this a non-string value throws instead of being escaped.
        return String(str).replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');
    }

    function initSidebar() {
        injectNavigation();
        var drawer = document.querySelector('.mainDrawer');
        // Fallback to document.body if .mainDrawer hasn't mounted yet (cold load / SPA timing)
        var target = drawer || document.body;
        var observer = new MutationObserver(function () {
            var sidebar = document.querySelector('.mainDrawer-scrollContainer');
            if (sidebar && !sidebar.querySelector('.' + NAV_ITEM_CLASS)) {
                injectNavigation();
            }
        });
        observer.observe(target, { childList: true, subtree: true });
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
                mediaSection.before(section);
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
            '<span class="sectionName navMenuOptionText">' + esc(t('discoveryTitle', 'Seerr Discovery')) + '</span>';
        navItem.addEventListener('click', function (e) {
            e.preventDefault();
            if (activateDiscoveryTab()) {
                return;
            }
            // Not on the home page (or the tab bar has not mounted yet): go home
            // first, then retry once the DOM settles.
            if (typeof Emby !== 'undefined' && Emby.Page && Emby.Page.show) {
                Emby.Page.show('/home.html');
                setTimeout(function () {
                    if (!activateDiscoveryTab()) {
                        notifyDiscoveryTabMissing();
                    }
                }, 800);
                return;
            }
            notifyDiscoveryTabMissing();
        });
        section.appendChild(navItem);
    }

    // Activate the Custom Tab that holds the discovery marker, across both the
    // Jellyfin 12 Modern (MUI) header/drawer and the legacy tab bar. Returns
    // true when a tab control was triggered (or a hash navigation was issued).
    function activateDiscoveryTab() {
        var container = document.querySelector(CUSTOM_TAB_SELECTOR);
        if (!container) {
            return false;
        }
        var panel = container.closest('[data-index]');
        var dataIndex = panel ? Number.parseInt(panel.dataset.index, 10) : Number.NaN;

        // Modern layout: tab controls are MUI anchors whose href carries the
        // same ?tab=N deep link the panel's data-index encodes. Match on the
        // href so the user-configured tab title/position is irrelevant.
        if (!Number.isNaN(dataIndex)) {
            var modernLink = document.querySelector(
                'header.MuiAppBar-root a[href$="?tab=' + dataIndex + '"], '
                + 'header.MuiAppBar-root a[href$="&tab=' + dataIndex + '"], '
                + '.MuiDrawer-paper a[href$="?tab=' + dataIndex + '"], '
                + '.MuiDrawer-paper a[href$="&tab=' + dataIndex + '"]');
            if (modernLink) {
                modernLink.click();
                return true;
            }
        }

        var tabs = document.querySelectorAll('.headerTabs button, [role="tab"]');
        // Legacy layout: data-index is the positional index into the tab bar.
        if (!Number.isNaN(dataIndex) && tabs[dataIndex]) {
            tabs[dataIndex].click();
            return true;
        }
        // data-attribute match (future Custom Tabs versions may set these).
        for (var i = 0; i < tabs.length; i++) {
            if (tabs[i].dataset.tab === 'jellyfinhelper-discovery' ||
                tabs[i].dataset.tabid === 'jellyfinhelper-discovery') {
                tabs[i].click();
                return true;
            }
        }
        // Modern fallback: no anchor found but the panel knows its deep link, so
        // route via the hash and let Custom Tabs' own hashchange handler render.
        if (!Number.isNaN(dataIndex) && document.querySelector('header.MuiAppBar-root')) {
            window.location.hash = '#/home?tab=' + dataIndex;
            return true;
        }
        return false;
    }

    function notifyDiscoveryTabMissing() {
        var msg = t('discoveryTabNotFound', 'The Discovery tab could not be found. Please ensure the Custom Tabs plugin is installed, or contact your server administrator.');
        if (typeof Dashboard !== 'undefined' && Dashboard.alert) {
            Dashboard.alert(msg);
        } else {
            showToast(msg);
        }
    }

    // The script is injected into the single index.html Jellyfin serves for every
    // SPA route, so it also loads on admin/dashboard pages (e.g. #!/configurationpage)
    // where no Discovery tab can ever appear. Probing /Discovery/My there is wasted
    // traffic and, when the feature is disabled, a 403 the browser logs as a console
    // error. Gate the whole bootstrap on the home context (the same isOnHomePage
    // signal the mount logic already trusts).
    //
    // The full UI (sidebar + tab) inits only once. An empty probe is NOT terminal: a
    // scheduled task may produce recommendations later in the same SPA session, so we
    // leave the gate open and re-probe on later home visits until results appear. A 403
    // (feature disabled by the admin) IS terminal - re-probing would only replay the 403.
    var _bootstrapped = false;
    var _discoveryDisabled = false;
    var _probeInFlight = false;
    // The user the gates above were last decided for. An in-tab account switch must not inherit
    // the previous user's terminal 403 or in-flight probe, so a change resets the bootstrap gate.
    var _probedUserId = null;

    function initDiscoveryUiEmpty() {
        // No discovery data available (task deactivated/dry-run/no results yet). Still init
        // Custom Tab so it can show a "no results" message if the container exists, but do NOT
        // inject sidebar navigation - no point advertising a feature with no content.
        initCustomTab();
        scheduleInitialMountRetries();
    }

    function initDiscoveryUiFull() {
        // Discovery is active and has recommendations - full initialization. Wait for
        // external links config (Seerr URL) before rendering to ensure the Seerr link
        // is available on the first card render.
        loadExternalLinksConfig().finally(function () {
            initCustomTab();
            initSidebar();
            scheduleInitialMountRetries();
        });
    }

    function handleDiscoveryProbe(data, startedUserId) {
        // A later account switch started a fresh bootstrap; this result predates it and must not
        // flip gates or inject UI for the previous user.
        if (startedUserId !== currentDiscoveryUserId()) {
            return;
        }
        // Check if Discovery is available before injecting UI elements.
        if (!data || !data.Recommendations || data.Recommendations.length === 0) {
            // Leave the gate open: results may appear after a later scheduled run, and a future
            // home visit re-probes to upgrade to the full UI without a page reload.
            initDiscoveryUiEmpty();
            return;
        }
        _bootstrapped = true;
        initDiscoveryUiFull();
    }

    function probeDiscoveryAvailability(startedUserId) {
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' })
            .then(function (data) {
                handleDiscoveryProbe(data, startedUserId);
            })
            .catch(function (err) {
                // Ignore a stale response once the account has switched - a prior user's 403 must
                // not disable Discovery for whoever is signed in now.
                if (startedUserId !== currentDiscoveryUserId()) {
                    return;
                }
                // 403 means the admin disabled the feature: terminal, do not re-probe. Any other
                // error (network/transient) leaves the gate open for a later retry.
                if (err?.status === 403) {
                    _discoveryDisabled = true;
                }
            })
            .finally(function () {
                // Only the current generation owns the gate; a stale finally must not clear a gate
                // a newer bootstrap set.
                if (startedUserId === currentDiscoveryUserId()) {
                    _probeInFlight = false;
                }
            });
    }

    function bootstrapDiscovery() {
        // Reset the gates on an in-tab account switch: a prior user's 403 or in-flight probe must
        // not block a different user who may well have access.
        var userId = currentDiscoveryUserId();
        if (userId && userId !== _probedUserId) {
            _probedUserId = userId;
            _bootstrapped = false;
            _discoveryDisabled = false;
            _probeInFlight = false;
        }
        if (_bootstrapped || _discoveryDisabled || _probeInFlight || !isOnHomePage()) {
            return;
        }
        // Start the DOM watcher the moment we know we are on a home context, before the
        // async probe. On a hard reload Custom Tabs restores the active Discovery panel
        // immediately; the watcher must already be listening or the restore mutation is
        // missed and the tab stays blank. Mounts stay gated (_mountEnabled) until strings
        // load, so this cannot render untranslated content early.
        startCustomTabWatcher();
        _probeInFlight = true;
        waitForApi(function () {
            // Pin the generation only once a user is actually present (waitForApi may have polled
            // through the signed-out gap). Every async step below re-checks it against the live user
            // so an account switch mid-flight cannot let a stale callback touch shared state or the UI.
            var startedUserId = currentDiscoveryUserId();
            loadStrings(function () {
                if (startedUserId !== currentDiscoveryUserId()) { return; }
                probeDiscoveryAvailability(startedUserId);
            });
        }, function () {
            // API never became available (timeout): release the gate so a later sign-in can re-probe.
            _probeInFlight = false;
        });
    }

    // Try at load, and on every SPA navigation until the first home context is seen.
    bootstrapDiscovery();
    window.addEventListener('hashchange', bootstrapDiscovery);
    window.addEventListener('popstate', bootstrapDiscovery);
})();