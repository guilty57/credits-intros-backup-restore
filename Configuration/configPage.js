define([], function () {
    'use strict';

    var pluginId = "7602a9a8-788d-4007-84c1-a08a3671e1aa";

    function setSectionEnabled(view, sectionSelector, enabled) {
        var section = view.querySelector(sectionSelector);
        section.style.display = enabled ? '' : 'none';
        section.querySelectorAll('input, button').forEach(function (el) {
            el.disabled = !enabled;
        });
    }

    // Exactly one of the two top-level modes is ever checked. Clicking one
    // unchecks the other and shows/dims the matching options section.
    function setMode(view, mode) {
        var isCustom = mode === 'Custom';
        view.querySelector('#chkModeCustom').checked = isCustom;
        view.querySelector('#chkModeAutomatic').checked = !isCustom;
        setSectionEnabled(view, '#customOptions', isCustom);
        setSectionEnabled(view, '#automaticOptions', !isCustom);
    }

    // Three-way mutually exclusive group (Automatic Backup: Json/Nfo/Both,
    // or Automatic Restore: Nfo/Json/Best) - checking one unchecks the
    // other two in the same group.
    function setExclusive(view, ids, checkedId) {
        ids.forEach(function (id) {
            view.querySelector('#' + id).checked = (id === checkedId);
        });
    }

    function getCheckedId(view, ids, fallback) {
        for (var i = 0; i < ids.length; i++) {
            if (view.querySelector('#' + ids[i]).checked) {
                return ids[i];
            }
        }
        return fallback;
    }

    function setAutomaticJsonSafetyPathVisible(view, visible) {
        view.querySelector('#automaticJsonSafetyPathContainer').style.display = visible ? '' : 'none';
    }

    var restoreSourceIds = ['chkAutoRestoreNfo', 'chkAutoRestoreJson', 'chkAutoRestoreBest'];

    function loadConfig(view) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#txtJsonBackupPath').value = config.JsonBackupPath || '';
            view.querySelector('#txtNfoBackupPath').value = config.NfoBackupPath || '';

            view.querySelector('#chkCustomBackupJson').checked = config.CustomBackupJson !== false;
            view.querySelector('#chkCustomBackupNfo').checked = config.CustomBackupNfo || false;
            view.querySelector('#chkCustomRestoreJson').checked = config.CustomRestoreJson !== false;
            view.querySelector('#chkCustomRestoreNfo').checked = config.CustomRestoreNfo || false;

            view.querySelector('#chkAutoBackupJson').checked = config.AutomaticBackupJson !== false;
            view.querySelector('#chkAutoBackupNfo').checked = config.AutomaticBackupNfo || false;

            var safetyCopyOn = config.AutomaticJsonSafetyCopy || false;
            view.querySelector('#chkAutoJsonSafetyCopy').checked = safetyCopyOn;
            view.querySelector('#txtAutomaticJsonSafetyPath').value = config.JsonBackupPath || '';
            setAutomaticJsonSafetyPathVisible(view, safetyCopyOn);

            var source = config.AutomaticRestoreSource || 'Best';
            setExclusive(view, restoreSourceIds, source === 'Nfo' ? 'chkAutoRestoreNfo' : (source === 'Json' ? 'chkAutoRestoreJson' : 'chkAutoRestoreBest'));

            setMode(view, config.BackupMode === 'Custom' ? 'Custom' : 'AutomaticMediaFolder');

            Dashboard.hideLoadingMsg();
        });
    }

    function onSubmit(view, e) {
        e.preventDefault();

        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            var isCustomForPath = view.querySelector('#chkModeCustom').checked;
            config.JsonBackupPath = isCustomForPath
                ? view.querySelector('#txtJsonBackupPath').value
                : view.querySelector('#txtAutomaticJsonSafetyPath').value;
            config.NfoBackupPath = view.querySelector('#txtNfoBackupPath').value;

            config.BackupMode = view.querySelector('#chkModeCustom').checked ? 'Custom' : 'AutomaticMediaFolder';

            config.CustomBackupJson = view.querySelector('#chkCustomBackupJson').checked;
            config.CustomBackupNfo = view.querySelector('#chkCustomBackupNfo').checked;
            config.CustomRestoreJson = view.querySelector('#chkCustomRestoreJson').checked;
            config.CustomRestoreNfo = view.querySelector('#chkCustomRestoreNfo').checked;

            config.AutomaticBackupJson = view.querySelector('#chkAutoBackupJson').checked;
            config.AutomaticBackupNfo = view.querySelector('#chkAutoBackupNfo').checked;
            config.AutomaticJsonSafetyCopy = view.querySelector('#chkAutoJsonSafetyCopy').checked;

            var checkedRestoreId = getCheckedId(view, restoreSourceIds, 'chkAutoRestoreBest');
            config.AutomaticRestoreSource = checkedRestoreId === 'chkAutoRestoreNfo' ? 'Nfo' : (checkedRestoreId === 'chkAutoRestoreJson' ? 'Json' : 'Best');

            // No standalone on/off switch for these anymore - whether the
            // background watchers are active is derived from whether any
            // backup/restore source is actually selected. In Automatic mode
            // one of the three exclusive options is always selected, so
            // this is effectively always true there; in Custom mode it's
            // only true if at least one of JSON/NFO is checked.
            var isCustom = view.querySelector('#chkModeCustom').checked;
            config.AutoBackupOnManualEdit = isCustom
                ? (config.CustomBackupJson || config.CustomBackupNfo)
                : true;
            config.EnableAutoRestore = isCustom
                ? (config.CustomRestoreJson || config.CustomRestoreNfo)
                : true;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });

        return false;
    }

    function onBrowseClick(view, inputSelector) {
        require(['directorybrowser'], function (directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                path: view.querySelector(inputSelector).value,
                network: false,
                includeFiles: false,
                includeDirectories: true,
                callback: function (path) {
                    if (path) {
                        view.querySelector(inputSelector).value = path;
                    }
                    picker.close();
                }
            });
        });
    }

    return function (view, params) {

        view.addEventListener('viewshow', function () {
            loadConfig(view);

            view.querySelector('.introsBackupReplacementConfigForm')
                .addEventListener('submit', function (e) { return onSubmit(view, e); });

            view.querySelector('#btnBrowseJsonBackupPath')
                .addEventListener('click', function () { onBrowseClick(view, '#txtJsonBackupPath'); });

            view.querySelector('#btnBrowseNfoBackupPath')
                .addEventListener('click', function () { onBrowseClick(view, '#txtNfoBackupPath'); });

            view.querySelector('#chkModeCustom')
                .addEventListener('change', function () { setMode(view, 'Custom'); });

            view.querySelector('#chkModeAutomatic')
                .addEventListener('change', function () { setMode(view, 'AutomaticMediaFolder'); });

            view.querySelector('#btnBrowseAutomaticJsonSafetyPath')
                .addEventListener('click', function () { onBrowseClick(view, '#txtAutomaticJsonSafetyPath'); });

            view.querySelector('#chkAutoJsonSafetyCopy')
                .addEventListener('change', function (e) { setAutomaticJsonSafetyPathVisible(view, e.target.checked); });

            restoreSourceIds.forEach(function (id) {
                view.querySelector('#' + id)
                    .addEventListener('change', function () { setExclusive(view, restoreSourceIds, id); });
            });
        });

        view.addEventListener('viewhide', function () {
        });

        view.addEventListener('viewdestroy', function () {
        });
    };
});
