(function () {
    'use strict';

    if (window.JellyfinHelperLifecycle) return;

    var disposed = false;
    var lastHref = window.location.href;
    var lastSession = null;
    var authBindings = new Map();
    var wrappedAuthMethods = new WeakSet();
    var originalPush = history.pushState;
    var originalReplace = history.replaceState;

    function readSession() {
        var client = typeof ApiClient === 'undefined' ? null : ApiClient;
        try {
            return { client: client, userId: client?.getCurrentUserId?.() || '', token: client?.accessToken?.() || '' };
        } catch (_) {
            return { client: client, userId: '', token: '' };
        }
    }

    function sameSession(first, second) {
        return first != null && first.client === second.client && first.userId === second.userId && first.token === second.token;
    }

    function notifySession(force) {
        if (disposed) return;
        var session = readSession();
        if (!force && sameSession(lastSession, session)) return;
        lastSession = session;
        document.dispatchEvent(new Event('jfh:session-changed'));
    }

    function installAuthHook(client) {
        if (!client || authBindings.has(client) || typeof client.setAuthenticationInfo !== 'function') return;
        var original = client.setAuthenticationInfo;
        if (wrappedAuthMethods.has(original)) return;
        var descriptor = Object.getOwnPropertyDescriptor(client, 'setAuthenticationInfo');
        var wrapped = function (token, userId) {
            var active = !disposed && typeof ApiClient !== 'undefined' && this === ApiClient;
            var previous = readSession();
            var changed = active && (!sameSession(lastSession, previous)
                || previous.userId !== (userId || '') || previous.token !== (token || ''));
            if (changed) document.dispatchEvent(new Event('jfh:session-changing'));
            try {
                return original.apply(this, arguments);
            } finally {
                if (active) notifySession(changed);
            }
        };
        try {
            // An instance hook preserves other plugins' prototype hooks and leaves secondary clients untouched.
            Object.defineProperty(client, 'setAuthenticationInfo', {
                value: wrapped, configurable: true, writable: true, enumerable: descriptor?.enumerable ?? false
            });
            authBindings.set(client, { original: original, descriptor: descriptor, wrapped: wrapped });
            wrappedAuthMethods.add(wrapped);
        } catch (_) {
            // Immutable host clients are reconciled through navigation and page changes instead.
        }
    }

    function notifyNavigation() {
        if (disposed || lastHref === window.location.href) return;
        lastHref = window.location.href;
        document.dispatchEvent(new Event('jfh:navigation'));
    }

    function refresh() {
        if (disposed) return;
        installAuthHook(typeof ApiClient === 'undefined' ? null : ApiClient);
        notifySession(false);
        notifyNavigation();
    }

    function pushed() {
        var result = originalPush.apply(this, arguments);
        refresh();
        return result;
    }

    function replaced() {
        var result = originalReplace.apply(this, arguments);
        refresh();
        return result;
    }

    history.pushState = pushed;
    history.replaceState = replaced;
    var observer = new MutationObserver(refresh);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'hidden'] });
    window.addEventListener('hashchange', refresh);
    window.addEventListener('popstate', refresh);
    window.addEventListener('pageshow', refresh);
    document.addEventListener('viewshow', refresh, true);
    document.addEventListener('visibilitychange', refresh);
    var timer = setInterval(refresh, 5000);

    window.JellyfinHelperLifecycle = {
        refresh: refresh,
        dispose: function () {
            disposed = true;
            observer.disconnect();
            clearInterval(timer);
            window.removeEventListener('hashchange', refresh);
            window.removeEventListener('popstate', refresh);
            window.removeEventListener('pageshow', refresh);
            document.removeEventListener('viewshow', refresh, true);
            document.removeEventListener('visibilitychange', refresh);
            if (history.pushState === pushed) history.pushState = originalPush;
            if (history.replaceState === replaced) history.replaceState = originalReplace;
            authBindings.forEach(function (binding, client) {
                if (client.setAuthenticationInfo !== binding.wrapped) return;
                if (binding.descriptor) Object.defineProperty(client, 'setAuthenticationInfo', binding.descriptor);
                else delete client.setAuthenticationInfo;
            });
            authBindings.clear();
            lastSession = null;
        }
    };
    refresh();
})();
