define([], function () {
    'use strict';

    var pluginId = "7602a9a8-788d-4007-84c1-a08a3671e1aa";

    function loadConfig(view) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#txtJsonBackupPath').value = config.JsonBackupPath || '';
            view.querySelector('#txtNfoBackupPath').value = config.NfoBackupPath || '';
            view.querySelector('#chkSaveJsonToMediaFolder').checked = config.SaveJsonToMediaFolder || false;
            view.querySelector('#chkSaveNfoToMediaFolder').checked = config.SaveNfoToMediaFolder || false;
            view.querySelector('#chkInsertIntoMediaNfo').checked = config.InsertIntoMediaNfo || false;
            Dashboard.hideLoadingMsg();
        });
    }

    function onSubmit(view, e) {
        e.preventDefault();

        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.JsonBackupPath = view.querySelector('#txtJsonBackupPath').value;
            config.NfoBackupPath = view.querySelector('#txtNfoBackupPath').value;
            config.SaveJsonToMediaFolder = view.querySelector('#chkSaveJsonToMediaFolder').checked;
            config.SaveNfoToMediaFolder = view.querySelector('#chkSaveNfoToMediaFolder').checked;
            config.InsertIntoMediaNfo = view.querySelector('#chkInsertIntoMediaNfo').checked;

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
        });

        view.addEventListener('viewhide', function () {
        });

        view.addEventListener('viewdestroy', function () {
        });
    };
});
