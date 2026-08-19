(function () {
    function resolve(pref) {
        if (pref === 'system') {
            return window.matchMedia?.('(prefers-color-scheme: dark)')?.matches
                ? 'dark'
                : 'light';
        }
        return pref === 'light' ? 'light' : 'dark';
    }

    function apply(pref) {
        const resolved = resolve(pref);
        document.documentElement.dataset.theme = resolved;
        document.documentElement.dataset.bsTheme = resolved;
        try { localStorage.setItem('fs.themePref', pref || 'dark'); } catch (e) { /* ignore */ }
        return resolved;
    }

    window.fsTheme = { resolve: resolve, apply: apply };

    // Paint with the last-known preference immediately (before the Blazor circuit
    // connects and fetches the authoritative per-project value) to avoid a flash.
    try {
        apply(localStorage.getItem('fs.themePref') || 'dark');
    } catch (e) {
        document.documentElement.dataset.theme = 'dark';
        document.documentElement.dataset.bsTheme = 'dark';
    }
})();

// Small UI helpers (no framework): keep a scrolling log pinned to its newest line.
window.PageToMovieUi = window.PageToMovieUi || {};
window.PageToMovieUi.scrollToBottom = function (el) {
    try { if (el) el.scrollTop = el.scrollHeight; } catch (e) { /* ignore */ }
};
