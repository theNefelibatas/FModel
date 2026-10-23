using System;
using System.Globalization;
using System.IO;
using System.Threading;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Objects.UObject;

namespace FModel.Automation;

internal static class ObjectReferences
{
    public static UObject Load(string path, Func<string, IPackage> loadPackage, StringComparison comparison, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An object path is required.");
        path = path.Replace('\\', '/');
        var separator = path.IndexOf('.', path.LastIndexOf('/') + 1);
        var packagePath = separator < 0 ? path : path[..separator];
        var objectName = separator < 0 ? path[(path.LastIndexOf('/') + 1)..] : path[(separator + 1)..];
        var package = loadPackage(packagePath);
        // Serialized hard references use zero-based export indices, unlike named soft references.
        if (int.TryParse(objectName, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            return package.GetExport(index) ?? throw new ArgumentOutOfRangeException(nameof(path), "Object reference export index is out of range.");
        if (!objectName.Contains(':') && !objectName.Contains('.')) return package.GetExport(objectName, comparison);
        for (var i = 0; i < package.ExportMapLength; i++)
        {
            ct.ThrowIfCancellationRequested();
            var reference = package.ResolvePackageIndex(new FPackageIndex(package, i + 1));
            if (reference?.GetPathName(false).Equals(objectName, comparison) == true)
                return package.GetExport(i) ?? throw new InvalidDataException("Object reference could not be loaded.");
        }
        throw new FileNotFoundException("The package does not contain the referenced subobject.");
    }
}
