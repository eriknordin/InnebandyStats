// Hämta data via Chrome-pluginet. När pluginet är installerat (det sätter data-ibs-plugin
// på <html>) laddar knapparna [data-ibs-fetch] sidan på stats.innebandy.se i en bakgrundsflik,
// visar förloppet och laddar om den här sidan när datan kommit in.
//   [data-ibs-container]    omsluter knappen och [data-ibs-progress] (förloppstext)
//   [data-ibs-plugin-only]  på containern: visas bara när pluginet finns
//   utan plugin fungerar knappen som en vanlig länk (om den är en <a>)
document.addEventListener('DOMContentLoaded', function () {
    const hasPlugin = !!document.documentElement.dataset.ibsPlugin;

    document.querySelectorAll('[data-ibs-fetch]').forEach(function (button) {
        const container = button.closest('[data-ibs-container]') || button.parentElement;
        const progress = container.querySelector('[data-ibs-progress]');
        const hint = container.querySelector('[data-ibs-hint]');
        const pluginOnly = button.hasAttribute('data-ibs-plugin-only') || container.hasAttribute('data-ibs-plugin-only');

        if (!hasPlugin) return;
        if (pluginOnly) {
            button.hidden = false;
            container.hidden = false;
        }
        if (button.dataset.ibsBound) return;
        button.dataset.ibsBound = '1';

        if (!pluginOnly) {
            button.textContent = 'Hämta med pluginet';
            button.removeAttribute('target');
            if (hint) hint.textContent = 'Pluginet laddar sidan på stats.innebandy.se i en bakgrundsflik och skickar datan hit.';
        }

        function show(text, isError) {
            if (!progress) return;
            progress.hidden = false;
            progress.textContent = text;
            progress.classList.toggle('text-danger', !!isError);
        }

        function setBusy(busy) {
            button.classList.toggle('disabled', busy);
            if (button.tagName === 'BUTTON') button.disabled = busy;
        }

        function onMessage(event) {
            const data = event.data;
            if (event.source !== window || !data || data.source !== 'ibs-plugin') return;

            if (data.type === 'progress') {
                const s = data.status || {};
                if (s.phase === 'open') show('Öppnar stats.innebandy.se…');
                else if (s.phase === 'sync') show('Skickar till servern…');
                else if (s.phase === 'collect') {
                    const handled = (s.done || 0) + (s.failed || 0);
                    const total = handled + (s.remaining || 0);
                    show(total > 0 ? 'Hämtar matcher: ' + handled + ' av ' + total : 'Läser in serien…');
                }
            } else if (data.type === 'done') {
                window.removeEventListener('message', onMessage);
                if (data.ok) {
                    show('Klart' + (data.fetched ? ' – ' + data.fetched + ' nya matcher' : '') + '. Laddar om…');
                    setTimeout(function () { location.reload(); }, 800);
                } else {
                    setBusy(false);
                    show(data.error || 'Något gick fel.', true);
                }
            }
        }

        button.addEventListener('click', function (event) {
            event.preventDefault();
            if (button.classList.contains('disabled')) return;
            setBusy(true);
            show('Startar…');
            window.addEventListener('message', onMessage);
            window.postMessage({ source: 'ibs-app', type: 'fetch', url: button.dataset.ibsFetch }, location.origin);
        });
    });

    // Rensa seriens data på servern – och i pluginet om det är installerat, så att nästa
    // synk hämtar allt på nytt från stats.innebandy.se
    document.querySelectorAll('[data-ibs-clear]').forEach(function (button) {
        const container = button.closest('[data-ibs-container]') || button.parentElement;
        const progress = container.querySelector('[data-ibs-progress]');
        const show = function (text, isError) {
            if (!progress) return;
            progress.textContent = text;
            progress.classList.toggle('text-danger', !!isError);
        };

        function clearInPlugin(competitionId) {
            if (!hasPlugin) return Promise.resolve();
            return new Promise(function (resolve) {
                const timer = setTimeout(done, 5000);
                function done() {
                    clearTimeout(timer);
                    window.removeEventListener('message', onMessage);
                    resolve();
                }
                function onMessage(event) {
                    if (event.source === window && event.data && event.data.source === 'ibs-plugin' && event.data.type === 'cleared') done();
                }
                window.addEventListener('message', onMessage);
                window.postMessage({ source: 'ibs-app', type: 'clear', competitionId: competitionId }, location.origin);
            });
        }

        button.addEventListener('click', function () {
            const competitionId = button.dataset.ibsClear;
            const message = 'Rensa all synkad data för serien (matchlista, matcher och laguppställningar)?'
                + (hasPlugin ? '\n\nPluginets egen kopia rensas också, så nästa synk hämtar allt på nytt från stats.innebandy.se.' : '');
            if (!confirm(message)) return;

            button.disabled = true;
            show('Rensar…');
            fetch('/api/series/' + encodeURIComponent(competitionId) + '/clear', { method: 'POST' })
                .then(function (response) {
                    if (!response.ok) throw new Error('Servern svarade ' + response.status);
                    return response.json();
                })
                .then(function (result) {
                    return clearInPlugin(competitionId).then(function () {
                        show('Rensat (' + result.deleted + ' dokument). Laddar om…');
                        setTimeout(function () { location.reload(); }, 800);
                    });
                })
                .catch(function (error) {
                    button.disabled = false;
                    show('Kunde inte rensa: ' + error.message, true);
                });
        });
    });
});
