/**
 * Bootstrap-style masonry (Masonry.js) for settings / admin card grids.
 * data-masonry alone only works if the library is present and content is static —
 * Blazor re-renders need an explicit refresh.
 */
window.ptmMasonry = {
  _loading: null,

  ensure: function () {
    if (window.Masonry) return Promise.resolve();
    if (this._loading) return this._loading;
    this._loading = new Promise(function (resolve, reject) {
      var s = document.createElement("script");
      s.src = "https://cdn.jsdelivr.net/npm/masonry-layout@4.2.2/dist/masonry.pkgd.min.js";
      s.integrity = "sha384-GNFwBvfVxBkLMJpYMOABq3c+d3KnQxudP/mGPkzpZSTYykLBNsZEnG2D9G/X/+7D";
      s.crossOrigin = "anonymous";
      s.onload = function () {
        resolve();
      };
      s.onerror = function () {
        reject(new Error("Failed to load masonry-layout"));
      };
      document.head.appendChild(s);
    });
    return this._loading;
  },

  /**
   * @param {string} selector - row container with .ptm-masonry-item children
   */
  refresh: async function (selector) {
    if (!selector) return;
    try {
      await this.ensure();
    } catch (e) {
      console.warn("ptmMasonry:", e);
      return;
    }
    var el = document.querySelector(selector);
    if (!el || !window.Masonry) return;

    // Defer one frame so Blazor has flushed DOM heights.
    await new Promise(function (r) {
      requestAnimationFrame(function () {
        requestAnimationFrame(r);
      });
    });

    var opts = { percentPosition: true, itemSelector: ".ptm-masonry-item" };
    var existing = Masonry.data(el);
    if (existing) {
      existing.reloadItems();
      existing.layout();
    } else {
      new Masonry(el, opts);
    }
  },
};
