// Jellyfin Helper - Discovery home tab and sidebar script.
// Injected into index.html per request by the plugin and rendered into a native
// Home tab registered through native-tabs.js.
(function () {
    'use strict';

    // Guard against double initialization when the script tag is served twice.
    if (window.__jfhDiscoverySidebarInitialized) {
        return;
    }
    window.__jfhDiscoverySidebarInitialized = true;

    var TAB_ID = 'discovery';
    var TAB_ICON = 'explore';
    var SECTION_CLASS = 'jellyfinHelperSection';
    var NAV_ITEM_CLASS = 'jfhelper-nav-discovery';
    // Discovery has no standalone page, so the sidebar link activates the native Home tab.
    var API_URL = '/JellyfinHelper/Discovery/My';
    var _reportedImpressions = new WeakMap();

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
    var _consumedDiscoveryItems = new Set();

    function currentDiscoveryUserId() {
        return (typeof ApiClient !== 'undefined' && ApiClient.getCurrentUserId?.()) || '';
    }

    function isLoginPage() {
        var route = window.location.hash.replace(/^#!?\/?/, '').split(/[/?]/)[0].toLowerCase();
        if (route.endsWith('.html')) route = route.slice(0, -5);
        return route === 'login' || route === 'selectserver' || route === 'addserver';
    }

    function captureDiscoveryRequest() {
        var client = typeof ApiClient === 'undefined' ? null : ApiClient;
        return {
            generation: _discoveryGeneration,
            userId: currentDiscoveryUserId(),
            client: client,
            token: client?.accessToken?.() || ''
        };
    }

    function isCurrentDiscoveryRequest(request) {
        return request.generation === _discoveryGeneration && isSameDiscoverySession(request);
    }

    function isSameDiscoverySession(request) {
        var current = captureDiscoveryRequest();
        return request.userId === current.userId
            && request.client === current.client && request.token === current.token && !isLoginPage() && _hostSessionChanges === 0;
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

    function loadStrings(callback, request) {
        request = request || captureDiscoveryRequest();
        // No lang parameter - the server returns the language configured in plugin settings
        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('/JellyfinHelper/Translations'),
            dataType: 'json'
        }).then(function (data) {
            if (!isCurrentDiscoveryRequest(request)) return;
            _strings = data || {};
            callback();
        }).catch(function () {
            if (!isCurrentDiscoveryRequest(request)) return;
            _strings = {};
            callback();
        });
    }

    function t(key, fallback) {
        if (_strings?.[key]) return _strings[key];
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
        var request = captureDiscoveryRequest();
        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl(EXTERNAL_LINKS_URL),
            dataType: 'json'
        }).then(function (data) {
            if (!isCurrentDiscoveryRequest(request)) return;
            var url = safeHttpUrl(data?.SeerrUrl);
            var end = url.length;
            while (end > 0 && url[end - 1] === '/') end--;
            _seerrBaseUrl = url.slice(0, end);
        }).catch(function () {
            // Non-critical - Seerr link option will be hidden in the popup
        });
    }


    /** * Shows a temporary toast notification at the bottom-center of the viewport. * Used to surface error details from non-200 API responses without blocking the UI. */
    function showToast(message, duration) {
        window.JellyfinHelperUi.showToast(message, {
            className: 'jfh-discovery-toast',
            duration: duration || TOAST_DURATION_MS,
            closeLabel: t('close', 'Close')
        });
    }

    /** * Extracts a human-readable error message from an API error response. * Handles both XHR-style objects (responseText/responseJSON) and plain error objects. */
    function extractErrorMessage(err) {
        if (!err) return '';
        try {
            // ApiClient.ajax may expose responseJSON directly
            if (err.responseJSON?.Message) {
                return err.responseJSON.Message;
            }
            // Fall back to parsing responseText
            if (err.responseText) {
                var parsed = JSON.parse(err.responseText);
                if (parsed?.Message) return parsed.Message;
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
            '.jellyfinHelperSection .jfh-discovery-sidebar-heading { box-sizing:border-box;margin:0;padding:0.45em 0.8em;overflow:hidden;text-overflow:ellipsis;white-space:nowrap; }' +
            '.jellyfinHelperSection .jfh-discovery-modern-link { box-sizing:border-box;display:grid;grid-template-columns:1.25em minmax(0,1fr);align-items:center;column-gap:0.5em;width:100%;min-width:0;max-width:100%;min-height:2.75em;padding:0.5em 0.8em;color:inherit;text-decoration:none;border-radius:0.25em; }' +
            '.jellyfinHelperSection .jfh-discovery-modern-link .jfh-discovery-navigation-icon { width:1em;font-size:1.25em;line-height:1;text-align:center; }' +
            '.jellyfinHelperSection .jfh-discovery-modern-link .jfh-discovery-navigation-text { min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap; }' +
            '@media (hover:hover) and (pointer:fine) { .jellyfinHelperSection .jfh-discovery-modern-link:hover { background:rgba(255,255,255,0.08); } }' +
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
            '.jfh-discovery-flip-inner { position: relative; width: 100%; aspect-ratio: 2/3; transition: transform 280ms cubic-bezier(0.45,0,0.55,1); transform-style: preserve-3d; }' +
            '.jfh-discovery-card-poster.flipped .jfh-discovery-flip-inner { transform: rotateY(180deg); }' +
            '.jfh-discovery-flip-front, .jfh-discovery-flip-back { position: absolute; top: 0; left: 0; width: 100%; height: 100%; backface-visibility: hidden; -webkit-backface-visibility: hidden; box-sizing: border-box; }' +
            '.jfh-discovery-flip-front img { width: 100%; height: 100%; object-fit: cover; display: block; }' +
            '.jfh-discovery-flip-back { transform: rotateY(180deg); background: rgba(20,20,30,0.95); padding: 1.2em; overflow-y: auto; box-sizing: border-box; overscroll-behavior:contain; scrollbar-width:thin; scrollbar-color:var(--color-primary-scrollbar) transparent; }' +
            '.jfh-discovery-flip-back::-webkit-scrollbar { width:6px;height:6px; }' +
            '.jfh-discovery-flip-back::-webkit-scrollbar-track,.jfh-discovery-flip-back::-webkit-scrollbar-corner { background:transparent; }' +
            '.jfh-discovery-flip-back::-webkit-scrollbar-thumb { background:var(--color-primary-scrollbar);border-radius:3px; }' +
            '.jfh-discovery-flip-back::-webkit-scrollbar-button { display:none;width:0;height:0; }' +
            '@media(hover:hover) and (pointer:fine){.jfh-discovery-flip-back::-webkit-scrollbar-thumb:hover{background:var(--color-primary-scrollbar-hover)}}' +
            '.jfh-discovery-flip-back-text { font-size: 0.92em; line-height: 1.5; opacity: 0.9; color: #eee; word-break: break-word; overflow-wrap: break-word; }' +
            '.jfh-discovery-no-poster { width: 100%; aspect-ratio: 2/3; display: flex; align-items: center; justify-content: center; background: rgba(255,255,255,0.02); }' +
            // Card body
            '.jfh-discovery-card-body { padding: 0.8em; flex: 1; display: flex; flex-direction: column; gap: 0.4em; }' +
            '.jfh-discovery-card-title { font-weight: 600; font-size: 0.95em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }' +
            // Wrap, never clip or scroll: on a narrow 2-column mobile card year+type+rating overflowed the
            // nowrap row and the rating tag was cut off at the edge. Wrapping drops it to the next line instead.
            '.jfh-discovery-card-meta { display: flex; flex-wrap: wrap; gap: 0.3em; }' +
            // Genres wrap too. A per-card horizontal scrollbar here was the unwanted "second scrollbar"; with at
            // most two genres a single wrap is clean and needs no scroll affordance.
            '.jfh-discovery-card-genres { display: flex; flex-wrap: wrap; gap: 0.3em; }' +
            '.jfh-discovery-tag { background: rgba(255,255,255,0.1); border-radius: 4px; padding: 0.15em 0.5em; font-size: 0.75em; white-space: nowrap; flex-shrink: 0; }' +
            '.jfh-discovery-flip-links { display: flex; gap: 0.6em; margin-bottom: 0.8em; padding-bottom: 0.6em; border-bottom: 1px solid rgba(255,255,255,0.1); flex-wrap: wrap; }' +
            '.jfh-discovery-flip-link { display: inline-flex; align-items: center; gap: 0.3em; color: #00a4dc; text-decoration: none; font-size: 0.85em; font-weight: 500; padding: 0.3em 0.5em; border-radius: 4px; transition: background 0.2s, opacity 0.2s; opacity: 0.9; }' +
            '@media (hover:hover) and (pointer:fine) { .jfh-discovery-flip-link:hover { background: rgba(0,164,220,0.15); opacity: 1; text-decoration: none; } }' +
            '.jfh-discovery-score { height: 4px; background: rgba(255,255,255,0.1); border-radius: 2px; overflow: hidden; margin: 0.3em 0; }' +
            '.jfh-discovery-score-bar { height: 100%; border-radius: 2px; }' +
            '.jfh-discovery-score-high .jfh-discovery-score-bar { background: #2ecc71; }' +
            '.jfh-discovery-score-mid .jfh-discovery-score-bar { background: #f39c12; }' +
            '.jfh-discovery-score-low .jfh-discovery-score-bar { background: #e74c3c; }' +
            '.jfh-discovery-score-text { font-size: 0.7em; opacity: 0.6; }' +
            '.jfh-discovery-local-score { display: inline-block; margin-left: 0.4em; padding: 0 0.4em; border-radius: 3px; background: rgba(255,255,255,0.1); font-size: 0.9em; opacity: 0.85; white-space: nowrap; vertical-align: baseline; }' +
            '.jfh-discovery-btn-row { margin-top: auto; display: flex; flex-wrap: wrap; gap: 0.4em; align-items: stretch; }' +
            // box-sizing + a fixed min-height keep every button state (Request / Requesting… / ✓ Requested /
            // Failed) exactly the same size, so swapping the label never changes the button's height or width
            // and the state transition doesn't flicker. The request button spans the full row (its dismiss
            // sibling is hidden for the duration), so these labels stay single-line.
            '.jfh-discovery-btn { flex: 1; box-sizing: border-box; min-height: 2.6em; padding: 0.5em; border: none; border-radius: 4px; background: #00a4dc; color: #fff; cursor: pointer; font-size: 0.85em; display: flex; align-items: center; justify-content: center; gap: 0.3em; transition: background 0.2s; white-space: normal; overflow-wrap: anywhere; text-align: center; min-width: 0; }' +
            '@media (hover:hover) and (pointer:fine) { .jfh-discovery-btn:hover:not(:disabled):not(.jfh-discovery-btn-dismiss) { background: #006487; } }' +
            '.jfh-discovery-btn:disabled { opacity: 0.6; cursor: not-allowed; }' +
            '.jfh-discovery-btn-done { background: #2ecc71 !important; }' +
            '.jfh-discovery-btn-failed { background: #e74c3c !important; }' +
            '.jfh-discovery-btn-dismiss { background: rgba(255,255,255,0.08); color: #ccc; overflow-wrap: anywhere; }' +
            '@media (hover:hover) and (pointer:fine) { .jfh-discovery-btn-dismiss:hover { background: rgba(231,76,60,0.2); color: var(--color-danger); } }' +
            '.jfh-discovery-msg { text-align: center; padding: 2em; opacity: 0.6; }' +
            '.jfh-discovery-reason { font-size: 0.78em; opacity: 0.7; margin: 0.2em 0; font-style: italic; overflow-wrap: anywhere; }' +
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
            '@media (hover:hover) and (pointer:fine) { .jfh-discovery-tab:hover { color: var(--color-text); } }' +
            '.jfh-discovery-tab:focus-visible { outline: 2px solid #00a4dc; outline-offset: 1px; }' +
            '.jfh-discovery-tab-active { background: #00a4dc; color: #fff; box-shadow: 0 2px 8px rgba(0,164,220,0.35); }' +
            // Narrow panels: slightly smaller tab labels so all three fit without truncation.
            '@container (max-width: 479px) { .jfh-discovery-tab { font-size: 0.8em; padding: 0.5em 0.4em; } }' +
            // Trakt pending slot: uncommented mini-spinner reserving roughly the tab bar's
            // height while the silent probe runs. The tab bar replaces it on success, a
            // 403/timeout removes it without a trace.
            '.jfh-trakt-pending { display: flex; justify-content: center; align-items: center; min-height: 40px; margin: 0 0 1em 0; }' +
            '.jfh-trakt-pending-dot { width: 12px; height: 12px; border: 2px solid rgba(255,255,255,0.2); border-top-color: #00a4dc; border-radius: 50%; animation: dspin 0.8s linear infinite; }' +
            '.jfh-discovery-card { position:relative;color:var(--color-text);background:var(--color-surface-card); }' +
            '.jfh-discovery-card::after { content:"";position:absolute;inset:0;border:1px solid var(--color-border);border-radius:inherit;pointer-events:none;z-index:1; }' +
            '.jfh-discovery-flip-back { background:var(--color-surface-card); }' +
            '.jfh-discovery-flip-back-text { color:var(--color-text);max-width:65ch; }' +
            '.jfh-discovery-poster-toggle { position:absolute;inset:0;width:100%;height:100%;padding:0;border:0;background:transparent;cursor:pointer;z-index:1; }' +
            '.jfh-discovery-card-poster.flipped .jfh-discovery-poster-toggle { pointer-events:none; }' +
            '.jfh-ui-surface .jfh-discovery-poster-toggle:focus-visible { outline:2px solid var(--color-primary);outline-offset:-3px; }' +
            '.jfh-discovery-btn { background:var(--color-button-primary);transition:background var(--motion-feedback) ease,transform var(--motion-press) var(--ease-out); }' +
            '.jfh-discovery-btn:active:not(:disabled) { transform:scale(.97); }' +
            '.jfh-discovery-btn-done { background:var(--color-button-success)!important; }' +
            '.jfh-discovery-btn-failed { background:var(--color-button-danger)!important; }' +
            '.jfh-discovery-btn-dismiss { background:transparent;box-shadow:inset 0 0 0 1px var(--color-border);color:var(--color-text-muted); }' +
            '.jfh-discovery-tab { color:var(--color-text-muted); }' +
            '.jfh-discovery-tabs { background:var(--color-surface-card);border-color:var(--color-border); }' +
            '.jfh-discovery-tab-active { background:var(--color-button-primary);color:#fff; }' +
            '.jfh-discovery-score-text { font-variant-numeric:tabular-nums;opacity:.85; }' +
            '.jfh-discovery-toast { position:relative;bottom:auto;left:auto;box-sizing:border-box;width:100%;max-width:none;padding-right:2.8rem;background:var(--color-surface-card);color:var(--color-text);transform:translateY(8px);transition:opacity var(--motion-feedback) var(--ease-out),transform var(--motion-feedback) var(--ease-out); }' +
            '.jfh-discovery-toast-visible { transform:none; }' +
            '.jfh-discovery-toast-hidden { transform:translateY(8px); }' +
            '@media(prefers-reduced-motion:reduce){.jfh-discovery-flip-inner,.jfh-discovery-toast,.jfh-discovery-btn{transition:none}.jfh-discovery-spinner::after,.jfh-trakt-pending-dot{animation:none}.jfh-discovery-card-poster.flipped .jfh-discovery-flip-inner,.jfh-discovery-flip-back{transform:none}.jfh-discovery-card-poster.flipped .jfh-discovery-flip-front,.jfh-discovery-card-poster:not(.flipped) .jfh-discovery-flip-back{display:none}.jfh-discovery-toast,.jfh-discovery-btn:active:not(:disabled){transform:none}}';
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
        _traktProbeResult = null;
        _trendingPreloadInFlight = false;
        _ownFetchPromise = null;
        _backgroundRefreshInFlight = false;
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

    // Card details can change without a new pool timestamp, so compare the complete visible payload.
    function discoveryVisibleKey(data) {
        var recs = data?.Recommendations;
        return Array.isArray(recs) && recs.length ? JSON.stringify(recs) : '';
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
        var request = captureDiscoveryRequest();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' })
            .then(function (data) {
                // Discard if a mutation happened, or the user switched, while the
                // request was in flight: committing it would clobber newer data
                // or leak another user's recommendations.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                var previous = _discoveryResultCache?.data;
                setCachedDiscoveryResult(data);
                var changed = !previous
                    || discoveryGeneratedAt(data) > discoveryGeneratedAt(previous)
                    || data?.DeliveryId !== previous?.DeliveryId
                    || discoveryVisibleKey(data) !== discoveryVisibleKey(previous);
                // Render into the panel that is live now, not the one captured at
                // call time, because Jellyfin may have rebuilt it while we were away.
                // With Trakt tabs on, the shell owns _tabPanel: only refresh the
                // own-tab host, and only while it is still the active tab.
                var target = null;
                if (_traktEnabled) {
                    var liveHost = _tabPanel?.querySelector('.jfh-discovery-tab-host');
                    if (_activeTab === TAB_OWN && liveHost && document.contains(liveHost)) {
                        target = liveHost;
                    }
                } else if (_tabPanel && document.contains(_tabPanel)) {
                    target = _tabPanel;
                } else if (document.contains(container)) {
                    target = container;
                }
                if (changed && target) {
                    renderCards(target, data);
                }
            })
            .catch(function (err) {
                if (isCurrentDiscoveryRequest(request) && err?.status === 403) {
                    disableDiscoveryUi();
                }
            })
            .finally(function () {
                if (_discoveryGeneration === startedGeneration) {
                    _backgroundRefreshInFlight = false;
                }
            });
    }

    // The panel native-tabs.js created for us. It is replaced when Jellyfin
    // rebuilds the Home page, and onMount hands us the new one.
    var _tabPanel = null;
    var _tabRegistered = false;

    function isOnHomePage() {
        var hash = window.location.hash;
        return hash === '' || hash === '#/home' || hash === '#/home.html'
            || hash.includes('#/home?') || hash.includes('#/home.html?');
    }

    // Registers the Discovery tab once the availability probe succeeded. The
    // registry only mounts the panel while the user is on the Home page and
    // calls this render callback again for every new panel.
    function registerDiscoveryTab() {
        if (_tabRegistered || !window.JellyfinHelperTabs) {
            return;
        }
        _tabRegistered = true;
        var session = captureDiscoveryRequest();
        window.JellyfinHelperTabs.register(TAB_ID, t('discoveryTabTitle', '\u2728 Discover'), function (panel) {
            if (!isSameDiscoverySession(session)) return;
            _tabPanel = panel;
            renderDiscovery(panel);
        }, TAB_ICON, function (panel) {
            if (!isSameDiscoverySession(session)) {
                bootstrapDiscovery();
                return;
            }
            if (!_discoveryDisabled && currentDiscoveryUserId() && !isLoginPage()) {
                _tabPanel = panel;
                renderDiscovery(panel);
            }
        });
    }

    // Returns true when the tab was activated or the pending focus will be
    // applied once the tab strip exists. False means the caller must navigate
    // to the Home page first.
    function focusDiscoveryTab() {
        return !!window.JellyfinHelperTabs && window.JellyfinHelperTabs.focus(TAB_ID);
    }

    function notifyTabUnavailable() {
        showToast(t('discoveryTabUnavailable', 'Discovery is not available right now. Please try again later.'));
    }

    // The active tab persists across remounts; each tab keeps its own cache
    // so switching tabs is instant and never shows another tab's data.
    var _traktEnabled = null;
    // Whether THIS user is linked to Trakt (distinct from _traktEnabled, which is the global feature switch).
    // An unlinked user still gets the Trending tab but not the personal "Trakt for you" tab.
    var _traktLinked = false;
    var _traktProbeUserId = null;
    var _activeTab = 'own';
    var _traktPersonalCache = null;
    var _traktTrendingCache = null;
    // Probe response kept so the personal tab can render without refetching the
    // same endpoint. Consumed once, cleared on any mutation or account switch.
    var _traktProbeResult = null;
    // Guards a second probe while one is in flight for the same user (re-mounts
    // and late resolvers otherwise fan out duplicate Trakt probes).
    var _traktProbeInFlight = false;
    // Sequence per fired probe so a stale timeout/finally can never clobber a newer probe's state.
    var _traktProbeSeq = 0;
    // Delayed pending-slot reveal: fast probes (e.g. instant local 403s) never flash
    // a spinner; only a genuinely slow probe shows one.
    var TRAKT_PENDING_REVEAL_MS = 300;
    // Silent probe budget: past this the tab stays single-grid and the next mount re-probes.
    var TRAKT_PROBE_TIMEOUT_MS = 12000;
    // A null/empty Trakt result is cached only this long, mirroring the server-side empty backoff: enough to
    // absorb duplicate tab loads, short enough that a genuine first result is not hidden behind an empty entry.
    var TRAKT_EMPTY_CACHE_MS = 15000;
    // Background trending preload so the first tab click usually hits the cache.
    // Keyed by user; cleared on mutation via invalidateDiscoveryResult.
    var _trendingPreloadInFlight = false;
    var _trendingPreloadUserId = null;
    // Shared own-grid fetch: concurrent mounts attach to one request instead of
    // firing duplicate GETs (shell-first render + probe-finally re-render).
    var _ownFetchPromise = null;
    var _ownFetchUserId = null;
    var _ownFetchGeneration = -1;

    var TAB_OWN = 'own';
    var TAB_TRAKT = 'trakt';
    var TAB_TRENDING = 'trending';

    // Clears every Trakt client-side state so the next mount re-probes from scratch.
    // Used when the feature is toggled off mid-session (403) or the account changes.
    function resetTraktState() {
        _traktEnabled = false;
        _traktLinked = false;
        _traktProbeUserId = null;
        _traktPersonalCache = null;
        _traktTrendingCache = null;
        _traktProbeResult = null;
        _traktProbeInFlight = false;
        _trendingPreloadInFlight = false;
        _trendingPreloadUserId = null;
    }

    function renderDiscovery(container, forceRefresh) {
        container.classList.add('jfh-ui-surface');
        // Probe Trakt once per account so one user's result never gates another user's tabs.
        // With Trakt off the panel stays the single ensemble grid, without layout shift.
        var probeUserId = currentDiscoveryUserId();
        if (_traktEnabled === null || _traktProbeUserId !== probeUserId) {
            // Read before resetting: a probe still in flight for THIS user must not
            // fire twice, but an account switch always needs a fresh probe even while
            // the previous user's request is away (its finally self-discards).
            var sameUserProbeRunning = _traktProbeInFlight && _traktProbeUserId === probeUserId;
            _traktProbeUserId = probeUserId;
            _traktEnabled = null;
            _traktProbeResult = null;
            // Clear the per-user link flag on an account switch too: leaving a prior user's _traktLinked=true
            // would let the next (possibly unlinked) account briefly see the personal tab until its probe returns.
            _traktLinked = false;
            // Render the single own-grid immediately so the tab never stays blank
            // while the probe is in flight; the probe upgrades to the tab bar below.
            renderShell(container, forceRefresh);
            scheduleTraktPending(container, probeUserId);
            if (sameUserProbeRunning) {
                return;
            }
            _traktProbeInFlight = true;
            var probeSeq = ++_traktProbeSeq;
            // Fire-time generation: a mutation (request/dismiss) bumping the generation
            // while the probe is away must not let the pre-mutation payload pass as fresh.
            // Only the payload is gated by it; the enabled flag stays live so the tabs
            // do not flicker away after every mutation (a full probe kill would re-hide
            // them for the whole probe latency on each request/dismiss).
            var firedGeneration = _discoveryGeneration;
            var probeRequest = captureDiscoveryRequest();
            var probeSettled = false;
            var probeTimer = setTimeout(function () {
                // Stalled probe: fail closed like a transient error so the pending slot
                // vanishes instead of spinning forever; the next mount re-probes.
                if (probeSettled || probeSeq !== _traktProbeSeq) { return; }
                probeSettled = true;
                _traktProbeInFlight = false;
                _traktEnabled = false;
                _traktProbeUserId = null;
                hideTraktPending(container);
            }, TRAKT_PROBE_TIMEOUT_MS);
            ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt'), dataType: 'json' })
                .then(function (resp) {
                    // Superseded probes never own shared state; the newest probe's
                    // callbacks alone decide _traktEnabled and the probe result.
                    if (probeSettled || probeSeq !== _traktProbeSeq || !isSameDiscoverySession(probeRequest)) { return; }
                    _traktEnabled = true;
                    // Record link state so renderShell can hide the personal tab for an unlinked user while
                    // still offering Trending (which needs no link).
                    _traktLinked = resp?.Linked === true;
                    // Keep the probe payload: it is exactly what the personal tab
                    // would refetch, so the first visit renders without a second GET.
                    // Stamped with the fire-time generation so a payload that raced a
                    // mutation is rejected by the consumer's freshness check.
                    _traktProbeResult = { resp: resp, userId: probeUserId, generation: firedGeneration };
                })
                .catch(function (err) {
                    if (probeSettled || probeSeq !== _traktProbeSeq || !isSameDiscoverySession(probeRequest)) { return; }
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
                    probeSettled = true;
                    clearTimeout(probeTimer);
                    // Only the newest probe owns the in-flight flag; a stale finally must
                    // not clear it while the replacement request is away.
                    if (probeSeq === _traktProbeSeq) {
                        _traktProbeInFlight = false;
                    } else {
                        // Superseded: the replacement probe owns state and UI from here.
                        return;
                    }
                    // The probe result belongs to probeUserId and to this mounted panel:
                    // drop it if the account switched or the panel detached meanwhile.
                    if (probeUserId !== currentDiscoveryUserId()) { _traktEnabled = null; return; }
                    if (!document.contains(container)) { return; }
                    // Re-render only when the shape changes (tabs now known). The own-grid
                    // rendered above stays live when Trakt is off, avoiding a duplicate fetch.
                    // Either way the pending slot is done: the tab bar replaces it, or the
                    // off path removes it without a trace.
                    hideTraktPending(container);
                    if (_traktEnabled) {
                        renderShell(container, forceRefresh);
                    }
                });
            return;
        }

        renderShell(container, forceRefresh);
    }

    // Schedules the uncommented pending slot reserving the tab bar's place while the
    // silent probe runs. Delayed so fast probes never flash it; guarded so it only ever
    // appears while a probe is genuinely outstanding and no tab bar exists yet.
    function scheduleTraktPending(container, probeUserId) {
        setTimeout(function () {
            if (probeUserId !== currentDiscoveryUserId()) { return; }
            if (!document.contains(container)) { return; }
            if (!_traktProbeInFlight || _traktEnabled) { return; }
            if (container.querySelector('.jfh-trakt-pending') || container.querySelector('.jfh-discovery-tabs')) { return; }
            var row = document.createElement('div');
            row.className = 'jfh-trakt-pending';
            row.setAttribute('role', 'status');
            row.innerHTML = '<span class="jfh-trakt-pending-dot" aria-hidden="true"></span>';
            container.insertBefore(row, container.firstChild);
        }, TRAKT_PENDING_REVEAL_MS);
    }

    function hideTraktPending(container) {
        if (!document.contains(container)) { return; }
        var row = container.querySelector('.jfh-trakt-pending');
        if (row) { row.remove(); }
    }

    // Builds the tab bar (when Trakt is on) plus a content host, then renders the active tab into the host.
    // With Trakt off there is no tab bar and the host fills with the own-grid directly.
    function renderShell(container, forceRefresh) {
        container.classList.add('jfh-ui-surface');
        if (!_traktEnabled) {
            _activeTab = TAB_OWN;
            if (container.querySelector('.jfh-discovery-tabs')) {
                var ownedFocus = container.contains(document.activeElement);
                container.innerHTML = '';
                if (ownedFocus) {
                    container.tabIndex = -1;
                    container.focus({ preventScroll: true });
                }
            }
            renderOwnTab(container, forceRefresh);
            return;
        }

        // An unlinked user cannot have a personal tab; if it was the active tab (e.g. after an account switch),
        // fall back to the own grid so the host never shows a tab whose button is absent.
        if (!_traktLinked && _activeTab === TAB_TRAKT) {
            _activeTab = TAB_OWN;
        }

        var personalButton = _traktLinked
            ? tabButton(TAB_TRAKT, t('discoveryTabTraktForYou', 'Trakt for you'))
            : '';
        var tabs =
            '<div class="jfh-discovery-container"><div class="jfh-discovery-tabs" role="tablist">' +
            tabButton(TAB_OWN, t('discoveryTabForYou', 'For you')) +
            personalButton +
            tabButton(TAB_TRENDING, t('discoveryTabTraktTrending', 'Trakt trending')) +
            '</div><div class="jfh-discovery-tab-host"></div></div>';
        var list = container.querySelector('.jfh-discovery-tabs');
        var shape = _traktLinked ? 'linked' : 'unlinked';
        if (list?.dataset.shape !== shape) {
            container.innerHTML = tabs;
            list = container.querySelector('.jfh-discovery-tabs');
            list.dataset.shape = shape;
        }

        bindDiscoveryTabHandlers(container);

        var host = container.querySelector('.jfh-discovery-tab-host');
        host.id = 'jfh-discovery-subtab-panel';
        window.JellyfinHelperUi.syncTabs(list, '.jfh-discovery-tab', list.querySelector('.jfh-discovery-tab-active'), function () { return host; });
        host.hidden = false;
        host.setAttribute('aria-labelledby', 'jfh-discovery-subtab-' + _activeTab);
        // Warm the trending pool in the background while the user reads the first
        // tab, so opening Trending usually hits the cache instead of cold-fetching.
        maybePreloadTrending();
        if (_activeTab === TAB_TRAKT) {
            renderTraktPersonal(host, forceRefresh);
        } else if (_activeTab === TAB_TRENDING) {
            renderTraktTrending(host, forceRefresh);
        } else {
            renderOwnTab(host, forceRefresh);
        }
    }

    function bindDiscoveryTabHandlers(container) {
        var buttons = container.querySelectorAll('.jfh-discovery-tab');
        for (const button of buttons) {
            button.classList.toggle('jfh-discovery-tab-active', button.dataset.tab === _activeTab);
            if (button.dataset.jfhClickBound) continue;
            button.dataset.jfhClickBound = '1';
            button.addEventListener('click', function () {
                var tab = this.dataset.tab;
                if (tab === _activeTab) { return; }
                _activeTab = tab;
                renderShell(container, false);
            });
        }
    }

    function maybePreloadTrending() {
        var userId = currentDiscoveryUserId();
        if (!userId) { return; }
        if (_traktEnabled !== true) { return; }
        if (traktCacheFresh(_traktTrendingCache)) { return; }
        if (_trendingPreloadInFlight && _trendingPreloadUserId === userId) { return; }
        _trendingPreloadInFlight = true;
        _trendingPreloadUserId = userId;
        var startedGeneration = _discoveryGeneration;
        var request = captureDiscoveryRequest();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt/Trending'), dataType: 'json' })
            .then(function (data) {
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                _traktTrendingCache = makeTraktCacheEntry(data, userId);
            })
            .catch(function () {
                // Ignored by design: a tab click retries with a spinner, so a failed
                // silent preload must never surface an error or clear Trakt state.
            })
            .finally(function () {
                if (userId === _trendingPreloadUserId && _discoveryGeneration === startedGeneration) {
                    _trendingPreloadInFlight = false;
                }
            });
    }

    function tabButton(tab, label) {
        var cls = 'jfh-discovery-tab' + (tab === _activeTab ? ' jfh-discovery-tab-active' : '');
        return '<button type="button" id="jfh-discovery-subtab-' + tab + '" class="' + cls + '" role="tab"'
            + (tab === _activeTab ? ' aria-selected="true"' : ' aria-selected="false"')
            + ' data-tab="' + tab + '">' + esc(label) + '</button>';
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
        var request = captureDiscoveryRequest();
        // A shell-first render plus the probe-finally upgrade can reach this path
        // twice for the same user/generation. Attach to the in-flight request
        // instead of firing a duplicate GET; each caller renders into its own host.
        if (_ownFetchPromise && _ownFetchUserId === startedUserId && _ownFetchGeneration === startedGeneration) {
            _ownFetchPromise.then(function (data) {
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                if (!document.contains(container)) {
                    return;
                }
                // The probe upgrade may have turned this container into a tab shell
                // meanwhile; the fresh tab-host render owns the UI, so a stale
                // own-grid result must not overwrite the tab bar.
                if (container.querySelector('.jfh-discovery-tabs')) {
                    return;
                }
                setCachedDiscoveryResult(data);
                renderCards(container, data);
                // An own render replaces the container content and may have wiped a
                // shown pending slot; re-schedule it (no-op unless a probe is still out).
                scheduleTraktPending(container, startedUserId);
            }).catch(function (err) {
                // Same staleness contract as the success path: a mutation or account
                // switch during the fetch makes this failure another render's problem.
                // Without the generation check a stale 403 would re-invalidate (bumping
                // the generation) and discard the newer in-flight result.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                if (!document.contains(container)) {
                    return;
                }
                renderOwnError(container, err);
            });
            return;
        }
        _ownFetchUserId = startedUserId;
        _ownFetchGeneration = startedGeneration;
        _ownFetchPromise = ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' });
        _ownFetchPromise
            .then(function (data) {
                // A mutation or account switch during the fetch makes this result
                // stale; drop it rather than cache/render outdated or cross-user data.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                if (!document.contains(container)) {
                    return;
                }
                // Same upgrade race as the shared-fetch path above: never let a stale
                // own-grid overwrite a tab shell built while this request was away.
                if (container.querySelector('.jfh-discovery-tabs')) {
                    return;
                }
                setCachedDiscoveryResult(data);
                renderCards(container, data);
                // Same pending-slot re-schedule as the shared-fetch path above.
                scheduleTraktPending(container, startedUserId);
            })
            .catch(function (err) {
                // Same staleness contract as above: drop the failure when a mutation
                // or account switch moved the generation on while it was in flight.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                renderOwnError(container, err);
            })
            .finally(function () {
                if (_ownFetchUserId === startedUserId && _ownFetchGeneration === startedGeneration) {
                    _ownFetchPromise = null;
                }
            });
    }

    function renderOwnError(container, err) {
        if (!document.contains(container)) {
            return;
        }
        var fallback = getCachedDiscoveryResult();
        if (err?.status === 403) {
            disableDiscoveryUi();
        } else if (fallback) {
            // Transient failure: rather show the last known cards than a blank page.
            // A tab shell built meanwhile owns the UI; do not overwrite it.
            if (!container.querySelector('.jfh-discovery-tabs')) {
                renderCards(container, fallback);
                scheduleTraktPending(container, currentDiscoveryUserId());
            }
            return;
        }
        // Same upgrade race as the success path: state above is still cleaned,
        // but the message must not wipe a freshly built tab bar.
        if (container.querySelector('.jfh-discovery-tabs')) {
            return;
        }
        var msg = t('discoveryLoadError', 'Could not load discovery suggestions.');
        if (err?.status === 403) {
            msg = t('discoveryDisabled', 'Discovery is not enabled. Ask your server administrator to enable this feature in Jellyfin Helper settings.');
        }
        // The error marker lets tests and styles tell a failed load apart from the no-results state.
        container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg jfh-discovery-error"><p>' + esc(msg) + '</p></div></div>';
        // Surface a user-friendly toast for non-200 responses (never raw backend details)
        var serverMessage = extractErrorMessage(err);
        if (serverMessage) {
            showToast(getUserFriendlyErrorMessage(serverMessage));
        }
        // Error renders replace the container content too; re-schedule the pending
        // slot (no-op unless a probe is still genuinely outstanding).
        scheduleTraktPending(container, currentDiscoveryUserId());
    }

    // Unlike the ensemble "own" tab, the Trakt tabs serve purely from the instant cache and do not
    // self-refresh in the background: the scheduled task warms both server-side pools, so a tab load
    // reflects the latest warmed data without a client-side refetch loop. A card mutation still bumps
    // _discoveryGeneration and nulls these caches, so the next render re-fetches fresh data.
    //
    // A null or empty Result is cached only briefly (mirroring the server-side empty backoff) so duplicate
    // tab loads coalesce, but a genuine first result that the server produced moments later is not hidden
    // behind a long-lived empty entry. A non-empty result stays cached for the session as before.
    // A Trakt result is the DiscoveryResult object renderCards consumes; empty means no Recommendations to show.
    function isEmptyTraktResult(data) {
        return !data?.Recommendations?.length;
    }

    function traktCacheFresh(entry) {
        if (!entry || entry.userId !== currentDiscoveryUserId()) {
            return false;
        }
        if (isEmptyTraktResult(entry.data)) {
            return (Date.now() - entry.storedAt) < TRAKT_EMPTY_CACHE_MS;
        }
        return !entry.data?.DeliveryExpiresAtUtc || Date.parse(entry.data.DeliveryExpiresAtUtc) > Date.now();
    }

    function makeTraktCacheEntry(data, userId) {
        return { data: data, userId: userId, storedAt: Date.now() };
    }

    function renderTraktPersonal(host, forceRefresh) {
        if (!forceRefresh && traktCacheFresh(_traktPersonalCache)) {
            renderCards(host, _traktPersonalCache.data);
            return;
        }
        // The shell probe already fetched this exact endpoint. Render from it
        // instead of firing a duplicate GET on the first visit.
        if (!forceRefresh && _traktProbeResult && _traktProbeResult.userId === currentDiscoveryUserId()
            && _traktProbeResult.generation === _discoveryGeneration) {
            var probed = _traktProbeResult;
            _traktProbeResult = null;
            // Defensive: renderShell hides the personal tab for an unlinked user, so this path is normally
            // reached only while linked. The not-linked branch stays as a safety net for the brief window where
            // a stale _traktLinked let the tab render before the server confirmed Linked:false.
            if (probed.resp?.Linked !== true) {
                renderTraktNotLinked(host);
                return;
            }
            _traktPersonalCache = makeTraktCacheEntry(probed.resp.Result, probed.userId);
            renderCards(host, probed.resp.Result);
            return;
        }

        host.innerHTML = spinnerHtml();
        var startedUserId = currentDiscoveryUserId();
        var request = captureDiscoveryRequest();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt'), dataType: 'json' })
            .then(function (resp) {
                // Drop results that raced an account switch or a mutation:
                // never cache or render cross-user data.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                if (resp?.Linked !== true) {
                    renderTraktNotLinked(host);
                    return;
                }
                _traktPersonalCache = makeTraktCacheEntry(resp.Result, startedUserId);
                renderCards(host, resp.Result);
            })
            .catch(function (err) {
                // Same stale-response guard as the success path: a 403 from the
                // previous account must not reset the new user's Trakt state.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                renderTraktError(host, err);
            });
    }

    // Global trending tab: no linking required, just fetch and render the per-user-scored pool.
    function renderTraktTrending(host, forceRefresh) {
        if (!forceRefresh && traktCacheFresh(_traktTrendingCache)) {
            renderCards(host, _traktTrendingCache.data);
            return;
        }

        host.innerHTML = spinnerHtml();
        var startedUserId = currentDiscoveryUserId();
        var request = captureDiscoveryRequest();
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl('/JellyfinHelper/Discovery/My/Trakt/Trending'), dataType: 'json' })
            .then(function (data) {
                // Same account-switch/mutation guard as the other tabs.
                if (!isCurrentDiscoveryRequest(request)) {
                    return;
                }
                _traktTrendingCache = makeTraktCacheEntry(data, startedUserId);
                renderCards(host, data);
            })
            .catch(function (err) {
                // Same stale-response guard as the success path (see personal tab).
                if (!isCurrentDiscoveryRequest(request)) {
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
        host.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg jfh-discovery-error"><p>' + esc(msg) + '</p></div></div>';
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
        container._jfhDiscoverySource = _activeTab;
        container.classList.add('jfh-ui-surface');
        var recommendations = (Array.isArray(userDiscovery?.Recommendations) ? userDiscovery.Recommendations : []).filter(function (item) {
            return !_consumedDiscoveryItems.has(discoveryCardKey(item.TmdbId, item.MediaType));
        });
        if (recommendations.length === 0) {
            renderEmptySidebarCards(container);
            return;
        }
        var grid = container.querySelector('.jfh-discovery-grid');
        if (!grid) {
            container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-grid"></div></div>';
            grid = container.querySelector('.jfh-discovery-grid');
        }
        var existing = new Map(Array.from(grid.children).map(function (card) { return [card.dataset.cardKey, card]; }));
        var preserveOrder = grid.contains(document.activeElement) || Array.from(existing.values()).some(function (card) {
            return card._jfhConsuming || window.JellyfinHelperUi.ownsDialogTrigger(card) || card.querySelector('button:disabled:not(.jfh-discovery-btn-done)');
        });
        var wanted = new Set();
        var reference = grid.firstElementChild;
        for (const recommendation of recommendations) {
            while (reference?._jfhConsuming) reference = reference.nextElementSibling;
            var key = discoveryCardKey(recommendation.TmdbId, recommendation.MediaType);
            wanted.add(key);
            var markup = renderSidebarCardHtml(recommendation);
            var card = existing.get(key) || createSidebarCard(markup, key);
            updateSidebarCard(card, markup, userDiscovery.DeliveryId, userDiscovery.DeliveryExpiresAtUtc);
            placeSidebarCard(grid, card, reference, preserveOrder);
            reference = card.nextElementSibling;
        }
        for (var old of existing.values()) {
            if (!wanted.has(old.dataset.cardKey) && !old._jfhConsuming && !window.JellyfinHelperUi.ownsDialogTrigger(old)) window.JellyfinHelperUi.removeCard(old);
        }
        bindSidebarCardHandlers(container);
        observeVisibleDiscoveryCards(container);
    }

    function observeVisibleDiscoveryCards(container) {
        if (typeof window.IntersectionObserver !== 'function') return;
        container._jfhImpressionObserver?.disconnect();
        if (container._jfhImpressionTimer) clearTimeout(container._jfhImpressionTimer);
        container._jfhImpressionTimer = null;
        var grid = container.querySelector('.jfh-discovery-grid');
        if (!grid || grid.closest('.jfh-ui-surface') !== container) return;
        var request = captureDiscoveryRequest();
        var pending = new Map();
        var observer = new window.IntersectionObserver(function (entries) {
            if (!isCurrentDiscoveryRequest(request) || _discoveryDisabled || document.visibilityState !== 'visible') return;
            for (var entry of entries) {
                var card = entry.target;
                var candidate = discoveryImpressionItem(card);
                if (!entry.isIntersecting || entry.intersectionRatio < 0.5 || !candidate || !card._jfhDeliveryId || _consumedDiscoveryItems.has(card.dataset.cardKey)
                    || _reportedImpressions.get(card) === card._jfhDeliveryId) continue;
                pending.set(card.dataset.cardKey, card);
            }
            if (!pending.size || container._jfhImpressionTimer) return;
            container._jfhImpressionTimer = setTimeout(function () {
                container._jfhImpressionTimer = null;
                if (!container.isConnected || !isCurrentDiscoveryRequest(request) || _discoveryDisabled || document.visibilityState !== 'visible'
                    || document.getElementById('jfhDiscoveryPopup')) { pending.clear(); return; }
                var batch = [];
                for (var card of pending.values()) {
                    if (deliveryExpired(card)) {
                        pending.clear();
                        refreshDiscoveryDelivery(container, request);
                        return;
                    }
                    var rect = card.getBoundingClientRect();
                    var candidate = discoveryImpressionItem(card);
                    if (!card.isConnected || !candidate || !card._jfhDeliveryId || _consumedDiscoveryItems.has(card.dataset.cardKey) || _reportedImpressions.get(card) === card._jfhDeliveryId || !rect.width || !rect.height
                        || rect.bottom <= 0 || rect.top >= window.innerHeight || rect.right <= 0 || rect.left >= window.innerWidth) continue;
                    var visibleWidth = Math.max(0, Math.min(rect.right, window.innerWidth) - Math.max(rect.left, 0));
                    var visibleHeight = Math.max(0, Math.min(rect.bottom, window.innerHeight) - Math.max(rect.top, 0));
                    if (visibleWidth * visibleHeight < rect.width * rect.height * 0.5) continue;
                    batch.push({ card: card, deliveryId: card._jfhDeliveryId, id: candidate.TmdbId, type: candidate.MediaType });
                    if (batch.length === 20) break;
                }
                pending.clear();
                if (!batch.length) return;
                // A protected dialog card can retain an older delivery while its siblings are refreshed.
                // Never send those cards under the new response's token.
                var deliveries = new Map();
                for (var item of batch) {
                    if (!deliveries.has(item.deliveryId)) deliveries.set(item.deliveryId, []);
                    deliveries.get(item.deliveryId).push(item);
                }
                for (const [deliveryId, items] of deliveries) {
                    request.client.ajax({
                        type: 'POST', url: request.client.getUrl(API_URL + '/Impressions'),
                        contentType: 'application/json',
                        data: JSON.stringify({ DeliveryId: deliveryId, Items: items.map(function (item) { return { TmdbId: item.id, MediaType: item.type }; }) })
                    }).then(function (result) {
                        if (result?.Success === false || !isCurrentDiscoveryRequest(request)) return;
                        for (var item of items) {
                            if (item.card._jfhDeliveryId === deliveryId) _reportedImpressions.set(item.card, deliveryId);
                        }
                    }, function (error) {
                        if (error?.status === 400 && isCurrentDiscoveryRequest(request)) refreshDiscoveryDelivery(container, request);
                        // Failed feedback writes do not claim an impression; a later render can retry.
                    });
                }
            }, 0);
        }, { threshold: 0.5 });
        container._jfhImpressionObserver = observer;
        for (var card of container.querySelectorAll('.jfh-discovery-card')) observer.observe(card);
    }

    function resumeDiscoveryImpressions() {
        if (_discoveryDisabled || _hostSessionChanges !== 0 || !currentDiscoveryUserId() || !isOnHomePage()) return;
        var owners = new Set();
        for (const grid of document.querySelectorAll('.jfh-discovery-grid')) {
            const owner = grid.closest('.jfh-ui-surface');
            if (owner) owners.add(owner);
        }
        for (const owner of owners) observeVisibleDiscoveryCards(owner);
    }

    function discoveryImpressionItem(card) {
        // Status checks never gate visibility feedback; recommendations carry their own delivery evidence.
        // Read identity from the card, not from optional action buttons.
        var id = Number.parseInt(card.dataset.tmdb, 10);
        var type = card.dataset.type;
        if (!Number.isInteger(id) || id <= 0 || (type !== 'movie' && type !== 'tv') || card._jfhConsuming
            || card.querySelector('.jfh-discovery-btn-done, [data-jfh-action="request"]:disabled')) return null;
        return { TmdbId: id, MediaType: type };
    }

    function renderEmptySidebarCards(container) {
        if (Array.from(container.querySelectorAll('.jfh-discovery-card')).some(function (card) {
            return card._jfhConsuming || window.JellyfinHelperUi.ownsDialogTrigger(card);
        })) return;
        var ownedFocus = container.contains(document.activeElement);
        container.innerHTML = '<div class="jfh-discovery-container"><div class="jfh-discovery-msg"><p>' + esc(t('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.')) + '</p></div></div>';
        if (ownedFocus) {
            var message = container.querySelector('.jfh-discovery-msg');
            message.tabIndex = -1;
            message.focus({ preventScroll: true });
        }
    }

    function createSidebarCard(markup, key) {
        var template = document.createElement('template');
        template.innerHTML = markup;
        var card = template.content.firstElementChild;
        card.dataset.cardKey = key;
        card._jfhMarkup = markup;
        return card;
    }

    function updateSidebarCard(card, markup, deliveryId, expiresAt) {
        if (card._jfhConsuming || window.JellyfinHelperUi.ownsDialogTrigger(card)
            || card.querySelector('button:disabled:not(.jfh-discovery-btn-done), .jfh-discovery-btn-failed')) return;
        card._jfhDeliveryId = deliveryId;
        if (card._jfhInvalidDeliveryId !== deliveryId) card._jfhInvalidDeliveryId = null;
        card._jfhDeliveryExpiresAt = expiresAt ? Date.parse(expiresAt) : 0;
        if (card._jfhMarkup === markup) return;
        var focused = card.contains(document.activeElement) ? document.activeElement.dataset.jfhAction : null;
        var flipped = card.querySelector('.flipped') != null;
        card.innerHTML = createSidebarCard(markup, card.dataset.cardKey).innerHTML;
        card._jfhMarkup = markup;
        if (flipped) toggleSidebarDetails(card.querySelector('.jfh-discovery-card-poster'), true);
        if (focused) card.querySelector('[data-jfh-action="' + focused + '"]')?.focus({ preventScroll: true });
    }

    function deliveryExpired(card) {
        return !!(card?._jfhInvalidDeliveryId && card._jfhInvalidDeliveryId === card._jfhDeliveryId)
            || (card?._jfhDeliveryExpiresAt > 0 && card._jfhDeliveryExpiresAt <= Date.now());
    }

    // Refresh the visible server response, never re-sign stale client features under a new delivery id.
    function refreshDiscoveryDelivery(container, request, refill) {
        if (!container.isConnected || !isCurrentDiscoveryRequest(request) || _discoveryDisabled
            || (!refill && document.getElementById('jfhDiscoveryPopup'))) return;
        if (container._jfhDeliveryRefreshInFlight) {
            if (refill) container._jfhPendingRefill = { request: request, source: container._jfhDiscoverySource };
            return;
        }
        container._jfhDeliveryRefreshInFlight = true;
        var source = container._jfhDiscoverySource;
        var url = API_URL;
        if (source === TAB_TRAKT) {
            url = API_URL + '/Trakt';
        } else if (source === TAB_TRENDING) {
            url = API_URL + '/Trakt/Trending';
        }
        request.client.ajax({ type: 'GET', url: request.client.getUrl(url), dataType: 'json' }).then(function (response) {
            if (!container.isConnected || !isCurrentDiscoveryRequest(request) || _discoveryDisabled || container._jfhDiscoverySource !== source) return;
            if (source === TAB_TRAKT && response?.Linked !== true) { renderTraktNotLinked(container); return; }
            var data = source === TAB_TRAKT ? response.Result : response;
            if (source === TAB_TRAKT) _traktPersonalCache = makeTraktCacheEntry(data, request.userId);
            else if (source === TAB_TRENDING) _traktTrendingCache = makeTraktCacheEntry(data, request.userId);
            else setCachedDiscoveryResult(data);
            renderCards(container, data);
        }).catch(function (error) {
            if (isCurrentDiscoveryRequest(request) && error?.status === 403 && source !== TAB_TRAKT && source !== TAB_TRENDING) disableDiscoveryUi();
            else if (refill && isCurrentDiscoveryRequest(request)) checkEmptyDiscoveryState(container.querySelector('.jfh-discovery-container'));
        }).finally(function () {
            container._jfhDeliveryRefreshInFlight = false;
            var pending = container._jfhPendingRefill;
            container._jfhPendingRefill = null;
            if (pending && isSameDiscoverySession(pending.request) && container._jfhDiscoverySource === pending.source) {
                refreshDiscoveryDelivery(container, captureDiscoveryRequest(), true);
            }
        });
    }

    function placeSidebarCard(grid, card, reference, preserveOrder) {
        if (preserveOrder) {
            if (card.parentElement !== grid) grid.appendChild(card);
        } else if (card !== reference) {
            if (reference) reference.before(card);
            else grid.appendChild(card);
        }
    }

    function discoveryCardKey(tmdbId, mediaType) {
        return String(mediaType || '').trim().toLowerCase() + '-' + (Number.parseInt(tmdbId, 10) || 0);
    }

    function consumeDiscoveryItem(tmdbId, mediaType) {
        var key = discoveryCardKey(tmdbId, mediaType);
        _consumedDiscoveryItems.add(key);
        function filtered(data) {
            return data ? { ...data, Recommendations: (data.Recommendations || []).filter(function (item) {
                return discoveryCardKey(item.TmdbId, item.MediaType) !== key;
            }) } : data;
        }
        var own = _discoveryResultCache;
        var personal = _traktPersonalCache;
        var trending = _traktTrendingCache;
        var probe = _traktProbeResult;
        invalidateDiscoveryResult();
        if (own) _discoveryResultCache = { ...own, data: filtered(own.data) };
        if (personal) _traktPersonalCache = { ...personal, data: filtered(personal.data) };
        if (trending) _traktTrendingCache = { ...trending, data: filtered(trending.data) };
        if (probe) _traktProbeResult = {
            ...probe,
            generation: _discoveryGeneration,
            resp: { ...probe.resp, Result: filtered(probe.resp.Result) }
        };
        // Mutations invalidate the captured generation. Fresh observers receive an initial
        // visibility check from the browser for remaining cards without weakening session guards.
        resumeDiscoveryImpressions();
    }

    function renderSidebarExternalLinksHtml(r, mediaType) {
        var tmdbPath = mediaType === 'tv' ? 'tv' : 'movie';
        var tmdbId = Number.parseInt(r.TmdbId, 10) || 0;
        var tmdbExtUrl = 'https://www.themoviedb.org/' + tmdbPath + '/' + tmdbId;
        var html = '<div class="jfh-discovery-flip-links">' +
            '<a class="jfh-discovery-flip-link" href="' + esc(tmdbExtUrl) + '" data-href="' + esc(tmdbExtUrl) + '" target="_blank" rel="noopener noreferrer">' +
            '<span class="material-icons" aria-hidden="true" style="font-size:0.95em;">open_in_new</span> TMDB</a>';
        if (_seerrBaseUrl) {
            var seerrExtUrl = _seerrBaseUrl + '/' + tmdbPath + '/' + tmdbId;
            html += '<a class="jfh-discovery-flip-link" href="' + esc(seerrExtUrl) + '" data-href="' + esc(seerrExtUrl) + '" target="_blank" rel="noopener noreferrer">' +
                '<span class="material-icons" aria-hidden="true" style="font-size:0.95em;">open_in_new</span> Seerr</a>';
        }
        // Use only canonical, validated API slugs; never guess one from a title.
        if (_traktEnabled && typeof r.TraktSlug === 'string' && /^[a-z0-9-]{1,100}$/.test(r.TraktSlug)) {
            var traktPath = mediaType === 'tv' ? 'shows' : 'movies';
            var traktExtUrl = 'https://trakt.tv/' + traktPath + '/' + r.TraktSlug;
            html += '<a class="jfh-discovery-flip-link" href="' + esc(traktExtUrl) + '" data-href="' + esc(traktExtUrl) + '" target="_blank" rel="noopener noreferrer">' +
                '<span class="material-icons" aria-hidden="true" style="font-size:0.95em;">open_in_new</span> Trakt</a>';
        }
        return html + '</div>';
    }

    function renderSidebarPosterHtml(r, mediaType) {
        var posterUrl = 'https://image.tmdb.org/t/p/w500' + r.PosterPath;
        var front = r.PosterPath ? '<img src="' + esc(posterUrl) + '" alt="' + esc(r.Title || '') + '" loading="lazy">'
            : '<div class="jfh-discovery-no-poster"><span aria-hidden="true" style="opacity:0.3;font-size:2em;">\uD83C\uDFAC</span></div>';
        return '<div class="jfh-discovery-card-poster">' +
            '<button type="button" class="jfh-discovery-poster-toggle" data-jfh-action="poster" aria-label="' + esc(r.Title || t('recsUnknownTitle', 'Unknown')) + '" aria-expanded="false" aria-controls="jfh-details-' + esc(discoveryCardKey(r.TmdbId, mediaType)) + '"></button>' +
            '<div class="jfh-discovery-flip-inner">' +
            '<div class="jfh-discovery-flip-front">' + front + '</div>' +
            '<div class="jfh-discovery-flip-back" id="jfh-details-' + esc(discoveryCardKey(r.TmdbId, mediaType)) + '" aria-hidden="true" inert><h3 style="font-size:1em;overflow-wrap:anywhere;">' + esc(r.Title || '') + '</h3>' + renderSidebarExternalLinksHtml(r, mediaType) + '<div class="jfh-discovery-flip-back-text">' + esc(r.Overview || t('discoveryNoDescription', 'No description available.')) + '</div></div>' +
            '</div></div>';
    }

    function renderSidebarMetadataHtml(r, mediaType) {
        var year = r.Year ? '<span class="jfh-discovery-tag">' + esc(String(r.Year)) + '</span>' : '';
        var mediaLabel = mediaType === 'movie' ? t('movies', 'Movie') : t('tvShows', 'TV');
        var type = r.MediaType ? '<span class="jfh-discovery-tag">' + esc(mediaLabel) + '</span>' : '';
        var ratingNum = Number(r.TmdbRating);
        var rating = (!Number.isNaN(ratingNum) && ratingNum > 0) ? '<span class="jfh-discovery-tag">\u2B50 ' + ratingNum.toFixed(1) + '</span>' : '';
        var genres = (r.Genres && r.Genres.length > 0) ? r.Genres.slice(0, 2).map(function(g) { return '<span class="jfh-discovery-tag">' + esc(g) + '</span>'; }).join('') : '';
        var genresHtml = genres ? '<div class="jfh-discovery-card-genres">' + genres + '</div>' : '';
        return '<div class="jfh-discovery-card-meta">' + year + type + rating + '</div>' + genresHtml;
    }

    function renderSidebarScoreHtml(r) {
        if (r.IsPersonalized === false) {
            return '<div class="jfh-discovery-score-text">' + esc(t('discoveryColdStart', 'Not personalized')) + '</div>';
        }
        var scorePercent = Math.max(0, Math.min(100, Math.round((Number(r.Score) || 0) * 100)));
        var scoreClass = getDiscoveryScoreClass(scorePercent);
        // Trakt's ranking is independent of our local match percentage.
        var isTraktTab = _activeTab === TAB_TRAKT || _activeTab === TAB_TRENDING;
        var localScoreTag = isTraktTab ? ' <span class="jfh-discovery-local-score">' + esc(t('discoveryLocalScore', 'Local score')) + '</span>' : '';
        return '<div class="jfh-discovery-score ' + scoreClass + '"><div class="jfh-discovery-score-bar" style="width:' + scorePercent + '%"></div></div><div class="jfh-discovery-score-text">' + scorePercent + '% ' + t('recsMatch', 'match') + localScoreTag + '</div>';
    }

    function renderSidebarCardHtml(r) {
        var mediaType = (r.MediaType || '').trim().toLowerCase();
        var reasonText = formatReason(r.ReasonKey, r.Reason, r.RelatedInfo);
        // Slash breaks are inserted only after escaping the reason.
        var reason = reasonText ? '<div class="jfh-discovery-reason">' + insertSlashBreaks(esc(reasonText)) + '</div>' : '';
        return '<div class="jfh-discovery-card" data-tmdb="' + (Number.parseInt(r.TmdbId, 10) || 0) + '" data-type="' + esc(mediaType) + '">' + renderSidebarPosterHtml(r, mediaType) +
            '<div class="jfh-discovery-card-body">' +
            '<div class="jfh-discovery-card-title" title="' + esc(r.Title || '') + '">' + esc(r.Title || t('recsUnknownTitle', 'Unknown')) + '</div>' +
            renderSidebarMetadataHtml(r, mediaType) + renderSidebarScoreHtml(r) + reason +
            '<div class="jfh-discovery-btn-row">' + renderSidebarActionsHtml(r, mediaType) + '</div></div></div>';
    }

    function renderSidebarActionsHtml(r, mediaType) {
        var request;
        if (mediaType === 'movie' || mediaType === 'tv') {
            var text = r.AlreadyRequested ? '\u2713 ' + t('discoveryRequested', 'Requested') : t('discoveryRequest', 'Request');
            var cls = r.AlreadyRequested ? 'jfh-discovery-btn jfh-discovery-btn-done' : 'jfh-discovery-btn';
            request = '<button type="button" class="' + cls + '" data-jfh-action="request" data-tmdb="' + (Number.parseInt(r.TmdbId, 10) || 0) + '" data-type="' + esc(mediaType) + '"' + (r.AlreadyRequested ? ' disabled' : '') + '>' + esc(text) + '</button>';
        } else {
            request = '';
        }
        var dismiss = r.AlreadyRequested ? '' : '<button type="button" class="jfh-discovery-btn jfh-discovery-btn-dismiss" data-jfh-action="dismiss" data-tmdb="' + (Number.parseInt(r.TmdbId, 10) || 0) + '" data-type="' + esc(mediaType) + '" data-title="' + esc(r.Title || '') + '">' + esc(t('discoveryDismiss', 'Not interested')) + '</button>';
        return request + dismiss;
    }

    function bindSidebarCardHandlers(container) {
        var buttons = container.querySelectorAll('.jfh-discovery-btn:not([disabled]):not(.jfh-discovery-btn-dismiss)');
        for (const button of buttons) {
            if (button.dataset.jfhRequestBound) continue;
            button.dataset.jfhRequestBound = '1';
            button.addEventListener('click', handleRequest);
        }
        // Attach dismiss button handlers
        var dismissBtns = container.querySelectorAll('.jfh-discovery-btn-dismiss');
        for (const button of dismissBtns) {
            if (button.dataset.jfhDismissBound) continue;
            button.dataset.jfhDismissBound = '1';
            button.addEventListener('click', handleDismissClick);
        }
        bindSidebarPosterHandlers(container);
        // Attach external link handlers on the flip back side.
        // Opens URLs in a new tab. stopPropagation prevents the poster flip from triggering.
        var flipLinks = container.querySelectorAll('.jfh-discovery-flip-link[data-href]');
        for (const link of flipLinks) {
            if (link.dataset.jfhLinkBound) continue;
            link.dataset.jfhLinkBound = '1';
            link.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var url = this.dataset.href;
                // Re-validate the scheme at the sink: even though data-href is HTML-escaped and
                // the source is filtered, never hand a non-http(s) URL to window.open().
                var safeUrl = safeHttpUrl(url);
                if (safeUrl) window.open(safeUrl, '_blank', 'noopener,noreferrer');
            });
        }
        bindSidebarImageHandlers(container);
    }

    function bindSidebarPosterHandlers(container) {
        var posters = container.querySelectorAll('.jfh-discovery-card-poster .jfh-discovery-flip-inner');
        for (const poster of posters) {
            if (poster.dataset.jfhFlipBound) continue;
            poster.dataset.jfhFlipBound = '1';
            poster.parentElement.addEventListener('click', function () {
                toggleSidebarDetails(this, !this.classList.contains('flipped'));
            });
            poster.parentElement.addEventListener('keydown', function (event) {
                if (event.key !== 'Escape' || !this.classList.contains('flipped')) return;
                event.preventDefault();
                event.stopPropagation();
                toggleSidebarDetails(this, false);
            });
        }
    }

    function bindSidebarImageHandlers(container) {
        for (var image of container.querySelectorAll('.jfh-discovery-flip-front img')) {
            if (image.dataset.jfhImageBound) continue;
            image.dataset.jfhImageBound = '1';
            image.addEventListener('error', function () {
                var placeholder = document.createElement('div');
                placeholder.className = 'jfh-discovery-no-poster';
                placeholder.setAttribute('aria-hidden', 'true');
                placeholder.textContent = '\uD83C\uDFAC';
                this.replaceWith(placeholder);
            });
        }
    }

    function toggleSidebarDetails(poster, expanded) {
        if (!poster) return;
        var button = poster.querySelector('.jfh-discovery-poster-toggle');
        var back = poster.querySelector('.jfh-discovery-flip-back');
        if (!expanded && back?.contains(document.activeElement)) button?.focus({ preventScroll: true });
        poster.classList.toggle('flipped', expanded);
        poster.querySelector('.jfh-discovery-flip-front')?.setAttribute('aria-hidden', String(expanded));
        if (back) {
            back.toggleAttribute('inert', !expanded);
            back.setAttribute('aria-hidden', String(!expanded));
        }
        button?.setAttribute('aria-expanded', String(expanded));
    }

    var _permCache = {};
    var PERM_CACHE_TTL_MS = 300000; // 5 minutes

    function handleRequest(e) {
        var btn = e.currentTarget;
        if (btn.disabled) return;
        if (refreshExpiredActionCard(btn)) return;
        var tmdbId = Number.parseInt(btn.dataset.tmdb, 10);
        var mediaType = btn.dataset.type;
        if (!tmdbId || !mediaType) return;
        // Collapse the sibling "Not interested" button for the duration of the request so the busy
        // "Requesting…" label gets the full row width (matching the final "Requested" state) instead of
        // being squeezed into half the row where its text would clip.
        setDismissSiblingHidden(btn, true);
        fetchPermissionsAndRequest(tmdbId, mediaType, btn);
    }

    // Shows/hides the dismiss button that shares a button row with the given request button.
    function setDismissSiblingHidden(requestBtn, hidden) {
        var row = requestBtn.parentElement;
        if (!row) return;
        var dismiss = row.querySelector('.jfh-discovery-btn-dismiss');
        if (dismiss) dismiss.style.display = hidden ? 'none' : '';
    }

    function fetchPermissionsAndRequest(tmdbId, mediaType, btn) {
        var request = captureDiscoveryRequest();
        // Clear any pending reset timer from a previous denial/error to prevent
        // stale callbacks from overwriting the new request's button state.
        if (btn._resetTimer) {
            clearTimeout(btn._resetTimer);
            btn._resetTimer = null;
        }
        btn.classList.remove('jfh-discovery-btn-failed');
        var serviceType = (mediaType === 'tv') ? 'sonarr' : 'radarr';
        var userId = currentDiscoveryUserId();
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
            if (!isCurrentDiscoveryRequest(request)) return;
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
            if (!isCurrentDiscoveryRequest(request)) return;
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
                setDismissSiblingHidden(btn, false);
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
        var request = captureDiscoveryRequest();
        var existing = document.getElementById('jfhDiscoveryPopup');
        if (existing) {
            if (existing._onEsc) {
                document.removeEventListener('keydown', existing._onEsc);
            }
            window.JellyfinHelperUi.closeDialog(existing, false);
        }
        injectPopupStyles();

        var serverIds = {};
        for (const profile of profiles) { serverIds[profile.ServerId] = true; }
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
        for (const prof of profiles) {
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
                return function () {
                    if (!isCurrentDiscoveryPopup(overlay, request)) return;
                    closeDiscoveryPopup(btn, false);
                    submitRequest(tmdbId, mediaType, sid, pid, rf, btn);
                };
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
            '.jfh-discovery-popup{background:var(--color-surface-card);color:var(--color-text);box-sizing:border-box;border-radius:12px;padding:1.5em;max-width:400px;width:calc(100% - 2rem);max-height:80vh;max-height:80dvh;overflow-y:auto;overscroll-behavior:contain;box-shadow:0 8px 32px rgba(0,0,0,.5)}' +
            '.jfh-discovery-popup-title{font-size:1.1em;font-weight:600;margin-bottom:.3em;color:inherit}' +
            '.jfh-discovery-popup-subtitle{font-size:.85em;margin-bottom:1em;color:var(--color-text-muted)}' +
            '.jfh-discovery-popup-list{display:flex;flex-direction:column;gap:.5em}' +
            '.jfh-discovery-popup-item{display:flex;flex-wrap:wrap;align-items:center;gap:.6em;padding:.7em 1em;border:1px solid var(--color-border);border-radius:8px;background:transparent;cursor:pointer;color:inherit;font-size:.9em;transition:background .2s,border-color .2s;text-align:left;width:100%;overflow-wrap:anywhere}' +
            '@media(hover:hover) and (pointer:fine){.jfh-discovery-popup-item:hover{background:rgba(0,164,220,.15);border-color:var(--color-primary)}}' +
            '.jfh-discovery-popup-item-default{border-color:rgba(0,164,220,.4);background:rgba(0,164,220,.08)}' +
            '.jfh-discovery-popup-cancel{display:block;width:100%;margin-top:1em;padding:.6em;border:1px solid var(--color-border);border-radius:6px;background:transparent;color:inherit;cursor:pointer;font-size:.85em;text-align:center;transition:background .2s}' +
            '@media(hover:hover) and (pointer:fine){.jfh-discovery-popup-cancel:hover{background:rgba(0,164,220,.1)}}';
        document.head.appendChild(s);
    }

    // Closes the quality-profile picker. When cancelled (cancel button, Escape, or overlay click) the
    // sibling "Not interested" button is restored, since handleRequest hid it in anticipation of a request
    // that is no longer happening. On profile selection cancelled is false: submitRequest re-hides it, so
    // restoring here would only flicker.
    function closeDiscoveryPopup(triggerBtn, cancelled) {
        var el = document.getElementById('jfhDiscoveryPopup');
        if (el?._onEsc) {
            document.removeEventListener('keydown', el._onEsc);
        }
        if (el) window.JellyfinHelperUi.closeDialog(el);
        if (cancelled !== false && triggerBtn) setDismissSiblingHidden(triggerBtn, false);
        if (triggerBtn?.focus) triggerBtn.focus();
        resumeDiscoveryImpressions();
    }

    function isCurrentDiscoveryPopup(overlay, request) {
        // A queued dialog event must not act on a replacement session or another dialog.
        return overlay.isConnected && document.getElementById('jfhDiscoveryPopup') === overlay && isSameDiscoverySession(request);
    }

    function finalizeDiscoveryPopup(overlay, popup, list, btn) {
        var request = captureDiscoveryRequest();
        function closeCurrentPopup() {
            if (isCurrentDiscoveryPopup(overlay, request)) closeDiscoveryPopup(btn);
        }
        popup.appendChild(list);
        var cancelBtn = document.createElement('button');
        cancelBtn.className = 'jfh-discovery-popup-cancel';
        cancelBtn.textContent = t('discoveryCancel', 'Cancel');
        cancelBtn.addEventListener('click', closeCurrentPopup);
        popup.appendChild(cancelBtn);
        overlay.appendChild(popup);
        window.JellyfinHelperUi.openDialog(overlay, popup, btn, closeCurrentPopup);
        cancelBtn.focus();
        overlay.addEventListener('click', function (ev) { if (ev.target === overlay) closeCurrentPopup(); });
        function onEsc(ev) { if (ev.key === 'Escape') closeCurrentPopup(); }
        document.addEventListener('keydown', onEsc);
        overlay._onEsc = onEsc;
    }

    function submitRequest(tmdbId, mediaType, serverId, profileId, rootFolder, btn) {
        var request = captureDiscoveryRequest();
        btn.disabled = true;
        btn.textContent = t('discoveryRequesting', 'Requesting...');
        var payload = { TmdbId: tmdbId, MediaType: mediaType };
        var deliveryId = btn.closest('.jfh-discovery-card')?._jfhDeliveryId;
        if (deliveryId) payload.DeliveryId = deliveryId;
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
            if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
            if (result?.Success) {
                if (result.AlreadyRequested === true) showToast(t('discoveryAlreadyRequested', 'Title has already been requested'));
                consumeDiscoveryItem(tmdbId, mediaType);
                btn.textContent = '\u2713 ' + t('discoveryRequested', 'Requested');
                btn.classList.add('jfh-discovery-btn-done');
                // Hide dismiss button in the same row
                var row = btn.closest('.jfh-discovery-btn-row');
                var dismissBtn = row ? row.querySelector('.jfh-discovery-btn-dismiss') : null;
                if (dismissBtn) dismissBtn.style.display = 'none';
                // After 5 seconds: fade out and remove the card (consumed from pool)
                var card = btn.closest('.jfh-discovery-card');
                if (card) {
                    card._jfhConsuming = true;
                    var scopeEl = card.closest('.jfh-discovery-container');
                    var deliveryContainer = card.closest('.jfh-ui-surface');
                    var deliverySource = deliveryContainer?._jfhDiscoverySource;
                    setTimeout(function () {
                        if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
                        window.JellyfinHelperUi.removeCard(card, function () {
                            if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
                            if (deliveryContainer && deliveryContainer._jfhDiscoverySource === deliverySource) {
                                refreshDiscoveryDelivery(deliveryContainer, captureDiscoveryRequest(), true);
                            } else checkEmptyDiscoveryState(scopeEl);
                        });
                    }, 5000);
                }
            } else {
                btn.textContent = t('discoveryRequestFailed', 'Failed');
                btn.classList.add('jfh-discovery-btn-failed');
                showToast(getUserFriendlyErrorMessage(result?.Message));
                setTimeout(function () { btn.textContent = t('discoveryRequest', 'Request'); btn.classList.remove('jfh-discovery-btn-failed'); btn.disabled = false; setDismissSiblingHidden(btn, false); }, 3000);
            }
        }).catch(function (err) {
            if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
            if (recoverActionDelivery(btn, err, request)) return;
            btn.textContent = t('discoveryRequestFailed', 'Failed');
            btn.classList.add('jfh-discovery-btn-failed');
            var serverMessage = extractErrorMessage(err);
            showToast(getUserFriendlyErrorMessage(serverMessage));
            setTimeout(function () { btn.textContent = t('discoveryRequest', 'Request'); btn.classList.remove('jfh-discovery-btn-failed'); btn.disabled = false; setDismissSiblingHidden(btn, false); }, 3000);
        });
    }

    /** * Checks if all discovery cards have been removed (requested/dismissed) and shows * the empty-state message so the user doesn't see a blank page. */
    function checkEmptyDiscoveryState(scopeContainer) {
        var grid = scopeContainer ? scopeContainer.querySelector('.jfh-discovery-grid') : null;
        if (!grid) return;
        if (grid.querySelectorAll('.jfh-discovery-card').length === 0) {
            var host = grid.closest('.jfh-discovery-container');
            if (host) {
                var ownedFocus = host.contains(document.activeElement);
                host.innerHTML = '<div class="jfh-discovery-msg"><p>' + esc(t('discoveryNoResults', 'No suggestions available yet. Results will appear after the next scheduled task run.')) + '</p></div>';
                if (ownedFocus) {
                    var message = host.querySelector('.jfh-discovery-msg');
                    message.tabIndex = -1;
                    message.focus({ preventScroll: true });
                }
            }
        }
    }


    /**
     * Handles the dismiss button click. Shows a confirmation popup before dismissing.
     */
    function handleDismissClick(e) {
        var btn = e.currentTarget;
        if (btn.disabled) return;
        if (refreshExpiredActionCard(btn)) return;
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
        var request = captureDiscoveryRequest();
        var existing = document.getElementById('jfhDiscoveryPopup');
        if (existing) {
            if (existing._onEsc) document.removeEventListener('keydown', existing._onEsc);
            window.JellyfinHelperUi.closeDialog(existing, false);
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
            if (!isCurrentDiscoveryPopup(overlay, request)) return;
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
        var request = captureDiscoveryRequest();
        var payload = { TmdbId: tmdbId, MediaType: mediaType };
        var deliveryId = btn.closest('.jfh-discovery-card')?._jfhDeliveryId;
        if (deliveryId) payload.DeliveryId = deliveryId;
        btn.disabled = true;
        btn.textContent = '...';
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(API_URL + '/Dismiss'),
            data: JSON.stringify(payload),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function () {
            if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
            consumeDiscoveryItem(tmdbId, mediaType);
            // Remove the card with a fade-out animation
            var card = btn.closest('.jfh-discovery-card');
            if (card) {
                card._jfhConsuming = true;
                var scopeEl = card.closest('.jfh-discovery-container');
                var deliveryContainer = card.closest('.jfh-ui-surface');
                var deliverySource = deliveryContainer?._jfhDiscoverySource;
                window.JellyfinHelperUi.removeCard(card, function () {
                    if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
                    if (deliveryContainer && deliveryContainer._jfhDiscoverySource === deliverySource) {
                        refreshDiscoveryDelivery(deliveryContainer, captureDiscoveryRequest(), true);
                    } else checkEmptyDiscoveryState(scopeEl);
                });
            }
        }).catch(function (error) {
            if (!isSameDiscoverySession(request) || _discoveryDisabled) return;
            if (recoverActionDelivery(btn, error, request)) return;
            btn.disabled = false;
            btn.textContent = t('discoveryDismiss', 'Not interested');
            showToast(t('discoveryDismissError', 'Could not dismiss this item. Please try again.'));
        });
    }

    function refreshExpiredActionCard(button) {
        var card = button.closest('.jfh-discovery-card');
        if (!deliveryExpired(card)) return false;
        var container = card.closest('.jfh-ui-surface');
        if (container) refreshDiscoveryDelivery(container, captureDiscoveryRequest());
        return true;
    }

    // An acknowledged card can lose its delivery before its timestamp expires (another tab may evict it).
    // Refresh evidence after that specific rejection, but never automatically repeat a media request or dismissal.
    function recoverActionDelivery(button, error, request) {
        if (error?.status !== 400 || extractErrorMessage(error) !== 'No matching recent discovery delivery for this action.'
            || !isSameDiscoverySession(request) || _discoveryDisabled) return false;
        var card = button.closest('.jfh-discovery-card');
        var container = card?.closest('.jfh-ui-surface');
        if (!container?.isConnected || !card._jfhDeliveryId) return false;
        card._jfhInvalidDeliveryId = card._jfhDeliveryId;
        button.disabled = false;
        button.classList.remove('jfh-discovery-btn-failed');
        var requesting = button.dataset.jfhAction === 'request';
        button.textContent = requesting ? t('discoveryRequest', 'Request') : t('discoveryDismiss', 'Not interested');
        if (requesting) setDismissSiblingHidden(button, false);
        // An unrelated successful action may have advanced the generation while this rejected request was away.
        refreshDiscoveryDelivery(container, captureDiscoveryRequest());
        return true;
    }

    // Adds a zero-width space (U+200B) after each slash so the browser may break a slashed compound like
    // "actors/directors" after the slash instead of treating it as one unbreakable token. Input is already
    // HTML-escaped; U+200B is not an HTML metacharacter, so appending it introduces no injection risk.
    var ZERO_WIDTH_SPACE = String.fromCodePoint(0x200B);
    function insertSlashBreaks(escapedText) {
        return escapedText.replaceAll('/', '/' + ZERO_WIDTH_SPACE);
    }

    // Reason keys whose localized text embeds a {0} anchor (person name, genre). Fallbacks match en.json.
    var REASON_TEMPLATES = {
        reasonPersonNamed: 'Featuring {0}',
        reasonGenre: 'Because you enjoy {0}',
        reasonFranchiseNamed: 'More from the {0} collection',
        reasonStudioNamed: 'From {0}'
    };
    // Reason keys that render as a plain localized phrase with no anchor. Fallbacks match en.json so a
    // discovery card still shows a reason when the /Translations fetch failed and _strings is empty.
    var REASON_PHRASES = {
        reasonTrending: 'Trending - new and highly rated',
        reasonPopular: 'Popular and highly rated',
        reasonHighlyRated: 'Highly rated',
        reasonPopularAmongViewers: 'Popular with other viewers',
        reasonFranchise: 'From a franchise you follow',
        reasonCollectionContinuation: 'Because you like this collection',
        reasonDefault: 'Recommended for you'
    };

    // Translate reason key to localized human-readable text.
    // Backend DetermineReason produces: reasonPersonNamed, reasonGenre, reasonTrending, reasonPopular
    function formatReason(reasonKey, reason, relatedInfo) {
        if (!reasonKey && !reason) return '';
        var key = reasonKey || '';
        if (relatedInfo && REASON_TEMPLATES[key]) {
            return t(key, REASON_TEMPLATES[key]).replace('{0}', relatedInfo);
        }
        if (REASON_PHRASES[key]) {
            return t(key, REASON_PHRASES[key]);
        }
        // Any other known i18n key: substitute the anchor when present.
        if (key && _strings?.[key]) {
            var val = _strings[key];
            return relatedInfo ? val.replace('{0}', relatedInfo) : val;
        }
        return formatRawReasonFallback(reason);
    }

    // Fallback for a reason that arrived as raw "reasonKey: anchor" text instead of a resolved key.
    // Re-dispatches through formatReason so it localizes; a bare "reason*" token with no anchor is hidden.
    function formatRawReasonFallback(reason) {
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

    function getDiscoveryNavigationContainer() {
        var lists = document.querySelectorAll('.MuiDrawer-paper ul.MuiList-root');
        for (var list of lists) {
            if (list.querySelector('a[href="#/home"], a[href="#/"], a[href^="#/home?"]')) return list;
        }
        return document.querySelector('.mainDrawer-scrollContainer');
    }

    function removeDiscoveryNavigation() {
        document.querySelectorAll('.' + NAV_ITEM_CLASS).forEach(function (item) { item.remove(); });
        document.querySelectorAll('.' + SECTION_CLASS).forEach(function (section) {
            if (!section.querySelector('a, button')) section.remove();
        });
    }

    function injectNavigation() {
        if (_hostSessionChanges > 0 || _discoveryDisabled || isLoginPage() || !currentDiscoveryUserId()) return;
        var sidebar = getDiscoveryNavigationContainer();
        if (!sidebar || sidebar.querySelector('.' + NAV_ITEM_CLASS)) return;
        removeDiscoveryNavigation();
        var modern = sidebar.matches('ul.MuiList-root');
        var section = sidebar.querySelector('.' + SECTION_CLASS);
        if (!section) {
            section = document.createElement(modern ? 'li' : 'div');
            section.className = SECTION_CLASS;
            section.innerHTML = modern
                ? '<div class="MuiListSubheader-root jfh-discovery-sidebar-heading">Jellyfin Helper</div>'
                : '<h3 class="sidebarHeader jfh-discovery-sidebar-heading">Jellyfin Helper</h3>';
            var mediaSection = sidebar.querySelector('.libraryMenuOptions');
            if (mediaSection) {
                mediaSection.before(section);
            } else {
                sidebar.appendChild(section);
            }
        }
        var navItem = document.createElement('a');
        if (!modern) navItem.setAttribute('is', 'emby-linkbutton');
        navItem.className = (modern ? 'MuiListItemButton-root jfh-discovery-modern-link ' : 'navMenuOption lnkMediaFolder emby-button ') + NAV_ITEM_CLASS;
        navItem.href = '#';
        var label = t('discoveryNavigationTitle', 'Discover');
        navItem.title = label;
        navItem.setAttribute('aria-label', label);
        navItem.innerHTML =
            '<span class="material-icons ' + (modern ? 'jfh-discovery-navigation-icon' : 'navMenuOptionIcon') + '" aria-hidden="true">explore</span>' +
            '<span class="' + (modern ? 'jfh-discovery-navigation-text' : 'sectionName navMenuOptionText') + '">' + esc(label) + '</span>';
        navItem.addEventListener('click', function (e) {
            e.preventDefault();
            var request = captureDiscoveryRequest();
            if (focusDiscoveryTab()) {
                return;
            }
            // The tab strip only exists on the Home page, so go there first and
            // let the registry apply the pending focus once it has mounted.
            if (typeof Emby !== 'undefined' && Emby.Page && Emby.Page.show) {
                Emby.Page.show('/home');
                setTimeout(function () {
                    if (!isSameDiscoverySession(request)) return;
                    // Off Home the registry keeps the pending focus and applies it once
                    // the tab strip mounts, so a slow navigation must not raise an error.
                    if (isOnHomePage() && !focusDiscoveryTab()) {
                        notifyTabUnavailable();
                    }
                }, 800);
                return;
            }
            notifyTabUnavailable();
        });
        section.appendChild(navItem);
    }

    // The script is injected into the single index.html Jellyfin serves for every
    // SPA route, so it also loads on admin/dashboard pages where no Discovery tab
    // can appear. Probing /Discovery/My there is wasted traffic and, when the
    // feature is disabled, a 403 the browser logs as a console error. Gate the
    // bootstrap on the home context.
    //
    // Availability does not depend on results because a scheduled task can populate an empty panel later.
    var _bootstrapped = false;
    var _discoveryDisabled = false;
    var _probeInFlight = false;
    // The user the gates above were last decided for. An in-tab account switch must not inherit
    // the previous user's terminal 403 or in-flight probe, so a change resets the bootstrap gate.
    var _probedUserId = null;
    var _sessionApiClient = null;
    var _sessionToken = '';
    var _bootstrapSequence = 0;
    var _bootstrapRequest = null;
    var _lifecycleState = null;
    var _hostSessionChanges = 0;

    function initDiscoveryUiFull() {
        // Render immediately; the Seerr-links config loads in parallel (fired in
        // bootstrapDiscovery) and re-renders once if it arrives late with a URL.
        injectStyles();
        registerDiscoveryTab();
        injectNavigation();
    }

    function handleDiscoveryProbe(data, startedUserId, request) {
        if (startedUserId !== currentDiscoveryUserId() || isLoginPage()
            || (request && !isCurrentDiscoveryRequest(request))) {
            return;
        }
        _bootstrapped = true;
        // Seed the own-grid cache with the probe payload: it is what the first mount
        // would fetch moments later, so the initial render is a cache hit.
        _discoveryResultCache = { data: data || { Recommendations: [] }, userId: startedUserId };
        initDiscoveryUiFull();
    }

    function probeDiscoveryAvailability(startedUserId, request) {
        request = request || captureDiscoveryRequest();
        _bootstrapRequest = request;
        ApiClient.ajax({ type: 'GET', url: ApiClient.getUrl(API_URL), dataType: 'json' })
            .then(function (data) {
                handleDiscoveryProbe(data, startedUserId, request);
            })
            .catch(function (err) {
                // Ignore a stale response once the account has switched - a prior user's 403 must
                // not disable Discovery for whoever is signed in now.
                if (startedUserId !== currentDiscoveryUserId() || !isCurrentDiscoveryRequest(request)) {
                    return;
                }
                // 403 means the admin disabled the feature: terminal, do not re-probe. Any other
                // error (network/transient) leaves the gate open for a later retry.
                if (err?.status === 403) {
                    disableDiscoveryUi();
                }
            })
            .finally(function () {
                // Only the current generation owns the gate; a stale finally must not clear a gate
                // a newer bootstrap set.
                if (_bootstrapRequest === request) {
                    _probeInFlight = false;
                    _bootstrapRequest = null;
                }
            });
    }

    function teardownDiscoveryEntryPoints() {
        _bootstrapSequence++;
        _bootstrapRequest = null;
        _traktProbeSeq++;
        invalidateDiscoveryResult();
        resetTraktState();
        _traktEnabled = null;
        _activeTab = TAB_OWN;
        _backgroundRefreshInFlight = false;
        _seerrBaseUrl = '';
        _strings = null;
        _permCache = {};
        _consumedDiscoveryItems.clear();
        _reportedImpressions = new WeakMap();
        document.querySelectorAll('.jfh-ui-surface').forEach(function (container) {
            container._jfhImpressionObserver?.disconnect();
            if (container._jfhImpressionTimer) clearTimeout(container._jfhImpressionTimer);
        });
        closeDiscoveryPopup(null, false);
        document.querySelectorAll('.jfh-discovery-toast').forEach(function (toast) { window.JellyfinHelperUi.closeToast(toast, true); });
        if (window.JellyfinHelperTabs && typeof window.JellyfinHelperTabs.unregister === 'function') {
            try {
                window.JellyfinHelperTabs.unregister(TAB_ID);
            } catch (_) {
                // The shell can replace its controls during navigation, so the remaining cleanup must still run.
            }
        }
        for (var id of ['jfh-tab-btn-discovery', 'jfh-tab-link-discovery', 'jfh-tab-panel-discovery']) {
            document.querySelectorAll('[id="' + id + '"]').forEach(function (element) { element.remove(); });
        }
        removeDiscoveryNavigation();
        _tabRegistered = false;
        _tabPanel = null;
        _bootstrapped = false;
        _discoveryDisabled = false;
        _probeInFlight = false;
        _probedUserId = null;
        _sessionApiClient = null;
        _sessionToken = '';
    }

    function disableDiscoveryUi() {
        teardownDiscoveryEntryPoints();
        var session = captureDiscoveryRequest();
        _probedUserId = session.userId;
        _sessionApiClient = session.client;
        _sessionToken = session.token;
        _discoveryDisabled = true;
    }

    function bootstrapDiscovery() {
        if (_hostSessionChanges > 0) return;
        var session = captureDiscoveryRequest();
        if (isLoginPage() || !session.userId) {
            if (_probedUserId || _tabRegistered) teardownDiscoveryEntryPoints();
            if (isLoginPage() || !isOnHomePage() || _probeInFlight) return;
            _probeInFlight = true;
            var sequence = _bootstrapSequence;
            waitForApi(function () {
                if (sequence !== _bootstrapSequence) return;
                _probeInFlight = false;
                bootstrapDiscovery();
            }, function () {
                if (sequence === _bootstrapSequence) _probeInFlight = false;
            });
            return;
        }
        if (session.userId !== _probedUserId || session.client !== _sessionApiClient || session.token !== _sessionToken) {
            // A new session must never inherit rendered cards or pending work from the previous session.
            teardownDiscoveryEntryPoints();
            _probedUserId = session.userId;
            _sessionApiClient = session.client;
            _sessionToken = session.token;
        }
        if (_bootstrapped || _discoveryDisabled || _probeInFlight || !isOnHomePage()) return;
        _probeInFlight = true;
        var request = captureDiscoveryRequest();
        _bootstrapRequest = request;
        var seerrBefore = _seerrBaseUrl;
        loadExternalLinksConfig().finally(function () {
            if (isCurrentDiscoveryRequest(request) && seerrBefore !== _seerrBaseUrl
                && _tabPanel && document.contains(_tabPanel)) {
                renderDiscovery(_tabPanel, false);
            }
        });
        loadStrings(function () {
            if (isCurrentDiscoveryRequest(request)) probeDiscoveryAvailability(request.userId, request);
        }, request).finally(function () {
            if (_bootstrapRequest === request && !isCurrentDiscoveryRequest(request)) {
                _probeInFlight = false;
                _bootstrapRequest = null;
            }
        });
    }

    function syncDiscoveryLifecycle() {
        if (_hostSessionChanges > 0) return;
        var session = captureDiscoveryRequest();
        var hash = window.location.hash;
        if ((!session.userId || isLoginPage())
            && document.querySelector('#jfh-tab-btn-discovery, #jfh-tab-link-discovery, #jfh-tab-panel-discovery, .jfhelper-nav-discovery')) {
            // Cached headers can restore old controls after logout, so signed out pages must also sweep orphaned elements.
            teardownDiscoveryEntryPoints();
        }
        if (_lifecycleState?.hash !== hash || _lifecycleState.userId !== session.userId
            || _lifecycleState.client !== session.client || _lifecycleState.token !== session.token) {
            _lifecycleState = { hash: hash, userId: session.userId, client: session.client, token: session.token };
            bootstrapDiscovery();
            window.JellyfinHelperTabs?.refresh?.();
        }
        // The drawer can mount or change its links without changing the session or route.
        if (_bootstrapped) injectNavigation();
    }

    function beforeHostSessionChange() {
        _hostSessionChanges++;
        teardownDiscoveryEntryPoints();
        _lifecycleState = null;
    }

    function afterHostSessionChange() {
        if (_hostSessionChanges > 0) _hostSessionChanges--;
        syncDiscoveryLifecycle();
    }

    function initDiscoveryLifecycle() {
        var pending = false;
        var disposed = false;
        var observer = new MutationObserver(function (records) {
            if (pending || records.every(function (record) {
                return record.target.closest?.('.jfh-discovery-grid, .jfh-discovery-popup-overlay, .jfh-toast-stack');
            })) return;
            pending = true;
            requestAnimationFrame(function () {
                pending = false;
                if (!disposed) syncDiscoveryLifecycle();
            });
        });
        observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'hidden', 'href'] });
        document.addEventListener('jfh:session-changing', beforeHostSessionChange);
        document.addEventListener('jfh:session-changed', afterHostSessionChange);
        document.addEventListener('jfh:navigation', syncDiscoveryLifecycle);
        window.addEventListener('hashchange', syncDiscoveryLifecycle);
        window.addEventListener('popstate', syncDiscoveryLifecycle);
        window.addEventListener('pageshow', syncDiscoveryLifecycle);
        document.addEventListener('viewshow', syncDiscoveryLifecycle, true);
        document.addEventListener('visibilitychange', syncDiscoveryLifecycle);
        syncDiscoveryLifecycle();
        return function () {
            disposed = true;
            observer.disconnect();
            document.removeEventListener('jfh:session-changing', beforeHostSessionChange);
            document.removeEventListener('jfh:session-changed', afterHostSessionChange);
            document.removeEventListener('jfh:navigation', syncDiscoveryLifecycle);
            window.removeEventListener('hashchange', syncDiscoveryLifecycle);
            window.removeEventListener('popstate', syncDiscoveryLifecycle);
            window.removeEventListener('pageshow', syncDiscoveryLifecycle);
            document.removeEventListener('viewshow', syncDiscoveryLifecycle, true);
            document.removeEventListener('visibilitychange', syncDiscoveryLifecycle);
            teardownDiscoveryEntryPoints();
        };
    }

    initDiscoveryLifecycle();
})();
