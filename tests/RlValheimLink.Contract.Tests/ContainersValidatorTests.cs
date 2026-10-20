using Newtonsoft.Json.Linq;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>`containers` rules the contract's fixtures do not exercise (CONTRACT.md §5a, Zod superRefine in valheim-link-containers.schema.ts).</summary>
public class ContainersValidatorTests
{
    private static JObject Basic() => (JObject)MessageJson.Parse(File.ReadAllText(Repo.Contract(Path.Combine("fixtures", "containers-basic.json"))));

    private static JObject Container(int i, int stacks) => new JObject
    {
        ["prefab"] = "piece_chest_wood",
        ["x"] = i, ["y"] = 30, ["z"] = 0,
        ["creatorPlayerId"] = "1000000000000001",
        ["privacy"] = "public",
        ["vehicle"] = false,
        ["slots"] = 4096,
        ["usedSlots"] = stacks,
        ["unreadable"] = false,
        ["omittedEntries"] = 0,
        ["items"] = new JArray(Enumerable.Range(0, stacks).Select(j => new JObject { ["prefab"] = "Item" + j, ["quality"] = 1, ["stack"] = 1 })),
    };

    [Fact]
    public void Entries_summed_over_containers_are_capped_at_the_message()
    {
        var msg = Basic();
        int full = Caps.ContainerEntriesMax / Caps.ContainerItemsMax;   // 195 full containers = 49 920 entries
        var cs = new JArray(Enumerable.Range(0, full).Select(i => Container(i, Caps.ContainerItemsMax)));
        cs.Add(Container(full, Caps.ContainerEntriesMax - full * Caps.ContainerItemsMax));
        msg["containers"] = cs;
        Assert.Empty(Validator.Validate(msg));

        ((JArray)cs.Last!["items"]!).Add(new JObject { ["prefab"] = "OneTooMany", ["quality"] = 1, ["stack"] = 1 });
        cs.Last!["usedSlots"] = (int)cs.Last!["usedSlots"]! + 1;
        Assert.Contains(Validator.Validate(msg), i => i.Path == "containers" && i.Code == "too_big");
    }

    [Fact]
    public void Minus_zero_and_zero_are_one_position()
    {
        var msg = Basic();
        var cs = (JArray)msg["containers"]!;
        var twin = (JObject)cs[0].DeepClone();
        cs[0]["x"] = 0; twin["x"] = -0.0;
        cs.Add(twin);
        Assert.Contains(Validator.Validate(msg), i => i.Path == "containers." + (cs.Count - 1) && i.Code == "custom");
    }

    [Fact]
    public void Unreadable_container_has_no_used_slots_either()
    {
        var msg = Basic();
        var c = (JObject)((JArray)msg["containers"]!)[0];
        c["unreadable"] = true;
        c["items"] = new JArray();
        c["usedSlots"] = 1;
        Assert.Contains(Validator.Validate(msg), i => i.Path == "containers.0.items" && i.Code == "custom");

        c["usedSlots"] = 0;
        Assert.Empty(Validator.Validate(msg));
    }

    [Fact]
    public void Empty_world_is_valid_and_body_cap_is_the_contracts()
    {
        var msg = Basic();
        msg["containers"] = new JArray();
        Assert.Empty(Validator.Validate(msg));
        Assert.Equal(10_485_760, Caps.BodyCapFor("containers"));
    }
}
