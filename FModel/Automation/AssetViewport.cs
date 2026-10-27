using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.GeometryCollection;
using CUE4Parse.UE4.Assets.Exports.Houdini;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.GameTypes.Nascar.Assets.Exports;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Services;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SkiaSharp;
using Snooper.Core.Managers;
using Snooper.Rendering;
using Snooper.Rendering.Actors;
using Snooper.Rendering.Cache;
using Snooper.Rendering.Components;
using Snooper.Rendering.Components.Camera;
using Snooper.Rendering.Components.Descriptors;
using Snooper.Rendering.Components.Light;
using Snooper.Rendering.Components.Mesh;
using Snooper.Rendering.Components.Transforms;

namespace FModel.Automation;

internal sealed class AssetViewport : IDisposable
{
    private static CUE4Parse.FileProvider.AbstractFileProvider Provider => ApplicationService.ApplicationView.CUE4Parse.Provider;
    private sealed class ViewportScene(GameWindow window) : SceneManager(window, Provider);

    private sealed class ViewportWindow(NativeWindowSettings settings) : GameWindow(GameWindowSettings.Default, settings), Snooper.Core.IGameWindow
    {
        private readonly ConcurrentQueue<Action> _commands = new();
        public void Invoke(Action command) => _commands.Enqueue(command);
        public void Drain()
        {
            while (_commands.TryDequeue(out var command)) command();
        }
    }

    private readonly ViewportWindow _window;
    private readonly ViewportScene _scene;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CameraActor _camera;
    private GridActor _grid;
    private EnvironmentActor _environment;
    private string _selected;
    private int _instance, _section, _morph, _width, _height;
    private float _distance = 1;
    private bool _paused;
    private float _speed = 1;

    public AssetViewport(int width, int height)
    {
        _window = new ViewportWindow(new NativeWindowSettings
        {
            ClientSize = new(width, height), StartVisible = false, StartFocused = false,
            APIVersion = new Version(4, 6), Profile = ContextProfile.Core, Flags = ContextFlags.ForwardCompatible
        });
        _window.MakeCurrent();
        _scene = new ViewportScene(_window);
        try
        {
            GL.ClipControl(ClipOrigin.LowerLeft, ClipDepthMode.ZeroToOne);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Greater);
            GL.ClearDepth(0);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.PatchParameter(PatchParameterInt.PatchVertices, 4);
            _scene.Load();
            Resize(width, height);
            Reset();
        }
        catch { Dispose(); throw; }
    }

    private void Reset()
    {
        if (_scene.RootActor != null) _scene.UnloadScene();
        _scene.LoadScene(new Actor("Scene"));
        _scene.LoadScene(_camera = new CameraActor());
        _scene.LoadScene(_grid = new GridActor(true));
        _scene.LoadScene(_environment = new EnvironmentActor());
        _scene.LoadScene(new SunActor());
        _selected = null;
        _instance = _section = _morph = 0;
        _paused = false;
        _speed = 1;
        _scene.Wireframe.Enabled = false;
        _clock.Restart();
    }

    private static IEnumerable<Actor> Actors(Actor root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var actor in Actors(child))
                yield return actor;
    }

    private IEnumerable<T> Components<T>() => Actors(_scene.RootActor).SelectMany(a => a.Components).OfType<T>();
    private IEnumerable<IGrouping<string, MeshComponent>> Models => Components<MeshComponent>().GroupBy(x => x.Descriptor.Guid.ToString());
    private IEnumerable<AnimationPlayback> Playbacks => Components<SkeletalMeshComponent>().Select(x => x.Playback).OfType<AnimationPlayback>().Distinct();
    private MeshComponent Selected => Models.FirstOrDefault(x => x.Key == _selected)?.ElementAtOrDefault(_instance)
        ?? throw new ArgumentException("Select a loaded model first.");
    private static NotSupportedException Unsupported(string field) => new($"The Snooper renderer does not support this viewport setting: {field}.");

    public object Load(UObject export, bool append, CancellationToken ct)
    {
        Tick();
        Actor actor = export switch
        {
            UStaticMesh x => new MeshActor(x), UIRMesh x => new MeshActor(x), UHoudiniStaticMesh x => new MeshActor(x),
            USkinnedAsset x => new MeshActor(x), UGeometryCollection x => new MeshActor(x),
            USkeleton x => Skeleton(x), UAnimationAsset x => new MeshActor(x),
            UWorld x => new WorldActor(x, ct: ct), UBlueprintGeneratedClass x => new BlueprintActor(x),
            UMaterialInterface x => new MaterialActor(x),
            _ => throw new NotSupportedException($"Snooper cannot load {export.ExportType} in this viewport.")
        };
        if (actor.Components.Count == 0 && actor.Children.Count == 0) throw new NotSupportedException("The export contains no renderable components.");
        if (!append) Reset();
        _scene.LoadScene(actor);
        Tick();
        if (Actors(actor).SelectMany(a => a.Components).OfType<MeshComponent>().FirstOrDefault() is { } mesh)
        {
            _selected = mesh.Descriptor.Guid.ToString();
            _instance = _section = _morph = 0;
            Focus(mesh);
        }
        return State(new JObject());

        static Actor Skeleton(USkeleton skeleton)
        {
            var root = new Actor(skeleton.Name);
            root.Components.Add(new SkeletalMeshComponent(skeleton));
            return root;
        }
    }

    private void Tick()
    {
        _window.MakeCurrent();
        _window.Drain();
        var delta = (float)_clock.Elapsed.TotalSeconds;
        _clock.Restart();
        _scene.Update(delta);
    }

    private void Focus(MeshComponent mesh)
    {
        var center = Vector3.Transform(mesh.Bounds.Center, mesh.WorldMatrix);
        var distance = Math.Max(0.1f, mesh.Bounds.Extents.Length() * 2.5f);
        Aim(center + Vector3.Normalize(new Vector3(1, .6f, 1)) * distance, center);
    }

    private void Aim(Vector3 position, Vector3 target)
    {
        var forward = target - position;
        if (forward.LengthSquared() < 1e-10f || Vector3.Cross(forward, Vector3.UnitY).LengthSquared() < 1e-10f)
            throw new ArgumentException("Camera position and target must define a nonvertical viewing direction.");
        _distance = forward.Length();
        var rotation = Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateWorld(Vector3.Zero, -forward, Vector3.UnitY));
        _camera.CameraComponent.SetLocalTransform(new Transform(position, rotation));
    }

    public object State(JObject args)
    {
        Tick();
        var camera = _camera.CameraComponent;
        var position = camera.GetLocalTransform().Position;
        var clips = Playbacks.ToArray();
        return new
        {
            width = _width, height = _height, backend = GL.GetString(StringName.Renderer),
            camera = new { position = Vector(position), target = Vector(position - camera.Forward * _distance), mode = camera.ViewType.ToString(),
                modes = Enum.GetNames<CameraType>(), zoom = camera.FieldOfView, far = camera.FarClipPlane, speed = camera.MovementSpeed },
            render = new { showGrid = _grid.IsVisible, showSkybox = _environment.IsVisible,
                showLights = Components<LightComponentBase>().Any(l => l.IsVisible), vertexColor = FragmentColorMode.Labels[_scene.FragmentColor],
                availableVertexColors = FragmentColorMode.Labels },
            selectedModel = _selected, models = QueryHelpers.Page(Models.Select(g => new { id = g.Key, name = g.First().Name, path = g.First().Descriptor.Path,
                type = g.First().Type, visible = g.First().IsVisible, instances = g.Count(), sections = g.First().Descriptor.Lods.FirstOrDefault()?.Sections.Length }), args),
            timeline = new { paused = _paused, seconds = clips.FirstOrDefault()?.Time ?? 0, duration = clips.Select(x => x.Duration).DefaultIfEmpty().Max(), speed = _speed },
            animations = clips.Select((x,i) => new { index = i, duration = x.Duration, name = x.Animation.Name }),
            lights = Components<LightComponentBase>().Select((x,i) => new { index = i, type = x.Type, intensity = x.Intensity, position = Axes(x.GetLocalTransform().Position),
                color = new { r = x.Color.X, g = x.Color.Y, b = x.Color.Z, a = 1 } }).ToArray()
        };
    }

    public object Configure(JObject args)
    {
        Tick();
        var camera = _camera.CameraComponent;
        var position = camera.GetLocalTransform().Position;
        var target = position - camera.Forward * _distance;
        foreach (var item in ((JObject)args["values"]).Properties())
            switch (item.Name)
            {
                case "position": position = ParseVector(item.Value); break;
                case "target": target = ParseVector(item.Value); break;
                case "zoom": camera.FieldOfView = Number(item.Value, 1, 89); break;
                case "far": camera.FarClipPlane = Number(item.Value, camera.NearClipPlane * 2, float.MaxValue); break;
                case "speed": camera.MovementSpeed = Number(item.Value, 1, float.MaxValue); break;
                case "mode":
                    camera.ViewType = item.Value.Value<string>()?.ToLowerInvariant() switch
                    {
                        "flycam" => CameraType.Free,
                        "arcball" => CameraType.Orbital,
                        _ => ProfileOperations.ParseEnum<CameraType>(item.Value.Value<string>())
                    };
                    break;
                case "showGrid": _grid.IsVisible = Boolean(item.Value); break;
                case "showSkybox": _environment.IsVisible = Boolean(item.Value); break;
                case "showLights": foreach (var light in Components<LightComponentBase>()) light.SetVisibility(Boolean(item.Value)); break;
                case "vertexColor":
                    var label = item.Value.Value<string>()?.ToLowerInvariant() switch
                    {
                        "default" => FragmentColorMode.Labels[FragmentColorMode.Disabled],
                        "colors" => FragmentColorMode.Labels[FragmentColorMode.VertexColor],
                        "texturecoordinates" => FragmentColorMode.Labels[FragmentColorMode.Uv],
                        _ => item.Value.Value<string>()
                    };
                    int index = Array.FindIndex(FragmentColorMode.Labels, x => string.Equals(x, label, StringComparison.OrdinalIgnoreCase));
                    if (index < 0) throw Unsupported(item.Value.ToString());
                    _scene.FragmentColor = (uint)index; break;
                default: throw Unsupported(item.Name);
            }
        Aim(position, target);
        return State(new JObject());
    }

    private static Vector3 Axes(Vector3 v) => new(v.X, v.Z, v.Y);
    private static Quaternion Axes(Quaternion q) => new(q.X, q.Z, q.Y, -q.W);

    public object Model(JObject args)
    {
        Tick();
        var id = args.Value<string>("modelId");
        var group = Models.SingleOrDefault(x => x.Key == id)?.ToArray() ?? throw new ArgumentException("Unknown modelId.");
        if (args.Value<bool?>("remove") == true)
        {
            foreach (var mesh in group) mesh.Actor.Components.Remove(mesh);
            if (_selected == id) _selected = null;
            return new { removed = id };
        }
        if (_selected != id) { _selected = id; _section = _instance = _morph = 0; }
        var values = args["values"] as JObject ?? new JObject();
        if (values["instance"] is { } instance) _instance = Integer(instance, group.Length);
        var model = group[_instance];
        var sections = model.Descriptor.Lods.FirstOrDefault()?.Sections ?? [];
        if (values["section"] is { } section) _section = Integer(section, sections.Length);
        if (values["morph"] is { } morph) _morph = Integer(morph, model.Descriptor.Morphs?.Count ?? 0);
        var transform = (Transform)model.GetLocalTransform().Clone();
        foreach (var item in values.Properties())
            switch (item.Name)
            {
                case "section": case "instance": case "morph": break;
                case "visible": model.SetVisibility(Boolean(item.Value)); break;
                case "showSection":
                    if (_section >= sections.Length) throw new ArgumentException("Select a section first.");
                    var slot = sections[_section].MaterialIndex;
                    if (sections.Count(x => x.MaterialIndex == slot) > 1) throw Unsupported("showSection for a shared material slot");
                    model.SetMaterialVisibility(slot, Boolean(item.Value)); break;
                case "position": transform.Position = Axes(ParseVector(item.Value)); break;
                case "scale": transform.Scale = Axes(ParseVector(item.Value)); break;
                case "rotation": transform.Rotation = Axes(ParseRotation(item.Value)); break;
                case "wireframe":
                    if (Models.Count() > 1) throw Unsupported("per-model wireframe in a multi-model scene");
                    if (!Snooper.Core.Hardware.DeviceInfo.HasFragmentBarycentric) throw Unsupported("wireframe on this GPU");
                    _scene.Wireframe.Enabled = Boolean(item.Value); break;
                case "morphWeight" when model is SkinnedMeshComponent sk && _morph < sk.MorphWeights.Length:
                    sk.MorphWeights[_morph] = Number(item.Value, 0, 1);
                    MarkDirty(sk, DirtyFlags.Morph);
                    break;
                default: throw Unsupported(item.Name);
            }
        model.SetLocalTransform(transform);
        if (args.Value<bool?>("focus") == true) Focus(model);
        return new { id, model.Name, path = model.Descriptor.Path, model.Type, model.IsVisible, showWireframe = _scene.Wireframe.Enabled,
            transform = new { position = Axes(transform.Position), rotation = Axes(transform.Rotation), scale = Axes(transform.Scale) },
            sections = sections.Select((s,i) => new { index = i, visible = model.Materials[s.MaterialIndex].IsVisible, faces = s.IndexCount / 3, material = s.MaterialIndex }).ToArray(),
            materials = model.Materials.Select((m,i) => new { index = i, m.Name, m.Path }).ToArray(),
            sockets = model.Descriptor.Sockets.OfType<ISocketDescriptor>().Select(s => new { name = s.Name, transform = s.LocalMatrix, bone = (s as SkeletalMeshSocketDescriptor)?.BoneName }).ToArray(),
            morphs = model.Descriptor.Morphs?.Names.Select((name,i) => new { index = i, name }).ToArray(),
            bones = model.Descriptor.Skeleton is { } skeleton ? QueryHelpers.Page(skeleton.BoneDescriptors.Select((b,i) => new { name=b.Name, index=i, parentIndex=b.ParentIndex, rest=b.BindPoseLocalMatrix }), args) : null };
    }

    public object Lighting(JObject args)
    {
        Tick();
        var lights = Components<LightComponentBase>().ToArray();
        var light = lights[Integer(args["index"], lights.Length)];
        var transform = (Transform)light.GetLocalTransform().Clone();
        foreach (var item in (args["values"] as JObject ?? new JObject()).Properties())
            switch (item.Name)
            {
                case "intensity": light.Intensity = Number(item.Value, 0, float.MaxValue); break;
                case "position": transform.Position = Axes(ParseVector(item.Value)); break;
                case "rotation": transform.Rotation = Axes(ParseRotation(item.Value)); break;
                case "color":
                    if (Number(item.Value["a"], 0, 1) != 1) throw Unsupported("light color alpha");
                    light.Color = new(Number(item.Value["r"], 0, 1), Number(item.Value["g"], 0, 1), Number(item.Value["b"], 0, 1)); break;
                default: throw Unsupported(item.Name);
            }
        light.SetLocalTransform(transform);
        return State(new JObject());
    }

    public object MaterialParameters(JObject args)
    {
        Tick();
        var model = Models.SingleOrDefault(g => g.Key == args.Value<string>("modelId"))?.First() ?? throw new ArgumentException("Unknown modelId.");
        var material = model.Materials[Integer(args["index"], model.Materials.Length)];
        var values = args["values"] as JObject ?? new JObject();
        foreach (var item in values.Properties())
            if (item.Name is not ("roughnessMin" or "roughnessMax")) throw Unsupported(item.Name);
        if (values.Count > 0)
        {
            var edit = material.BeginEdit() ?? throw new InvalidOperationException("Material data is not ready.");
            var min = values["roughnessMin"] is { } lo ? Number(lo, 0, 1) : (float?)null;
            var max = values["roughnessMax"] is { } hi ? Number(hi, 0, 1) : (float?)null;
            foreach (var layer in edit.Layers) SetRoughness(layer, new(min ?? layer.Roughness.X, max ?? layer.Roughness.Y));
            material.CommitEdit();
        }
        var layers = (material.MaterialDataContainer as MaterialDataContainer)?.Layers;
        return new { material.Name, material.Path, index = material.Index, roughnessMin = layers?.FirstOrDefault()?.Roughness.X,
            roughnessMax = layers?.FirstOrDefault()?.Roughness.Y, diffuseColors = layers?.Select(x => x.DiffuseColor).ToArray() };
    }

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "set_Roughness")]
    private static extern void SetRoughness(MaterialLayer layer, Vector2 value);

    [System.Runtime.CompilerServices.UnsafeAccessor(System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = "MarkDirty")]
    private static extern void MarkDirty(ActorComponent component, DirtyFlags flags);

    public object Animate(UObject export, bool applyToSelected)
    {
        if (export is not UAnimationAsset animation) throw new ArgumentException("Select an animation asset.");
        if (!applyToSelected) Load(export, true, CancellationToken.None);
        else if (Selected is SkeletalMeshComponent mesh) mesh.SetAnimation(animation);
        else throw new ArgumentException("Select a skeletal mesh.");
        if (Selected is not SkeletalMeshComponent { Playback: { } playback }) throw new NotSupportedException("No compatible animation playback.");
        playback.IsPlaying = !_paused;
        playback.PlayRate = _speed;
        return State(new JObject());
    }

    public object Timeline(JObject args)
    {
        Tick();
        if (args["paused"] != null) _paused = args.Value<bool>("paused");
        if (args["speed"] is { } rate) _speed = Number(rate, 0, 100);
        if (args.Value<bool?>("clear") == true)
            foreach (var mesh in Components<SkeletalMeshComponent>()) mesh.Bind(null);
        var clips = Playbacks.ToArray();
        var seek = args["seconds"] is { } time ? Number(time, 0, clips.Select(x => x.Duration).DefaultIfEmpty().Max()) : (float?)null;
        foreach (var clip in clips)
        {
            clip.IsPlaying = !_paused;
            clip.PlayRate = _speed;
            if (seek.HasValue) clip.Seek(Math.Min(seek.Value, clip.Duration));
        }
        return State(new JObject());
    }

    public object SwapMaterial(UObject export)
    {
        Tick();
        if (export is not UMaterialInstance) throw new ArgumentException("Select a material instance.");
        var model = Selected;
        var lod = model.Descriptor.Lods.First();
        var slot = model.Materials[lod.Sections[_section].MaterialIndex];
        var path = new FSoftObjectPath(export.GetPathName(), string.Empty);
        slot.SwapMaterial(MaterialCache.GetOrCreateKey(path, Provider, lod.LayerCount));
        return State(new JObject());
    }

    public object Capture(JObject args)
    {
        var width = QueryHelpers.Integer(args, "width", _width, 32, 2048);
        var height = QueryHelpers.Integer(args, "height", _height, 32, 2048);
        _window.MakeCurrent();
        if (width != _width || height != _height) Resize(width, height);
        Tick();
        _scene.Render();
        var pixels = new byte[_width * _height * 4];
        GL.GetTextureImage((int)(uint)_scene.Pipeline.GetFinalTexture(), 0, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.Length, pixels);
        using var source = new SKBitmap(_width, _height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        Marshal.Copy(pixels, 0, source.GetPixels(), pixels.Length);
        using var bitmap = new SKBitmap(source.Info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Translate(0, _height);
            canvas.Scale(1, -1);
            canvas.DrawBitmap(source, 0, 0);
        }
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return new { mimeType = "image/png", imageData = Convert.ToBase64String(data.ToArray()), width = _width, height = _height };
    }

    private void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        _scene.Resize(width, height);
        _camera?.CameraComponent.Resize(width, height);
    }

    private static object Vector(Vector3 value) => new { x = value.X, y = value.Y, z = value.Z };
    private static Vector3 ParseVector(JToken value) => value is JObject vector && vector.Count == 3 && vector["x"] != null && vector["y"] != null && vector["z"] != null
        ? new Vector3(Number(vector["x"], -float.MaxValue, float.MaxValue), Number(vector["y"], -float.MaxValue, float.MaxValue), Number(vector["z"], -float.MaxValue, float.MaxValue))
        : throw new ArgumentException("Vector must contain numeric x, y and z fields.");
    internal static float Number(JToken token, float min, float max)
    {
        if (token?.Type is not (JTokenType.Integer or JTokenType.Float)) throw new ArgumentException("A number is required.");
        var value = token.Value<float>();
        return float.IsFinite(value) && value >= min && value <= max ? value : throw new ArgumentOutOfRangeException(nameof(token), $"Value must be between {min} and {max}.");
    }
    private static bool Boolean(JToken token) => token.Type == JTokenType.Boolean ? token.Value<bool>() : throw new ArgumentException("A boolean is required.");
    private static int Integer(JToken token, int count) => token.Type == JTokenType.Integer && token.Value<long>() >= 0 && token.Value<long>() < count
        ? token.Value<int>() : throw new ArgumentException("Index must be an integer within the available item range.");
    private static Quaternion ParseRotation(JToken token)
    {
        if (token is not JObject value || value.Count != 4) throw new ArgumentException("Rotation requires quaternion x,y,z,w fields.");
        var q = new Quaternion(Number(value["x"], -1, 1), Number(value["y"], -1, 1), Number(value["z"], -1, 1), Number(value["w"], -1, 1));
        if (q.LengthSquared() < 1e-10f) throw new ArgumentException("Rotation quaternion must be nonzero.");
        q = Quaternion.Normalize(q);
        return q;
    }

    public void Dispose()
    {
        _window.MakeCurrent();
        try { _scene.Dispose(); }
        finally
        {
            _window.Dispose();
            FModel.ViewModels.SnooperViewModel.Instance.EndAutomation();
        }
    }
}
