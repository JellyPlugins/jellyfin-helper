// --- Health Tab ---

    // Store last scan data for health detail clicks
    var _lastScanData = null;

    // Collect health paths split by media type (movies vs tvShows)
    // Health checks only apply to video libraries, so music is always empty.
    function collectHealthPaths(data, prop) {
        var moviePaths = [];
        var tvPaths = [];
        var otherPaths = [];

        if (data.Movies) {
            for (var m = 0; m < data.Movies.length; m++) {
                var libPaths = data.Movies[m][prop];
                if (libPaths) {
                    for (var i = 0; i < libPaths.length; i++) {
                        moviePaths.push(libPaths[i]);
                    }
                }
            }
        }

        if (data.TvShows) {
            for (var t = 0; t < data.TvShows.length; t++) {
                var tvLibPaths = data.TvShows[t][prop];
                if (tvLibPaths) {
                    for (var j = 0; j < tvLibPaths.length; j++) {
                        tvPaths.push(tvLibPaths[j]);
                    }
                }
            }
        }

        if (data.Other) {
            for (var o = 0; o < data.Other.length; o++) {
                var otherLibPaths = data.Other[o][prop];
                if (otherLibPaths) {
                    for (var k = 0; k < otherLibPaths.length; k++) {
                        otherPaths.push(otherLibPaths[k]);
                    }
                }
            }
        }

        return {
            movies: moviePaths,
            tvShows: tvPaths,
            other: otherPaths,
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
        _lastScanData = data;
        var totalNoSubs = 0, totalNoImages = 0, totalNoNfo = 0, totalOrphaned = 0;
        for (var i = 0; i < data.Libraries.length; i++) {
            totalNoSubs += data.Libraries[i].VideosWithoutSubtitles || 0;
            totalNoImages += data.Libraries[i].VideosWithoutImages || 0;
            totalNoNfo += data.Libraries[i].VideosWithoutNfo || 0;
            totalOrphaned += data.Libraries[i].OrphanedMetadataDirectories || 0;
        }

        var html = '<div class="health-grid">';

        html += '<div class="health-item health-clickable" data-health-type="noSubs" role="button" tabindex="0"><div class="health-value ' + (totalNoSubs > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoSubs + '</div>';
        html += '<div class="health-label">' + T('noSubtitles', 'Videos without subtitles') + '</div></div>';

        html += '<div class="health-item health-clickable" data-health-type="noImages" role="button" tabindex="0"><div class="health-value ' + (totalNoImages > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoImages + '</div>';
        html += '<div class="health-label">' + T('noImages', 'Videos without images') + '</div></div>';

        html += '<div class="health-item health-clickable" data-health-type="noNfo" role="button" tabindex="0"><div class="health-value ' + (totalNoNfo > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoNfo + '</div>';
        html += '<div class="health-label">' + T('noNfo', 'Videos without NFO') + '</div></div>';

        html += '<div class="health-item health-clickable" data-health-type="orphaned" role="button" tabindex="0"><div class="health-value ' + (totalOrphaned > 0 ? 'health-bad' : 'health-ok') + '">' + totalOrphaned + '</div>';
        html += '<div class="health-label">' + T('orphanedDirs', 'Orphaned metadata dirs') + '</div></div>';

        html += '</div>';
        html += '<div class="file-tree-panel" id="healthDetailPanel"></div>';
        return html;
    }

    // Map health types to their path property names and titles
    var HEALTH_PATH_MAP = {
        'noSubs': { prop: 'VideosWithoutSubtitlesPaths', titleKey: 'noSubtitles', titleFallback: 'Videos without subtitles' },
        'noImages': { prop: 'VideosWithoutImagesPaths', titleKey: 'noImages', titleFallback: 'Videos without images' },
        'noNfo': { prop: 'VideosWithoutNfoPaths', titleKey: 'noNfo', titleFallback: 'Videos without NFO' },
        'orphaned': { prop: 'OrphanedMetadataDirectoriesPaths', titleKey: 'orphanedDirs', titleFallback: 'Orphaned metadata dirs' }
    };

    function attachHealthClickHandlers() {
        var items = document.querySelectorAll('.health-clickable');
        for (var i = 0; i < items.length; i++) {
            items[i].addEventListener('keydown', function (e) {
                if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); this.click(); }
            });
            items[i].addEventListener('click', function () {
                var type = this.getAttribute('data-health-type');
                var panel = document.getElementById('healthDetailPanel');
                if (!panel || !_lastScanData) return;

                // Toggle: if same type is already shown, hide it
                if (this.classList.contains('health-active')) {
                    panel.innerHTML = '';
                    panel.classList.remove('file-tree-panel-visible');
                    // Remove active state from all items
                    var toggleItems = document.querySelectorAll('.health-clickable');
                    for (var j = 0; j < toggleItems.length; j++) toggleItems[j].classList.remove('health-active');
                    return;
                }

                // Remove active state from all items
                var allItems = document.querySelectorAll('.health-clickable');
                for (var k = 0; k < allItems.length; k++) {
                    allItems[k].classList.remove('health-active');
                }

                this.classList.add('health-active');

                var mapping = HEALTH_PATH_MAP[type];
                if (!mapping) return;

                var result = collectHealthPaths(_lastScanData, mapping.prop);
                var title = T(mapping.titleKey, mapping.titleFallback);
                panel.innerHTML = renderFileTree(result, title);
                panel.classList.add('file-tree-panel-visible');

                // Smooth scroll the panel into view
                setTimeout(function () { panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' }); }, 50);
            });
        }
    }

    function loadTrashHealthSection() {
        var apiClient = ApiClient;
        // First check if trash is enabled
        apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Configuration'), dataType: 'json' }).then(function (cfg) {
            if (!cfg.UseTrash) {
                // Trash not enabled — don't show section
                return;
            }
            // Load trash contents
            apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyfinHelper/Trash/Contents'), dataType: 'json' }).then(function (data) {
                var container = document.getElementById('healthContent');
                if (!container) return;

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
                html += '<div class="section-title">🗑️ ' + T('trashContents', 'Trash Contents') + '</div>';

                // Summary card
                html += '<div class="health-grid">';
                html += '<div class="health-item"><div class="health-value ' + (totalItems > 0 ? 'health-warn' : 'health-ok') + '">' + totalItems + '</div>';
                html += '<div class="health-label">' + T('trashItems', 'Items in Trash') + '</div></div>';
                html += '<div class="health-item"><div class="health-value" style="font-size:1.2em;">' + formatBytes(totalSize) + '</div>';
                html += '<div class="health-label">' + T('trashTotalSize', 'Trash Size') + '</div></div>';
                html += '<div class="health-item"><div class="health-value" style="font-size:1.2em;">' + data.RetentionDays + 'd</div>';
                html += '<div class="health-label">' + T('trashRetentionDays', 'Retention') + '</div></div>';
                html += '</div>';

                if (data.Libraries.length > 0) {
                    html += '<div id="trashDetailContainer">';
                    for (var li = 0; li < data.Libraries.length; li++) {
                        var trashLib = data.Libraries[li];
                        html += '<div style="margin-top:1em;">';
                        html += '<h4 style="margin:0 0 0.3em 0;opacity:0.8;">📁 ' + escHtml(trashLib.LibraryName) + ' <span style="opacity:0.5;font-weight:400;">(' + trashLib.Items.length + ' ' + T('items', 'items') + ')</span></h4>';
                        html += '<div class="health-detail-list"><ul>';
                        for (var ti = 0; ti < trashLib.Items.length; ti++) {
                            var item = trashLib.Items[ti];
                            var purgeInfo = item.PurgeDate ? ' — ' + T('purgesOn', 'purges') + ' ' + new Date(item.PurgeDate).toLocaleDateString() : '';
                            html += '<li>' + escHtml(item.OriginalName || item.Name) + ' <span style="opacity:0.5;">(' + formatBytes(item.Size) + purgeInfo + ')</span></li>';
                        }
                        html += '</ul></div></div>';
                    }
                    html += '</div>';
                } else {
                    html += '<p style="opacity:0.5;padding:0.5em;">' + T('trashEmpty', 'Trash is empty.') + '</p>';
                }

                container.insertAdjacentHTML('beforeend', html);
            }, function () {
                // Silently ignore errors loading trash contents
                console.log('Jellyfin Helper: Could not load trash contents for health tab');
            });
        }, function () {
            // Config load failed — silently skip
        });
    }

    function fillHealthData(data) {
        var healthHtml = '<div class="section-title">' + T('healthChecks', 'Library Health Checks') + '</div>';
        healthHtml += renderHealthChecks(data);

        var healthContainer = document.getElementById('healthContent');
        if (healthContainer) {
            healthContainer.innerHTML = healthHtml;
            attachHealthClickHandlers();
            loadTrashHealthSection();
        }
    }
