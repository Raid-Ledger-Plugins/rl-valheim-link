using RlValheimLink.Core;
using Xunit;

namespace RlValheimLink.Contract.Tests;

/// <summary>
/// Core/KnownNameMapper: the character's known lists hold name tokens (Player.AddKnownRecipe / AddKnownPiece /
/// AddKnownItem store m_shared.m_name and Piece.m_name, 0.221.4 IL); the contract wants prefab ids (CONTRACT §3).
/// </summary>
public class KnownNameMapperTests
{
    static Dictionary<string, List<string>> Lookup(params (string key, string prefab)[] pairs)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (k, p) in pairs) KnownNameMapper.Add(d, k, p);
        return d;
    }

    static readonly Dictionary<string, List<string>> Recipes = Lookup(
        ("$item_sword_bronze", "SwordBronze"), ("SwordBronze", "SwordBronze"),
        ("$item_helmet_bronze", "HelmetBronze"),
        ("$piece_workbench", "piece_workbench"),
        ("$item_meat_cooked", "CookedMeat"),
        // One token, two prefabs (variants sharing a display name): both are sent.
        ("$item_cape_linen", "CapeLinen"), ("$item_cape_linen", "CapeLinen_Ashlands"));

    [Fact]
    public void Recipe_and_piece_tokens_map_to_prefabs_sorted_and_unique()
    {
        var r = KnownNameMapper.Map(new[] { "$piece_workbench", "$item_sword_bronze", "$item_helmet_bronze", "$item_sword_bronze" }, Recipes, 100);
        Assert.Equal(new[] { "HelmetBronze", "SwordBronze", "piece_workbench" }, r.Prefabs);
        Assert.Equal(0, r.Unmatched);
    }

    [Fact]
    public void One_token_naming_two_prefabs_emits_both()
    {
        var r = KnownNameMapper.Map(new[] { "$item_cape_linen" }, Recipes, 100);
        Assert.Equal(new[] { "CapeLinen", "CapeLinen_Ashlands" }, r.Prefabs);
    }

    [Fact]
    public void Unknown_tokens_are_dropped_counted_and_never_sent()
    {
        var raw = new[] { "$item_mod_gone", "$item_meat_cooked", "$piece_removed", "plain text", "", null };
        var r = KnownNameMapper.Map(raw, Recipes, 100);
        Assert.Equal(new[] { "CookedMeat" }, r.Prefabs);
        Assert.Equal(3, r.Unmatched);
        Assert.Equal(new[] { "$item_mod_gone", "$piece_removed", "plain text" }, r.UnmatchedExamples);
        Assert.DoesNotContain(r.Prefabs, p => p.Contains('$') || p.StartsWith("_item", StringComparison.Ordinal));
    }

    [Fact]
    public void An_entry_that_already_is_a_prefab_id_is_kept_through_the_identity_entry()
    {
        var r = KnownNameMapper.Map(new[] { "SwordBronze" }, Recipes, 100);
        Assert.Equal(new[] { "SwordBronze" }, r.Prefabs);
    }

    [Fact]
    public void Examples_stop_at_five_and_the_cap_applies_after_mapping()
    {
        var many = Enumerable.Range(0, 9).Select(i => "$item_unknown_" + i);
        var r = KnownNameMapper.Map(many, Recipes, 100);
        Assert.Equal(9, r.Unmatched);
        Assert.Equal(KnownNameMapper.MaxExamples, r.UnmatchedExamples.Count);

        var capped = KnownNameMapper.Map(new[] { "$item_cape_linen", "$item_sword_bronze", "$item_helmet_bronze" }, Recipes, 2);
        Assert.Equal(new[] { "CapeLinen", "CapeLinen_Ashlands" }, capped.Prefabs);
        Assert.Equal(2, capped.OverCap);
    }

    [Fact]
    public void Mapped_lists_pass_the_contract_validator()
    {
        var m = SyntheticCaptures.CharacterVanilla();
        m.KnownRecipes = KnownNameMapper.Map(new[] { "$item_sword_bronze", "$piece_workbench", "$item_cape_linen" }, Recipes, Caps.KnownMax).Prefabs;
        m.KnownItems = KnownNameMapper.Map(new[] { "$item_meat_cooked" }, Recipes, Caps.KnownMax).Prefabs;
        Assert.Empty(Validator.Validate(m));
    }

    [Fact]
    public void Prefab_names_from_the_lookup_are_sanitised_and_invalid_ones_count_as_unmatched()
    {
        var odd = Lookup(("$item_a", "ModSword(Clone)"), ("$item_b", ""));
        var r = KnownNameMapper.Map(new[] { "$item_a", "$item_b" }, odd, 100);
        Assert.Equal(new[] { "ModSword" }, r.Prefabs);
        Assert.Equal(1, r.Unmatched);
    }
}
