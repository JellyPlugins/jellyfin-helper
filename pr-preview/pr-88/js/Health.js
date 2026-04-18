// --- Health Tab ---

// collectFlatPaths is now in shared.js

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
    for (var i = 0; i < data.Libraries.length; i++) {
        totalNoSubs += data.Libraries[i].VideosWithoutSubtitles || 0;
        totalNoImages += data.Libraries[i].VideosWithoutImages || 0;
        totalNoNfo += data.Libraries[i].VideosWithoutNfo || 0;
        totalOrphaned += data.Libraries[i].OrphanedMetadataDirectories || 0;
    }

    var html = '<div class="health-grid">';

    html += '<div class="health-item health-clickable" data-health-type="noSubs" role="button" tabindex="0"><div class="health-value '
        + (totalNoSubs > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoSubs
        + '</div>';
    html += '<div class="health-label">' + T('noSubtitles',
        'Videos without subtitles') + '</div></div>';

    html += '<div class="health-item health-clickable" data-health-type="noImages" role="button" tabindex="0"><div class="health-value '
        + (totalNoImages > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoImages
        + '</div>';
    html += '<div class="health-label">' + T('noImages', 'Videos without images')
        + '</div></div>';

    html += '<div class="health-item health-clickable" data-health-type="noNfo" role="button" tabindex="0"><div class="health-value '
        + (totalNoNfo > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoNfo
        + '</div>';
    html += '<div class="health-label">' + T('noNfo', 'Videos without NFO')
        + '</div></div>';

    html += '<div class="health-item health-clickable" data-health-type="orphaned" role="button" tabindex="0"><div class="health-value '
        + (totalOrphaned > 0 ? 'health-bad' : 'health-ok') + '">' + totalOrphaned
        + '</div>';
    html += '<div class="health-label">' + T('orphanedDirs',
        'Orphaned metadata dirs') + '</div></div>';

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
            var type = item.getAttribute('data-health-type');
            var mapping = HEALTH_PATH_MAP[type];
            if (!mapping || !_lastScanResult) {
                return '';
            }
            var result = collectHealthPaths(_lastScanResult, mapping.prop);
            return renderFileTree(result, T(mapping.titleKey, mapping.titleFallback));
        }
    });
}

function loadTrashHealthSection() {
    // First check if trash is enabled
    apiGet('JellyfinHelper/Configuration', function (cfg) {
        if (!cfg.UseTrash) {
            return;
        }
        // Load trash contents
        apiGet('JellyfinHelper/Trash/Contents', function (data) {
            var container = document.getElementById('healthContent');
            if (!container) {
                return;
            }

            var totalItems = 0;
            var totalSize = 0;
            for (var i = 0; i < data.Libraries.length; i++) {
                var lib = data.Libraries[i];
                totalItems += lib.Items.length;
                for (var j = 0; j < lib.Items.length; j++) {
                    totalSize += lib.Items[j].Size || 0;
                }
            }

            var html = '<div class="section-divider" style="margin:1.5em 0;"></div>';
            html += '<div class="section-title">🗑️ ' + T('trashContents',
                'Trash Contents') + '</div>';

            // Summary card
            html += '<div class="health-grid">';
            html += '<div class="health-item"><div class="health-value ' + (totalItems
            > 0 ? 'health-warn' : 'health-ok') + '">' + totalItems + '</div>';
            html += '<div class="health-label">' + T('trashItems', 'Items in Trash')
                + '</div></div>';
            html += '<div class="health-item"><div class="health-value" style="font-size:1.2em;">'
                + formatBytes(totalSize) + '</div>';
            html += '<div class="health-label">' + T('trashTotalSize', 'Trash Size')
                + '</div></div>';
            html += '<div class="health-item"><div class="health-value" style="font-size:1.2em;">'
                + data.RetentionDays + 'd</div>';
            html += '<div class="health-label">' + T('trashRetentionDays',
                'Retention') + '</div></div>';
            html += '</div>';

            if (data.Libraries.length > 0) {
                html += '<div id="trashDetailContainer">';
                for (var li = 0; li < data.Libraries.length; li++) {
                    var trashLib = data.Libraries[li];
                    html += '<div style="margin-top:1em;">';
                    html += '<h4 style="margin:0 0 0.3em 0;opacity:0.8;">📁 ' + escHtml(
                            trashLib.LibraryName)
                        + ' <span style="opacity:0.5;font-weight:400;">('
                        + trashLib.Items.length + ' ' + T('items', 'items')
                        + ')</span></h4>';
                    html += '<div class="health-detail-list"><ul>';
                    for (var ti = 0; ti < trashLib.Items.length; ti++) {
                        var item = trashLib.Items[ti];
                        var purgeInfo = item.PurgeDate ? ' — ' + T('purgesOn', 'purges')
                            + ' ' + new Date(item.PurgeDate).toLocaleDateString() : '';
                        html += '<li>' + escHtml(item.OriginalName || item.Name)
                            + ' <span style="opacity:0.5;">(' + formatBytes(item.Size)
                            + purgeInfo + ')</span></li>';
                    }
                    html += '</ul></div></div>';
                }
                html += '</div>';
            } else {
                html += '<p style="opacity:0.5;padding:0.5em;">' + T('trashEmpty',
                    'Trash is empty.') + '</p>';
            }

            container.insertAdjacentHTML('beforeend', html);
        }, function () {
            console.log(
                'Jellyfin Helper: Could not load trash contents for health tab');
        });
    }, function () { /* Config load failed — silently skip */
    });
}

function fillHealthData(data) {
    var healthHtml = '<div class="section-title">' + T('healthChecks',
        'Library Health Checks') + '</div>';
    healthHtml += renderHealthChecks(data);

    var healthContainer = document.getElementById('healthContent');
    if (healthContainer) {
        healthContainer.innerHTML = healthHtml;
        attachHealthClickHandlers();
        loadTrashHealthSection();
    }
}
