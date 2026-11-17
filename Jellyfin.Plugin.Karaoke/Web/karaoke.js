/*
 * Karaoke mode for the Jellyfin web client.
 *
 * Two ways of lowering the vocals, chosen per song:
 *
 *   stems  - the server has an AI-separated file for the song: 4 channels,
 *            instrumental on 1-2 and vocals on 3-4. The player is pointed at
 *            that file and the vocal channels are mixed back in at the slider level.
 *   cancel - no stems yet: the centre of the stereo image (where lead vocals
 *            usually sit) is turned down in real time. Bass is kept.
 *
 * The script hooks the audio element Jellyfin's own player uses, so play,
 * pause, seek, queue and progress reporting keep working unchanged.
 * Only native Web Audio nodes are used: AudioWorklet needs HTTPS, and many
 * home servers are plain HTTP.
 */
(function () {
    'use strict';

    if (window.__jfKaraoke) return;
    var AC = window.AudioContext || window.webkitAudioContext;
    if (!AC) return;

    var VOCAL_KEY = 'karaoke.vocalLevel';
    var BASS_KEEP_HZ = 130;      // centre content below this is never removed
    var BUTTERWORTH_Q_DB = -3.0103; // Web Audio low/highpass Q is in dB; this is Q = 0.7071
    var POLL_MS = 30000;

    var K = window.__jfKaraoke = {
        enabled: false,
        vocal: loadVocal(),   // 0..1
        ready: {},            // item id -> true when stems exist
        autoQueue: true,
        el: null,
        itemId: null,
        usingStems: false,
        itemState: 'unknown', // unknown | none | queued | processing | failed | ready
        itemError: null,
        graph: null
    };

    // ---------------------------------------------------------------- helpers

    function loadVocal() {
        try {
            var v = parseFloat(localStorage.getItem(VOCAL_KEY));
            if (!isNaN(v) && v >= 0 && v <= 1) return v;
        } catch (e) { /* storage unavailable */ }
        return 0;
    }

    function saveVocal() {
        try { localStorage.setItem(VOCAL_KEY, String(K.vocal)); } catch (e) { /* storage unavailable */ }
    }

    function pick(o, name) {
        if (!o) return undefined;
        var lower = name.charAt(0).toLowerCase() + name.slice(1);
        return o[name] !== undefined ? o[name] : o[lower];
    }

    function client() {
        var c = window.ApiClient;
        return c && typeof c.accessToken === 'function' && c.accessToken() ? c : null;
    }

    function api(path, method) {
        var c = client();
        if (!c) return Promise.reject(new Error('not signed in'));
        return fetch(c.getUrl(path), {
            method: method || 'GET',
            headers: { Authorization: 'MediaBrowser Token="' + c.accessToken() + '"' }
        }).then(function (r) {
            if (!r.ok) throw new Error(path + ' returned ' + r.status);
            return r.status === 204 ? null : r.json().catch(function () { return null; });
        });
    }

    function stemsUrl(id) {
        var c = client();
        return c.getUrl('Karaoke/Items/' + id + '/Stems', { ApiKey: c.accessToken() });
    }

    function isPlayerEl(el) {
        return !!(el && el.classList && el.classList.contains('mediaPlayerAudio'));
    }

    // ------------------------------------------------------------ audio graph

    function buildGraph(ctx, el) {
        var g = { ctx: ctx, el: el };

        function gain(v) {
            var n = ctx.createGain();
            n.gain.value = v;
            return n;
        }

        function crossover(type, input) {
            // Two Butterworth sections in series = 4th-order Linkwitz-Riley,
            // so the low and high halves add back up without a dip or bump.
            var a = ctx.createBiquadFilter();
            var b = ctx.createBiquadFilter();
            a.type = b.type = type;
            a.frequency.value = b.frequency.value = BASS_KEEP_HZ;
            a.Q.value = b.Q.value = BUTTERWORTH_Q_DB;
            input.connect(a);
            a.connect(b);
            return b;
        }

        var split = g.input = ctx.createChannelSplitter(4);
        var merge = ctx.createChannelMerger(2);
        g.out = gain(1);
        merge.connect(g.out);

        // Off: channels 1-2 straight through.
        g.bypL = gain(1);
        g.bypR = gain(1);
        split.connect(g.bypL, 0);
        split.connect(g.bypR, 1);
        g.bypL.connect(merge, 0, 0);
        g.bypR.connect(merge, 0, 1);

        // Stems: instrumental (1-2) plus vocals (3-4) at the slider level.
        g.stemL = gain(0);
        g.stemR = gain(0);
        g.vocL = gain(1);
        g.vocR = gain(1);
        split.connect(g.stemL, 0);
        split.connect(g.stemR, 1);
        split.connect(g.vocL, 2);
        split.connect(g.vocR, 3);
        g.vocL.connect(g.stemL);
        g.vocR.connect(g.stemR);
        g.stemL.connect(merge, 0, 0);
        g.stemR.connect(merge, 0, 1);

        // Cancel: split into mid (L+R) and side (L-R), turn down the mid above the bass.
        var mid = gain(1);
        var side = gain(1);
        var halfL = gain(0.5);
        var halfR = gain(0.5);
        var halfLs = gain(0.5);
        var halfRs = gain(-0.5);
        split.connect(halfL, 0);
        split.connect(halfR, 1);
        split.connect(halfLs, 0);
        split.connect(halfRs, 1);
        halfL.connect(mid);
        halfR.connect(mid);
        halfLs.connect(side);
        halfRs.connect(side);

        var midLow = crossover('lowpass', mid);
        g.midHigh = gain(1);
        crossover('highpass', mid).connect(g.midHigh);

        // The side signal goes through the same filters so it stays in step with the mid.
        var sideAll = gain(1);
        crossover('lowpass', side).connect(sideAll);
        crossover('highpass', side).connect(sideAll);
        var sideInv = gain(-1);
        sideAll.connect(sideInv);

        g.canL = gain(0);
        g.canR = gain(0);
        midLow.connect(g.canL);
        g.midHigh.connect(g.canL);
        sideAll.connect(g.canL);
        midLow.connect(g.canR);
        g.midHigh.connect(g.canR);
        sideInv.connect(g.canR);
        g.canL.connect(merge, 0, 0);
        g.canR.connect(merge, 0, 1);

        return g;
    }

    function currentMode() {
        if (K.usingStems) return 'stems';
        return K.enabled ? 'cancel' : 'bypass';
    }

    function applyMode() {
        var g = K.graph;
        if (!g) return;
        var mode = currentMode();
        var vocal = K.enabled ? K.vocal : 1;
        var t = g.ctx.currentTime;

        function set(param, value) {
            try {
                param.setTargetAtTime(value, t, 0.02);
            } catch (e) {
                param.value = value;
            }
        }

        set(g.bypL.gain, mode === 'bypass' ? 1 : 0);
        set(g.bypR.gain, mode === 'bypass' ? 1 : 0);
        set(g.stemL.gain, mode === 'stems' ? 1 : 0);
        set(g.stemR.gain, mode === 'stems' ? 1 : 0);
        set(g.canL.gain, mode === 'cancel' ? 1 : 0);
        set(g.canR.gain, mode === 'cancel' ? 1 : 0);
        set(g.vocL.gain, vocal);
        set(g.vocR.gain, vocal);
        set(g.midHigh.gain, vocal);
    }

    // Jellyfin's player creates its own Web Audio source for volume normalisation.
    // An element can only ever have one source, so when the player makes one the
    // karaoke chain is slipped in between that source and whatever it connects to.
    var origCreateSource = AC.prototype.createMediaElementSource;
    AC.prototype.createMediaElementSource = function (el) {
        if (!isPlayerEl(el)) return origCreateSource.call(this, el);

        if (K.graph && K.graph.el === el) {
            // Karaoke got there first and already owns the element's source.
            // Give the player a stand-in so it carries on without an error.
            return { context: this, connect: function (dest) { return dest; }, disconnect: function () {} };
        }

        var src = origCreateSource.call(this, el);
        var g = buildGraph(this, el);
        src.connect(g.input);
        src.connect = function (dest) {
            g.out.connect.apply(g.out, arguments);
            return dest;
        };
        src.disconnect = function () {
            g.out.disconnect.apply(g.out, arguments);
        };
        K.graph = g;
        K.el = el;
        applyMode();
        return src;
    };

    function ensureGraph() {
        if (K.graph || !K.el) return;
        try {
            var ctx = new AC();
            var src = origCreateSource.call(ctx, K.el);
            var g = buildGraph(ctx, K.el);
            src.connect(g.input);
            g.out.connect(ctx.destination);
            K.graph = g;
            applyMode();
        } catch (e) {
            console.error('[karaoke] could not attach to the audio player', e);
        }
    }

    function resumeContext() {
        var g = K.graph;
        if (g && g.ctx.state === 'suspended' && g.ctx.resume) {
            g.ctx.resume().catch(function () {});
        }
    }

    // ------------------------------------------------ following the player

    // Every song the player starts passes through the element's src. Watching
    // it tells us which song is playing and lets us substitute the stems file.
    var srcDesc = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'src');
    var ITEM_RE = /\/Audio\/([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\//i;

    Object.defineProperty(HTMLMediaElement.prototype, 'src', {
        configurable: true,
        enumerable: srcDesc.enumerable,
        get: function () { return srcDesc.get.call(this); },
        set: function (val) {
            if (isPlayerEl(this)) {
                try {
                    val = onPlayerSrc(this, String(val));
                } catch (e) {
                    console.error('[karaoke] src hook failed', e);
                }
            }
            srcDesc.set.call(this, val);
        }
    });

    function onPlayerSrc(el, val) {
        if (K.el !== el) {
            K.el = el;
            el.addEventListener('error', onPlayerError);
        }

        var m = ITEM_RE.exec(val);
        var id = m ? m[1].replace(/-/g, '').toLowerCase() : null;

        K.itemId = id;
        K.itemError = null;
        K.itemState = id ? (K.ready[id] ? 'ready' : 'unknown') : 'unknown';
        K.usingStems = false;

        if (id && K.enabled && K.ready[id] && client()) {
            var hash = val.indexOf('#') >= 0 ? val.slice(val.indexOf('#')) : '';
            val = stemsUrl(id) + hash;
            K.usingStems = true;
        }

        if (K.enabled) {
            // The player may be about to create its own source; let it go first.
            setTimeout(function () { ensureGraph(); applyMode(); }, 250);
        }
        applyMode();
        setTimeout(function () { refresh(); render(); }, 0);
        return val;
    }

    function onPlayerError() {
        if (K.usingStems && K.itemId) {
            console.warn('[karaoke] stems failed to load for', K.itemId);
            delete K.ready[K.itemId];
        }
    }

    function switchToStems() {
        var el = K.el;
        if (!el || !K.itemId || !K.ready[K.itemId] || K.usingStems || !client()) return;
        var t = el.currentTime || 0;
        var paused = el.paused;
        K.usingStems = true;
        applyMode();
        if (paused) el.autoplay = false;
        srcDesc.set.call(el, stemsUrl(K.itemId) + '#t=' + t.toFixed(2));
        if (!paused) {
            var p = el.play();
            if (p && p.catch) p.catch(function () {});
        }
        render();
    }

    // ------------------------------------------------------- server state

    // switchIfReady: move the current song onto its stems straight away if the
    // server turns out to have them (used when karaoke mode is switched on).
    function refresh(switchIfReady) {
        if (!client()) return;
        api('Karaoke/State').then(function (s) {
            var ids = pick(s, 'Ids') || [];
            var ready = {};
            ids.forEach(function (id) { ready[String(id).toLowerCase()] = true; });
            K.ready = ready;
            K.autoQueue = pick(s, 'AutoQueue') !== false;

            var id = K.itemId;
            if (!id) return null;
            if (ready[id]) {
                K.itemState = 'ready';
                if (switchIfReady === true && K.enabled) switchToStems();
                return null;
            }
            if (!K.enabled) return null;
            return api('Karaoke/Items/' + id + '/Status').then(function (st) {
                if (K.itemId !== id) return null;
                K.itemState = pick(st, 'State') || 'none';
                K.itemError = pick(st, 'Error') || null;
                if (K.itemState === 'none' && K.autoQueue) return queueCurrent();
                return null;
            });
        }).then(render, function (e) {
            console.debug('[karaoke] state refresh failed', e);
        });
    }

    function queueCurrent() {
        var id = K.itemId;
        if (!id) return Promise.resolve();
        return api('Karaoke/Items/' + id + '/Queue', 'POST').then(function (r) {
            if (K.itemId !== id) return;
            K.itemState = pick(r, 'State') || 'queued';
            K.itemError = null;
            render();
        }).catch(function (e) {
            console.debug('[karaoke] queue failed', e);
        });
    }

    setInterval(function () {
        if (K.enabled && !document.hidden) refresh();
    }, POLL_MS);

    // ------------------------------------------------------------ controls

    function setEnabled(on) {
        K.enabled = !!on;
        if (K.enabled) {
            ensureGraph();
            resumeContext();
            if (K.itemId && K.ready[K.itemId]) switchToStems();
            refresh(true);
        }
        applyMode();
        render();
    }

    function setVocal(v) {
        K.vocal = Math.min(1, Math.max(0, v));
        saveVocal();
        applyMode();
        render();
    }

    K.setEnabled = setEnabled;
    K.setVocal = setVocal;

    // ------------------------------------------------------------------ UI

    var ui = {};

    function buildUi() {
        var style = document.createElement('style');
        style.textContent = [
            '.kk-btn{background:none;border:0;color:inherit;cursor:pointer;padding:.5em;border-radius:50%;font-size:inherit;line-height:1;display:inline-flex;align-items:center;justify-content:center}',
            '.kk-btn svg{width:1.6em;height:1.6em;display:block}',
            '.kk-btn.kk-on{color:#00a4dc}',
            '.kk-btn.kk-float{position:fixed;right:1em;bottom:7em;z-index:1099;background:#202020;color:#fff;box-shadow:0 2px 8px rgba(0,0,0,.5);padding:.7em}',
            '.kk-btn.kk-float.kk-on{color:#00a4dc}',
            '.kk-panel{position:fixed;z-index:1100;width:17em;max-width:calc(100vw - 2em);box-sizing:border-box;background:#202020;color:#fff;border-radius:.5em;padding:1em;box-shadow:0 4px 16px rgba(0,0,0,.6);font-size:.95em}',
            '.kk-panel[hidden]{display:none}',
            '.kk-row{display:flex;align-items:center;gap:.75em;margin-bottom:.9em}',
            '.kk-title{flex:1;font-weight:600}',
            '.kk-row input[type=range]{flex:1;min-width:0;accent-color:#00a4dc}',
            '.kk-row input[type=checkbox]{width:1.3em;height:1.3em;accent-color:#00a4dc}',
            '.kk-val{width:3em;text-align:right;font-variant-numeric:tabular-nums}',
            '.kk-dim{opacity:.45}',
            '.kk-status{font-size:.85em;opacity:.8;line-height:1.35}',
            '.kk-action{margin-top:.7em;background:#00a4dc;color:#fff;border:0;border-radius:.3em;padding:.45em .8em;cursor:pointer;font-size:.85em}',
            '.kk-action[hidden]{display:none}'
        ].join('\n');
        document.head.appendChild(style);

        var btn = ui.btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'kk-btn';
        btn.title = 'Karaoke mode';
        btn.setAttribute('aria-label', 'Karaoke mode');
        // Inline icon, so the button does not depend on the web client's icon font.
        btn.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M12 14a3 3 0 0 0 3-3V5a3 3 0 0 0-6 0v6a3 3 0 0 0 3 3zm5-3a5 5 0 0 1-10 0H5a7 7 0 0 0 6 6.92V21h2v-3.08A7 7 0 0 0 19 11h-2z"/></svg>';
        btn.addEventListener('click', function (e) {
            e.stopPropagation();
            togglePanel();
        });

        var panel = ui.panel = document.createElement('div');
        panel.className = 'kk-panel';
        panel.hidden = true;
        panel.innerHTML =
            '<div class="kk-row"><label class="kk-title" for="kk-toggle">Karaoke mode</label>' +
            '<input type="checkbox" id="kk-toggle"></div>' +
            '<div class="kk-row kk-slider-row"><label for="kk-slider">Vocals</label>' +
            '<input type="range" id="kk-slider" min="0" max="100" step="1">' +
            '<span class="kk-val"></span></div>' +
            '<div class="kk-status"></div>' +
            '<button type="button" class="kk-action" hidden></button>';
        panel.addEventListener('click', function (e) { e.stopPropagation(); });
        document.body.appendChild(panel);

        ui.toggle = panel.querySelector('#kk-toggle');
        ui.slider = panel.querySelector('#kk-slider');
        ui.sliderRow = panel.querySelector('.kk-slider-row');
        ui.val = panel.querySelector('.kk-val');
        ui.status = panel.querySelector('.kk-status');
        ui.action = panel.querySelector('.kk-action');

        ui.toggle.addEventListener('change', function () { setEnabled(ui.toggle.checked); });
        ui.slider.addEventListener('input', function () { setVocal(parseInt(ui.slider.value, 10) / 100); });
        ui.action.addEventListener('click', function () {
            if (ui.actionKind === 'switch') switchToStems();
            else if (ui.actionKind === 'queue') queueCurrent();
        });
        document.addEventListener('click', function () { ui.panel.hidden = true; });
        window.addEventListener('resize', positionPanel);
    }

    function togglePanel() {
        ui.panel.hidden = !ui.panel.hidden;
        if (!ui.panel.hidden) {
            refresh();
            render();
            positionPanel();
        }
    }

    function positionPanel() {
        if (!ui.panel || ui.panel.hidden || !ui.btn.isConnected) return;
        var r = ui.btn.getBoundingClientRect();
        var w = ui.panel.offsetWidth;
        var h = ui.panel.offsetHeight;
        var left = Math.min(window.innerWidth - w - 8, Math.max(8, r.right - w));
        var top = r.top - h - 8;
        if (top < 8) top = Math.min(window.innerHeight - h - 8, r.bottom + 8);
        ui.panel.style.left = left + 'px';
        ui.panel.style.top = Math.max(8, top) + 'px';
    }

    function placeButton() {
        if (!ui.btn) return;
        var bars = document.querySelectorAll('.nowPlayingBar .nowPlayingBarRight');
        var host = null;
        for (var i = 0; i < bars.length; i++) {
            if (bars[i].offsetParent !== null) { host = bars[i]; break; }
        }

        if (host) {
            if (ui.btn.parentNode !== host) {
                ui.btn.classList.remove('kk-float');
                host.insertBefore(ui.btn, host.firstChild);
            }
        } else if (K.el && K.itemId) {
            if (ui.btn.parentNode !== document.body) {
                ui.btn.classList.add('kk-float');
                document.body.appendChild(ui.btn);
            }
        } else if (ui.btn.parentNode) {
            ui.btn.parentNode.removeChild(ui.btn);
            ui.panel.hidden = true;
        }
    }

    function render() {
        if (!ui.btn) return;
        ui.btn.classList.toggle('kk-on', K.enabled);
        ui.toggle.checked = K.enabled;
        ui.slider.value = String(Math.round(K.vocal * 100));
        ui.val.textContent = Math.round(K.vocal * 100) + '%';
        ui.sliderRow.classList.toggle('kk-dim', !K.enabled);

        var text;
        var action = null;
        if (!K.itemId) {
            text = 'Start a song to use karaoke mode.';
        } else if (K.usingStems) {
            text = 'Using AI-separated vocals for this song (best quality).';
        } else if (K.itemState === 'ready') {
            text = 'AI-separated vocals are ready for this song.';
            if (K.enabled) action = ['switch', 'Switch to them now'];
        } else if (K.itemState === 'queued') {
            text = 'Instant mode. This song is queued for AI separation.';
        } else if (K.itemState === 'processing') {
            text = 'Instant mode. AI separation is running for this song.';
        } else if (K.itemState === 'failed') {
            text = 'Instant mode. AI separation failed' + (K.itemError ? ': ' + K.itemError : '.');
            action = ['queue', 'Try again'];
        } else {
            text = 'Instant mode: turns down whatever sits in the centre of the mix.';
            if (K.itemState === 'none') action = ['queue', 'Prepare AI version'];
        }
        ui.status.textContent = text;
        ui.actionKind = action ? action[0] : null;
        ui.action.hidden = !action;
        ui.action.textContent = action ? action[1] : '';
        positionPanel();
    }

    function start() {
        buildUi();
        render();
        placeButton();
        setInterval(placeButton, 1000);
    }

    if (document.body) start();
    else document.addEventListener('DOMContentLoaded', start);
})();
