using Kaijura.App.Models;
using Kaijura.App.Services;

namespace Kaijura.Tests;

public sealed class JiraTransitionAnalyzerTests
{
    [Fact]
    public void BuildOptionsClassifiesDirectFormAndUnsupportedTransitions()
    {
        var options = new JiraTransitionAnalyzer().BuildOptions(
        [
            Transition("11", "Start Progress", "In Progress"),
            Transition("21", "Resolve", "Done", Field("comment", "Comment", "comment", "comment", "", "add")),
            Transition("31", "Close", "Closed", SelectField("resolution", "Resolucion", "resolution", "resolution", "10000", "Solucionada")),
            Transition("41", "Force Close", "Closed"),
            Transition("51", "Escalate", "Escalated", Field("attachment", "Adjunto", "attachment", "attachment", "", "set"))
        ]);

        var direct = Assert.Single(options, option => option.Id == "11");
        Assert.True(direct.IsEnabled);
        Assert.False(direct.RequiresForm);
        Assert.Equal("In Progress", direct.Label);

        var withComment = Assert.Single(options, option => option.Id == "21");
        Assert.True(withComment.IsEnabled);
        Assert.True(withComment.RequiresForm);
        Assert.True(withComment.RequiresComment);
        Assert.Equal("Done", withComment.Label);

        var withSelect = Assert.Single(options, option => option.Id == "31");
        Assert.True(withSelect.IsEnabled);
        Assert.True(withSelect.RequiresForm);
        var selectField = Assert.Single(withSelect.RequiredSelectFields);
        Assert.Equal("resolution", selectField.Id);
        Assert.Equal("Resolucion", selectField.Name);
        Assert.Equal("Solucionada", Assert.Single(selectField.Options).Name);
        Assert.Equal("Closed (Close)", withSelect.Label);

        var unsupported = Assert.Single(options, option => option.Id == "51");
        Assert.False(unsupported.IsEnabled);
        Assert.Contains("Adjunto", unsupported.DisabledReason);

        var duplicate = Assert.Single(options, option => option.Id == "41");
        Assert.Equal("Closed (Force Close)", duplicate.Label);
    }

    [Fact]
    public void BuildOptionsSupportsWorklogAndRequiredTextFields()
    {
        var options = new JiraTransitionAnalyzer().BuildOptions(
        [
            Transition(
                "51",
                "Block",
                "On Hold",
                Field("worklog", "Log Work", "array", "worklog", "worklog", "add"),
                Field("customfield_12345", "Block Comment", "string", "", "", "set"),
                Field("customfield_67890", "Root Cause", "text", "", "", "set"))
        ]);

        var option = Assert.Single(options);
        Assert.True(option.IsEnabled);
        Assert.True(option.RequiresForm);
        Assert.True(option.RequiresWorklog);
        Assert.Collection(
            option.RequiredTextFields,
            field =>
            {
                Assert.Equal("customfield_12345", field.Id);
                Assert.Equal("Block Comment", field.Name);
            },
            field =>
            {
                Assert.Equal("customfield_67890", field.Id);
                Assert.Equal("Root Cause", field.Name);
            });
    }

    private static JiraTransition Transition(string id, string name, string toStatus, params JiraTransitionField[] fields)
    {
        return new JiraTransition(id, name, toStatus, fields);
    }

    private static JiraTransitionField Field(
        string id,
        string name,
        string schemaType,
        string schemaSystem,
        string schemaItems,
        params string[] operations)
    {
        return new JiraTransitionField(id, name, Required: true, schemaType, schemaSystem, schemaItems, operations);
    }

    private static JiraTransitionField SelectField(
        string id,
        string name,
        string schemaType,
        string schemaSystem,
        string optionId,
        string optionName)
    {
        return new JiraTransitionField(id, name, Required: true, schemaType, schemaSystem, string.Empty, ["set"])
        {
            AllowedValues = [new JiraTransitionAllowedValue(optionId, optionName, string.Empty)]
        };
    }
}
