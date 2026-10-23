using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using FModel.Views.Snooper;
using FModel.Views.Snooper.Models;
using Newtonsoft.Json.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using SkiaSharp;

namespace FModel.Automation;

internal sealed class AssetViewport : IDisposable
{
    private readonly NativeWindow _window;
    private readonly Renderer _renderer;
    private readonly int _framebuffer;
    private readonly int _color;
    private readonly int _depth;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastUpdate;
    private int _width;
    private int _height;

    public AssetViewport(int width, int height)
    {
        _window = new NativeWindow(Snooper.CreateWindowSettings(width, height));
        _window.MakeCurrent();
        _renderer = new Renderer(width, height);
        GL.Enable(EnableCap.Blend);
        GL.Enable(EnableCap.CullFace);
        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.VertexProgramPointSize);
        GL.StencilOp(StencilOp.Keep, StencilOp.Replace, StencilOp.Replace);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        try
        {
            _renderer.Setup();
            _framebuffer = GL.GenFramebuffer();
            _color = GL.GenTexture();
            _depth = GL.GenRenderbuffer();
            Resize(width, height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public object Load(UObject export, bool append, CancellationToken ct)
    {
        _window.MakeCurrent();
        if (!append) _renderer.Options.ResetModelsLightsAnimations();
        var before = _renderer.Options.Models.Values.Sum(x => x.TransformsCount);
        _renderer.Load(ct, export, new Lazy<UObject>(() => export));
        if (_renderer.Options.Models.Values.Sum(x => x.TransformsCount) <= before)
            throw new NotSupportedException("The desktop renderer did not load any model for this export.");
        _renderer.Options.SetupModelsAndLights();
        return State(new JObject());
    }

    public object State(JObject args)
    {
        Tick();
        var camera = _renderer.CameraOp;
        return new
        {
            width = _width, height = _height, backend = GL.GetString(StringName.Renderer),
            camera = new { position = Vector(camera.Position), target = Vector(camera.Direction), mode = camera.Mode.ToString(), modes = Enum.GetNames<Camera.WorldMode>(), zoom = camera.Zoom, far = camera.Far, speed = camera.Speed },
            render = new { showGrid = _renderer.ShowGrid, showSkybox = _renderer.ShowSkybox, showLights = _renderer.ShowLights,
                showSkeleton = _renderer.IsSkeletonTreeOpen, vertexColor = _renderer.Color.ToString(), availableVertexColors = Enum.GetNames<VertexColor>() },
            selectedModel = _renderer.Options.SelectedModel.ToString(),
            models = QueryHelpers.Page(_renderer.Options.Models.Select(x => new { id = x.Key.ToString(), name = x.Value.Name, path = x.Value.Path,
                type = x.Value.Type, visible = x.Value.IsVisible, instances = x.Value.TransformsCount, sections = x.Value.Sections.Length,
                wireframe = x.Value.ShowWireframe, collisions = x.Value.ShowCollisions }), args),
            timeline = new { paused = _renderer.Options.Tracker.IsPaused, seconds = _renderer.Options.Tracker.ElapsedTime,
                duration = _renderer.Options.Tracker.MaxElapsedTime, speed = _renderer.Options.Tracker.TimeMultiplier },
            animations = _renderer.Options.Animations.Select((x, i) => new { index = i, name = x.Name, path = x.Path, duration = x.TotalElapsedTime }).ToArray(),
            lights = _renderer.Options.Lights.Select((x, i) => new { index = i, type = x.GetType().Name, intensity = x.Intensity,
                color = new { r = x.Color.X, g = x.Color.Y, b = x.Color.Z, a = x.Color.W }, position = x.Transform.Position }).ToArray()
        };
    }

    public object Configure(JObject args)
    {
        Tick();
        var values = (JObject) args["values"];
        var camera = _renderer.CameraOp;
        var position = camera.Position;
        var target = camera.Direction;
        foreach (var item in values.Properties())
        {
            switch (item.Name)
            {
                case "position": position = ParseVector(item.Value); break;
                case "target": target = ParseVector(item.Value); break;
                case "zoom": camera.Zoom = Number(item.Value, 1, 89); break;
                case "far": camera.Far = Number(item.Value, camera.Near * 2, float.MaxValue); break;
                case "speed": camera.Speed = Number(item.Value, 0, float.MaxValue); break;
                case "mode": camera.Mode = ProfileOperations.ParseEnum<Camera.WorldMode>(item.Value.Value<string>()); break;
                case "showGrid": _renderer.ShowGrid = Boolean(item.Value); break;
                case "showSkybox": _renderer.ShowSkybox = Boolean(item.Value); break;
                case "showLights": _renderer.ShowLights = Boolean(item.Value); break;
                case "showSkeleton": _renderer.IsSkeletonTreeOpen = Boolean(item.Value); break;
                case "vertexColor": _renderer.Color = ProfileOperations.ParseEnum<VertexColor>(item.Value.Value<string>()); break;
                default: throw new ArgumentException($"Unknown viewport setting: {item.Name}");
            }
        }
        if (Vector3.DistanceSquared(position, target) < 1e-10f || Vector3.Cross(target - position, Vector3.UnitY).LengthSquared() < 1e-10f)
            throw new ArgumentException("Camera position and target must define a nonvertical viewing direction.");
        camera.Position = position;
        camera.Direction = target;
        return State(new JObject());
    }

    public object Model(JObject args)
    {
        Tick();
        var guid = new FGuid(args.Value<string>("modelId"));
        if (!_renderer.Options.TryGetModel(guid, out var model)) throw new ArgumentException("Unknown modelId. Query viewer_state first.");
        if (_renderer.Options.SelectedModel != guid) _renderer.Options.SelectModel(guid);
        if (args.Value<bool?>("remove") == true)
        {
            _renderer.Options.RemoveModel(guid);
            return new { removed = guid.ToString() };
        }
        if (args["values"] is JObject values)
        {
            // JSON field order must not decide which instance or section receives an edit.
            foreach (var item in values.Properties().OrderBy(x => x.Name is "instance" or "section" or "morph" ? 0 : 1))
            {
                switch (item.Name)
                {
                    case "visible": model.IsVisible = Boolean(item.Value); break;
                    case "wireframe": model.ShowWireframe = Boolean(item.Value); break;
                    case "collisions": model.ShowCollisions = Boolean(item.Value); break;
                    case "instance": model.SelectedInstance = Integer(item.Value, model.TransformsCount); break;
                    case "position": var p = ParseVector(item.Value); model.GetTransform().Position = new FVector(p.X, p.Y, p.Z); break;
                    case "scale": var s = ParseVector(item.Value); model.GetTransform().Scale = new FVector(s.X, s.Y, s.Z); break;
                    case "rotation": model.GetTransform().Rotation = ParseRotation(item.Value); break;
                    case "section": _renderer.Options.SelectSection(Integer(item.Value, model.Sections.Length)); break;
                    case "showSection":
                        if (!_renderer.Options.TryGetSection(model, out var section)) throw new ArgumentException("Select a section first.");
                        section.Show = Boolean(item.Value); break;
                    case "morph":
                        if (model is not SkeletalModel sk) throw new ArgumentException("The selected model is not skeletal.");
                        _renderer.Options.SelectMorph(Integer(item.Value, sk.Morphs.Count), sk); break;
                    case "morphWeight":
                        if (model is not SkeletalModel skeletal) throw new ArgumentException("The selected model is not skeletal.");
                        skeletal.MorphTime = Number(item.Value, 0, 1); break;
                    default: throw new ArgumentException($"Unknown model setting: {item.Name}");
                }
            }
        }
        model.Update(_renderer.Options);
        if (args.Value<bool?>("focus") == true)
        {
            var matrix = model.GetTransform().Matrix;
            _renderer.CameraOp.Teleport(new Vector3(matrix.M41, matrix.M42, matrix.M43), model.Box, true);
        }
        return new { id = guid.ToString(), model.Name, model.Path, model.Type, model.IsVisible, model.ShowWireframe, model.ShowCollisions,
            transform = new { position = model.GetTransform().Position, rotation = model.GetTransform().Rotation, scale = model.GetTransform().Scale },
            sections = model.Sections.Select((x, i) => new { index = i, visible = x.Show, faces = x.FacesCount, material = x.MaterialIndex }).ToArray(),
            materials = model.Materials.Select((x, i) => new { index = i, name = x.Name, path = x.Path }).ToArray(),
            morphs = (model as SkeletalModel)?.Morphs.Select((x, i) => new { index = i, name = x.Name }).ToArray(),
            sockets = model.Sockets.Select((x, i) => new { index = i, name = x.Name, bone = x.BoneName.Text, transform = x.Transform.LocalMatrix,
                attachedModels = x.AttachedModels.Select(a => new { id = a.Guid.ToString(), instance = a.Instance }).ToArray() }).ToArray(),
            bones = model is SkeletalModel sm ? QueryHelpers.Page(sm.Skeleton.BonesByLoweredName.Select(x => new { name = x.Key, index = x.Value.Index,
                parentIndex = x.Value.ParentIndex, rest = x.Value.Rest.LocalMatrix }), args) : null };
    }

    public object Lighting(JObject args)
    {
        Tick();
        var index = QueryHelpers.Integer(args, "index", -1, 0, _renderer.Options.Lights.Count - 1);
        var light = _renderer.Options.Lights[index];
        if (args["values"] is JObject values)
            foreach (var item in values.Properties())
                switch (item.Name)
                {
                    case "intensity": light.Intensity = Number(item.Value, 0, float.MaxValue); break;
                    case "position": var p = ParseVector(item.Value); light.Transform.Position = new FVector(p.X, p.Y, p.Z); break;
                    case "rotation": light.Transform.Rotation = ParseRotation(item.Value); break;
                    case "color":
                        var c = item.Value as JObject ?? throw new ArgumentException("Color requires r,g,b,a fields.");
                        if (c.Count != 4) throw new ArgumentException("Color requires r,g,b,a fields.");
                        light.Color = new Vector4(Number(c["r"], 0, 1), Number(c["g"], 0, 1), Number(c["b"], 0, 1), Number(c["a"], 0, 1)); break;
                    default: throw new ArgumentException($"Unknown light setting: {item.Name}");
                }
        light.UpdateMatrices();
        return State(new JObject());
    }

    public object MaterialParameters(JObject args)
    {
        Tick();
        if (!_renderer.Options.TryGetModel(new FGuid(args.Value<string>("modelId")), out var model)) throw new ArgumentException("Unknown modelId.");
        var index = QueryHelpers.Integer(args, "index", -1, 0, model.Materials.Length - 1);
        var material = model.Materials[index];
        if (args["values"] is JObject values)
            foreach (var item in values.Properties())
                switch (item.Name)
                {
                    case "roughnessMin": material.RoughnessMin = Number(item.Value, 0, 1); break;
                    case "roughnessMax": material.RoughnessMax = Number(item.Value, 0, 1); break;
                    case "emissiveMultiplier": material.EmissiveMult = Number(item.Value, 0, float.MaxValue); break;
                    default: throw new ArgumentException($"Unknown material setting: {item.Name}");
                }
        return new { index, material.Name, material.Path, roughnessMin = material.RoughnessMin, roughnessMax = material.RoughnessMax,
            emissiveMultiplier = material.EmissiveMult, diffuseColors = material.DiffuseColor, emissiveColors = material.EmissiveColor };
    }

    public object Animate(UObject animation, bool applyToSelected)
    {
        Tick();
        var model = Services.ApplicationService.ApplicationView.CUE4Parse;
        var previous = model.ModelIsWaitingAnimation;
        try
        {
            model.ModelIsWaitingAnimation = applyToSelected;
            var before = _renderer.Options.Animations.Count;
            _renderer.Animate(animation);
            if (_renderer.Options.Animations.Count <= before) throw new NotSupportedException("Animation could not be attached to a compatible skeleton.");
            _renderer.Options.SetupModelsAndLights();
            if (!applyToSelected && _renderer.Options.TryGetModel(out var selected)) _renderer.CameraOp.Setup(selected.Box);
        }
        finally { model.ModelIsWaitingAnimation = previous; }
        return State(new JObject());
    }

    public object Timeline(JObject args)
    {
        Tick();
        var tracker = _renderer.Options.Tracker;
        if (args["paused"] != null) tracker.IsPaused = args.Value<bool>("paused");
        if (args["seconds"] != null) tracker.SafeSetElapsedTime(Number(args["seconds"], 0, tracker.MaxElapsedTime));
        if (args["speed"] != null) tracker.TimeMultiplier = Number(args["speed"], 0, 100);
        if (args.Value<bool?>("clear") == true) _renderer.Options.RemoveAnimations();
        _renderer.UpdateScene(0);
        return State(new JObject());
    }

    public object SwapMaterial(UObject material)
    {
        Tick();
        if (material is not UMaterialInstance instance) throw new NotSupportedException("Select a material instance export.");
        if (!_renderer.Options.TryGetSection(out _)) throw new InvalidOperationException("Select a model and section first.");
        _renderer.Swap(instance);
        return State(new JObject());
    }

    public object Capture(JObject args)
    {
        Tick();
        var width = QueryHelpers.Integer(args, "width", _width, 32, 2048);
        var height = QueryHelpers.Integer(args, "height", _height, 32, 2048);
        if (width != _width || height != _height) Resize(width, height);
        GL.Viewport(0, 0, _width, _height);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.ClearColor(0, 0, 0, 1);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        _renderer.Render();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        var pixels = new byte[_width * _height * 4];
        GL.ReadPixels(0, 0, _width, _height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
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
        _width = width; _height = height;
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
        GL.BindTexture(TextureTarget.Texture2D, _color);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int) TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int) TextureMagFilter.Linear);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _color, 0);
        GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.Depth24Stencil8, width, height);
        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depth);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete) throw new InvalidOperationException("Preview framebuffer is incomplete.");
        _renderer.WindowResized(width, height);
    }

    private void Tick()
    {
        _window.MakeCurrent();
        var now = _clock.Elapsed.TotalSeconds;
        _renderer.UpdateScene((float) (now - _lastUpdate));
        _lastUpdate = now;
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
    private static FQuat ParseRotation(JToken token)
    {
        if (token is not JObject value || value.Count != 4) throw new ArgumentException("Rotation requires quaternion x,y,z,w fields.");
        var q = new Quaternion(Number(value["x"], -1, 1), Number(value["y"], -1, 1), Number(value["z"], -1, 1), Number(value["w"], -1, 1));
        if (q.LengthSquared() < 1e-10f) throw new ArgumentException("Rotation quaternion must be nonzero.");
        q = Quaternion.Normalize(q);
        return new FQuat(q.X, q.Y, q.Z, q.W);
    }

    public void Dispose()
    {
        _window.MakeCurrent();
        _renderer.Dispose();
        GL.DeleteFramebuffer(_framebuffer);
        GL.DeleteTexture(_color);
        GL.DeleteRenderbuffer(_depth);
        _window.Dispose();
    }
}
