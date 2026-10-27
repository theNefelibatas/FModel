using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;
using FModel.Automation;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using System.Runtime.InteropServices;

internal static class InspectionChecks
{
    public static void Run()
    {
        var package = new ReferencePackage();
        IPackage Load(string path) => path == package.Name ? package : throw new Exception("Incorrect package path.");
        var reference = package.ResolvePackageIndex(new FPackageIndex(package, 3));
        var serialized = JObject.FromObject(reference!);
        var expected = ((IPackage) package).GetExport(2);
        Require(ReferenceEquals(ObjectReferences.Load(serialized.Value<string>("ObjectPath")!, Load, StringComparison.Ordinal, default), expected),
            "Serialized parser references round-trip by export index");
        Require(ReferenceEquals(ObjectReferences.Load(expected!.GetPathName(), Load, StringComparison.Ordinal, default), expected),
            "Nested references retain their outer object identity");
        Require(ReferenceEquals(ObjectReferences.Load(package.Name + ".Parent", Load, StringComparison.Ordinal, default), ((IPackage) package).GetExport(0)),
            "Named object references still resolve");
        try { ObjectReferences.Load(package.Name + ".99", Load, StringComparison.Ordinal, default); throw new Exception("Invalid index succeeded."); }
        catch (ArgumentOutOfRangeException) { Console.WriteLine("PASS: Invalid reference indices fail explicitly"); }

        using var source = new SKBitmap(4, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var pixels = Enumerable.Range(0, source.Width * source.Height).SelectMany(_ => new byte[] { 200, 100, 50, 0 }).ToArray();
        Marshal.Copy(pixels, 0, source.GetPixels(), pixels.Length);
        foreach (var channel in new[] { "rgb", "r", "g", "b", "a", "rgba" })
        {
            using var result = TexturePreview.Create(source, 2, channel);
            var pixel = result.GetPixel(0, 0);
            var expectedColor = channel switch
            {
                "rgb" => new SKColor(200, 100, 50), "r" => new SKColor(200, 200, 200),
                "g" => new SKColor(100, 100, 100), "b" => new SKColor(50, 50, 50),
                "a" => SKColors.Black, _ => SKColors.Transparent
            };
            Require(result.Width == 2 && result.Height == 1 && (channel == "rgba" ? pixel.Alpha == 0 : pixel == expectedColor),
                $"Texture channel {channel} preserves data beneath transparent pixels when resized");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private sealed class ReferencePackage : AbstractUePackage
    {
        public ReferencePackage() : base("/Fixture/Reference", null)
        {
            var parent = new UObject { Name = "Parent", Outer = new ResolvedPackageObject(this) };
            var other = new UObject { Name = "Other", Outer = new ResolvedPackageObject(this) };
            var child = new UObject { Name = "Child", Outer = new ResolvedLoadedObject(parent) };
            var sibling = new UObject { Name = "Child", Outer = new ResolvedLoadedObject(other) };
            ExportsLazy = new[] { parent, other, child, sibling }.Select(x => new Lazy<UObject>(() => x)).ToArray();
        }

        public override FPackageFileSummary Summary => throw new NotSupportedException();
        public override FNameEntrySerialized[] NameMap => [];
        public override int ImportMapLength => 0;
        public override int ExportMapLength => ExportsLazy.Length;
        public override int GetExportIndex(string name, StringComparison comparisonType = StringComparison.Ordinal) =>
            Array.FindIndex(ExportsLazy, x => x.Value.Name.Equals(name, comparisonType));
        public override ResolvedObject? ResolvePackageIndex(FPackageIndex? index) => index is { Index: > 0 } && index.Index <= ExportMapLength
            ? new Reference(this, index.Index - 1) : null;
        private sealed class Reference(ReferencePackage package, int index) : ResolvedObject(package, index)
        {
            public override FName Name => new(package.ExportsLazy[ExportIndex].Value.Name);
            public override ResolvedObject? Outer => package.ExportsLazy[ExportIndex].Value.Outer;
        }
    }
}
