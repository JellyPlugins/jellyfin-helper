(function () {
    'use strict';
    if (window.JellyfinHelperRewind) return;
    var TAB = 'yearly-rewind';
    var generation = 0;
    var session = null;
    var timer = null;
    var registered = false;
    var panel = null;
    var report = null;
    var reportId = null;
    var strings = {};
    var locale = 'en';
    var inFlight = null;
    var disposed = false;
    var refreshing = null;

    function value(data, key) { return data?.[key] ?? data?.[key.charAt(0).toLowerCase() + key.slice(1)]; }
    function text(key) { return strings['rewind.' + key] || english[key] || key; }
    function node(tag, content, className) {
        var el = document.createElement(tag);
        if (content != null) el.textContent = content;
        if (className) el.className = className;
        return el;
    }
    function request(path) { return session.client.ajax({ type: 'GET', url: session.client.getUrl(path), dataType: 'json' }); }
    function readSession() {
        try {
            var client = typeof ApiClient === 'undefined' ? null : ApiClient;
            return client && client.getCurrentUserId?.() && client.accessToken?.()
                ? { client: client, user: client.getCurrentUserId(), token: client.accessToken(), server: client.serverAddress?.() || '' } : null;
        } catch (_) { return null; }
    }
    function same(a, b) { return !!a && !!b && a.client === b.client && a.user === b.user && a.token === b.token && a.server === b.server; }
    function valid(epoch) { return !disposed && epoch === generation && same(session, readSession()); }
    function clear() {
        generation++;
        if (timer) clearTimeout(timer);
        timer = null;
        refreshing = null;
        inFlight = null;
        report = null;
        reportId = null;
        if (panel) panel.replaceChildren();
        panel = null;
        if (registered) window.JellyfinHelperTabs?.unregister(TAB);
        registered = false;
    }
    function reconstructedCount() {
        return value(report, 'ReconstructedItemCount') || (value(report, 'Items') || []).filter(function (item) { return value(item, 'HasReconstructedActivity'); }).length;
    }
    function hasMeasuredPlayback() {
        return (value(value(report, 'Insights'), 'HourlySeconds') || []).some(function (amount) { return amount > 0; });
    }
    function number(amount, digits) { return new Intl.NumberFormat(locale, { maximumFractionDigits: digits || 0 }).format(amount); }
    function date(instant, timeZone) {
        return new Intl.DateTimeFormat(locale, { timeZone: timeZone || value(report, 'TimeZoneId') || 'UTC', day: '2-digit', month: '2-digit', year: 'numeric' }).format(new Date(instant));
    }
    function year(instant, timeZone) {
        return new Intl.DateTimeFormat(locale, { year: 'numeric', timeZone: timeZone || 'UTC' }).format(new Date(instant));
    }
    function addStat(root, amount, label, icon) {
        var card = node('div', null, 'jfh-rewind-stat');
        var heading = node('h3');
        var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('fill', 'currentColor');
        svg.setAttribute('aria-hidden', 'true');
        svg.setAttribute('focusable', 'false');
        svg.classList.add('jfh-rewind-stat-icon');
        var path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
        path.setAttribute('d', statIcons[icon]);
        svg.append(path);
        heading.append(svg, node('span', label));
        card.append(heading, node('strong', amount));
        root.append(card);
    }
    function svgNode(tag) { return document.createElementNS('http://www.w3.org/2000/svg', tag); }
    function section(root, title, className) {
        var result = node('section', null, className);
        result.append(node('h3', title)); root.append(result); return result;
    }
    function genreNames(item) {
        return Array.from(new Set((value(item, 'Genres') || []).filter(function (name) { return typeof name === 'string' && name.trim(); }).map(function (name) { return name.trim(); })));
    }
    function genreWeights(items) {
        var result = new Map();
        items.forEach(function (item) {
            var names = genreNames(item); if (!names.length) names = [''];
            names.forEach(function (name) { result.set(name, (result.get(name) || 0) + (value(item, 'ActiveSeconds') || 0) / names.length); });
        });
        return result;
    }
    function genreEntries(weights) {
        var entries = Array.from(weights.entries()).map(function (entry) { return { label: entry[0] || text('unknownGenre'), seconds: entry[1] }; })
            .filter(function (entry) { return entry.seconds > 0; }).sort(function (a, b) { return b.seconds - a.seconds || a.label.localeCompare(b.label); });
        if (entries.length > 6) entries = entries.slice(0, 5).concat([{ label: text('otherGenres'), seconds: entries.slice(5).reduce(function (sum, entry) { return sum + entry.seconds; }, 0) }]);
        return entries;
    }
    function pie(root, title, entries) {
        root.replaceChildren();
        entries = entries.filter(function (entry) { return Number.isFinite(entry.seconds) && entry.seconds > 0; });
        var total = entries.reduce(function (sum, entry) { return sum + entry.seconds; }, 0);
        if (!total || !Number.isFinite(total)) { root.append(node('p', text(reconstructedCount() && !(value(report, 'Items') || []).some(function (item) { return value(item, 'ActiveSeconds') > 0; }) ? 'noDuration' : 'empty'))); return; }
        var svg = svgNode('svg'); svg.setAttribute('viewBox', '0 0 220 220'); svg.classList.add('jfh-rewind-pie');
        svg.setAttribute('role', 'img');
        var legend = node('ul', null, 'jfh-rewind-pie-legend');
        var angle = -Math.PI / 2;
        var descriptions = [];
        entries.forEach(function (entry, index) {
            var share = entry.seconds / total;
            var end = angle + share * Math.PI * 2;
            var shape = svgNode(entries.length === 1 ? 'circle' : 'path');
            if (entries.length === 1) { shape.setAttribute('cx', '110'); shape.setAttribute('cy', '110'); shape.setAttribute('r', '100'); }
            else shape.setAttribute('d', 'M110 110 L' + (110 + 100 * Math.cos(angle)) + ' ' + (110 + 100 * Math.sin(angle)) + ' A100 100 0 ' + (share > .5 ? 1 : 0) + ' 1 ' + (110 + 100 * Math.cos(end)) + ' ' + (110 + 100 * Math.sin(end)) + ' Z');
            var color = pieColors[index % pieColors.length]; shape.setAttribute('fill', color);
            var percent = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 }).format(share);
            var amount = percent + ' · ' + number(entry.seconds / 3600, 1) + ' ' + text('hourUnit');
            var description = entry.label + ': ' + amount;
            var tooltip = svgNode('title'); tooltip.textContent = description; shape.append(tooltip); svg.append(shape);
            descriptions.push(description);
            var row = node('li'); var swatch = node('span', null, 'jfh-rewind-swatch'); swatch.style.backgroundColor = color; swatch.setAttribute('aria-hidden', 'true');
            row.append(swatch, node('span', entry.label, 'jfh-rewind-legend-label'), node('strong', amount)); legend.append(row);
            angle = end;
        });
        svg.setAttribute('aria-label', title + ': ' + descriptions.join('; '));
        root.append(svg, legend);
    }
    function renderPies(items, genres, months) {
        var grid = node('div', null, 'jfh-rewind-pies');
        var mix = section(grid, text('mediaMix'), 'jfh-rewind-mix');
        mix.append(node('p', text('byWatchTime'), 'jfh-rewind-secondary'));
        var mixBody = node('div', null, 'jfh-rewind-pie-body'); mix.append(mixBody);
        pie(mixBody, text('mediaMix'), ['Movie', 'Episode'].map(function (kind) {
            return { label: text(kind === 'Movie' ? 'movies' : 'series'), seconds: items.filter(function (item) { return value(item, 'ItemType') === kind; }).reduce(function (sum, item) { return sum + (value(item, 'ActiveSeconds') || 0); }, 0) };
        }));
        var genreSection = section(grid, text('topGenres'), 'jfh-rewind-genres');
        var count = node('p', null, 'jfh-rewind-secondary'); genreSection.append(count);
        var body = node('div', null, 'jfh-rewind-pie-body');
        function showGenres(weights) {
            count.textContent = text('genreCount').replace('{count}', number(Array.from(weights.keys()).filter(function (name) { return name; }).length));
            pie(body, text('topGenres'), genreEntries(weights));
        }
        if (months.length && months.every(function (month) { return Array.isArray(value(month, 'Genres')); })) {
            var label = node('label', text('genrePeriod'), 'jfh-rewind-genre-period'); label.htmlFor = 'jfh-rewind-genre-period';
            var select = node('select', null, 'jfh-rewind-select'); select.id = label.htmlFor;
            var all = node('option', text('wholePeriod')); all.value = 'all'; select.append(all);
            months.forEach(function (month, index) {
                var option = node('option', new Intl.DateTimeFormat(locale, { month: 'long', year: 'numeric', timeZone: value(report, 'TimeZoneId') || 'UTC' }).format(new Date(value(month, 'StartUtc'))));
                option.value = String(index); select.append(option);
            });
            select.addEventListener('change', function () {
                if (select.value === 'all') showGenres(genres);
                else showGenres(new Map(value(months[Number(select.value)], 'Genres').map(function (genre) { return [value(genre, 'Name') || '', value(genre, 'ActiveSeconds') || 0]; })));
            });
            genreSection.append(label, select);
        }
        genreSection.append(body, node('p', text('genreAllocation'), 'jfh-rewind-secondary'));
        showGenres(genres); panel.append(grid);
    }
    function distribution(root, labels, amounts) {
        var maximum = Math.max(1, ...amounts);
        var list = node('ul', null, 'jfh-rewind-distribution');
        labels.forEach(function (label, index) {
            var row = node('li'); var track = node('div', null, 'jfh-rewind-distribution-track');
            var bar = node('div'); bar.style.width = (amounts[index] / maximum * 100) + '%'; track.setAttribute('aria-hidden', 'true'); track.append(bar);
            row.append(node('span', label), node('strong', number(amounts[index] / 3600, 1) + ' ' + text('hourUnit')), track); list.append(row);
        });
        root.append(list);
    }
    function favorites(labels, amounts) {
        var maximum = Math.max(...amounts);
        return labels.filter(function (_, index) { return amounts[index] === maximum; }).join(' / ');
    }
    function calendarDate(instant) {
        return new Intl.DateTimeFormat(locale, { day: '2-digit', month: '2-digit', year: 'numeric', timeZone: 'UTC' }).format(new Date(instant + 'T00:00:00Z'));
    }
    function renderInsights() {
        var grid = node('div', null, 'jfh-rewind-insight-grid');
        var patterns = section(grid, text('viewingHabits'), 'jfh-rewind-patterns');
        var streak = section(grid, text('streak'), 'jfh-rewind-streak');
        var insights = value(report, 'Insights');
        if (!insights) { [patterns, streak].forEach(function (root) { root.append(node('p', text('insightsMissing'), 'jfh-rewind-secondary')); }); panel.append(grid); return; }
        if (reconstructedCount() && !hasMeasuredPlayback()) {
            [patterns, streak].forEach(function (root) { root.append(node('p', text('noMeasuredInsights'), 'jfh-rewind-secondary')); });
            panel.append(grid); return;
        }
        var hours = value(insights, 'HourlySeconds') || [];
        var weekdays = value(insights, 'WeekdaySeconds') || [];
        if (!hours.some(function (amount) { return amount > 0; })) patterns.append(node('p', text('empty')));
        else {
            var groups = node('div', null, 'jfh-rewind-pattern-grid');
            var week = node('div'); week.append(node('h4', text('weekday')));
            var labels = weekdays.map(function (_, index) { return new Intl.DateTimeFormat(locale, { weekday: 'long', timeZone: 'UTC' }).format(new Date(Date.UTC(2024, 0, 1 + index))); });
            week.append(node('p', favorites(labels, weekdays), 'jfh-rewind-favorite')); distribution(week, labels, weekdays);
            var day = node('div'); day.append(node('h4', text('daytime')));
            var periods = [[6, 12, 'morning'], [12, 18, 'afternoon'], [18, 24, 'evening'], [0, 6, 'night']];
            var periodLabels = periods.map(function (entry) { return text(entry[2]) + ' (' + String(entry[0]).padStart(2, '0') + ':00–' + String(entry[1]).padStart(2, '0') + ':00)'; });
            var amounts = periods.map(function (entry) { return hours.slice(entry[0], entry[1]).reduce(function (sum, amount) { return sum + amount; }, 0); });
            day.append(node('p', favorites(periodLabels, amounts), 'jfh-rewind-favorite')); distribution(day, periodLabels, amounts);
            groups.append(week, day); patterns.append(groups, node('p', text('serverTime').replace('{zone}', value(report, 'TimeZoneId') || 'UTC'), 'jfh-rewind-secondary'));
        }
        var days = value(insights, 'LongestStreakDays') || 0;
        streak.append(node('p', number(days) + ' ' + text(new Intl.PluralRules(locale).select(days) === 'one' ? 'daySingular' : 'dayPlural'), 'jfh-rewind-streak-length'));
        if (days) streak.append(node('p', calendarDate(value(insights, 'LongestStreakStart')) + ' – ' + calendarDate(value(insights, 'LongestStreakEnd'))));
        streak.append(node('p', text('streakHelp'), 'jfh-rewind-secondary'));
        panel.append(grid);
    }
    function renderComparison(items, series, genres) {
        var root = section(panel, text('comparison'), 'jfh-rewind-comparison');
        var previous = value(report, 'Comparison');
        if (!previous) { root.append(node('p', text('noComparison'), 'jfh-rewind-secondary')); return; }
        root.append(node('p', text('previousPeriod') + ': ' + date(value(previous, 'PeriodStartUtc'), value(previous, 'TimeZoneId')) + ' – ' + date(new Date(new Date(value(previous, 'SnapshotAtUtc')).getTime() - 1).toISOString(), value(previous, 'TimeZoneId')), 'jfh-rewind-secondary'));
        if (reconstructedCount() || value(previous, 'ReconstructedItemCount')) root.append(node('p', text('comparisonEstimated'), 'jfh-rewind-notice'));
        else if (value(report, 'DataState') === 'Partial' || value(previous, 'DataState') === 'Partial') root.append(node('p', text('partialComparison'), 'jfh-rewind-notice'));
        var table = node('table', null, 'jfh-rewind-comparison-table'); var head = node('thead'); var heading = node('tr');
        ['metric', 'current', 'previous', 'change'].forEach(function (key) { var cell = node('th', text(key)); cell.scope = 'col'; heading.append(cell); }); head.append(heading); table.append(head);
        var rows = node('tbody');
        var currentHours = items.reduce(function (sum, item) { return sum + (value(item, 'ActiveSeconds') || 0); }, 0) / 3600;
        var previousHours = (value(previous, 'ActiveSeconds') || 0) / 3600;
        var metrics = [
            ['hours', reconstructedCount() && !currentHours ? null : currentHours, value(previous, 'ReconstructedItemCount') && !previousHours ? null : previousHours, 1],
            ['movies', items.filter(function (item) { return value(item, 'ItemType') === 'Movie'; }).length, value(previous, 'MovieCount') || 0, 0],
            ['episodes', items.filter(function (item) { return value(item, 'ItemType') === 'Episode'; }).length, value(previous, 'EpisodeCount') || 0, 0],
            ['series', series.size, value(previous, 'SeriesCount') || 0, 0], ['activeDays', reconstructedCount() && !hasMeasuredPlayback() ? null : value(report, 'ActiveDays') || 0, value(previous, 'ReconstructedItemCount') && (value(previous, 'ActiveSeconds') || 0) <= (value(previous, 'EstimatedSeconds') || 0) ? null : value(previous, 'ActiveDays') || 0, 0]
        ];
        metrics.forEach(function (metric) {
            var row = node('tr'); var label = node('th', text(metric[0] === 'activeDays' && (reconstructedCount() || value(previous, 'ReconstructedItemCount')) ? 'measuredActiveDays' : metric[0])); label.scope = 'row';
            if (metric[1] === null || metric[2] === null) {
                row.append(label, node('td', metric[1] === null ? '—' : number(metric[1], metric[3])), node('td', metric[2] === null ? '—' : number(metric[2], metric[3])), node('td', '—')); rows.append(row); return;
            }
            var difference = new Intl.NumberFormat(locale, { maximumFractionDigits: metric[3], signDisplay: 'exceptZero' }).format(metric[1] - metric[2]);
            if (metric[2] > 0) difference += ' (' + new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1, signDisplay: 'exceptZero' }).format((metric[1] - metric[2]) / metric[2]) + ')';
            row.append(label, node('td', number(metric[1], metric[3])), node('td', number(metric[2], metric[3])), node('td', difference)); rows.append(row);
        });
        table.append(rows); root.append(table);
        var known = Array.from(genres.entries()).filter(function (entry) { return entry[0]; }).sort(function (a, b) { return b[1] - a[1] || (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0); });
        root.append(node('p', text('previousTopGenre') + ': ' + (value(previous, 'TopGenre') || text('unknownGenre')) + ' → ' + text('currentTopGenre') + ': ' + (known[0]?.[0] || text('unknownGenre'))));
        var oldGenres = new Set(value(previous, 'Genres') || []);
        var discovered = known.map(function (entry) { return entry[0]; }).filter(function (name) { return !oldGenres.has(name); });
        root.append(node('p', text('newGenres') + ': ' + (discovered.length ? discovered.join(', ') : text('none')), 'jfh-rewind-secondary'));
    }
    function render() {
        if (!panel) return;
        panel.replaceChildren();
        panel.classList.add('jfh-rewind');
        var start = value(report, 'PeriodStartUtc');
        var cutoff = value(report, 'SnapshotAtUtc');
        var heading = node('h2', null, 'jfh-rewind-heading');
        heading.append(node('span', text('title') + ' ' + year(cutoff, value(report, 'TimeZoneId'))));
        heading.append(' ', node('span', '(' + date(start) + ' – ' + date(new Date(new Date(cutoff).getTime() - 1).toISOString()) + ')', 'jfh-rewind-period'));
        panel.append(heading);
        var state = value(report, 'DataState');
        if (reconstructedCount()) panel.append(node('p', text('reconstructed').replace('{count}', number(reconstructedCount())), 'jfh-rewind-notice'));
        if ((state === 'Partial' && !reconstructedCount()) || state === 'NotCollected') panel.append(node('p', text(state === 'Partial' ? 'partial' : 'notCollected'), 'jfh-rewind-notice'));
        if (state === 'NotCollected') return;
        var items = value(report, 'Items') || [];
        var stats = node('div', null, 'jfh-rewind-stats');
        var movies = items.filter(function (item) { return value(item, 'ItemType') === 'Movie'; });
        var episodes = items.filter(function (item) { return value(item, 'ItemType') === 'Episode'; });
        var series = new Map();
        var genres = genreWeights(items);
        items.forEach(function (item) {
            var seconds = value(item, 'ActiveSeconds') || 0;
            var id = value(item, 'SeriesId');
            if (id) {
                var entry = series.get(id) || { ItemId: id, Name: value(item, 'SeriesName'), ActiveSeconds: 0 };
                entry.ActiveSeconds += seconds;
                entry.HasReconstructedActivity = entry.HasReconstructedActivity || value(item, 'HasReconstructedActivity');
                series.set(id, entry);
            }
        });
        var totalSeconds = items.reduce(function (sum, item) { return sum + (value(item, 'ActiveSeconds') || 0); }, 0);
        addStat(stats, reconstructedCount() && !totalSeconds ? '—' : (reconstructedCount() ? '≈ ' : '') + number(totalSeconds / 3600, 1), text('hours'), 'schedule');
        addStat(stats, number(movies.length), text('movies'), 'movie');
        addStat(stats, number(episodes.length), text('episodes'), 'episodes');
        addStat(stats, number(series.size), text('series'), 'tv');
        addStat(stats, reconstructedCount() && !hasMeasuredPlayback() ? '—' : number(value(report, 'ActiveDays') || 0), text(reconstructedCount() ? 'measuredActiveDays' : 'activeDays'), 'activeDays');
        panel.append(stats);
        if (!items.length) panel.append(node('p', text('empty')));
        var months = value(report, 'Months') || [];
        renderPies(items, genres, months);
        renderInsights();
        var maximum = Math.max(1, ...months.map(function (month) { return value(month, 'ActiveSeconds') || 0; }));
        var chart = node('section', null, 'jfh-rewind-months');
        var chartHeading = node('h3', text('months') + ' ');
        chartHeading.append(node('span', '(' + text('hourUnitLong') + ')', 'jfh-rewind-chart-unit'));
        chart.append(chartHeading);
        var plot = node('div', null, 'jfh-rewind-chart');
        plot.setAttribute('role', 'list');
        plot.setAttribute('aria-label', text('months') + ' (' + text('hourUnitLong') + ')');
        if (reconstructedCount() && !totalSeconds) { chart.append(node('p', text('noDuration'))); panel.append(chart); renderComparison(items, series, genres); return; }
        months.forEach(function (month) {
            var column = node('div', null, 'jfh-rewind-month');
            var amount = value(month, 'ActiveSeconds') || 0;
            var instant = new Date(value(month, 'StartUtc'));
            var timeZone = value(report, 'TimeZoneId') || 'UTC';
            var label = new Intl.DateTimeFormat(locale, { timeZone: timeZone, month: 'short' }).format(instant);
            var fullLabel = new Intl.DateTimeFormat(locale, { timeZone: timeZone, month: 'long', year: 'numeric' }).format(instant);
            var estimate = value(month, 'EstimatedSeconds') || 0;
            var hours = number(amount / 3600, 1) + ' ' + text('hourUnit');
            if (estimate > 0) hours += ' (' + text('estimatedPart').replace('{hours}', number(estimate / 3600, 1)) + ')';
            column.setAttribute('role', 'listitem');
            column.setAttribute('aria-label', fullLabel + ': ' + hours);
            column.title = fullLabel + ': ' + hours;
            var track = node('div', null, 'jfh-rewind-bar-track');
            var bar = node('div', null, 'jfh-rewind-bar');
            bar.style.height = (amount / maximum * 100) + '%';
            if (estimate > 0 && amount > 0) { var estimatedBar = node('div', null, 'jfh-rewind-estimated-bar'); estimatedBar.style.height = (estimate / amount * 100) + '%'; bar.append(estimatedBar); }
            track.append(bar);
            column.append(node('span', (estimate > 0 ? '≈ ' : '') + number(amount / 3600, 1), 'jfh-rewind-bar-value'), track, node('span', label, 'jfh-rewind-bar-label'));
            plot.append(column);
        });
        var scroll = node('div', null, 'jfh-rewind-chart-scroll');
        scroll.append(plot);
        chart.append(scroll);
        if (months.some(function (month) { return value(month, 'EstimatedSeconds') > 0; })) {
            var legend = node('p', null, 'jfh-rewind-time-legend');
            ['measured', 'estimated'].forEach(function (key) { var swatch = node('span', null, 'jfh-rewind-swatch jfh-rewind-' + key + '-swatch'); swatch.setAttribute('aria-hidden', 'true'); legend.append(swatch, document.createTextNode(text(key) + ' ')); });
            chart.append(legend);
        }
        panel.append(chart);
        renderComparison(items, series, genres);
    }
    function showError() {
        if (!panel) return;
        panel.replaceChildren(node('p', text('unavailable')));
        var retry = node('button', text('retry'), 'emby-button');
        retry.type = 'button'; retry.addEventListener('click', load); panel.append(retry);
    }
    function load() {
        var epoch = generation;
        if (report) { render(); return; }
        if (panel) panel.replaceChildren(node('p', text('loading')));
        if (!inFlight) {
            inFlight = Promise.resolve(request('/JellyfinHelper/YearlyRewind/My')).then(function (data) {
                if (!valid(epoch)) return;
                report = data; reportId = value(data, 'SnapshotId'); render();
            }, function (error) {
                if (!valid(epoch)) return;
                if (error?.status === 401 || error?.status === 403) { clear(); return; }
                showError();
            }).finally(function () { if (valid(epoch)) inFlight = null; });
        }
    }
    function probe() {
        var current = readSession();
        if (!same(session, current)) { clear(); session = current; }
        if (!session || disposed) return;
        if (refreshing) return;
        var epoch = generation;
        var owner = session;
        var nextDelay = 60000;
        refreshing = Promise.resolve(request('/JellyfinHelper/YearlyRewind/My/Status')).then(function (status) {
            if (!valid(epoch)) return;
            var boundary = new Date(value(status, 'NextCheckAtUtc')).getTime() - new Date(value(status, 'ServerTimeUtc')).getTime();
            if (Number.isFinite(boundary)) nextDelay = Math.max(1000, Math.min(60000, boundary));
            if (!value(status, 'Enabled') || !value(status, 'TabVisible')) {
                clear(); return;
            }
            if (reportId && reportId !== value(status, 'SnapshotId')) { report = null; reportId = null; }
            if (!value(status, 'Available')) { report = null; reportId = null; if (panel) panel.replaceChildren(node('p', text('unavailable'))); }
            if (!registered && window.JellyfinHelperTabs) {
                registered = true;
                window.JellyfinHelperTabs.register(TAB, text('tabTitle') + ' ' + year(value(status, 'SnapshotAtUtc') || value(status, 'ServerTimeUtc') || Date.now(), value(status, 'TimeZoneId')), function (root) { panel = root; load(); }, 'history', function (root) { panel = root; load(); });
            } else {
                window.JellyfinHelperTabs?.updateTitle?.(TAB, text('tabTitle') + ' ' + year(value(status, 'SnapshotAtUtc') || value(status, 'ServerTimeUtc') || Date.now(), value(status, 'TimeZoneId')));
                if (panel && !report) load();
            }
        }, function () { if (valid(epoch)) clear(); }).finally(function () {
            if (!disposed && session === owner && same(owner, readSession())) {
                refreshing = null;
                if (timer) clearTimeout(timer);
                timer = setTimeout(probe, nextDelay);
            }
        });
    }
    function refresh() {
        var current = readSession();
        if (!same(session, current)) { clear(); session = current; strings = {}; }
        if (!session) return;
        var selected = (document.documentElement.lang || navigator.language || 'en').split(/[-_]/)[0].toLowerCase();
        locale = ['en', 'de', 'fr', 'es', 'pt', 'zh', 'tr', 'sv'].includes(selected) ? selected : 'en';
        var epoch = generation;
        Promise.resolve(request('/JellyfinHelper/Translations?lang=' + locale)).then(function (data) {
            if (!valid(epoch)) return;
            strings = data || {}; probe();
        }, function () { if (valid(epoch)) probe(); });
    }
    function changing() { clear(); session = null; }
    function visible() { if (!document.hidden) probe(); }
    function dispose() {
        clear(); disposed = true;
        document.removeEventListener('jfh:session-changing', changing);
        document.removeEventListener('jfh:session-changed', refresh);
        document.removeEventListener('jfh:navigation', probe);
        document.removeEventListener('visibilitychange', visible);
    }
    var english = {
        title: 'Your year in review', tabTitle: 'Year in review', loading: 'Loading your rewind…', unavailable: 'Your rewind is not available yet.', retry: 'Try again',
        partial: 'This rewind includes only the periods for which playback was recorded.', notCollected: 'Playback was not recorded before this rewind’s cutoff.',
        empty: 'No qualifying playback was recorded.', hourUnit: 'h', hourUnitLong: 'hours', hours: 'Hours watched', movies: 'Films', episodes: 'Episodes', series: 'Series', activeDays: 'Active days',
        topGenres: 'Your top genres', months: 'Your viewing year',
        mediaMix: "Your film / series mix",
        byWatchTime: "By watch time (hours)",
        genreCount: "Distinct genres: {count}",
        genrePeriod: "Viewing period",
        wholePeriod: "Entire period",
        genreAllocation: "Watch time is divided equally among a title’s genres.",
        otherGenres: "Other genres",
        viewingHabits: "Your viewing habits",
        insightsMissing: "These details were not included when this report was created.",
        weekday: "Weekdays",
        daytime: "Time of day",
        morning: "Morning",
        afternoon: "Afternoon",
        evening: "Evening",
        night: "Night",
        serverTime: "Time zone: {zone}",
        streak: "Your longest viewing streak",
        daySingular: "day",
        dayPlural: "days",
        streakHelp: "Consecutive calendar days with recorded playback.",
        comparison: "Compared with your previous rewind",
        noComparison: "No report for the preceding period is available yet.",
        previousPeriod: "Previous period",
        partialComparison: "At least one period was only partially recorded. These differences do not represent a complete annual comparison.",
        metric: "Metric",
        current: "Current",
        previous: "Previous",
        change: "Change",
        previousTopGenre: "Previous top genre",
        currentTopGenre: "Current top genre",
        newGenres: "Genres absent from the previous report",
        none: "None",
        reconstructed: "Includes {count} titles reconstructed from Jellyfin data. Watch time may be estimated or unavailable. Viewing habits and active days use measured playback only.",
        measuredActiveDays: "Measured active days",
        noMeasuredInsights: "These details require measured playback. Only reconstructed activity is available for this period.",
        measured: "Measured",
        estimated: "Estimated",
        estimatedPart: "{hours} h estimated",
        comparisonEstimated: "At least one report includes reconstructed activity. Differences may include estimated values.",
        noDuration: "No watch duration is available for the reconstructed titles.",
        unknownGenre: 'Unknown genre'
    };
    // Clock, film and TV paths match Shared.js's existing plugin icons.
    var pieColors = ['#00a4dc', '#e67e22', '#2ecc71', '#e74c3c', '#9b59b6', '#f1c40f'];
    var statIcons = {
        "schedule": "M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zM12 20c-4.42 0-8-3.58-8-8s3.58-8 8-8 8 3.58 8 8-3.58 8-8 8zm.5-13H11v6l5.25 3.15.75-1.23-4.5-2.67z",
        "movie": "M18 4l2 4h-3l-2-4h-2l2 4h-3l-2-4H8l2 4H7L5 4H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V4h-4z",
        "tv": "M21 3H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h5v2h8v-2h5c1.1 0 2-.9 2-2V5c0-1.1-.9-2-2-2zm0 14H3V5h18v12z",
        "episodes": "M3 6h12v2H3V6zm0 5h12v2H3v-2zm0 5h8v2H3v-2zm14-5 5 3.5-5 3.5v-7z",
        "activeDays": "M19 4h-1V2h-2v2H8V2H6v2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2V6c0-1.1-.9-2-2-2zm0 16H5V9h14v11zm-8.4-2.4-3.2-3.2 1.4-1.4 1.8 1.8 4.6-4.6 1.4 1.4-6 6z"
    };
    var style = node('style');
    // Match the settings Overview cards, with scoped fallbacks outside the dashboard.
    style.textContent = `
.jfh-rewind{max-width:1100px;margin:0 auto;padding:2em;color:inherit;--rewind-primary:var(--color-primary,#00a4dc);--rewind-border:var(--color-border,rgba(255,255,255,.1))}
.jfh-rewind-heading{display:flex;flex-wrap:wrap;align-items:baseline;gap:.3em .5em;font-size:2em;margin:0 0 1em}
.jfh-rewind-period{font-size:.5em;font-weight:400;opacity:.6}
.jfh-rewind-stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:1.2em;margin-bottom:1.2em}
.jfh-rewind-stat{min-width:0;background:rgba(255,255,255,.05);border:1px solid var(--rewind-border);border-radius:var(--radius-lg,8px);padding:var(--spacing-lg,1.2em) var(--spacing-xl,1.5em)}
.jfh-rewind-stat h3{display:flex;align-items:center;gap:.3em;margin:0 0 .3em;font-size:.85em;text-transform:uppercase;letter-spacing:.05em;opacity:.7}
.jfh-rewind-stat-icon{width:1.15em;height:1.15em;flex-shrink:0}.jfh-rewind-stat h3 span{min-width:0;overflow-wrap:anywhere}
.jfh-rewind-stat strong{font-size:1.8em;font-weight:700;color:var(--rewind-primary)}
.jfh-rewind-notice{padding:1em;border-inline-start:4px solid var(--rewind-primary)}
.jfh-rewind section{min-width:0;background:rgba(255,255,255,.03);border:1px solid var(--rewind-border);border-radius:var(--radius-lg,8px);padding:.8em 1.1em 1.1em}
.jfh-rewind > section{margin-top:1.2em}
.jfh-rewind section > h3{font-size:1.1em;font-weight:600;margin:0 0 .8em;padding-bottom:.5em;border-bottom:2px solid var(--rewind-border)}
.jfh-rewind-chart-unit{font-size:.85em;font-weight:400;opacity:.6}
.jfh-rewind-chart-scroll{overflow-x:auto;scrollbar-width:thin}.jfh-rewind-chart{display:flex;gap:clamp(4px,1vw,16px);min-width:0;padding:.5em 0}
.jfh-rewind-month{flex:1;min-width:0;display:flex;flex-direction:column;align-items:center;gap:.5em}
.jfh-rewind-bar-value{font-size:.75em;opacity:.7}.jfh-rewind-bar-track{height:180px;display:flex;align-items:flex-end;width:100%;border-bottom:1px solid var(--rewind-border);background:repeating-linear-gradient(to top,transparent 0,transparent calc(25% - 1px),var(--rewind-border) calc(25% - 1px),var(--rewind-border) 25%)}
.jfh-rewind-bar{position:relative;overflow:hidden;width:100%;background:var(--rewind-primary);border-radius:4px 4px 0 0}.jfh-rewind-bar-label{max-width:100%;font-size:.7em;text-align:center;overflow-wrap:anywhere;opacity:.7}
.jfh-rewind-pies{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.2em;margin-top:1.2em}
.jfh-rewind-estimated-bar{position:absolute;inset:0 0 auto;background:#e67e22}.jfh-rewind-time-legend{display:flex;align-items:center;gap:.5em;font-size:.8em;opacity:.8}.jfh-rewind-measured-swatch{background:var(--rewind-primary)}.jfh-rewind-estimated-swatch{background:#e67e22}
.jfh-rewind-secondary{font-size:.85em;opacity:.65;line-height:1.5;overflow-wrap:anywhere}
.jfh-rewind-pie-body{display:flex;align-items:center;justify-content:center;flex-wrap:wrap;gap:1em;margin-top:1em}
.jfh-rewind-pie{width:180px;max-width:100%;height:auto;flex-shrink:0}.jfh-rewind-pie-legend{list-style:none;padding:0;margin:0;flex:1 1 180px;min-width:0}
.jfh-rewind-pie-legend li{display:flex;align-items:center;gap:.5em;margin:.6em 0;flex-wrap:wrap}.jfh-rewind-swatch{width:.7em;height:.7em;border-radius:2px;flex-shrink:0}.jfh-rewind-legend-label{flex:1;min-width:0;overflow-wrap:anywhere}.jfh-rewind-pie-legend strong{font-size:.85em;font-weight:400;white-space:nowrap}
.jfh-rewind-genre-period{display:block;font-size:.85em;opacity:.7;margin-bottom:.4em}.jfh-rewind-select{font:inherit;color:inherit;background:rgba(127,127,127,.1);border:1px solid var(--rewind-border);border-radius:4px;padding:.4em;max-width:100%;color-scheme:light dark}.jfh-rewind-select option{background:Canvas;color:CanvasText}
.jfh-rewind-insight-grid{display:grid;grid-template-columns:minmax(0,2fr) minmax(0,1fr);gap:1.2em;margin-top:1.2em}.jfh-rewind-pattern-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:1.2em}.jfh-rewind-pattern-grid h4{font-size:.9em;margin:.3em 0}.jfh-rewind-favorite{font-size:1.05em;font-weight:600;color:var(--rewind-primary);overflow-wrap:anywhere}
.jfh-rewind-distribution{list-style:none;padding:0;margin:0}.jfh-rewind-distribution li{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:.4em;margin:.8em 0;font-size:.8em}.jfh-rewind-distribution li span{overflow-wrap:anywhere}.jfh-rewind-distribution strong{font-weight:400;white-space:nowrap;opacity:.7}.jfh-rewind-distribution-track{grid-column:1/-1;background:rgba(127,127,127,.1);border-radius:3px;overflow:hidden;height:5px}.jfh-rewind-distribution-track div{height:100%;background:var(--rewind-primary)}
.jfh-rewind-streak-length{font-size:2em;font-weight:700;color:var(--rewind-primary);margin:.8em 0 .3em;overflow-wrap:anywhere}
.jfh-rewind-comparison-table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:.85em}.jfh-rewind-comparison-table th,.jfh-rewind-comparison-table td{text-align:end;padding:.6em .3em;border-bottom:1px solid var(--rewind-border);overflow-wrap:anywhere}.jfh-rewind-comparison-table th:first-child{text-align:start}.jfh-rewind-comparison-table thead{opacity:.7}
@media(max-width:800px){.jfh-rewind-pies,.jfh-rewind-insight-grid{grid-template-columns:1fr}}@media(max-width:500px){.jfh-rewind-pattern-grid{grid-template-columns:1fr}}
@media(prefers-color-scheme:light){.jfh-rewind{--rewind-primary:var(--color-primary,#00749c);--rewind-border:var(--color-border,rgba(0,0,0,.15))}}
@media(max-width:700px){.jfh-rewind{padding:1em}.jfh-rewind-heading{font-size:1.6em}.jfh-rewind-period{font-size:.6em}}
`;
    document.head.append(style);
    window.JellyfinHelperRewind = { dispose: function () { dispose(); style.remove(); } };
    document.addEventListener('jfh:session-changing', changing);
    document.addEventListener('jfh:session-changed', refresh);
    document.addEventListener('jfh:navigation', probe);
    document.addEventListener('visibilitychange', visible);
    refresh();
})();
