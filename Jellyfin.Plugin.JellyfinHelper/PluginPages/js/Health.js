// --- Health Tab ---

    // Store last scan data for health detail clicks
    var _lastScanData = null;

    function collectHealthPaths(data, prop) {
        var paths = [];
        for (var i = 0; i < data.Libraries.length; i++) {
            var libPaths = data.Libraries[i][prop];
            if (libPaths) {
                for (var j = 0; j < libPaths.length; j++) {
                    paths.push(libPaths[j]);
                }
            }
        }
        return paths;
    }

    function renderHealthDetailList(paths) {
        if (!paths || paths.length === 0) {
            return '<div class="health-detail-list"><p style="opacity:0.5;padding:0.5em;">' + T('noEntries', 'No entries found.') + '</p></div>';
        }
        var html = '<div class="health-detail-list"><div class="health-detail-header">' + paths.length + ' ' + (paths.length === 1 ? T('entry', 'entry') : T('entries', 'entries')) + '</div><ul>';
        for (var i = 0; i < paths.length; i++) {
            html += '<li>' + escHtml(paths[i]) + '</li>';
        }
        html += '</ul></div>';
        return html;
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

        html += '<div class="health-item health-clickable" data-health-type="noSubs"><div class="health-value ' + (totalNoSubs > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoSubs + '</div>';
        html += '<div class="health-label">' + T('noSubtitles', 'Videos without subtitles') + '</div></div>';

        html += '<div class="health-item health-clickable" data-health-type="noImages"><div class="health-value ' + (totalNoImages > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoImages + '</div>';
        html += '<div class="health-label">' + T('noImages', 'Videos without images') + '</div></div>';

        html += '<div class="health-item health-clickable" data-health-type="noNfo"><div class="health-value ' + (totalNoNfo > 0 ? 'health-warn' : 'health-ok') + '">' + totalNoNfo + '</div>';
        html += '<div class="health-label">' + T('noNfo', 'Videos without NFO') + '</div></div>';

        html += '<div class="health-item health-clickable" data-health-type="orphaned"><div class="health-value ' + (totalOrphaned > 0 ? 'health-bad' : 'health-ok') + '">' + totalOrphaned + '</div>';
        html += '<div class="health-label">' + T('orphanedDirs', 'Orphaned metadata dirs') + '</div></div>';

        html += '</div>';
        html += '<div id="healthDetailContainer"></div>';
        return html;
    }

    function attachHealthClickHandlers() {
        var items = document.querySelectorAll('.health-clickable');
        for (var i = 0; i < items.length; i++) {
            items[i].addEventListener('click', function () {
                var type = this.getAttribute('data-health-type');
                var container = document.getElementById('healthDetailContainer');
                if (!container || !_lastScanData) return;

                // Toggle: if same type is already shown, hide it
                if (container.getAttribute('data-active-type') === type) {
                    container.innerHTML = '';
                    container.removeAttribute('data-active-type');
                    // Remove active state from all items
                    var toggleItems = document.querySelectorAll('.health-clickable');
                    for (var j = 0; j < toggleItems.length; j++) toggleItems[j].classList.remove('health-active');
                    return;
                }

                var pathProp;
                var title;
                if (type === 'noSubs') {
                    pathProp = 'VideosWithoutSubtitlesPaths';
                    title = T('noSubtitles', 'Videos without subtitles');
                } else if (type === 'noImages') {
                    pathProp = 'VideosWithoutImagesPaths';
                    title = T('noImages', 'Videos without images');
                } else if (type === 'noNfo') {
                    pathProp = 'VideosWithoutNfoPaths';
                    title = T('noNfo', 'Videos without NFO');
                } else if (type === 'orphaned') {
                    pathProp = 'OrphanedMetadataDirectoriesPaths';
                    title = T('orphanedDirs', 'Orphaned metadata dirs');
                }

                var paths = collectHealthPaths(_lastScanData, pathProp);

                var html = '<div class="section-title" style="margin-top:1.5em;">' + escHtml(title) + '</div>';
                html += renderHealthDetailList(paths);
                container.innerHTML = html;
                container.setAttribute('data-active-type', type);

                // Update active state styling
                var allItems = document.querySelectorAll('.health-clickable');
                for (var k = 0; k < allItems.length; k++) {
                    allItems[k].classList.toggle('health-active', allItems[k].getAttribute('data-health-type') === type);
                }
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