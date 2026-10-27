using System;
using System.IO;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Versions;
using FModel.Settings;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

internal static class ProfileOperations
{
    public static object Execute(string operation, JObject args)
    {
        if (operation == "profile_options") return new
        {
            engines = Enum.GetNames<EGame>(), texturePlatforms = Enum.GetNames<ETexturePlatform>(),
            languages = Enum.GetNames<ELanguage>(), editable = new[] { "name", "engine", "texturePlatform", "language", "outputDirectory", "mappingsFilePath", "versionOptions" },
            versionOptionsContract = "Object of parser option names to booleans; replaces the saved option overrides. Existing overrides are returned by get_profile."
        };
        var profiles = ProfileStore.Enumerate(out _);
        if (operation == "create_profile")
        {
            var directory = AbsolutePath(Required(args, "directory"));
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Game directory does not exist.");
            var id = ProfileStore.ComputeId(ProfileStore.NormalizeDirectory(directory));
            using var lease = ProfileLock.TryAcquire(id) ?? throw new InvalidOperationException("The profile is running.");
            if (profiles.Any(x => x.Id == id)) throw new InvalidOperationException("A profile already exists for this directory. Use its returned ID.");
            GameProfile template = null;
            if (args.Value<string>("templateProfileId") is { } templateId)
                template = profiles.FirstOrDefault(x => x.Id == templateId) ?? throw new ArgumentException("Unknown templateProfileId.");
            var game = template == null ? DirectorySettings.Default(Required(args, "name"), directory, true,
                ParseEnum<EGame>(Required(args, "engine"))) : GameProfile.DeepClone(template.Game);
            game.GameName = Required(args, "name");
            game.GameDirectory = directory;
            if (args["engine"] != null) game.UeVersion = ParseEnum<EGame>(Required(args, "engine"));
            var settings = template == null ? new UserSettings() : GameProfile.DeepClone(template.Settings);
            SetOutput(settings, Required(args, "outputDirectory"), directory);
            var created = GameProfile.FromSettings(id, game, settings);
            ProfileStore.Save(created);
            return Describe(created, false);
        }
        var profileId = Required(args, "profileId");
        var profile = profiles.FirstOrDefault(x => x.Id == profileId) ?? throw new ArgumentException("Unknown profileId.");
        if (operation == "get_profile") return Describe(profile);
        using var profileLease = ProfileLock.TryAcquire(profileId) ?? throw new InvalidOperationException("Close the profile before editing or deleting it.");
        if (operation == "delete_profile")
        {
            ProfileStore.Delete(profileId);
            return new { deleted = profileId, gameFilesDeleted = false };
        }
        var values = (JObject) args["values"];
        foreach (var item in values.Properties())
        {
            if (item.Name == "versionOptions")
            {
                if (item.Value is not JObject options || options.Properties().Any(x => x.Value.Type != JTokenType.Boolean))
                    throw new ArgumentException("versionOptions must contain boolean parser option overrides.");
                profile.Game.Versioning ??= new VersioningSettings();
                profile.Game.Versioning.Options = options.Properties().ToDictionary(x => x.Name, x => x.Value.Value<bool>());
                continue;
            }
            var value = Required(values, item.Name);
            switch (item.Name)
            {
                case "name": profile.Game.GameName = value; break;
                case "engine": profile.Game.UeVersion = ParseEnum<EGame>(value); break;
                case "texturePlatform": profile.Game.TexturePlatform = ParseEnum<ETexturePlatform>(value); break;
                case "language": profile.Settings.AssetLanguage = ParseEnum<ELanguage>(value); break;
                case "outputDirectory": ValidateOutput(value, profile.Game.GameDirectory); break;
                case "mappingsFilePath":
                    var mappingPath = AbsolutePath(value);
                    if (!File.Exists(mappingPath)) throw new FileNotFoundException("Mappings file does not exist.");
                    if (!mappingPath.EndsWith(".usmap", StringComparison.OrdinalIgnoreCase) && !mappingPath.EndsWith(".jmap", StringComparison.OrdinalIgnoreCase) &&
                        !mappingPath.EndsWith(".jmap.gz", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Select a usmap, jmap or compressed jmap file.");
                    if (profile.Game.Endpoints == null || profile.Game.Endpoints.Length <= (int) EEndpointType.Mapping)
                        throw new InvalidDataException("The profile has no mapping endpoint configuration.");
                    profile.Game.Endpoints[(int) EEndpointType.Mapping] = new EndpointSettings { FilePath = mappingPath, Overwrite = true };
                    break;
                default: throw new ArgumentException($"Unknown profile setting: {item.Name}");
            }
        }
        if (values.Value<string>("outputDirectory") is { } output) SetOutput(profile.Settings, output, profile.Game.GameDirectory);
        ProfileStore.Save(profile);
        return Describe(profile, false);
    }

    private static object Describe(GameProfile profile, bool? running = null) => new
    {
        id = profile.Id, name = profile.Game.GameName, directory = profile.Game.GameDirectory,
        engine = profile.Game.UeVersion.ToString(), texturePlatform = profile.Game.TexturePlatform.ToString(),
        language = profile.Settings.AssetLanguage.ToString(), outputDirectory = profile.Settings.OutputDirectory,
        mappingsFilePath = profile.Game.Endpoints?.ElementAtOrDefault((int) EEndpointType.Mapping)?.FilePath,
        versionOptions = profile.Game.Versioning?.Options,
        availableVersionOptions = new VersionContainer(profile.Game.UeVersion, profile.Game.TexturePlatform).Options,
        createdAt = profile.CreatedAt, running = running ?? ProfileLock.IsHeldByAliveProcess(profile.Id),
        hasMainKey = !string.IsNullOrWhiteSpace(profile.Game.AesKeys?.MainKey), dynamicKeyCount = profile.Game.AesKeys?.DynamicKeys?.Count ?? 0
    };

    private static string Required(JObject args, string name) => args[name]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(args.Value<string>(name))
        ? args.Value<string>(name) : throw new ArgumentException($"A non-empty string is required: {name}");

    internal static T ParseEnum<T>(string value) where T : struct, Enum => Enum.TryParse<T>(value, false, out var result) && Enum.IsDefined(result)
        ? result : throw new ArgumentException($"Invalid {typeof(T).Name} value. Query the available choices first.");

    private static string ValidateOutput(string output, string gameDirectory)
    {
        var full = AbsolutePath(output);
        if (ProfileStore.NormalizeDirectory(full) == ProfileStore.NormalizeDirectory(gameDirectory) ||
            QueryHelpers.InDirectory(full, gameDirectory)) throw new ArgumentException("Output must be outside the game directory.");
        return full;
    }

    private static string AbsolutePath(string path) => System.IO.Path.IsPathFullyQualified(path) ? System.IO.Path.GetFullPath(path)
        : throw new ArgumentException("An absolute path is required.");

    private static void SetOutput(UserSettings settings, string output, string gameDirectory)
    {
        var full = ValidateOutput(output, gameDirectory);
        Directory.CreateDirectory(full);
        settings.OutputDirectory = full;
        settings.RawDataDirectory = settings.PropertiesDirectory = settings.TextureDirectory = settings.ModelDirectory =
            settings.AudioDirectory = settings.CodeDirectory = full;
    }
}
