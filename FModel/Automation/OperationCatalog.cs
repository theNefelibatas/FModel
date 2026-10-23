using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FModel.Automation;

public sealed record OperationDefinition(string Name, string Description, JObject InputSchema, bool ReadOnly);

public static class OperationCatalog
{
    private static JObject Field(string type, string description) => new() { ["type"] = type, ["description"] = description };
    private static JObject Choice(string description, params string[] values) => new()
    {
        ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values)
    };

    private static OperationDefinition Define(string name, string description, bool readOnly, string[] required,
        params (string Name, JObject Schema)[] fields)
    {
        var properties = new JObject();
        foreach (var (key, schema) in fields) properties[key] = schema;
        if (properties["path"] != null && name != "file_versions")
            properties["sourceArchive"] = Field("string", "Optional exact source container for this file version. Discover versions through file_versions; omitted uses provider mount priority. Referenced dependencies still use provider resolution.");
        return new OperationDefinition(name, description, new JObject
        {
            ["type"] = "object", ["properties"] = properties,
            ["required"] = new JArray(required), ["additionalProperties"] = false
        }, readOnly);
    }

    private static readonly (string, JObject) Profile = ("profileId", Field("string", "Exact ID returned by profiles. Never infer a game from a previous session."));
    private static readonly (string, JObject) Path = ("path", Field("string", "Exact virtual file path including extension, as returned by search_files."));
    private static readonly (string, JObject) Offset = ("offset", Field("integer", "Zero-based offset; defaults to zero."));
    private static readonly (string, JObject) Limit = ("limit", Field("integer", "Maximum returned items, from 1 to 500; defaults to 100."));

    public static IReadOnlyList<OperationDefinition> All { get; } =
    [
        Define("profiles", "List configured game profiles and running sessions, without exposing encryption keys.", true, []),
        Define("profile_options", "Discover supported engine, texture platform and language values for profile management.", true, []),
        Define("get_profile", "Read saved profile configuration without exposing encryption keys or starting the game.", true, ["profileId"], Profile),
        Define("create_profile", "Create a profile for an existing local game directory. Never replaces an existing profile; output must be outside the game directory.", false,
            ["name", "directory", "outputDirectory"], ("name", Field("string", "Display name.")),
            ("directory", Field("string", "Existing absolute game directory.")), ("outputDirectory", Field("string", "Absolute output directory.")),
            ("engine", Field("string", "Engine from profile_options; required without a template.")),
            ("templateProfileId", Field("string", "Optional existing profile whose parsing settings and keys should be copied."))),
        Define("update_profile", "Edit a closed profile. Discover editable settings through profile_options; opening sessions must be closed first.", false,
            ["profileId", "values"], Profile, ("values", Field("object", "Profile configuration changes."))),
        Define("delete_profile", "Delete a closed profile's configuration only. Never deletes game files, caches or exported assets.", false, ["profileId"], Profile),
        Define("capabilities", "Describe available operations and explicit GUI parity limitations. No game is opened.", true, []),
        Define("session_status", "Connect to an open game or start a persistent headless session. Report loading, errors, and mounted file counts.", true, ["profileId"], Profile),
        Define("show_gui", "Show or activate this profile's main window in the existing process without reloading archives or disconnecting other clients. Waits for initialization and active operations. Closing a window opened from a hidden host hides it; the shared session keeps running.", false, ["profileId"], Profile),
        Define("read_session_log", "Read a bounded tail of the profile's active log, including parser warnings and initialization failures.", true, ["profileId"], Profile,
            ("length", Field("integer", "Maximum tail characters, from 1 to 65536; defaults to 16000."))),
        Define("list_archives", "List mounted and unmounted containers; missing keys or unsupported archives can make the file index incomplete.", true, ["profileId"], Profile, Offset, Limit),
        Define("select_archives", "Select mounted containers for subsequent directory/search/statistics queries, like desktop multi-selection. This does not unmount containers or invalidate references.", false,
            ["profileId"], Profile, ("archives", new JObject { ["type"] = "array", ["items"] = Field("string", "Exact container name.") }),
            ("includeLooseFiles", Field("boolean", "Include loose files in the selection.")), ("reset", Field("boolean", "Clear selection and query all mounted files."))),
        Define("aes_status", "Report required and configured AES key GUIDs without returning key material.", true, ["profileId"], Profile),
        Define("submit_aes_key", "Submit an AES key to the provider and optionally persist it in the profile. Returns mount counts, never the key.", false,
            ["profileId", "guid", "key"], Profile, ("guid", Field("string", "Encryption GUID, including the all-zero GUID for the main key.")),
            ("key", Field("string", "AES key hex string.")), ("persist", Field("boolean", "Explicitly save this key to the profile; false by default."))),
        Define("create_index_backup", "Create a new GUI-compatible index backup without overwriting a file. Contains index metadata, not payload data.", false,
            ["profileId", "backupPath"], Profile, ("backupPath", Field("string", "Absolute new backup filename in an existing directory."))),
        Define("compare_index_backup", "Compare a versioned GUI index backup with the current mounted index. Reports added, removed and metadata-modified files.", true,
            ["profileId", "backupPath"], Profile, ("backupPath", Field("string", "Existing absolute backup filename.")), Offset, Limit),
        Define("list_directory", "Browse immediate child directories and files without exporting or parsing packages. Empty path lists virtual roots.", true, ["profileId"], Profile,
            ("directory", Field("string", "Virtual directory; empty means root.")), Offset, Limit),
        Define("search_files", "Search the mounted virtual file index, with pagination, path terms, extension, directory and container filters. Asset class is not inferred from extension.", true, ["profileId"], Profile,
            ("query", Field("string", "Space-separated path terms, or a regular expression when regex is true.")),
            ("regex", Field("boolean", "Enable regular expressions with a bounded execution time.")),
            ("directory", Field("string", "Restrict to this directory and descendants.")),
            ("extension", Field("string", "File extension with or without a leading dot.")),
            ("archive", Field("string", "Exact container name returned by list_archives.")),
            ("includePayloads", Field("boolean", "Include UE sidecar payload files; defaults to false.")),
            ("sort", Choice("Sort order; defaults to path.", "path", "size_ascending", "size_descending")), Offset, Limit),
        Define("file_info", "Read file index metadata and optionally resolve GUI asset category and supported export actions.", true, ["profileId", "path"], Profile, Path,
            ("resolveType", Field("boolean", "Parse the package to resolve its main asset type and actions."))),
        Define("file_versions", "List container versions of one virtual path in provider priority order, including shadowed patch entries.", true, ["profileId", "path"], Profile, Path, Offset, Limit),
        Define("index_statistics", "Summarize mounted file counts and uncompressed bytes by extension, optionally within a directory. Does not parse assets.", true, ["profileId"], Profile,
            ("directory", Field("string", "Virtual directory and descendants; empty means all files.")), Offset, Limit),
        Define("list_imports", "List package import table references, including unresolved entries. This is not a complete graph of soft references.", true, ["profileId", "path"], Profile, Path, Offset, Limit),
        Define("read_object", "Follow a UE object reference using the provider's package path resolution and existing serializer.", true, ["profileId", "objectPath"], Profile,
            ("objectPath", Field("string", "Object path from list_exports, list_imports or serialized properties; omit class-name wrappers.")),
            ("selector", Field("string", "Optional JSONPath within serialized properties.")), Offset,
            ("length", Field("integer", "Maximum characters, from 1 to 65536; defaults to 16000."))),
        Define("package_metadata", "Read the same serialized package metadata as the GUI, with JSONPath selection and bounded text chunks.", true, ["profileId", "path"], Profile, Path,
            ("selector", Field("string", "Optional JSONPath within serialized metadata.")), Offset,
            ("length", Field("integer", "Maximum characters, from 1 to 65536; defaults to 16000."))),
        Define("list_exports", "List export object indices, names, classes and object paths. Parses only the requested page.", true, ["profileId", "path"], Profile, Path, Offset, Limit),
        Define("read_export", "Read an export object through the existing UE serializer. Supports JSONPath selection and bounded chunks.", true, ["profileId", "path", "index"], Profile, Path,
            ("index", Field("integer", "Zero-based export index from list_exports.")),
            ("selector", Field("string", "Optional JSONPath, for example a property or nested reference.")), Offset,
            ("length", Field("integer", "Maximum characters, from 1 to 65536; defaults to 16000."))),
        Define("read_document", "Read text or structured non-package content using the same decoders as the GUI. For UE packages use list_exports/read_export for bounded parsing.", true, ["profileId", "path"], Profile, Path, Offset,
            ("length", Field("integer", "Maximum characters, from 1 to 65536; defaults to 16000."))),
        Define("find_referencers", "Find IoStore packages whose container import tables reference this package. This is not a complete soft-reference graph; unsupported formats return an error.", true, ["profileId", "path"], Profile, Path, Offset, Limit),
        Define("decompile", "Read the GUI blueprint pseudocode representation. This does not recover original source code.", true, ["profileId", "path"], Profile, Path, Offset,
            ("length", Field("integer", "Maximum characters, from 1 to 65536; defaults to 16000."))),
        Define("export_assets", "Start a cancellable export job using GUI decoders/exporters and the active profile settings. Poll job_status and inspect individual failures and output paths.", false, ["profileId", "paths", "type"], Profile,
            ("paths", new JObject { ["type"] = "array", ["items"] = Field("string", "Exact virtual file path."), ["minItems"] = 1, ["maxItems"] = 500 }),
            ("type", Choice("Export operation.", "raw", "properties", "textures", "models", "worlds", "animations", "audio", "code", "embedded"))),
        Define("preview_texture", "Decode a texture export into an inline PNG preview without writing a file.", true, ["profileId", "path", "index"], Profile, Path,
            ("index", Field("integer", "Zero-based UTexture export index.")),
            ("maxSize", Field("integer", "Maximum image dimension, from 32 to 2048; defaults to 512.")),
            ("mipIndex", Field("integer", "Optional explicit mip index; otherwise chosen from maxSize. Available ordinary mips are returned with the image.")),
            ("channel", Choice("Display channels; defaults to rgba. Single channels are shown as grayscale.", "rgba", "rgb", "r", "g", "b", "a"))),
        Define("viewer_load", "Load an export into an isolated viewport using the desktop renderer. Does not change the saved GUI scene.", false, ["profileId", "path", "index"], Profile, Path,
            ("index", Field("integer", "Export index.")), ("append", Field("boolean", "Append to the current scene.")),
            ("width", Field("integer", "Initial width, 32 to 2048.")), ("height", Field("integer", "Initial height, 32 to 2048."))),
        Define("viewer_state", "Read camera, scene models and animation timeline from the automation viewport.", true, ["profileId"], Profile, Offset, Limit),
        Define("viewer_configure", "Set camera and rendering options. Camera vectors contain x,y,z in renderer coordinates, with Y up.", false, ["profileId", "values"], Profile,
            ("values", Field("object", "position, target, zoom, far, speed, mode, showGrid, showSkybox, showLights, showSkeleton, vertexColor; inspect viewer_state first."))),
        Define("viewer_model", "Inspect or edit a scene model, sections, morphs and bones. Changes affect this viewport only.", false, ["profileId", "modelId"], Profile,
            ("modelId", Field("string", "ID from viewer_state.")), ("values", Field("object", "visible, wireframe, collisions, instance, position, scale, rotation, section, showSection, morph, morphWeight. Position is in meters with Z up; rotation is a normalized x,y,z,w quaternion.")),
            ("focus", Field("boolean", "Frame the model with the camera.")), ("remove", Field("boolean", "Remove the model from the scene.")), Offset, Limit),
        Define("viewer_lighting", "Edit an existing scene light without changing source assets.", false, ["profileId", "index"], Profile,
            ("index", Field("integer", "Light index from viewer_state.")), ("values", Field("object", "intensity, position (x,y,z meters with Z up), rotation (x,y,z,w quaternion), color (r,g,b,a from 0 to 1)."))),
        Define("viewer_material_parameters", "Inspect or adjust the desktop renderer's material parameters without saving source assets.", false, ["profileId", "modelId", "index"], Profile,
            ("modelId", Field("string", "ID from viewer_state.")), ("index", Field("integer", "Material index from viewer_model.")),
            ("values", Field("object", "roughnessMin and roughnessMax (0 to 1), emissiveMultiplier (nonnegative)."))),
        Define("viewer_animation", "Load an animation using the desktop animation converter and compatible skeleton.", false, ["profileId", "path", "index"], Profile, Path,
            ("index", Field("integer", "Export index.")), ("applyToSelected", Field("boolean", "Apply to the selected skeletal model instead of loading the animation skeleton."))),
        Define("viewer_material", "Apply a material instance to the selected model section.", false, ["profileId", "path", "index"], Profile, Path,
            ("index", Field("integer", "Material instance export index."))),
        Define("viewer_timeline", "Pause, seek, change animation speed or clear animations in the automation viewport.", false, ["profileId"], Profile,
            ("paused", Field("boolean", "Pause playback.")), ("seconds", Field("number", "Seek within the reported timeline duration.")),
            ("speed", Field("number", "Playback multiplier, 0 to 100.")), ("clear", Field("boolean", "Remove animations."))),
        Define("viewer_capture", "Render the automation scene to an inline PNG without saving a file. Requires an available OpenGL context.", true, ["profileId"], Profile,
            ("width", Field("integer", "Width, 32 to 2048.")), ("height", Field("integer", "Height, 32 to 2048."))),
        Define("viewer_close", "Release the automation viewport without saving scene settings.", false, ["profileId"], Profile),
        Define("audio_load", "Decode audio into the player's in-memory playlist without exporting or automatically playing it.", false, ["profileId", "path"], Profile, Path),
        Define("audio_state", "Read playlist and actual playback state.", true, ["profileId"], Profile),
        Define("audio_control", "Control the audio player. Volume changes are session-only; play and resume produce sound.", false, ["profileId", "action"], Profile,
            ("action", Choice("Player action.", "load", "play", "pause", "resume", "stop", "remove", "seek", "volume")),
            ("trackId", Field("integer", "Track ID from audio_state.")), ("fraction", Field("number", "Seek fraction, 0 to 1.")), ("volume", Field("number", "Volume, 0 to 1."))),
        Define("job_status", "Read a job's state, progress, per-file results and failures. Completed results are paginated.", true, ["profileId", "jobId"], Profile,
            ("jobId", Field("string", "ID returned when starting a job.")), Offset, Limit),
        Define("cancel_job", "Request cooperative cancellation. Cancellation does not remove outputs already written.", false, ["profileId", "jobId"], Profile,
            ("jobId", Field("string", "ID returned when starting a job."))),
        Define("get_export_settings", "Read the active export settings and discover editable setting names, types and enum values.", true, ["profileId"], Profile),
        Define("set_export_settings", "Validate and update export settings in the active session. Persist only when persist is explicitly true.", false, ["profileId", "values"], Profile,
            ("values", new JObject { ["type"] = "object", ["description"] = "Setting names and values returned by get_export_settings." }),
            ("persist", Field("boolean", "Save the updated settings to the game profile; defaults to false."))),
        Define("load_localization", "Start loading the profile language's localized resources and virtual paths using the existing provider.", true, ["profileId"], Profile),
        Define("search_localization", "Search loaded localized text, namespaces and keys. Call load_localization first; unloaded resources return an error.", true, ["profileId"], Profile,
            ("namespace", Field("string", "Optional exact namespace.")),
            ("query", Field("string", "Case-insensitive substring in the key or translated text.")), Offset, Limit),
        Define("open_in_gui", "Open an asset in the attached GUI using its normal preview, metadata or reference view. Use show_gui first if the session has no visible window.", false, ["profileId", "path"], Profile, Path,
            ("view", Choice("View to open; defaults to preview.", "preview", "metadata", "references", "code"))),
        Define("close_session", "Close a headless session after its jobs finish. Never closes a user's GUI window.", false, ["profileId"], Profile)
    ];

    public static void Validate(string name, JObject args)
    {
        var operation = All.FirstOrDefault(x => x.Name == name) ?? throw new ArgumentException($"Unknown operation: {name}");
        var properties = (JObject) operation.InputSchema["properties"];
        foreach (var key in operation.InputSchema["required"].Values<string>())
            if (args[key] == null || args[key].Type == JTokenType.Null) throw new ArgumentException($"Missing argument: {key}");
        foreach (var property in args.Properties())
        {
            if (properties[property.Name] is not JObject schema) throw new ArgumentException($"Unknown argument: {property.Name}");
            var valid = schema.Value<string>("type") switch
            {
                "string" => property.Value.Type == JTokenType.String,
                "integer" => property.Value.Type == JTokenType.Integer,
                "boolean" => property.Value.Type == JTokenType.Boolean,
                "array" => property.Value is JArray array && array.Count >= (schema.Value<int?>("minItems") ?? 0) && array.Count <= (schema.Value<int?>("maxItems") ?? 500) && array.All(x => x.Type == JTokenType.String),
                "number" => property.Value.Type is JTokenType.Float or JTokenType.Integer,
                "object" => property.Value is JObject,
                _ => false
            };
            if (!valid) throw new ArgumentException($"Invalid type for argument: {property.Name}");
            if (schema["enum"] is JArray choices && !choices.Any(x => JToken.DeepEquals(x, property.Value)))
                throw new ArgumentException($"Invalid value for argument: {property.Name}");
        }
    }
}
