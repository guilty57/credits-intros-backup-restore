define([], function () {
    'use strict';

    var pluginId = "7602a9a8-788d-4007-84c1-a08a3671e1aa";

    function loadConfig(view) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#txtIntroBackupPath').value = config.IntroBackupPath || '';
            view.querySelector('#chkAlsoWriteNfo').checked = config.AlsoWriteNfo || false;
            Dashboard.hideLoadingMsg();
        });
    }

    function onSubmit(view, e) {
        e.preventDefault();

        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.IntroBackupPath = view.querySelector('#txtIntroBackupPath').value;
            config.AlsoWriteNfo = view.querySelector('#chkAlsoWriteNfo').checked;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
            });
        });

        return false;
    }

    function onBrowseClick(view) {
        require(['directorybrowser'], function (directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                path: view.querySelector('#txtIntroBackupPath').value,
                network: false,
                includeFiles: false,
                includeDirectories: true,
                callback: function (path) {
                    if (path) {
                        view.querySelector('#txtIntroBackupPath').value = path;
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

            view.querySelector('#btnBrowseIntroBackupPath')
                .addEventListener('click', function () { onBrowseClick(view); });
        });

        view.addEventListener('viewhide', function () {
        });

        view.addEventListener('viewdestroy', function () {
        });
    };
});
