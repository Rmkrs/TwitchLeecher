using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using TwitchLeecher.Core.Models;
using TwitchLeecher.Gui.Interfaces;
using TwitchLeecher.Services.Interfaces;
using TwitchLeecher.Shared.Commands;
using TwitchLeecher.Shared.Events;

namespace TwitchLeecher.Gui.ViewModels
{
    public class SearchResultViewVM : ViewModelBase, INavigationState
    {
        #region Fields

        private readonly ITwitchService _twitchService;
        private readonly IDialogService _dialogService;
        private readonly INavigationService _navigationService;
        private readonly INotificationService _notificationsService;
        private readonly IEventAggregator _eventAggregator;
        private readonly IPreferencesService _preferencesService;
        private readonly IFilenameService _filenameService;

        private readonly object _commandLockObject;

        private ICommand _viewCommand;
        private ICommand _downloadCommand;
        private ICommand _downloadAllCommand;
        private ICommand _seachCommand;

        #endregion Fields

        #region Constructors

        public SearchResultViewVM(
            ITwitchService twitchService,
            IDialogService dialogService,
            INavigationService navigationService,
            INotificationService notificationService,
            IEventAggregator eventAggregator,
            IPreferencesService preferencesService,
            IFilenameService filenameService)
        {
            _twitchService = twitchService;
            _dialogService = dialogService;
            _navigationService = navigationService;
            _notificationsService = notificationService;
            _eventAggregator = eventAggregator;
            _preferencesService = preferencesService;
            _filenameService = filenameService;

            _twitchService.PropertyChanged += TwitchService_PropertyChanged;

            _commandLockObject = new object();
        }

        #endregion Constructors

        #region Properties

        public double ScrollPosition { get; set; }

        public ObservableCollection<TwitchVideo> Videos
        {
            get
            {
                return _twitchService.Videos;
            }
        }

        public ICommand ViewCommand
        {
            get
            {
                if (_viewCommand == null)
                {
                    _viewCommand = new DelegateCommand<string>(ViewVideo);
                }

                return _viewCommand;
            }
        }

        public ICommand DownloadCommand
        {
            get
            {
                if (_downloadCommand == null)
                {
                    _downloadCommand = new DelegateCommand<string>(DownloadVideo);
                }

                return _downloadCommand;
            }
        }

        public ICommand SeachCommnad
        {
            get
            {
                if (_seachCommand == null)
                {
                    _seachCommand = new DelegateCommand(ShowSearch);
                }

                return _seachCommand;
            }
        }

        public ICommand DownloadAllCommand
        {
            get
            {
                if (_downloadAllCommand == null)
                {
                    _downloadAllCommand = new DelegateCommand(DownloadAll);
                }

                return _downloadAllCommand;
            }
        }

        #endregion Properties

        #region Methods

        private void ViewVideo(string id)
        {
            try
            {
                lock (_commandLockObject)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        TwitchVideo video = Videos.Where(v => v.Id == id).FirstOrDefault();

                        if (video != null && video.Url != null && video.Url.IsAbsoluteUri)
                        {
                            StartVideoStream(video);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        private void StartVideoStream(TwitchVideo video)
        {
            try
            {
                Preferences currentPrefs = _preferencesService.CurrentPreferences;

                if (currentPrefs.MiscUseExternalPlayer)
                {
                    Process.Start(currentPrefs.MiscExternalPlayer, video.Url.ToString());
                }
                else
                {
                    Process.Start(video.Url.ToString());
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        private void DownloadAll()
        {
            var addedCount = 0;
            var skippedCount = 0;
            var overwrittenCount = 0;

            try
            {
                var existingCount = 0;
                bool overrideAllExisting = false;
                bool skipAllExisting = false;
                foreach (var video in _twitchService.Videos)
                {
                    lock (_commandLockObject)
                    {
                        if (video != null)
                        {
                            VodAuthInfo vodAuthInfo = _twitchService.RetrieveVodAuthInfo(video.Id);

                            if (!vodAuthInfo.Privileged && vodAuthInfo.SubOnly)
                            {
                                _dialogService.ShowMessageBox($"This video ({video.Title}) is sub-only! Twitch removed the ability for 3rd party software to download such videos, sorry :(", "SUB HYPE!", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                                continue;
                            }

                            Preferences currentPrefs = _preferencesService.CurrentPreferences.Clone();

                            string folder = currentPrefs.DownloadSubfoldersForFav && _preferencesService.IsChannelInFavourites(video.Channel)
                                ? Path.Combine(currentPrefs.DownloadFolder, video.Channel)
                                : currentPrefs.DownloadFolder;

                            string filename = _filenameService.SubstituteWildcards(currentPrefs.DownloadFileName, video);
                            filename = _filenameService.EnsureExtension(filename, currentPrefs.DownloadDisableConversion);

                            DownloadParameters downloadParams = new DownloadParameters(video, vodAuthInfo, video.Qualities.First(), folder, filename, currentPrefs.DownloadDisableConversion);

                            if (File.Exists(downloadParams.FullPath))
                            {
                                existingCount++;

                                if (existingCount == 2)
                                {
                                    var messageMultiple = $"It seems there are multiple files that already exist.{Environment.NewLine}{Environment.NewLine}Press Cancel if you want to get a question for each existing file.{Environment.NewLine}{Environment.NewLine}Press Yes if you want to override all existing files.{Environment.NewLine}{Environment.NewLine}Press No if you want to skip all existing files";
                                    var resultMultiple = _dialogService.ShowMessageBox(messageMultiple, "Download", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                                    switch (resultMultiple)
                                    {
                                        case MessageBoxResult.None:
                                        case MessageBoxResult.OK:
                                            break;
                                        case MessageBoxResult.Yes:
                                            overrideAllExisting = true;
                                            break;
                                        case MessageBoxResult.Cancel:
                                            break;
                                        case MessageBoxResult.No:
                                            skipAllExisting = true;
                                            break;
                                        default:
                                            throw new ArgumentOutOfRangeException();
                                    }
                                }

                                if (skipAllExisting)
                                {
                                    skippedCount++;
                                    continue;
                                }

                                if (overrideAllExisting)
                                {
                                    overwrittenCount++;
                                    addedCount++;
                                    _twitchService.Enqueue(downloadParams);
                                    continue;
                                }

                                var message = $"The file: {Environment.NewLine}{downloadParams.FullPath}{Environment.NewLine} already exists. Do you want to overwrite it?{Environment.NewLine}{Environment.NewLine}If you press Cancel the rest of the downloads will not be added.";
                                var result = _dialogService.ShowMessageBox(message, "Download", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

                                switch (result)
                                {
                                    case MessageBoxResult.None:
                                    case MessageBoxResult.OK:
                                    case MessageBoxResult.Yes:
                                        overwrittenCount++;
                                        break;
                                    case MessageBoxResult.Cancel:
                                        this.ShowMultiDownloadNotification(addedCount, skippedCount, overwrittenCount);
                                        return;
                                    case MessageBoxResult.No:
                                        skippedCount++;
                                        continue;
                                    default:
                                        throw new ArgumentOutOfRangeException();
                                }
                            }

                            addedCount++;
                            _twitchService.Enqueue(downloadParams);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }

            this.ShowMultiDownloadNotification(addedCount, skippedCount, overwrittenCount);
        }

        private void ShowMultiDownloadNotification(int addedCount, int skippedCount, int overwrittenCount)
        {
            string message = $"{addedCount} Downloads added";
            if (skippedCount > 0)
            {
                message += $", {skippedCount} existing files have been skipped";
            }

            if (overwrittenCount > 0)
            {
                message += $", {overwrittenCount} existing files have been overwritten";
            }

            _notificationsService.ShowNotification(message);
        }

        private void DownloadVideo(string id)
        {
            try
            {
                lock (_commandLockObject)
                {
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        TwitchVideo video = Videos.Where(v => v.Id == id).FirstOrDefault();

                        if (video != null)
                        {
                            VodAuthInfo vodAuthInfo = _twitchService.RetrieveVodAuthInfo(video.Id);

                            if (!vodAuthInfo.Privileged && vodAuthInfo.SubOnly)
                            {
                                _dialogService.ShowMessageBox("This video is sub-only! Twitch removed the ability for 3rd party software to download such videos, sorry :(", "SUB HYPE!", MessageBoxButton.OK, MessageBoxImage.Exclamation);

                                return;
                            }

                            Preferences currentPrefs = _preferencesService.CurrentPreferences.Clone();

                            string folder = currentPrefs.DownloadSubfoldersForFav && _preferencesService.IsChannelInFavourites(video.Channel)
                                ? Path.Combine(currentPrefs.DownloadFolder, video.Channel)
                                : currentPrefs.DownloadFolder;

                            string filename = _filenameService.SubstituteWildcards(currentPrefs.DownloadFileName, video);
                            filename = _filenameService.EnsureExtension(filename, currentPrefs.DownloadDisableConversion);

                            DownloadParameters downloadParams = new DownloadParameters(video, vodAuthInfo, video.Qualities.First(), folder, filename, currentPrefs.DownloadDisableConversion);

                            _navigationService.ShowDownload(downloadParams);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        public void ShowSearch()
        {
            try
            {
                lock (_commandLockObject)
                {
                    _navigationService.ShowSearch();
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowAndLogException(ex);
            }
        }

        protected override List<MenuCommand> BuildMenu()
        {
            List<MenuCommand> menuCommands = base.BuildMenu();

            if (menuCommands == null)
            {
                menuCommands = new List<MenuCommand>();
            }

            menuCommands.Add(new MenuCommand(SeachCommnad, "New Search", "Search"));
            menuCommands.Add(new MenuCommand(DownloadAllCommand, "Download All", "Download"));

            return menuCommands;
        }

        #endregion Methods

        #region EventHandlers

        private void TwitchService_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            string propertyName = e.PropertyName;

            FirePropertyChanged(propertyName);

            if (propertyName.Equals(nameof(Videos)))
            {
                ScrollPosition = 0;
            }
        }

        #endregion EventHandlers
    }
}