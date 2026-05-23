using System.Text.Json;
using System.Text.Json.Serialization;
using AddMissingQuestRequirements.Models;
using FluentAssertions;

namespace AddMissingQuestRequirements.Tests.Models;

public class TypeRuleTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ApplyToManualOverrides_missing_defaults_to_false()
    {
        var json = """{ "conditions": {}, "type": "MyType" }""";
        var rule = JsonSerializer.Deserialize<TypeRule>(json, Options);
        rule!.ApplyToManualOverrides.Should().BeFalse();
    }

    [Fact]
    public void ApplyToManualOverrides_true_deserializes()
    {
        var json = """{ "conditions": {}, "type": "MyType", "applyToManualOverrides": true }""";
        var rule = JsonSerializer.Deserialize<TypeRule>(json, Options);
        rule!.ApplyToManualOverrides.Should().BeTrue();
    }

    [Fact]
    public void ApplyToManualOverrides_false_deserializes()
    {
        var json = """{ "conditions": {}, "type": "MyType", "applyToManualOverrides": false }""";
        var rule = JsonSerializer.Deserialize<TypeRule>(json, Options);
        rule!.ApplyToManualOverrides.Should().BeFalse();
    }
}
