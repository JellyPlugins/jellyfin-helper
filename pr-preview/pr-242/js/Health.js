'use strict';

var _lastScanResult = null;

// collectFlatPaths is now in Shared.js

function collectHealthPaths(data, prop) {
    return {
        movies: collectFlatPaths(data.Movies, prop),
        tvShows: collectFlatPaths(data.TvShows, prop),
        other: collectFlatPaths(data.Other, prop),
        music: [],
        rootPaths: {
            movies: data.MovieRootPaths || [],
            tvShows: data.TvShowRootPaths || [],
            other: data.OtherRootPaths || [],
            music: []
        }
    };
}

function renderHealthChecks(data) {
    _lastScanResult = data;
    var totalNoSubs = 0, totalNoImages = 0, totalNoNfo = 0, totalOrphaned = 0;
    for (const lib of data.Libraries) {
        totalNoSubs += lib.VideosWithoutSubtitles || 0;
        totalNoImages += lib.VideosWithoutImages || 0;
        totalNoNfo += lib.VideosWithoutNfo || 0;
        totalOrphaned += lib.OrphanedMetadataDirectories || 0;
    }

    var html = '<div class="health-grid">';

    html += '<div class="health-item health-clickable" data-health-type="noSubs" role="button" tabindex="0"><div class="health-value '
        + (totalNoSubs > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoSubs
        + '</div>';
    html += '<div class="health-label">' + escHtml(T('noSubtitles',
        'Videos without subtitles')) + '</div></div>';

    html += '<div class="health-item health-clickable" data-health-type="noImages" role="button" tabindex="0"><div class="health-value '
        + (totalNoImages > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoImages
        + '</div>';
    html += '<div class="health-label">' + escHtml(T('noImages', 'Videos without images'))
        + '</div></div>';

    html += '<div class="health-item health-clickable" data-health-type="noNfo" role="button" tabindex="0"><div class="health-value '
        + (totalNoNfo > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoNfo
        + '</div>';
    html += '<div class="health-label">' + escHtml(T('noNfo', 'Videos without NFO'))
        + '</div></div>';

    html += '<div class="health-item health-clickable" data-health-type="orphaned" role="button" tabindex="0"><div class="health-value '
        + (totalOrphaned > 0 ? 'health-bad' : 'health-ok') + '">' + totalOrphaned
        + '</div>';
    html += '<div class="health-label">' + escHtml(T('orphanedDirs',
        'Orphaned metadata dirs')) + '</div></div>';

    html += '</div>';
    html += '<div class="file-tree-panel" id="healthDetailPanel"></div>';
    return html;
}

// Map health types to their path property names and titles
var HEALTH_PATH_MAP = {
    'noSubs': {
        prop: 'VideosWithoutSubtitlesPaths',
        titleKey: 'noSubtitles',
        titleFallback: 'Videos without subtitles'
    },
    'noImages': {
        prop: 'VideosWithoutImagesPaths',
        titleKey: 'noImages',
        titleFallback: 'Videos without images'
    },
    'noNfo': {
        prop: 'VideosWithoutNfoPaths',
        titleKey: 'noNfo',
        titleFallback: 'Videos without NFO'
    },
    'orphaned': {
        prop: 'OrphanedMetadataDirectoriesPaths',
        titleKey: 'orphanedDirs',
        titleFallback: 'Orphaned metadata dirs'
    }
};

function attachHealthClickHandlers() {
    attachTogglePanelHandlers({
        itemSelector: '.health-clickable',
        activeClass: 'health-active',
        typeAttr: 'data-health-type',
        getPanelId: function () {
            return 'healthDetailPanel';
        },
        renderContent: function (item) {
            var type = item.dataset.healthType;
            var mapping = HEALTH_PATH_MAP[type];
            if (!mapping || !_lastScanResult) {
                return '';
            }
            var result = collectHealthPaths(_lastScanResult, mapping.prop);
            return renderFileTree(result, T(mapping.titleKey, mapping.titleFallback));
        }
    });
}

var _trashHealthRequestId = 0;

function loadTrashHealthSection() {
    var requestId = ++_trashHealthRequestId;
    // First check if trash is enabled
    apiGet('JellyfinHelper/Configuration', function (cfg) {
        if (!cfg.UseTrash) {
            return;
        }
        // Load trash contents
        apiGet('JellyfinHelper/Trash/Contents', function (data) {
            // Guard against stale/overlapping responses
            if (requestId !== _trashHealthRequestId) {
                return;
            }
            var container = document.getElementById('healthContent');
            if (!container) {
                return;
            }
            // Remove the entire previously rendered trash section
            var existingTrash = container.querySelector('#trashHealthSection');
            if (existingTrash) {
                existingTrash.remove();
            }

            var totalItems = 0;
            var totalSize = 0;
            for (const lib of data.Libraries) {
                totalItems += lib.Items.length;
                for (const trashItem of lib.Items) {
                    totalSize += trashItem.Size || 0;
                }
            }

            var html = '<div class="health-card" id="trashHealthSection">';
            html += '<div class="section-title">' + mi('delete') + escHtml(T('trashContents',
                'Trash Contents')) + '</div>';

            // Summary card
            html += '<div class="health-grid">';
            html += '<div class="health-item"><div class="health-value ' + (totalItems
            > 0 ? 'health-warn' : 'health-ok') + '">' + totalItems + '</div>';
            html += '<div class="health-label">' + escHtml(T('trashItems', 'Items in Trash'))
                + '</div></div>';
            html += '<div class="health-item"><div class="health-value" style="font-size:1.2em;">'
                + formatBytes(totalSize) + '</div>';
            html += '<div class="health-label">' + escHtml(T('trashTotalSize', 'Trash Size'))
                + '</div></div>';
            html += '<div class="health-item"><div class="health-value" style="font-size:1.2em;">'
                + data.RetentionDays + 'd</div>';
            html += '<div class="health-label">' + escHtml(T('trashRetentionDays',
                'Retention')) + '</div></div>';
            html += '</div>';

            if (data.Libraries.length > 0) {
                html += '<div id="trashDetailContainer">';
                for (const trashLib of data.Libraries) {
                    html += '<div class="trash-library-block">';
                    html += '<h4 class="trash-library-heading">' + mi('folder') + escHtml(
                            trashLib.LibraryName)
                        + ' <span class="trash-library-count">('
                        + trashLib.Items.length + ' ' + escHtml(T('items', 'items'))
                        + ')</span></h4>';
                    html += '<div class="health-detail-list"><ul>';
                    for (const item of trashLib.Items) {
                        var purgeInfo = item.PurgeDate ? ' - ' + escHtml(T('purgesOn', 'purges'))
                            + ' ' + new Date(item.PurgeDate).toLocaleDateString() : '';
                        html += '<li>' + escHtml(item.OriginalName || item.Name)
                            + ' <span class="trash-item-meta">(' + formatBytes(item.Size)
                            + purgeInfo + ')</span></li>';
                    }
                    html += '</ul></div></div>';
                }
                html += '</div>';
            } else {
                html += '<p class="trash-empty-hint">' + escHtml(T('trashEmpty',
                    'Trash is empty.')) + '</p>';
            }

            html += '</div>';
            container.insertAdjacentHTML('beforeend', html);
        }, function () {
            console.warn(
                'Jellyfin Helper: Could not load trash contents for health tab');
        });
    }, function () { /* Config load failed - silently skip */
    });
}

function fillHealthData(data) {
    var healthHtml = '<div class="health-card" id="healthChecksCard">';
    healthHtml += '<div class="section-title">' + T('healthChecks',
        'Library Health Checks') + '</div>';
    healthHtml += renderHealthChecks(data);
    healthHtml += '</div>';

    var healthContainer = document.getElementById('healthContent');
    if (healthContainer) {
        healthContainer.innerHTML = healthHtml;
        attachHealthClickHandlers();
        loadTrashHealthSection();
    }
}
