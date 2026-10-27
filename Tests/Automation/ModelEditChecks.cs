using Newtonsoft.Json.Linq;

internal static class ModelEditChecks
{
    public static async Task Run(RpcClient client, JObject assetArgs, JToken modelId)
    {
        var args = new JObject { ["profileId"] = assetArgs["profileId"], ["modelId"] = modelId };
        var original = await client.Call("viewer_model", args);
        if (original["sections"]!.Count() > 1)
        {
            var changed = await client.Call("viewer_model", new JObject(args)
            {
                ["values"] = new JObject { ["showSection"] = !original["sections"]![1]!.Value<bool>("visible"), ["section"] = 1 }
            });
            Require(changed["sections"]![1]!.Value<bool>("visible") != original["sections"]![1]!.Value<bool>("visible") &&
                JToken.DeepEquals(changed["sections"]![0], original["sections"]![0]), "Section edits are independent of JSON property order");
            await client.Call("viewer_model", new JObject(args)
            {
                ["values"] = new JObject { ["showSection"] = original["sections"]![1]!["visible"], ["section"] = 1 }
            });
        }
        var appended = await client.Call("viewer_load", new JObject(assetArgs) { ["append"] = true });
        var instance = appended["models"]!["items"]!.Single(x => JToken.DeepEquals(x["id"], modelId)).Value<int>("instances") - 1;
        Require(instance > 0, "Appending a mesh adds an instance");
        await client.Call("viewer_model", new JObject(args) { ["values"] = new JObject { ["instance"] = 0 } });
        var edited = await client.Call("viewer_model", new JObject(args)
        {
            ["values"] = new JObject { ["position"] = new JObject { ["x"] = 1, ["y"] = 2, ["z"] = 3 }, ["instance"] = instance }
        });
        Require(edited["transform"]!["position"]!.Value<float>("X") == 1 && edited["transform"]!["position"]!.Value<float>("Y") == 2,
            "Transforms apply to the requested instance even when its index is the last JSON property");
        var unchanged = await client.Call("viewer_model", new JObject(args) { ["values"] = new JObject { ["instance"] = 0 } });
        Require(JToken.DeepEquals(unchanged["transform"], original["transform"]), "Editing another instance preserves the original transform");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}
