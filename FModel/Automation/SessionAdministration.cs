using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.VirtualFileSystem;
using FModel.Settings;
using FModel.ViewModels.ApiEndpoints.Models;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

internal sealed partial class GameOperations
{
    private HashSet<string> _archiveSelection;
    private bool _includeLooseFiles;

    private IEnumerable<GameFile> SelectedFiles() => _archiveSelection == null ? Model.Provider.Files.Values :
        Model.Provider.Files.Values.Where(x => x is VfsEntry entry ? _archiveSelection.Contains(entry.Vfs.Name) : _includeLooseFiles);

    private object SelectArchives(JObject args)
    {
        if (args.Value<bool?>("reset") == true)
        {
            _archiveSelection = null;
            _includeLooseFiles = true;
        }
        else
        {
            if (args["archives"] == null) throw new ArgumentException("Provide archives or reset=true.");
            var selection = args["archives"].Values<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mounted = Model.Provider.MountedVfs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (selection.Any(x => !mounted.Contains(x))) throw new ArgumentException("Selection contains an unknown or unmounted archive.");
            _archiveSelection = selection;
            _includeLooseFiles = args.Value<bool?>("includeLooseFiles") == true;
        }
        if (HasGui)
        {
            var entries = SelectedFiles().Where(x => !x.IsUePackagePayload).ToList();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Model.AssetsFolder.Clear();
                Model.SearchVm.ChangeCollection([]);
                Model.AssetsFolder.BulkPopulate(entries);
            });
        }
        return new { allArchives = _archiveSelection == null, archives = _archiveSelection?.ToArray(), includeLooseFiles = _includeLooseFiles,
            files = SelectedFiles().Count(), coverage = "Display/search selection only; mounted dependencies remain available for parsing." };
    }

    private object AesStatus() => new
    {
        required = Model.Provider.RequiredKeys.Select(x => x.ToString()).ToArray(),
        configured = Model.Provider.Keys.Keys.Select(x => x.ToString()).ToArray(),
        mountedArchives = Model.Provider.MountedVfs.Count, unmountedArchives = Model.Provider.UnloadedVfs.Count
    };

    private async Task<object> SubmitAesAsync(JObject args)
    {
        var guid = new FGuid(args.Value<string>("guid"));
        FAesKey key;
        try { key = new FAesKey(args.Value<string>("key")); }
        catch { throw new ArgumentException("Invalid AES key: provide exactly 32 bytes of hexadecimal data."); }
        var mounted = await Model.Provider.SubmitKeyAsync(guid, key);
        Model.Provider.PostMount();
        if (args.Value<bool?>("persist") == true)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                var profile = ProfileStore.Load(UserSettings.Default.CurrentProfileId);
                profile.Game.AesKeys ??= new AesResponse();
                if (guid == Constants.ZERO_GUID) profile.Game.AesKeys.MainKey = key.KeyString;
                else
                {
                    profile.Game.AesKeys.DynamicKeys ??= [];
                    var dynamicKey = profile.Game.AesKeys.DynamicKeys.FirstOrDefault(x => x.Guid == guid.ToString());
                    if (dynamicKey == null)
                    {
                        dynamicKey = new DynamicKey { Guid = guid.ToString(), Name = Model.Provider.MountedVfs.FirstOrDefault(x => x.EncryptionKeyGuid == guid)?.Name ?? guid.ToString() };
                        profile.Game.AesKeys.DynamicKeys.Add(dynamicKey);
                    }
                    dynamicKey.Key = key.KeyString;
                }
                ProfileStore.Save(profile);
                UserSettings.Default.CurrentDir.AesKeys = GameProfile.DeepClone(profile.Game.AesKeys);
            });
        }
        return new { newlyMountedArchives = mounted, persisted = args.Value<bool?>("persist") == true, status = AesStatus() };
    }
}
