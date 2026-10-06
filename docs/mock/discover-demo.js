// Demo-only Discover preview tab for the github.io demo page (docs/mock/, never
// shipped in the plugin). Injects its own tab button + content and renders the
// end-user discovery grids with the shared dashboard renderers and mock data.
// Static tab titles are intentional: this page demonstrates, it is not localized UI.
(function () {
    'use strict';

    var TAB_ID = 'discover-demo';
    var POLL_MS = 300;
    var POLL_BUDGET = 100;

    function injectStyles() {
        if (document.getElementById('discoverDemoStyles')) { return; }
        var style = document.createElement('style');
        style.id = 'discoverDemoStyles';
        style.textContent =
            '.jfh-discovery-tabs { display: flex; flex-wrap: nowrap; gap: 0.4em; margin: 0 0 1em 0; overflow-x: auto; overflow-y: hidden; scrollbar-width: thin; }' +
            '.jfh-discovery-tab { flex-shrink: 0; padding: 0.5em 1em; border: none; border-radius: 6px; background: rgba(255,255,255,0.06); color: #ddd; cursor: pointer; font-size: 0.9em; white-space: nowrap; }' +
            '.jfh-discovery-tab:hover { background: rgba(255,255,255,0.12); }' +
            '.jfh-discovery-tab-active { background: #00a4dc; color: #fff; }';
        document.head.appendChild(style);
    }

    function renderGrid(kind) {
        var grid = document.getElementById('demoDiscoverGrid');
        if (!grid) { return; }
        grid.innerHTML = '<div class="loading-overlay" style="padding:0.5em;"><div class="spinner"></div></div>';

        var done = function (userDiscovery) {
            var target = document.getElementById('demoDiscoverGrid');
            if (!target) { return; }
            renderDiscoveryCards(target, null, userDiscovery);
        };
        var failed = function () {
            var target = document.getElementById('demoDiscoverGrid');
            if (!target) { return; }
            renderDiscoveryCards(target, null, null);
        };

        if (kind === 'trakt') {
            apiGet('JellyfinHelper/Discovery/My/Trakt', function (resp) {
                done(resp && resp.Result ? resp.Result : null);
            }, failed);
            return;
        }
        if (kind === 'trending') {
            apiGet('JellyfinHelper/Discovery/My/Trakt/Trending', done, failed);
            return;
        }
        apiGet('JellyfinHelper/Discovery', function (data) {
            done(Array.isArray(data) && data.length > 0 ? data[0] : null);
        }, failed);
    }

    function selectSubTab(host, kind) {
        var active = host.querySelectorAll('.jfh-discovery-tab-active');
        for (var i = 0; i < active.length; i++) { active[i].classList.remove('jfh-discovery-tab-active'); }
        var btn = host.querySelector('.jfh-discovery-tab[data-dtab="' + kind + '"]');
        if (btn) { btn.classList.add('jfh-discovery-tab-active'); }
        renderGrid(kind);
    }

    function buildPanel(host) {
        var html = '<div class="jfh-discovery-container"><div class="jfh-discovery-tabs" role="tablist">';
        html += '<button class="jfh-discovery-tab jfh-discovery-tab-active" role="tab" data-dtab="own">' +
            escHtml(T('discoveryTabForYou', 'For you')) + '</button>';
        html += '<button class="jfh-discovery-tab" role="tab" data-dtab="trakt">' +
            escHtml(T('discoveryTabTraktForYou', 'Trakt for you')) + '</button>';
        html += '<button class="jfh-discovery-tab" role="tab" data-dtab="trending">' +
            escHtml(T('discoveryTabTraktTrending', 'Trakt trending')) + '</button>';
        html += '</div><div id="demoDiscoverGrid"></div></div>';
        host.innerHTML = html;

        var buttons = host.querySelectorAll('.jfh-discovery-tab');
        for (var i = 0; i < buttons.length; i++) {
            buttons[i].addEventListener('click', function () {
                selectSubTab(host, this.dataset.dtab);
            });
        }
        renderGrid('own');
    }

    function switchToDemoTab(btn, panel) {
        var allBtns = document.querySelectorAll('.tab-btn');
        for (var i = 0; i < allBtns.length; i++) { allBtns[i].classList.remove('active'); }
        var allContent = document.querySelectorAll('.tab-content');
        for (var j = 0; j < allContent.length; j++) { allContent[j].classList.remove('active'); }
        btn.classList.add('active');
        panel.classList.add('active');
        if (!panel.dataset.initialized) {
            panel.dataset.initialized = 'true';
            buildPanel(panel);
        }
    }

    function tryInject(remaining) {
        if (document.getElementById('tab-discover-demo')) { return; }
        var bar = document.querySelector('.tab-bar');
        var anchor = document.querySelector('.tab-content');
        if (!bar || !anchor || typeof renderDiscoveryCards !== 'function' || typeof apiGet !== 'function') {
            if (remaining > 0) { setTimeout(function () { tryInject(remaining - 1); }, POLL_MS); }
            return;
        }
        injectStyles();

        var btn = document.createElement('button');
        btn.className = 'tab-btn';
        btn.id = 'tabbtn-discover-demo';
        btn.textContent = 'Per User Discovery Tab Demo';
        bar.appendChild(btn);

        var panel = document.createElement('div');
        panel.className = 'tab-content';
        panel.id = 'tab-discover-demo';
        anchor.parentElement.appendChild(panel);

        btn.addEventListener('click', function () { switchToDemoTab(btn, panel); });
    }

    tryInject(POLL_BUDGET);
})();
