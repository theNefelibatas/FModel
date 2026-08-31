using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;
using FModel.Framework;
using FModel.Settings;
using FModel.Views;
using MessageBox = AdonisUI.Controls.MessageBox;
using MessageBoxButton = AdonisUI.Controls.MessageBoxButton;
using MessageBoxImage = AdonisUI.Controls.MessageBoxImage;
using MessageBoxResult = AdonisUI.Controls.MessageBoxResult;

namespace FModel.ViewModels;

/* 启动器（第一层）：列出所有游戏档案，开/删/新建档案。
 * 关掉本窗口不影响任何已启动的游戏进程（它们各自独立进程）。 */
public class LauncherViewModel : ViewModel
{
    public sealed class ProfileViewModel
    {
        public string Id { get; init; }
        public string GameName { get; init; }
        public string GameDirectory { get; init; }
        public string UeVersion { get; init; }
        public DateTime LastUsedAt { get; init; }
        public bool IsRunning { get; set; }
        public bool IsCorrupt { get; init; }
        public bool IsDirectoryMissing { get; init; }
        public string LastUsedLabel => LastUsedAt == DateTime.MinValue ? "" : $"Last used: {LastUsedAt:yyyy-MM-dd HH:mm}";
    }

    private readonly ObservableCollection<ProfileViewModel> _profiles = new();
    public ObservableCollection<ProfileViewModel> Profiles => _profiles;

    private ProfileViewModel _selectedProfile;
    public ProfileViewModel SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    public ICommand OpenSelectedCommand { get; }
    public ICommand AddGameCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand OpenProfilesFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public LauncherViewModel()
    {
        OpenSelectedCommand = new DelegateCommand(OpenSelected);
        AddGameCommand = new DelegateCommand(AddGame);
        DeleteCommand = new DelegateCommand(DeleteSelected);
        OpenProfilesFolderCommand = new DelegateCommand(OpenProfilesFolder);
        RefreshCommand = new DelegateCommand(Refresh);

        Refresh();
    }

    public void Refresh()
    {
        IReadOnlyList<string> corrupt = null;
        var profiles = ProfileStore.Enumerate(out corrupt)
            .Select(p => new ProfileViewModel
            {
                Id = p.Id,
                GameName = p.Game.GameName,
                GameDirectory = p.Game.GameDirectory,
                UeVersion = p.Game.UeVersion.ToString(),
                LastUsedAt = p.LastUsedAt,
                IsRunning = ProfileLock.IsHeldByAliveProcess(p.Id),
                /* 目录失效：仅对绝对路径判断；LIVE 触发串（非绝对路径）不视为目录缺失 */
                IsDirectoryMissing = Path.IsPathRooted(p.Game.GameDirectory) && !Directory.Exists(p.Game.GameDirectory)
            })
            .OrderByDescending(p => p.LastUsedAt)
            .ToList();

        /* 损坏档案：仍列出（标注），可以删除；id 从文件名解析 */
        foreach (var file in corrupt)
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var pos = id.LastIndexOf('_');
            if (pos > 0) id = id[(pos + 1)..];
            profiles.Add(new ProfileViewModel
            {
                Id = id,
                GameName = $"（损坏档案）{Path.GetFileName(file)}",
                GameDirectory = "unreadable",
                UeVersion = "",
                LastUsedAt = DateTime.MinValue,
                IsCorrupt = true
            });
        }

        var keepId = SelectedProfile?.Id;
        _profiles.Clear();
        foreach (var p in profiles) _profiles.Add(p);

        /* 刷新保持选中：先按 Id 找回；没了（档案已删/损坏消失）再回落到第一项 */
        if (keepId != null)
            SelectedProfile = _profiles.FirstOrDefault(x => x.Id == keepId);
        if (SelectedProfile == null && _profiles.Count > 0)
            SelectedProfile = _profiles[0];
    }

    public void OpenSelectedProfile() => OpenSelected();

    private void OpenSelected()
    {
        if (SelectedProfile == null || SelectedProfile.IsCorrupt) return;
        if (SelectedProfile.IsDirectoryMissing)
        {
            MessageBox.Show($"{SelectedProfile.GameName} directory no longer exists:\n{SelectedProfile.GameDirectory}",
                "Game directory missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (SelectedProfile.IsRunning)
        {
            MessageBox.Show($"{SelectedProfile.GameName} is already running.", "Game already running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ApplicationViewModel.LaunchProfileProcess(SelectedProfile.Id);
    }

    private void AddGame()
    {
        var vm = new GameSelectorViewModel(string.Empty);
        var dialog = new DirectorySelector(vm);
        if (!(dialog.ShowDialog() ?? false)) return;
        var selected = vm.SelectedDirectory;
        if (selected == null) return;

        var profile = GameProfile.FromSettings(
            ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(selected.GameDirectory)),
            selected, new UserSettings());
        profile = ProfileStore.Create(profile); // 同目录则复用既有档案（含已存设置）
        if (ProfileLock.IsHeldByAliveProcess(profile.Id))
        {
            MessageBox.Show($"{profile.Game.GameName} is already running.", "Game already running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Refresh();
        SelectedProfile = _profiles.FirstOrDefault(x => x.Id == profile.Id);
        ApplicationViewModel.LaunchProfileProcess(profile.Id);
    }

    private void DeleteSelected()
    {
        if (SelectedProfile == null) return;
        if (SelectedProfile.IsRunning)
        {
            MessageBox.Show($"{SelectedProfile.GameName} is still running. Close the game first.", "Game running", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var confirm = MessageBox.Show(
            $"Delete game '{SelectedProfile.GameName}'?\nIts settings and AES keys will be removed.",
            "Delete game", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (SelectedProfile.IsCorrupt)
        {
            var exact = ProfileStore.FilePathById(SelectedProfile.Id);
            if (exact != null)
                File.Delete(exact);
            else
                foreach (var f in Directory.GetFiles(ProfileStore.ProfilesDirectory, "*_" + SelectedProfile.Id + ".json"))
                    File.Delete(f);
        }
        else
        {
            ProfileStore.Delete(SelectedProfile.Id);
        }
        Refresh();
    }

    private void OpenProfilesFolder()
    {
        Directory.CreateDirectory(ProfileStore.ProfilesDirectory);
        Process.Start(new ProcessStartInfo { FileName = ProfileStore.ProfilesDirectory, UseShellExecute = true });
    }

    /* 本项目 ViewModelCommand 是带上下文抽象类（绑定只需 ICommand），这里用最小实现 */
    private sealed class DelegateCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public DelegateCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object parameter) => _execute();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
}
